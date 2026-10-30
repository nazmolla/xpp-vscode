using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Finds standard implementations to learn from or reuse, the way X++ developers work: "something like this object"
    /// (objects that share the most distinctive references with it: base class, interfaces, attributes, called APIs,
    /// tables) or a described task ("batch job with a contract", "number sequence", "form with the ListPage pattern")
    /// mapped to the framework classes such implementations extend, call or are attributed with.
    /// Also lists obsolete classes and methods ([SysObsolete]) with their messages.
    /// </summary>
    internal sealed class CodeExamples
    {
        private readonly XRefQuery _xref;

        public CodeExamples(XRefQuery xref) { _xref = xref; }

        internal sealed class Example
        {
            public string ObjectType, ObjectName, Model, Reason;
            public double Score;
        }

        // Object part of a cross-reference path: /Classes/X/Methods/m -> /Classes/X.
        private const string ObjectOf =
            "LEFT({0}, CHARINDEX('/', {0} + '/', CHARINDEX('/', {0}, 2) + 1) - 1)";

        private sealed class Recipe
        {
            public string[] Words;
            public string Target;      // cross-reference path
            public int[] Kinds;        // 1 call, 2 type reference, 4 extends, 7 attribute
            public string Describe;
        }

        private static readonly Recipe[] Recipes =
        {
            new Recipe { Words = new[] { "sysoperation", "batch", "batch job", "service operation", "controller" }, Target = "/Classes/SysOperationServiceController", Kinds = new[] { 4 }, Describe = "SysOperation controllers (batch jobs with a contract and a service)" },
            new Recipe { Words = new[] { "data contract", "contract", "parameters class" }, Target = "/Classes/DataContractAttribute", Kinds = new[] { 7 }, Describe = "data contract classes ([DataContract])" },
            new Recipe { Words = new[] { "runbase", "dialog" }, Target = "/Classes/RunBaseBatch", Kinds = new[] { 4 }, Describe = "RunBaseBatch classes (legacy dialogs and batch jobs; prefer SysOperation for new code)" },
            new Recipe { Words = new[] { "number sequence", "numbersequence", "numseq", "number seq" }, Target = "/Classes/NumberSeqApplicationModule", Kinds = new[] { 4 }, Describe = "number sequence modules (NumberSeqApplicationModule subclasses)" },
            new Recipe { Words = new[] { "business event" }, Target = "/Classes/BusinessEventsBase", Kinds = new[] { 4 }, Describe = "business events (BusinessEventsBase subclasses)" },
            new Recipe { Words = new[] { "report", "ssrs", "data provider", "dp class" }, Target = "/Classes/SrsReportDataProviderBase", Kinds = new[] { 4 }, Describe = "SSRS report data providers" },
            new Recipe { Words = new[] { "report controller", "print management", "printmgmt" }, Target = "/Classes/SrsPrintMgmtController", Kinds = new[] { 4 }, Describe = "report controllers with print management" },
            new Recipe { Words = new[] { "ui builder", "uibuilder" }, Target = "/Classes/SysOperationAutomaticUIBuilder", Kinds = new[] { 4 }, Describe = "SysOperation UI builders" },
            new Recipe { Words = new[] { "custom service", "web service", "service class", "json service", "entry point" }, Target = "/Classes/SysEntryPointAttribute", Kinds = new[] { 7 }, Describe = "service operations ([SysEntryPoint])" },
            new Recipe { Words = new[] { "lookup", "systablelookup" }, Target = "/Classes/SysTableLookup", Kinds = new[] { 1, 2 }, Describe = "custom lookups (SysTableLookup)" },
            new Recipe { Words = new[] { "workflow" }, Target = "/Classes/WorkflowDocument", Kinds = new[] { 4 }, Describe = "workflow documents" },
            new Recipe { Words = new[] { "chain of command", "coc", "extension class", "extensionof" }, Target = "/Classes/ExtensionOf", Kinds = new[] { 7 }, Describe = "Chain of Command extension classes" },
            new Recipe { Words = new[] { "data event", "event handler", "dataeventhandler" }, Target = "/Classes/DataEventHandler", Kinds = new[] { 7 }, Describe = "data event handlers" },
            new Recipe { Words = new[] { "form event", "formeventhandler" }, Target = "/Classes/FormEventHandler", Kinds = new[] { 7 }, Describe = "form event handlers" },
            new Recipe { Words = new[] { "delegate", "subscribes", "subscriber" }, Target = "/Classes/SubscribesTo", Kinds = new[] { 7 }, Describe = "delegate subscribers ([SubscribesTo])" },
            new Recipe { Words = new[] { "test", "unit test", "systest" }, Target = "/Classes/SysTestCase", Kinds = new[] { 4 }, Describe = "SysTest test classes" },
            new Recipe { Words = new[] { "query builder", "query run", "queryrun", "dynamic query" }, Target = "/Classes/QueryBuildDataSource", Kinds = new[] { 2 }, Describe = "code building queries (QueryBuildDataSource)" },
            new Recipe { Words = new[] { "feature", "feature management", "feature flag" }, Target = "/Classes/IFeatureMetadata", Kinds = new[] { 2 }, Describe = "feature management classes (IFeatureMetadata)" },
            new Recipe { Words = new[] { "infolog", "message", "action center" }, Target = "/Classes/Message", Kinds = new[] { 1, 2 }, Describe = "code using the Message API (action center)" },
            new Recipe { Words = new[] { "multithread", "task bundle", "batch task", "parallel" }, Target = "/Classes/SysOperationServiceBase", Kinds = new[] { 4 }, Describe = "SysOperation services (split work into batch tasks)" },
            new Recipe { Words = new[] { "data entity", "entity" }, Target = "/Classes/DataEntityViewAttribute", Kinds = new[] { 7 }, Describe = "data entities with code" },
            new Recipe { Words = new[] { "excel", "office add-in", "export to excel" }, Target = "/Classes/ExportToExcelDataEntityContext", Kinds = new[] { 2 }, Describe = "Office/Excel integration" },
            new Recipe { Words = new[] { "email", "mail" }, Target = "/Classes/SysMailerMessageBuilder", Kinds = new[] { 1, 2 }, Describe = "sending email (SysMailerMessageBuilder)" },
            new Recipe { Words = new[] { "file upload", "upload", "download", "file" }, Target = "/Classes/FileUploadTemporaryStorageResult", Kinds = new[] { 2 }, Describe = "file upload and download" },
            new Recipe { Words = new[] { "json", "rest", "http" }, Target = "/Classes/FormJsonSerializer", Kinds = new[] { 1, 2 }, Describe = "JSON serialization" },
        };

        private static readonly string[] FormPatterns =
        {
            "DetailsMaster", "DetailsTransaction", "ListPage", "SimpleList", "SimpleListDetails", "TableOfContents", "Dialog",
            "DropDialog", "Lookup", "Wizard", "Workspace", "Operational Workspace", "FormPart", "FactBox", "SimpleDetails",
            "TaskSingle", "TaskDouble", "HubPart",
        };

        public Dictionary<string, object> Find(string query, string objectType, string objectName, int max)
        {
            max = Math.Max(1, Math.Min(max <= 0 ? 15 : max, 50));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var items = new List<Example>();
            var notes = new List<string>();
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null)
                    return Failure("The cross-reference database is not available: " + problem);

                if (!string.IsNullOrEmpty(objectName))
                {
                    string folder = XRefQuery.FolderOf(objectType ?? "AxClass");
                    if (folder == null) return Failure("Unknown objectType '" + objectType + "'.");
                    string path = "/" + folder + "/" + objectName;
                    if (folder.StartsWith("MenuItem", StringComparison.OrdinalIgnoreCase))
                    {
                        // A menu item references little: compare what it opens (its form, class or report) instead.
                        string target = Column(conn, "SELECT TOP 1 t.Path FROM [References] r JOIN Names s ON s.Id = r.SourceId JOIN Names t ON t.Id = r.TargetId "
                            + "WHERE s.Path = @p AND (t.Path LIKE '/Forms/%' OR t.Path LIKE '/Classes/%' OR t.Path LIKE '/Reports/%') AND t.Path NOT LIKE '/%/%/%'",
                            ("@p", path)).FirstOrDefault() ?? MenuItemTarget(objectType ?? "AxMenuItemDisplay", objectName);
                        if (target != null)
                        {
                            notes.Add(objectName + " opens " + Short(target));
                            path = target;
                            objectName = Short(target);
                        }
                    }
                    items.AddRange(Similar(conn, path, max));
                    notes.Add("objects sharing the most distinctive references (base class, interfaces, attributes, APIs, tables) with " + objectName);
                }
                if (!string.IsNullOrWhiteSpace(query))
                {
                    string q = " " + Regex.Replace(query.ToLowerInvariant(), @"[^a-z0-9]+", " ") + " ";
                    foreach (var recipe in Recipes.Where(r => r.Words.Any(w => q.Contains(" " + w + " ") || q.Contains(" " + w.Replace(" ", "") + " "))).Take(3))
                    {
                        items.AddRange(Anchored(conn, recipe, max));
                        notes.Add(recipe.Describe);
                    }
                    string pattern = FormPatterns.FirstOrDefault(p => q.Contains(" " + p.ToLowerInvariant().Replace(" ", "") + " ")
                        || q.Contains(" " + Regex.Replace(p, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant() + " "));
                    if (pattern != null)
                    {
                        items.AddRange(FormsWithPattern(pattern.Replace(" ", ""), max));
                        notes.Add("forms with the " + pattern + " pattern");
                    }
                    if (notes.Count == 0 || (string.IsNullOrEmpty(objectName) && items.Count == 0))
                    {
                        items.AddRange(ByName(conn, q, max));
                        notes.Add("classes whose name or method names contain the most query words (no framework pattern recognized; words like batch, contract, number sequence, lookup, workflow, business event, report, service, data entity, or a form pattern name find framework examples)");
                    }
                }
            }
            // Standard application code only: not the user's own models, and not Microsoft's test packages unless tests were asked for.
            bool tests = (query ?? "").IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0;
            var custom = CustomModels();
            items = items.Where(i => i.Model == null || (!custom.Contains(i.Model) && (tests || i.Model.IndexOf("Test", StringComparison.OrdinalIgnoreCase) < 0))).ToList();
            var distinct = items.GroupBy(i => i.ObjectType + "/" + i.ObjectName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(i => i.Score).First())
                .OrderByDescending(i => i.Score).Take(max).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["interpretation"] = notes.Count == 0 ? "Give a query or an object." : string.Join("; ", notes),
                ["items"] = distinct.Select(i => new Dictionary<string, object>
                {
                    ["objectType"] = i.ObjectType, ["objectName"] = i.ObjectName, ["model"] = i.Model, ["reason"] = i.Reason, ["score"] = Math.Round(i.Score, 2),
                }).ToList(),
                ["ms"] = watch.ElapsedMilliseconds,
            };
        }

        private static Dictionary<string, object> Failure(string message) =>
            new Dictionary<string, object> { ["success"] = false, ["message"] = message };

        /// <summary>Objects of the same kind that share the most distinctive references (rare targets count most).</summary>
        private List<Example> Similar(SqlConnection conn, string objectPath, int max)
        {
            string obj = string.Format(ObjectOf, "s.Path");
            string folder = objectPath.Substring(0, objectPath.IndexOf('/', 1) + 1);
            string sql = @"
WITH tgt AS (
  SELECT DISTINCT r.TargetId FROM [References] r JOIN Names s ON s.Id = r.SourceId
  WHERE (s.Path = @p OR s.Path LIKE @pp) AND r.Kind IN (1, 2, 4, 7)),
tc AS (
  SELECT t.TargetId, (SELECT COUNT(*) FROM [References] r2 WHERE r2.TargetId = t.TargetId) AS c FROM tgt t),
uses AS (
  SELECT DISTINCT r.TargetId, " + obj + @" AS Obj, s.ModuleId FROM [References] r
  JOIN tc ON tc.TargetId = r.TargetId AND tc.c BETWEEN 2 AND 4000
  JOIN Names s ON s.Id = r.SourceId
  WHERE s.Path LIKE @folder),
freq AS (SELECT TargetId, COUNT(DISTINCT Obj) AS n FROM uses GROUP BY TargetId)
SELECT TOP (@max) u.Obj, SUM(1.0 / LOG(1.0 + f.n)) AS score, COUNT(DISTINCT u.TargetId) AS shared, MAX(m.Module) AS module
FROM uses u JOIN freq f ON f.TargetId = u.TargetId LEFT JOIN Modules m ON m.Id = u.ModuleId
WHERE u.Obj <> @p AND f.n >= 2
GROUP BY u.Obj ORDER BY score DESC";
            var found = new List<Example>();
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 })
            {
                cmd.Parameters.AddWithValue("@p", objectPath);
                cmd.Parameters.AddWithValue("@pp", Like(objectPath) + "/%");
                cmd.Parameters.AddWithValue("@folder", Like(folder) + "%");
                cmd.Parameters.AddWithValue("@max", max * 2);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        found.Add(ToExample(r.GetString(0), r.IsDBNull(3) ? null : r.GetString(3), Convert.ToDouble(r.GetValue(1)),
                            "shares " + r.GetInt32(2) + " distinctive reference(s)"));
            }
            // The shared targets with the fewest users explain the match (one query each: the top few only).
            foreach (var e in found.Take(Math.Min(max, 5)))
            {
                var shared = Column(conn, @"
SELECT TOP 4 t.Path FROM Names t
WHERE t.Id IN (SELECT r.TargetId FROM [References] r JOIN Names s ON s.Id = r.SourceId WHERE (s.Path = @a OR s.Path LIKE @aa) AND r.Kind IN (1, 2, 4, 7))
  AND t.Id IN (SELECT r.TargetId FROM [References] r JOIN Names s ON s.Id = r.SourceId WHERE (s.Path = @b OR s.Path LIKE @bb) AND r.Kind IN (1, 2, 4, 7))
ORDER BY (SELECT COUNT(*) FROM [References] r3 WHERE r3.TargetId = t.Id)",
                    ("@a", objectPath), ("@aa", Like(objectPath) + "/%"), ("@b", PathOf(e)), ("@bb", Like(PathOf(e)) + "/%"));
                if (shared.Count > 0) e.Reason += ": " + string.Join(", ", shared.Select(Short));
            }
            return found;
        }

        /// <summary>Models of the custom metadata folder (the user's own), excluded from standard examples.</summary>
        private static HashSet<string> CustomModels()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string package in DynamicsConfigurationReader.ReadActiveConfiguration().CustomPackageFolders())
                {
                    set.Add(Path.GetFileName(package));
                    foreach (string model in Directory.GetDirectories(package).Where(m => !string.Equals(Path.GetFileName(m), "Descriptor", StringComparison.OrdinalIgnoreCase)))
                        set.Add(Path.GetFileName(model));
                }
            }
            catch { }
            return set;
        }

        private static string PathOf(Example e) => "/" + XRefQuery.FolderOf(e.ObjectType) + "/" + e.ObjectName;

        /// <summary>Objects that extend, call or are attributed with a framework class; smaller ones first (easier to read).</summary>
        private List<Example> Anchored(SqlConnection conn, Recipe recipe, int max)
        {
            string obj = string.Format(ObjectOf, "s.Path");
            string sql = @"
SELECT TOP 200 " + obj + @" AS Obj, MAX(m.Module) AS module, COUNT(*) AS n
FROM [References] r JOIN Names t ON t.Id = r.TargetId JOIN Names s ON s.Id = r.SourceId LEFT JOIN Modules m ON m.Id = s.ModuleId
WHERE t.Path = @target AND r.Kind IN (" + string.Join(",", recipe.Kinds) + @")
GROUP BY " + obj;
            var candidates = new List<KeyValuePair<string, string>>();
            var custom = CustomModels();
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                cmd.Parameters.AddWithValue("@target", recipe.Target);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        string module = r.IsDBNull(1) ? null : r.GetString(1);
                        if (module != null && (custom.Contains(module) || (module.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0 && !recipe.Target.EndsWith("SysTestCase")))) continue;
                        candidates.Add(new KeyValuePair<string, string>(r.GetString(0), module));
                    }
            }
            // Size = number of methods: small, focused examples are the best to copy from.
            var sized = new List<KeyValuePair<KeyValuePair<string, string>, int>>();
            foreach (var c in candidates.Take(120))
            {
                int methods = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM Names WHERE Path LIKE @p", ("@p", Like(c.Key) + "/Methods/%")) ?? 0);
                sized.Add(new KeyValuePair<KeyValuePair<string, string>, int>(c, methods));
            }
            string verb = recipe.Kinds.Contains(4) ? "extends " : recipe.Kinds.Contains(7) ? "is attributed with " : "uses ";
            return sized.OrderBy(s => s.Value).Take(max)
                .Select((s, i) => ToExample(s.Key.Key, s.Key.Value, 100 - i, verb + Short(recipe.Target) + " (" + s.Value + " methods)"))
                .ToList();
        }

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "how", "the", "and", "for", "with", "from", "into", "that", "this", "code", "standard", "example", "examples", "using",
            "create", "make", "want", "need", "when", "what", "where", "which", "some", "does", "find", "show", "like", "similar",
        };

        // posting -> post, journals -> journal, created -> creat (still a substring of "create").
        private static string Stem(string w) =>
            w.Length > 6 && w.EndsWith("ing") ? w.Substring(0, w.Length - 3)
            : w.Length > 5 && w.EndsWith("ed") ? w.Substring(0, w.Length - 2)
            : w.Length > 4 && w.EndsWith("s") && !w.EndsWith("ss") ? w.Substring(0, w.Length - 1)
            : w;

        /// <summary>Free text: classes whose name (then method names) contain the most query words.</summary>
        private List<Example> ByName(SqlConnection conn, string q, int max)
        {
            var words = q.Split(' ').Where(w => w.Length >= 3 && !StopWords.Contains(w)).Select(Stem).Where(w => w.Length >= 3)
                .Distinct().Take(5).ToList();
            if (words.Count == 0) return new List<Example>();
            bool tests = words.Any(w => w.StartsWith("test"));
            var args = words.Select((w, i) => ("@w" + i, (object)("%" + Like(w) + "%"))).ToArray();
            string all = string.Join(" AND ", words.Select((w, i) => "Path LIKE @w" + i));
            string any = string.Join(" OR ", words.Select((w, i) => "Path LIKE @w" + i));
            const string classLevel = "Path LIKE '/Classes/%' AND Path NOT LIKE '/Classes/%/%'";
            var scored = new Dictionary<string, KeyValuePair<double, string>>(StringComparer.OrdinalIgnoreCase);
            void Add(string cls, double score, string reason)
            {
                if (!tests && cls.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0) return;
                if (!scored.TryGetValue(cls, out var best) || best.Key < score) scored[cls] = new KeyValuePair<double, string>(score, reason);
            }
            // Class names containing every word, then the most words (short names first: the framework class itself).
            foreach (string path in Column(conn, "SELECT TOP 200 Path FROM Names WHERE " + classLevel + " AND " + all + " ORDER BY LEN(Path)", args)
                .Concat(words.Count > 1 ? Column(conn, "SELECT TOP 2000 Path FROM Names WHERE " + classLevel + " AND (" + any + ") ORDER BY LEN(Path)", args) : new List<string>()))
            {
                string cls = path.Substring("/Classes/".Length);
                var matched = words.Where(w => cls.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (matched.Count > 0) Add(cls, matched.Count * 10 - cls.Length / 10.0, "name matches " + string.Join(", ", matched));
            }
            // Method names complete the picture when no class name has every word.
            if (words.Count > 1 && !scored.Values.Any(v => v.Key >= words.Count * 10 - 5))
                foreach (string path in Column(conn, "SELECT TOP 300 Path FROM Names WHERE Path LIKE '/Classes/%/Methods/%' AND " + all + " ORDER BY LEN(Path)", args))
                {
                    var parts = path.TrimStart('/').Split('/');
                    if (parts.Length != 4) continue;
                    Add(parts[1], words.Count * 8 - parts[1].Length / 10.0, "method " + parts[3] + " matches " + string.Join(", ", words));
                }
            return scored.OrderByDescending(kv => kv.Value.Key).Take(max)
                .Select(kv => ToExample("/Classes/" + kv.Key, null, kv.Value.Key / 10.0, kv.Value.Value)).ToList();
        }

        // Forms by Design pattern: a cached scan of the forms' Design element (patterns are not in the cross-reference database).
        private static Dictionary<string, List<string>> _formPatterns;
        private static readonly object FormPatternLock = new object();
        private static readonly Regex DesignPattern = new Regex(@"<Design>[\s\S]*?<Pattern(?:\s[^>]*)?>([^<]+)</Pattern>", RegexOptions.Compiled);

        private static List<Example> FormsWithPattern(string pattern, int max)
        {
            lock (FormPatternLock)
            {
                var files = CodeSearch.Folders(new[] { "AxForm" }, null).SelectMany(f => SafeFiles(f.Key).Select(x => x + "|" + f.Value)).ToList();
                string key = files.Count + "-" + CodeSearch.Folders(new[] { "AxForm" }, null)
                    .Select(f => Directory.GetLastWriteTimeUtc(f.Key).Ticks).Aggregate(17L, (h, t) => unchecked(h * 31 + t));
                string cacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode", "cache", "form-patterns.json");
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (_formPatterns == null && File.Exists(cacheFile))
                {
                    try
                    {
                        var cached = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(cacheFile));
                        if ((cached["key"] as string) == key)
                            _formPatterns = ((Dictionary<string, object>)cached["forms"]).ToDictionary(kv => kv.Key,
                                kv => ((System.Collections.ArrayList)kv.Value).Cast<string>().ToList(), StringComparer.OrdinalIgnoreCase);
                    }
                    catch { }
                }
                if (_formPatterns == null)
                {
                    var map = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
                    System.Threading.Tasks.Parallel.ForEach(files, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, entry =>
                    {
                        string file = entry.Substring(0, entry.LastIndexOf('|'));
                        string text;
                        try { text = File.ReadAllText(file); } catch { return; }
                        int design = text.IndexOf("<Design>", StringComparison.Ordinal);
                        if (design < 0) return;
                        var m = DesignPattern.Match(text, design);
                        if (!m.Success) return;
                        map.GetOrAdd(m.Groups[1].Value.Trim(), _ => new System.Collections.Concurrent.ConcurrentBag<string>())
                            .Add(Path.GetFileNameWithoutExtension(file) + "|" + entry.Substring(entry.LastIndexOf('|') + 1) + "|" + new FileInfo(file).Length);
                    });
                    _formPatterns = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                        File.WriteAllText(cacheFile, serializer.Serialize(new Dictionary<string, object> { ["key"] = key, ["forms"] = _formPatterns }));
                    }
                    catch { }
                }
            }
            if (!_formPatterns.TryGetValue(pattern, out var forms)) return new List<Example>();
            return forms.Select(f => f.Split('|')).OrderBy(f => long.Parse(f[2])).Take(max)
                .Select((f, i) => new Example { ObjectType = "AxForm", ObjectName = f[0], Model = f[1], Score = 90 - i, Reason = "form with the " + pattern + " pattern" })
                .ToList();
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*.xml"); } catch { return new string[0]; }
        }

        private static Example ToExample(string path, string model, double score, string reason)
        {
            var parts = path.TrimStart('/').Split('/');
            return new Example
            {
                ObjectType = XRefQuery.TypeOfFolder(parts[0]) ?? parts[0], ObjectName = parts.Length > 1 ? parts[1] : path,
                Model = model, Score = score, Reason = reason,
            };
        }

        private static string Short(string path)
        {
            var parts = (path ?? "").TrimStart('/').Split('/');
            return parts.Length >= 4 && parts[2] == "Methods" ? parts[1] + "." + parts[3] : parts.Length >= 2 ? parts[1] : path;
        }

        // ── Obsolete classes and methods ──

        private static readonly Regex ObsoleteAttribute = new Regex(
            @"\[\s*(?:[^\]]*,\s*)?SysObsolete(?:Attribute)?\s*\(\s*(?:(?:""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)'|(@?[\w:]+))\s*)?(?:,\s*(true|false))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<Dictionary<string, object>> _obsolete;
        private static string _obsoleteKey;

        public Dictionary<string, object> Obsolete()
        {
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null) return Failure("The cross-reference database is not available: " + problem);
                string key = Convert.ToString(Scalar(conn, "SELECT COUNT_BIG(*) FROM Names"));
                if (_obsolete == null || _obsoleteKey != key)
                {
                    string cacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode", "cache",
                        "obsolete-" + _xref.DatabaseName + ".json");
                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    try
                    {
                        if (File.Exists(cacheFile))
                        {
                            var cached = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(cacheFile));
                            if ((cached["key"] as string) == key)
                            {
                                _obsolete = ((System.Collections.ArrayList)cached["items"]).Cast<Dictionary<string, object>>().ToList();
                                _obsoleteKey = key;
                            }
                        }
                    }
                    catch { }
                    if (_obsoleteKey != key)
                    {
                        _obsolete = BuildObsolete(conn);
                        _obsoleteKey = key;
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                            File.WriteAllText(cacheFile, serializer.Serialize(new Dictionary<string, object> { ["key"] = key, ["items"] = _obsolete }));
                        }
                        catch { }
                    }
                }
                return new Dictionary<string, object> { ["success"] = true, ["items"] = _obsolete };
            }
        }

        private static List<Dictionary<string, object>> BuildObsolete(SqlConnection conn)
        {
            var paths = Column(conn, @"SELECT DISTINCT s.Path FROM [References] r JOIN Names t ON t.Id = r.TargetId JOIN Names s ON s.Id = r.SourceId
WHERE t.Path IN ('/Classes/SysObsoleteAttribute', '/Classes/SysObsolete') AND r.Kind = 7");
            var items = new List<Dictionary<string, object>>();
            foreach (var group in paths.GroupBy(p => string.Join("/", p.TrimStart('/').Split('/').Take(2))))
            {
                var parts = group.Key.Split('/');
                string objectType = XRefQuery.TypeOfFolder(parts[0]);
                if (objectType == null || parts.Length < 2) continue;
                var sources = CodeSearch.SourcesOf(objectType, parts[1]);
                foreach (string path in group)
                {
                    var p = path.TrimStart('/').Split('/');
                    string member = p.Length >= 4 && p[2] == "Methods" ? p[3] : null;
                    string source = null;
                    if (sources != null)
                        source = member == null ? sources.FirstOrDefault(s => s.Key.Last() == "classDeclaration").Value
                            : sources.FirstOrDefault(s => s.Key.Length == 2 && string.Equals(s.Key[1], member, StringComparison.OrdinalIgnoreCase)).Value;
                    var m = source == null ? null : ObsoleteAttribute.Match(source);
                    string message = m != null && m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : "";
                    items.Add(new Dictionary<string, object>
                    {
                        ["path"] = path, ["objectType"] = objectType, ["objectName"] = parts[1], ["member"] = member,
                        ["message"] = message, ["isError"] = m != null && m.Success && m.Groups[4].Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
            return items;
        }

        private static readonly Regex MenuItemObject = new Regex(@"<Object>([^<]+)</Object>", RegexOptions.Compiled);
        private static readonly Regex MenuItemObjectType = new Regex(@"<ObjectType>([^<]+)</ObjectType>", RegexOptions.Compiled);

        /// <summary>The cross-reference path of what a menu item opens (Object / ObjectType of its metadata; forms by default).</summary>
        private static string MenuItemTarget(string objectType, string name)
        {
            foreach (var folder in CodeSearch.Folders(new[] { objectType }, null))
            {
                string file = Path.Combine(folder.Key, name + ".xml");
                if (!File.Exists(file)) continue;
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }
                var obj = MenuItemObject.Match(text);
                if (!obj.Success) return null;
                string kind = MenuItemObjectType.Match(text) is var t && t.Success ? t.Groups[1].Value.Trim() : "Form";
                string targetFolder = kind == "Class" ? "Classes" : kind == "SSRSReport" ? "Reports" : kind == "Form" ? "Forms" : null;
                return targetFolder == null ? null : "/" + targetFolder + "/" + obj.Groups[1].Value.Trim();
            }
            return null;
        }

        // ── SQL helpers ──

        private static string Like(string s) => (s ?? "").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        private static List<string> Column(SqlConnection conn, string sql, params (string, object)[] args)
        {
            var list = new List<string>();
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
            }
            return list;
        }

        private static object Scalar(SqlConnection conn, string sql, params (string, object)[] args)
        {
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return cmd.ExecuteScalar();
            }
        }
    }
}
