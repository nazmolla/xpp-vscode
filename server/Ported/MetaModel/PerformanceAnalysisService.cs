using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using XppVsCode.Server.Analysis;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>Static performance analysis of a class's, table's or form's methods (PerformanceRules with metadata facts).</summary>
    internal sealed class PerformanceAnalysisService : IPerformanceAnalysis
    {
        private readonly IMetadataCore _core;

        public PerformanceAnalysisService(IMetadataCore core)
        {
            _core = core;
        }

        public string AnalyzePerformanceJson(string objectType, string objectName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (!TryReadCode(objectType, objectName, out string declaration, out var methods, out string problem))
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = problem });

            var facts = new Dictionary<string, TableFacts>(StringComparer.OrdinalIgnoreCase);
            TableFacts Facts(string type)
            {
                if (!facts.TryGetValue(type, out var f))
                    facts[type] = f = ReadFacts(type);
                return f;
            }
            var findings = methods.SelectMany(m => PerformanceRules.Analyze(m.Key, m.Value, declaration, Facts)).ToList();
            if (objectType == "AxTable")
                findings.AddRange(TableCacheFindings(_core.MetaService.GetTable(objectName)));
            if (objectType == "AxForm")
                findings.AddRange(DisplayMethodFindings(_core.MetaService.GetForm(objectName), methods));
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["objectType"] = objectType,
                ["objectName"] = objectName,
                ["methods"] = methods.Count,
                ["findings"] = findings.Select(f => new Dictionary<string, object>
                {
                    ["rule"] = f.Rule, ["severity"] = f.Severity, ["method"] = f.Method, ["line"] = f.Line, ["message"] = f.Message, ["fix"] = f.Fix,
                }).ToList(),
            });
        }

        private bool TryReadCode(string objectType, string objectName, out string declaration, out List<KeyValuePair<string, string>> methods, out string problem)
        {
            declaration = null;
            problem = null;
            methods = new List<KeyValuePair<string, string>>();
            IEnumerable<AxMethod> source;
            switch (objectType)
            {
                case "AxClass":
                    var cls = _core.MetaService.GetClass(objectName);
                    declaration = cls?.Declaration;
                    source = cls?.Methods;
                    break;
                case "AxTable":
                    var table = _core.MetaService.GetTable(objectName);
                    declaration = table?.Declaration;
                    source = table?.Methods;
                    break;
                case "AxForm":
                    var form = _core.MetaService.GetForm(objectName);
                    declaration = form?.SourceCode?.Declaration;
                    source = form?.Methods;
                    break;
                default:
                    problem = "Performance analysis covers classes, tables and forms; " + objectType + " has no code to analyze.";
                    return false;
            }
            if (source == null)
            {
                problem = objectType.Substring(2) + " " + objectName + " not found.";
                return false;
            }
            methods.AddRange(source.Select(m => new KeyValuePair<string, string>(m.Name, m.Source)));
            return true;
        }

        private TableFacts ReadFacts(string type)
        {
            var table = _core.MetaService.GetTable(type);
            if (table == null) return null;
            var names = new HashSet<string>((table.Methods ?? Enumerable.Empty<AxMethod>()).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            var indexes = Items(table.Indexes).Concat(MetaModelBridge.GetExtensionNames(_core.MetadataProvider.TableExtensions.GetPrimaryKeys(), table.Name)
                    .SelectMany(n => Items(_core.MetadataProvider.TableExtensions.Read(n)?.Indexes)))
                .Select(ix => Items(ix.GetType().GetProperty("Fields")?.GetValue(ix)).Select(f => MetaModelBridge.GetStringProperty(f, "DataField")).Where(f => !string.IsNullOrEmpty(f)).ToArray())
                .ToList();
            return new TableFacts
            {
                Name = table.Name,
                OverridesInsert = names.Contains("insert"),
                OverridesUpdate = names.Contains("update"),
                OverridesDelete = names.Contains("delete"),
                HasDeleteActions = table.DeleteActions != null && table.DeleteActions.Count > 0,
                DateEffective = !"None".Equals(table.ValidTimeStateFieldType.ToString(), StringComparison.OrdinalIgnoreCase),
                Indexes = indexes,
            };
        }

        private static IEnumerable<object> Items(object collection) =>
            collection is System.Collections.IEnumerable items ? items.Cast<object>().Where(i => i != null) : Enumerable.Empty<object>();

        /// <summary>CacheLookup against the table's group: setup tables should cache, often-written tables should not cache entirely.</summary>
        private static IEnumerable<PerformanceFinding> TableCacheFindings(AxTable table)
        {
            if (table == null || !"Regular".Equals(table.TableType.ToString(), StringComparison.OrdinalIgnoreCase)) yield break;
            string group = table.TableGroup.ToString(), cache = table.CacheLookup.ToString();
            bool setup = group == "Parameter" || group == "Group" || group == "Reference" || group == "Framework";
            bool written = group.StartsWith("Transaction", StringComparison.Ordinal) || group.StartsWith("Worksheet", StringComparison.Ordinal);
            PerformanceFinding Finding(string severity, string message, string fix) =>
                new PerformanceFinding { Rule = "TableCache", Severity = severity, Method = "(table)", Line = 0, Message = message, Fix = fix };
            if (cache == "EntireTable" && (written || group == "Main"))
                yield return Finding("Warning", table.Name + " (TableGroup " + group + ") caches the entire table: every insert, update or delete flushes the whole cache on every server.",
                    "Use CacheLookup Found (or NotInTTS) for tables that are written during normal work.");
            else if (setup && (cache == "None" || cache == "NotInTTS"))
                yield return Finding("Info", table.Name + " (TableGroup " + group + ") has CacheLookup " + cache + ": setup data is read far more than written.",
                    group == "Parameter" ? "Use EntireTable for parameter tables." : "Use Found, or EntireTable when the table is small.");
            else if (group == "Main" && cache == "None")
                yield return Finding("Info", table.Name + " (TableGroup Main) has CacheLookup None: find() lookups by key go to the database every time.",
                    "Use Found so lookups by a unique index are cached.");
        }

        /// <summary>Display methods shown in a grid run once per visible row unless the data source caches them with cacheAddMethod.</summary>
        private static IEnumerable<PerformanceFinding> DisplayMethodFindings(AxForm form, List<KeyValuePair<string, string>> formMethods)
        {
            if (form == null) yield break;
            var code = string.Join("\n", formMethods.Select(m => m.Value)
                .Concat(Items(form.DataSources).SelectMany(ds => Items(ds.GetType().GetProperty("Methods")?.GetValue(ds))).Select(m => MetaModelBridge.GetStringProperty(m, "Source"))));
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grid in Controls(form.Design).Where(c => c.GetType().Name == "AxFormGridControl"))
                foreach (var control in Controls(grid))
                {
                    string method = MetaModelBridge.GetStringProperty(control, "DataMethod");
                    string dataSource = MetaModelBridge.GetStringProperty(control, "DataSource") ?? MetaModelBridge.GetStringProperty(grid, "DataSource");
                    if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(dataSource) || method.Contains("::") || !reported.Add(dataSource + "." + method)) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(code, @"cacheAddMethod\s*\([^;]*\b" + System.Text.RegularExpressions.Regex.Escape(method) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    string table = Items(form.DataSources).Where(ds => dataSource.Equals(MetaModelBridge.GetStringProperty(ds, "Name"), StringComparison.OrdinalIgnoreCase))
                        .Select(ds => MetaModelBridge.GetStringProperty(ds, "Table")).FirstOrDefault() ?? dataSource;
                    yield return new PerformanceFinding
                    {
                        Rule = "UncachedDisplayMethod", Severity = "Info", Method = "(form)", Line = 0,
                        Message = "Grid control " + MetaModelBridge.GetStringProperty(control, "Name") + " shows display method " + dataSource + "." + method + ", which runs for every row shown.",
                        Fix = "In the data source's init(), after super(): " + dataSource + "_ds.cacheAddMethod(tableMethodStr(" + table + ", " + method + "));",
                    };
                }
        }

        private static IEnumerable<object> Controls(object container)
        {
            foreach (var child in Items(container?.GetType().GetProperty("Controls")?.GetValue(container)))
            {
                yield return child;
                foreach (var grandChild in Controls(child)) yield return grandChild;
            }
        }
    }
}
