using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Entity relationship diagram data: a table's fields, primary key and relations (table extensions included),
    // and which tables have relations to a table (the cross-reference database does not record relations).
    public sealed partial class MetaModelBridge
    {
        public string TableErdJson(string[] tableNames)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var tables = new List<Dictionary<string, object>>();
            var missing = new List<string>();
            foreach (string name in (tableNames ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).Take(200))
            {
                var table = MetaService.GetTable(name);
                if (table == null) { missing.Add(name); continue; }
                var extensions = GetExtensionNames(MetadataProvider.TableExtensions.GetPrimaryKeys(), table.Name)
                    .Select(n => MetadataProvider.TableExtensions.Read(n)).Where(e => e != null).ToList();

                var fields = (GetEffectiveTableFields(table.Name) ?? new Dictionary<string, AxTableField>()).Values
                    .Select(f => new Dictionary<string, object> { ["name"] = f.Name, ["type"] = FieldTypeName(f) }).ToList();

                var relations = new List<Dictionary<string, object>>();
                foreach (object r in Items(table.Relations).Concat(extensions.SelectMany(e => Items(e.Relations))))
                {
                    var constraints = new List<Dictionary<string, object>>();
                    foreach (object c in Items(r.GetType().GetProperty("Constraints")?.GetValue(r)))
                    {
                        var d = new Dictionary<string, object> { ["kind"] = c.GetType().Name.Replace("AxTableRelationConstraint", "") };
                        foreach (string p in new[] { "Field", "RelatedField", "Value", "ValueStr" })
                        {
                            object v = c.GetType().GetProperty(p)?.GetValue(c);
                            if (v != null && v.ToString() != "") d[p == "ValueStr" ? "value" : char.ToLowerInvariant(p[0]) + p.Substring(1)] = v.ToString();
                        }
                        constraints.Add(d);
                    }
                    relations.Add(new Dictionary<string, object>
                    {
                        ["name"] = GetStringProperty(r, "Name"),
                        ["relatedTable"] = GetStringProperty(r, "RelatedTable"),
                        ["cardinality"] = r.GetType().GetProperty("Cardinality")?.GetValue(r)?.ToString(),
                        ["relatedCardinality"] = r.GetType().GetProperty("RelatedTableCardinality")?.GetValue(r)?.ToString(),
                        ["relationshipType"] = r.GetType().GetProperty("RelationshipType")?.GetValue(r)?.ToString(),
                        ["constraints"] = constraints,
                    });
                }

                // Primary key: the PrimaryIndex's fields; a surrogate key is RecId.
                string primaryIndex = table.PrimaryIndex;
                var key = new List<string>();
                if (string.IsNullOrEmpty(primaryIndex) || primaryIndex.Equals("SurrogateKey", StringComparison.OrdinalIgnoreCase))
                    key.Add("RecId");
                else
                {
                    object index = Items(table.Indexes).FirstOrDefault(i => string.Equals(GetStringProperty(i, "Name"), primaryIndex, StringComparison.OrdinalIgnoreCase));
                    foreach (object f in Items(index?.GetType().GetProperty("Fields")?.GetValue(index)))
                        key.Add(GetStringProperty(f, "DataField"));
                }

                string model = GetObjectModelName("AxTable", table.Name);
                tables.Add(new Dictionary<string, object>
                {
                    ["name"] = table.Name,
                    ["label"] = table.Label,
                    ["model"] = model,
                    ["isCustom"] = IsCustomModel(model),
                    ["tableGroup"] = table.TableGroup.ToString(),
                    ["primaryIndex"] = primaryIndex,
                    ["primaryKey"] = key.Where(k => !string.IsNullOrEmpty(k)).ToList(),
                    ["fields"] = fields,
                    ["relations"] = relations,
                });
            }
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["tables"] = tables, ["missing"] = missing });
        }

        private static IEnumerable<object> Items(object collection) =>
            collection is IEnumerable e ? e.Cast<object>().Where(o => o != null) : Enumerable.Empty<object>();

        // ── Incoming relations ──

        private static readonly object RelationIndexLock = new object();
        private static Dictionary<string, HashSet<string>> _relationIndex;
        private static string _relationIndexKey;
        private static readonly Regex RelatedTablePattern = new Regex(@"<RelatedTable>\s*([A-Za-z_]\w*)\s*</RelatedTable>", RegexOptions.Compiled);

        /// <summary>Tables (and table extensions' base tables) that have a relation to the table.</summary>
        public string IncomingRelationsJson(string tableName, bool force)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var index = RelationIndex(force, out bool fromCache);
            var sources = index.TryGetValue(tableName ?? "", out var set) ? set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true, ["table"] = tableName, ["tables"] = sources, ["ms"] = sw.ElapsedMilliseconds, ["cached"] = fromCache,
            });
        }

        private static Dictionary<string, HashSet<string>> RelationIndex(bool force, out bool fromCache)
        {
            lock (RelationIndexLock)
            {
                var config = DynamicsConfigurationReader.ReadActiveConfiguration();
                var roots = config.ReferenceMetadataFolders.Concat(new[] { config.CustomMetadataFolder })
                    .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var dirs = new List<string>();
                foreach (string root in roots)
                    foreach (string package in Directory.GetDirectories(root))
                        foreach (string model in Directory.GetDirectories(package))
                            foreach (string kind in new[] { "AxTable", "AxTableExtension" })
                            {
                                string dir = Path.Combine(model, kind);
                                if (Directory.Exists(dir)) dirs.Add(dir);
                            }
                string key = dirs.Count + "-" + string.Join(";", dirs.Select(d => Directory.GetLastWriteTimeUtc(d).Ticks)).GetHashCode();
                fromCache = true;
                if (!force && _relationIndex != null && _relationIndexKey == key) return _relationIndex;

                string cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode", "cache");
                string file = Path.Combine(cacheFolder, "relations-" + string.Join("_", roots).GetHashCode().ToString("x") + ".json");
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (!force && File.Exists(file))
                {
                    try
                    {
                        var cached = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                        if (cached != null && (cached["key"] as string) == key)
                        {
                            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in (Dictionary<string, object>)cached["incoming"])
                                map[kv.Key] = new HashSet<string>(((IEnumerable)kv.Value).Cast<object>().Select(o => o.ToString()), StringComparer.OrdinalIgnoreCase);
                            _relationIndex = map; _relationIndexKey = key;
                            return map;
                        }
                    }
                    catch { }
                }

                fromCache = false;
                var found = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.xml")), new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
                {
                    string text;
                    try { text = File.ReadAllText(path); } catch { return; }
                    int start = text.IndexOf("<Relations>", StringComparison.Ordinal);
                    if (start < 0) return;
                    int end = text.IndexOf("</Relations>", start, StringComparison.Ordinal);
                    string source = Path.GetFileNameWithoutExtension(path).Split('.')[0];
                    foreach (Match m in RelatedTablePattern.Matches(end > start ? text.Substring(start, end - start) : text.Substring(start)))
                        if (!m.Groups[1].Value.Equals(source, StringComparison.OrdinalIgnoreCase))
                            found.GetOrAdd(m.Groups[1].Value, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[source] = 0;
                });
                var result = found.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value.Keys, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                try
                {
                    Directory.CreateDirectory(cacheFolder);
                    File.WriteAllText(file, serializer.Serialize(new Dictionary<string, object>
                    {
                        ["key"] = key, ["incoming"] = result.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                    }));
                }
                catch { }
                _relationIndex = result; _relationIndexKey = key;
                return result;
            }
        }
    }
}
