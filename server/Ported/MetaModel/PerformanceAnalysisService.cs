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
            {
                var form = _core.MetaService.GetForm(objectName);
                findings.AddRange(DisplayMethodFindings(form, methods));
                findings.AddRange(DataSourceFindings(form));
            }
            if (objectType == "AxDataEntityView")
                findings.AddRange(EntityReadinessFindings(_core.MetaService.GetDataEntityView(objectName), methods));
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
                case "AxDataEntityView":
                    var entity = _core.MetaService.GetDataEntityView(objectName);
                    declaration = MetaModelBridge.GetStringProperty(entity, "Declaration");
                    source = entity == null ? null : (IEnumerable<AxMethod>)entity.Methods ?? Enumerable.Empty<AxMethod>();
                    break;
                default:
                    problem = "Performance analysis covers classes, tables, forms and data entities; " + objectType + " has no code to analyze.";
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

        /// <summary>A property as text, enums (NoYes, FormLinkType) included.</summary>
        private static string Text(object target, string propertyName) =>
            target?.GetType().GetProperty(propertyName)?.GetValue(target)?.ToString();

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

        /// <summary>Data entity readiness for OData, DMF and dual-write: public names, keys, staging fields, mandatory fields, set-based processing.</summary>
        private IEnumerable<PerformanceFinding> EntityReadinessFindings(AxDataEntityView entity, List<KeyValuePair<string, string>> methods)
        {
            if (entity == null) yield break;
            PerformanceFinding Finding(string rule, string severity, string message, string fix) =>
                new PerformanceFinding { Rule = rule, Severity = severity, Method = "(entity)", Line = 0, Message = message, Fix = fix };
            string Prop(string name) => Text(entity, name) ?? "";
            bool Yes(string name) => Prop(name) == "Yes" || Prop(name) == "True";

            if (Yes("IsPublic") && (Prop("PublicEntityName") == "" || Prop("PublicCollectionName") == ""))
                yield return Finding("EntityPublicNames", "Warning", entity.Name + " is public but has no " + (Prop("PublicEntityName") == "" ? "PublicEntityName" : "PublicCollectionName") + ": OData can't expose it.",
                    "Set PublicEntityName (singular) and PublicCollectionName (plural).");

            var keys = Items(entity.GetType().GetProperty("Keys")?.GetValue(entity)).ToList();
            if (keys.Count == 0)
                yield return Finding("EntityKey", "Warning", entity.Name + " has no entity key: OData updates and data management imports need one.",
                    "Add a key (Keys node) on the natural key fields of the root data source.");

            var fields = Items(entity.GetType().GetProperty("Fields")?.GetValue(entity)).ToList();
            var fieldNames = new HashSet<string>(fields.Select(f => MetaModelBridge.GetStringProperty(f, "Name")).Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);

            string staging = Prop("DataManagementStagingTable");
            if (Yes("DataManagementEnabled") && staging == "")
                yield return Finding("EntityStaging", "Warning", entity.Name + " is enabled for data management but has no staging table.", "Generate the staging table (right-click the entity > Regenerate staging table).");
            else if (staging != "")
            {
                var stagingTable = _core.MetaService.GetTable(staging);
                if (stagingTable == null)
                    yield return Finding("EntityStaging", "Warning", "Staging table " + staging + " of " + entity.Name + " does not exist.", "Regenerate the staging table.");
                else
                {
                    var stagingFields = new HashSet<string>((stagingTable.Fields ?? Enumerable.Empty<AxTableField>()).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (string ext in MetaModelBridge.GetExtensionNames(_core.MetadataProvider.TableExtensions.GetPrimaryKeys(), staging))
                        foreach (var f in Items(_core.MetadataProvider.TableExtensions.Read(ext)?.Fields))
                            stagingFields.Add(MetaModelBridge.GetStringProperty(f, "Name"));
                    // RecId references (Int64) and computed or unmapped fields are not staged.
                    var dsTables = RootDataSources(entity).ToDictionary(ds => MetaModelBridge.GetStringProperty(ds, "Name") ?? "", ds => MetaModelBridge.GetStringProperty(ds, "Table"), StringComparer.OrdinalIgnoreCase);
                    bool Staged(object f)
                    {
                        if (!f.GetType().Name.Contains("Mapped") || f.GetType().Name.Contains("Unmapped")) return false;
                        string ds = MetaModelBridge.GetStringProperty(f, "DataSource"), dataField = MetaModelBridge.GetStringProperty(f, "DataField");
                        var tableField = ds != null && dsTables.TryGetValue(ds, out string t) && t != null
                            ? _core.MetaService.GetTable(t)?.Fields?.FirstOrDefault(tf => string.Equals(tf.Name, dataField, StringComparison.OrdinalIgnoreCase)) : null;
                        // System fields (RecId, ValidFrom, DataAreaId) are not in the table's field list and are not staged either.
                        return tableField != null && !(tableField is AxTableFieldInt64);
                    }
                    var missing = fields.Where(Staged).Select(f => MetaModelBridge.GetStringProperty(f, "Name"))
                        .Where(n => !string.IsNullOrEmpty(n) && !stagingFields.Contains(n)).OrderBy(n => n).ToList();
                    if (missing.Count > 0)
                        yield return Finding("EntityStagingFields", "Warning", "Staging table " + staging + " lacks " + missing.Count + " field(s) of " + entity.Name + ": " + string.Join(", ", missing.Take(15)) + (missing.Count > 15 ? ", ..." : "") + ". Imports and exports of these fields fail.",
                            "Add the fields to the staging table (or a staging table extension) with the same names and types.");
                }
            }

            var dataSources = RootDataSources(entity).ToList();
            var root = dataSources.FirstOrDefault();
            string rootName = root == null ? null : MetaModelBridge.GetStringProperty(root, "Name");
            string rootTable = root == null ? null : MetaModelBridge.GetStringProperty(root, "Table");
            var table = string.IsNullOrEmpty(rootTable) ? null : _core.MetaService.GetTable(rootTable);
            if (table != null)
            {
                var mapped = new HashSet<string>(fields.Where(f => string.Equals(MetaModelBridge.GetStringProperty(f, "DataSource"), rootName, StringComparison.OrdinalIgnoreCase))
                    .Select(f => MetaModelBridge.GetStringProperty(f, "DataField")).Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
                var unmappedMandatory = (table.Fields ?? Enumerable.Empty<AxTableField>())
                    .Where(f => "Yes".Equals(Text(f, "Mandatory"), StringComparison.OrdinalIgnoreCase) && !mapped.Contains(f.Name))
                    .Select(f => f.Name).ToList();
                if (unmappedMandatory.Count > 0)
                    yield return Finding("EntityMandatoryFields", "Warning", entity.Name + " does not map mandatory field(s) of " + rootTable + ": " + string.Join(", ", unmappedMandatory.Take(10)) + ". Inserts through the entity fail unless code sets them.",
                        "Map the fields, or default them in the entity's initValue/mapEntityToDataSource.");
            }

            if (Yes("EnableSetBasedSqlOperations"))
            {
                var blockers = dataSources.Select(ds => MetaModelBridge.GetStringProperty(ds, "Table")).Where(t => !string.IsNullOrEmpty(t))
                    .Where(t => _core.MetaService.GetView(t) != null || _core.MetaService.GetDataEntityView(t) != null
                        || !"Regular".Equals(_core.MetaService.GetTable(t)?.TableType.ToString() ?? "Regular", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (blockers.Count > 0)
                    yield return Finding("EntitySetBased", "Warning", entity.Name + " enables set-based processing but reads " + string.Join(", ", blockers) + " (a view, entity or temporary table): imports fall back to row by row or fail.",
                        "Turn off EnableSetBasedSqlOperations, or use regular tables only.");
                var overrides = methods.Select(m => m.Key).Where(n => n.Equals("insertEntityDataSource", StringComparison.OrdinalIgnoreCase) || n.Equals("updateEntityDataSource", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("insert", StringComparison.OrdinalIgnoreCase) || n.Equals("update", StringComparison.OrdinalIgnoreCase)).ToList();
                if (overrides.Count > 0)
                    yield return Finding("EntitySetBased", "Info", entity.Name + " enables set-based processing and overrides " + string.Join(", ", overrides) + ": set-based imports skip this code.",
                        "Move the logic to the staging-to-target step, or turn off set-based processing.");
            }
        }

        /// <summary>The entity's query data sources, root first, depth first.</summary>
        private static IEnumerable<object> RootDataSources(AxDataEntityView entity)
        {
            var viewMetadata = entity.GetType().GetProperty("ViewMetadata")?.GetValue(entity);
            var stack = new Stack<object>(Items(viewMetadata?.GetType().GetProperty("DataSources")?.GetValue(viewMetadata)).Reverse());
            while (stack.Count > 0)
            {
                var ds = stack.Pop();
                yield return ds;
                foreach (var child in Items(ds.GetType().GetProperty("DataSources")?.GetValue(ds)).Reverse())
                    stack.Push(child);
            }
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

        /// <summary>A bound control with the data source it inherits from its containers and the grid it sits in.</summary>
        private sealed class BoundControl
        {
            public string Name, DataSource;
            public object Grid;
        }

        private static IEnumerable<BoundControl> BoundControls(object container, string dataSource, object grid)
        {
            foreach (var child in Items(container?.GetType().GetProperty("Controls")?.GetValue(container)))
            {
                // Hidden controls (often country-specific ones) don't show data.
                if ("No".Equals(Text(child, "Visible"), StringComparison.OrdinalIgnoreCase)) continue;
                string ds = MetaModelBridge.GetStringProperty(child, "DataSource");
                ds = string.IsNullOrEmpty(ds) ? dataSource : ds;
                var inGrid = child.GetType().Name == "AxFormGridControl" ? child : grid;
                bool bound = !string.IsNullOrEmpty(MetaModelBridge.GetStringProperty(child, "DataField"))
                    || !string.IsNullOrEmpty(MetaModelBridge.GetStringProperty(child, "DataGroup"))
                    || !string.IsNullOrEmpty(MetaModelBridge.GetStringProperty(child, "DataMethod"));
                if (bound && !string.IsNullOrEmpty(ds))
                    yield return new BoundControl { Name = MetaModelBridge.GetStringProperty(child, "Name"), DataSource = ds, Grid = inGrid };
                foreach (var nested in BoundControls(child, ds, inGrid)) yield return nested;
            }
        }

        /// <summary>
        /// Form data sources: OnlyFetchActive where only a few fields are shown in grids (or on lookup forms), and joined data
        /// sources whose link type queries again for every record the user moves to.
        /// </summary>
        private IEnumerable<PerformanceFinding> DataSourceFindings(AxForm form)
        {
            if (form?.Design == null) yield break;
            PerformanceFinding Finding(string rule, string severity, string message, string fix) =>
                new PerformanceFinding { Rule = rule, Severity = severity, Method = "(form)", Line = 0, Message = message, Fix = fix };
            var bound = BoundControls(form.Design, MetaModelBridge.GetStringProperty(form.Design, "DataSource"), null).ToList();
            string pattern = Text(form.Design, "Pattern") ?? "";
            bool lookup = pattern.IndexOf("Lookup", StringComparison.OrdinalIgnoreCase) >= 0 || form.Name.EndsWith("Lookup", StringComparison.OrdinalIgnoreCase);
            var dataSources = Items(form.DataSources).ToList();
            foreach (var ds in dataSources)
            {
                string name = MetaModelBridge.GetStringProperty(ds, "Name") ?? "";
                string tableName = MetaModelBridge.GetStringProperty(ds, "Table");
                var shown = bound.Where(c => name.Equals(c.DataSource, StringComparison.OrdinalIgnoreCase)).ToList();

                if (!"Yes".Equals(Text(ds, "OnlyFetchActive"), StringComparison.OrdinalIgnoreCase) && shown.Count > 0 && shown.All(c => c.Grid != null))
                {
                    int fieldCount = _core.MetaService.GetTable(tableName ?? "")?.Fields?.Count ?? 0;
                    if (lookup || (fieldCount >= 30 && shown.Count <= 10))
                        yield return Finding("OnlyFetchActive", lookup ? "Warning" : "Info",
                            "Data source " + name + " fetches every field of " + tableName + (fieldCount > 0 ? " (" + fieldCount + ")" : "") + " but the form shows " + shown.Count + " of them, only in a grid" + (lookup ? " of a lookup form" : "") + ".",
                            "Set OnlyFetchActive = Yes on " + name + ", unless code reads fields of the record that no control shows.");
                }

                string joinSource = MetaModelBridge.GetStringProperty(ds, "JoinSource");
                string linkType = Text(ds, "LinkType") ?? "";
                if (string.IsNullOrEmpty(joinSource)) continue;
                var parentGrids = new HashSet<object>(bound.Where(c => joinSource.Equals(c.DataSource, StringComparison.OrdinalIgnoreCase) && c.Grid != null).Select(c => c.Grid));
                bool sameGrid = shown.Any(c => c.Grid != null && parentGrids.Contains(c.Grid));
                // Passive links are refreshed by code on purpose (country-specific tables), so only Delayed and Active are reported.
                if (sameGrid && (linkType == "Delayed" || linkType == "Active"))
                    yield return Finding("FormLinkType", "Warning",
                        "Data source " + name + " (LinkType " + linkType + ") is shown in " + joinSource + "'s grid without being joined to it: it is queried separately for the current " + joinSource + " record instead of with the grid's rows.",
                        "Join it to " + joinSource + " with LinkType InnerJoin (or OuterJoin when the record may be missing), so the grid reads both in one query.");
                else if (linkType == "Active")
                    yield return Finding("FormLinkType", "Info",
                        "Data source " + name + " (LinkType Active) queries again on every move in " + joinSource + ", even while the user scrolls.",
                        "Use LinkType Delayed: it waits until the user stops on a record.");
            }
        }
    }
}
