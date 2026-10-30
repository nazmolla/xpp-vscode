using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Who extends what: Chain of Command classes and the methods they wrap, the methods extension classes add, and event
    /// handlers (pre/post, delegate subscribers, form/data events). Answers both directions from memory.
    ///
    /// Sources, merged:
    /// 1. The cross-reference database (set-based, about 6 s for the whole application, cached on disk until it changes):
    ///    an [ExtensionOf] attribute row (kind Attribute) and the type reference on the same line give the extended
    ///    element (class, table, form, form data source/control, entity, view, map); a wrapper is a method of the
    ///    extension class that calls (kind MethodCall) the method of the same name - its "next" call. Pre/post handlers and
    ///    delegate subscribers come the same way from their attribute row and the method call on the same line.
    /// 2. The classes of your own models, read from disk on every refresh, so extensions written since the last build count.
    /// 3. Form and data event handlers ([FormControlEventHandler], [DataEventHandler], ...), which the cross-reference
    ///    database does not record: one scan of the reference packages (about a minute the first time), cached on disk.
    /// </summary>
    internal sealed class ExtensionIndex
    {
        public sealed class ExtensionClass
        {
            public string Name;
            public string Target;          // element path, e.g. /Classes/SalesFormLetter or /Forms/F/DataSources/DS
            public List<string> Methods = new List<string>();
            public bool Custom;
        }

        public sealed class Wrapper
        {
            public string ExtensionClass;
            public string Method;
            public string Wrapped;         // path of the wrapped method
        }

        public sealed class Handler
        {
            public string Class;
            public string Method;
            public string Kind;            // Pre, Post, Delegate, FormControl, FormDataSource, FormDataField, Form, Data
            public string Target;          // handled element or method path
            public string Event;           // e.g. Modified, Inserted (form/data events)
        }

        public sealed class Snapshot
        {
            public string Key;
            public List<ExtensionClass> Extensions = new List<ExtensionClass>();
            public List<Wrapper> Wrappers = new List<Wrapper>();
            public List<Handler> Handlers = new List<Handler>();
        }

        private readonly XRefQuery _xref;
        private readonly object _lock = new object();
        private Snapshot _xrefPart, _eventPart, _customPart;
        private Task _building;
        private DateTime _customReadUtc;
        public string Status { get; private set; } = "not built";
        public bool Ready => _xrefPart != null;

        // Merged views (rebuilt when a part changes).
        private Dictionary<string, List<ExtensionClass>> _byTarget;
        private Dictionary<string, ExtensionClass> _byClass;
        private Dictionary<string, List<Wrapper>> _byWrapped;
        private Dictionary<string, List<Handler>> _handlersByTarget;
        private Dictionary<string, List<Handler>> _handlersByClass;

        public ExtensionIndex(XRefQuery xref) { _xref = xref; }

        private static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode", "cache");

        /// <summary>Starts building (or refreshing) in the background; returns at once.</summary>
        public void EnsureBuilt(bool scanEventHandlers, bool force)
        {
            lock (_lock)
            {
                if (_building != null && !_building.IsCompleted) return;
                if (!force && _xrefPart != null && (DateTime.UtcNow - _customReadUtc).TotalSeconds < 20
                    && (!scanEventHandlers || _eventPart != null)) return;
                _building = Task.Run(() => Build(scanEventHandlers, force));
            }
        }

        /// <summary>Waits for a running build up to the timeout.</summary>
        public bool WaitReady(int timeoutMs)
        {
            var t = _building;
            if (t != null && !t.IsCompleted) t.Wait(timeoutMs);
            return Ready;
        }

        private void Build(bool scanEventHandlers, bool force)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_xrefPart == null || force)
                {
                    Status = "reading cross references";
                    _xrefPart = LoadXRefPart(force);
                }
                Status = "reading your models";
                _customPart = ScanCustomModels();
                _customReadUtc = DateTime.UtcNow;
                Merge();
                if (scanEventHandlers && (_eventPart == null || force))
                {
                    Status = "scanning form/data event handlers";
                    _eventPart = LoadEventHandlerPart(force);
                    Merge();
                }
                Status = "ready (" + _byClass.Count + " extension classes, " + _byWrapped.Values.Sum(l => l.Count) + " wrapped methods, "
                    + _handlersByClass.Values.Sum(l => l.Count) + " handlers" + (_eventPart == null ? "; form/data event handlers off" : "") + ")";
                McpLogger.Log("Extension index " + Status + " in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                Status = "failed: " + ex.Message;
                McpLogger.Log("Extension index failed: " + ex);
                if (_xrefPart == null) _xrefPart = new Snapshot();
                Merge();
            }
        }

        // ── Queries ──

        public List<ExtensionClass> ExtensionsOf(string path, bool includeChildren)
        {
            var result = new List<ExtensionClass>();
            var map = _byTarget;
            if (map == null) return result;
            if (map.TryGetValue(path, out var exact)) result.AddRange(exact);
            if (includeChildren)
                foreach (var kv in map.Where(kv => kv.Key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                    result.AddRange(kv.Value);
            return result;
        }

        public List<Wrapper> WrappersOf(string methodPath) =>
            _byWrapped != null && _byWrapped.TryGetValue(methodPath, out var list) ? list : new List<Wrapper>();

        public ExtensionClass ExtensionClassInfo(string className) =>
            _byClass != null && _byClass.TryGetValue(className, out var e) ? e : null;

        public List<Wrapper> WrappersIn(string className) =>
            _byWrapped == null ? new List<Wrapper>()
                : _byWrapped.Values.SelectMany(l => l).Where(w => w.ExtensionClass.Equals(className, StringComparison.OrdinalIgnoreCase)).ToList();

        public List<Handler> HandlersOf(string path, bool includeChildren)
        {
            var result = new List<Handler>();
            var map = _handlersByTarget;
            if (map == null) return result;
            if (map.TryGetValue(path, out var exact)) result.AddRange(exact);
            if (includeChildren)
                foreach (var kv in map.Where(kv => kv.Key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                    result.AddRange(kv.Value);
            return result;
        }

        public List<Handler> HandlersIn(string className) =>
            _handlersByClass != null && _handlersByClass.TryGetValue(className, out var list) ? list : new List<Handler>();

        // ── Merge ──

        private void Merge()
        {
            var parts = new[] { _xrefPart, _eventPart, _customPart }.Where(p => p != null).ToList();
            var custom = _customPart;
            var customClasses = new HashSet<string>(
                (custom?.Extensions.Select(e => e.Name) ?? Enumerable.Empty<string>())
                    .Concat(custom?.Handlers.Select(h => h.Class) ?? Enumerable.Empty<string>()), StringComparer.OrdinalIgnoreCase);
            if (custom != null)
                foreach (string scanned in _scannedCustomClasses) customClasses.Add(scanned);
            bool Keep(Snapshot part, string cls) => part == custom || !customClasses.Contains(cls);

            var byClass = new Dictionary<string, ExtensionClass>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
                foreach (var e in part.Extensions.Where(x => Keep(part, x.Name)))
                {
                    // Several rows per class (e.g. the form and its data source): keep the most specific target.
                    if (!byClass.TryGetValue(e.Name, out var existing) || e.Target.Length > existing.Target.Length) byClass[e.Name] = e;
                }
            var byTarget = new Dictionary<string, List<ExtensionClass>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in byClass.Values) Add(byTarget, e.Target, e);

            var byWrapped = new Dictionary<string, List<Wrapper>>(StringComparer.OrdinalIgnoreCase);
            var wrapperSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
                foreach (var w in part.Wrappers.Where(x => Keep(part, x.ExtensionClass)))
                    if (wrapperSeen.Add(w.ExtensionClass + "|" + w.Method)) Add(byWrapped, w.Wrapped, w);

            var handlersByTarget = new Dictionary<string, List<Handler>>(StringComparer.OrdinalIgnoreCase);
            var handlersByClass = new Dictionary<string, List<Handler>>(StringComparer.OrdinalIgnoreCase);
            var handlerSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
                foreach (var h in part.Handlers.Where(x => Keep(part, x.Class)))
                    if (handlerSeen.Add(h.Class + "|" + h.Method + "|" + h.Target + "|" + h.Event))
                    {
                        Add(handlersByTarget, h.Target, h);
                        Add(handlersByClass, h.Class, h);
                    }

            lock (_lock)
            {
                _byClass = byClass; _byTarget = byTarget; _byWrapped = byWrapped;
                _handlersByTarget = handlersByTarget; _handlersByClass = handlersByClass;
            }
        }

        private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
            list.Add(value);
        }

        // ── Part 1: cross-reference database ──

        private const string ExtensionRowsSql = @"
SELECT s.Path ext, t.Path base
FROM [References] a JOIN Names s ON s.Id = a.SourceId
JOIN [References] b ON b.SourceId = a.SourceId AND b.Line = a.Line AND b.Kind = 2
JOIN Names t ON t.Id = b.TargetId
WHERE a.TargetId = (SELECT Id FROM Names WHERE Path = '/Classes/ExtensionOf') AND a.Kind = 7
  AND t.Path <> '/Classes/ExtensionOf' AND s.Path NOT LIKE '/Classes/%/%'";

        private const string ExtensionMethodsSql = @"
WITH ext AS (
  SELECT DISTINCT s.Path ext
  FROM [References] a JOIN Names s ON s.Id = a.SourceId
  WHERE a.TargetId = (SELECT Id FROM Names WHERE Path = '/Classes/ExtensionOf') AND a.Kind = 7 AND s.Path NOT LIKE '/Classes/%/%')
SELECT e.ext, m.Path method,
  (SELECT TOP 1 tm.Path FROM [References] r JOIN Names tm ON tm.Id = r.TargetId
   WHERE r.SourceId = m.Id AND r.Kind = 1
     AND tm.Path LIKE '%/Methods/' + RIGHT(m.Path, CHARINDEX('/', REVERSE(m.Path)) - 1)
     AND tm.Path NOT LIKE e.ext + '/%') wrapped
FROM ext e JOIN Names m ON m.Path LIKE e.ext + '/Methods/%'";

        private const string HandlerRowsSql = @"
SELECT s.Path handler, at.Path attr, t.Path target
FROM Names at JOIN [References] a ON a.TargetId = at.Id AND a.Kind = 7
JOIN Names s ON s.Id = a.SourceId
JOIN [References] b ON b.SourceId = a.SourceId AND b.Line = a.Line AND b.Kind = 1
JOIN Names t ON t.Id = b.TargetId
WHERE at.Path IN ('/Classes/PostHandlerFor', '/Classes/PreHandlerFor', '/Classes/SubscribesTo')";

        private Snapshot LoadXRefPart(bool force)
        {
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null) throw new InvalidOperationException("cross-reference database unavailable: " + problem);
                string key = Scalar(conn, "SELECT CAST((SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID('References') AND p.index_id IN (0,1)) AS varchar(20)) + '-' + CAST((SELECT MAX(Id) FROM Names) AS varchar(20))");
                string file = Path.Combine(CacheFolder, "extensions-" + Safe(_xref.DatabaseName) + ".json");
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (!force && File.Exists(file))
                {
                    try
                    {
                        var cached = serializer.Deserialize<Snapshot>(File.ReadAllText(file));
                        if (cached?.Key == key) return cached;
                    }
                    catch { }
                }

                var snap = new Snapshot { Key = key };
                var byName = new Dictionary<string, ExtensionClass>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in Rows(conn, ExtensionRowsSql))
                {
                    string name = row[0].Substring("/Classes/".Length);
                    if (!byName.TryGetValue(name, out var e) || row[1].Length > e.Target.Length)
                        byName[name] = new ExtensionClass { Name = name, Target = row[1] };
                }
                foreach (var row in Rows(conn, ExtensionMethodsSql))
                {
                    string name = row[0].Substring("/Classes/".Length);
                    if (!byName.TryGetValue(name, out var e)) continue;
                    string method = row[1].Substring(row[1].LastIndexOf('/') + 1);
                    e.Methods.Add(method);
                    if (!string.IsNullOrEmpty(row[2])) snap.Wrappers.Add(new Wrapper { ExtensionClass = name, Method = method, Wrapped = row[2] });
                }
                snap.Extensions = byName.Values.ToList();
                foreach (var row in Rows(conn, HandlerRowsSql))
                {
                    var parts = row[0].TrimStart('/').Split('/');
                    if (parts.Length < 4 || parts[0] != "Classes" || parts[2] != "Methods") continue;
                    snap.Handlers.Add(new Handler
                    {
                        Class = parts[1], Method = parts[3], Target = row[2],
                        Kind = row[1].EndsWith("PostHandlerFor") ? "Post" : row[1].EndsWith("PreHandlerFor") ? "Pre" : "Delegate",
                    });
                }
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(file, serializer.Serialize(snap));
                return snap;
            }
        }

        private static string Scalar(SqlConnection conn, string sql)
        {
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 })
                return Convert.ToString(cmd.ExecuteScalar());
        }

        private static IEnumerable<string[]> Rows(SqlConnection conn, string sql)
        {
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 })
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    var row = new string[reader.FieldCount];
                    for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
                    yield return row;
                }
        }

        private static string Safe(string s) => Regex.Replace(s ?? "default", @"[^\w.-]", "_");

        // ── Parts 2 and 3: source scans ──

        private static readonly Regex MethodXml = new Regex(@"<Method>\s*<Name>(\w+)</Name>\s*<Source><!\[CDATA\[([\s\S]*?)\]\]></Source>", RegexOptions.Compiled);
        private static readonly Regex DeclarationXml = new Regex(@"<Declaration><!\[CDATA\[([\s\S]*?)\]\]></Declaration>", RegexOptions.Compiled);
        private static readonly Regex ExtensionOfAttr = new Regex(@"\[\s*ExtensionOf\s*\(([^\]]*)\)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HandlerAttr = new Regex(
            @"\[\s*(PostHandlerFor|PreHandlerFor|SubscribesTo|FormControlEventHandler|FormDataSourceEventHandler|FormDataFieldEventHandler|FormEventHandler|DataEventHandler)\s*\(([^\]]*)\)\s*[,\]]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IntrinsicArg = new Regex(@"(\w+)Str\s*\(([^()]*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EventArg = new Regex(@"\w+EventType\s*::\s*(\w+)", RegexOptions.Compiled);

        private readonly HashSet<string> _scannedCustomClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Snapshot ScanCustomModels()
        {
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            var snap = new Snapshot();
            _scannedCustomClasses.Clear();
            foreach (string file in PackageClassFolders(config.CustomPackageFolders()).SelectMany(d => Directory.EnumerateFiles(d, "*.xml")))
            {
                _scannedCustomClasses.Add(Path.GetFileNameWithoutExtension(file));
                ScanClassFile(file, File.ReadAllText(file), snap, includeXRefKinds: true);
            }
            return snap;
        }

        private Snapshot LoadEventHandlerPart(bool force)
        {
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            var roots = config.ReferenceMetadataFolders.Where(Directory.Exists).ToList();
            // The user's packages are scanned by ScanCustomModels; on a shared store they sit among the reference packages.
            var dirs = PackageClassFolders(roots.SelectMany(Directory.GetDirectories).Where(p => !config.IsCustomPath(p))).ToList();
            string key = dirs.Count + "-" + string.Join(";", dirs.Select(d => Directory.GetLastWriteTimeUtc(d).Ticks)).GetHashCode();
            string file = Path.Combine(CacheFolder, "event-handlers-" + Safe(string.Join("_", roots).GetHashCode().ToString()) + ".json");
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (!force && File.Exists(file))
            {
                try
                {
                    var cached = serializer.Deserialize<Snapshot>(File.ReadAllText(file));
                    if (cached?.Key == key) return cached;
                }
                catch { }
            }
            var snap = new Snapshot { Key = key };
            var found = new System.Collections.Concurrent.ConcurrentBag<Handler>();
            Parallel.ForEach(dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.xml")), new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
            {
                string text;
                try { text = File.ReadAllText(path); } catch { return; }
                if (text.IndexOf("EventHandler(", StringComparison.Ordinal) < 0) return;
                var local = new Snapshot();
                ScanClassFile(path, text, local, includeXRefKinds: false);
                foreach (var h in local.Handlers) found.Add(h);
            });
            snap.Handlers = found.ToList();
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(file, serializer.Serialize(snap));
            return snap;
        }

        private static IEnumerable<string> PackageClassFolders(IEnumerable<string> packages)
        {
            foreach (string package in packages)
                foreach (string model in Directory.GetDirectories(package))
                {
                    string dir = Path.Combine(model, "AxClass");
                    if (Directory.Exists(dir)) yield return dir;
                }
        }

        /// <summary>Extension declarations, wrappers and handler attributes of one class file.</summary>
        private static void ScanClassFile(string path, string xml, Snapshot snap, bool includeXRefKinds)
        {
            string className = Path.GetFileNameWithoutExtension(path);
            var methods = MethodXml.Matches(xml).Cast<Match>().Select(m => new { Name = m.Groups[1].Value, Source = m.Groups[2].Value }).ToList();
            if (includeXRefKinds)
            {
                string declaration = DeclarationXml.Match(xml).Groups[1].Value;
                var ext = ExtensionOfAttr.Match(declaration);
                if (ext.Success)
                {
                    string target = TargetPath(ext.Groups[1].Value);
                    if (target != null)
                    {
                        var e = new ExtensionClass { Name = className, Target = target, Custom = true, Methods = methods.Select(m => m.Name).ToList() };
                        snap.Extensions.Add(e);
                        // A wrapper calls next on its own name; the wrapped method belongs to the extended element.
                        string owner = target;
                        foreach (var m in methods)
                            if (Regex.IsMatch(m.Source, @"\bnext\s+" + Regex.Escape(m.Name) + @"\s*\("))
                                snap.Wrappers.Add(new Wrapper { ExtensionClass = className, Method = m.Name, Wrapped = owner + "/Methods/" + m.Name });
                    }
                }
            }
            foreach (var m in methods)
                foreach (Match attr in HandlerAttr.Matches(m.Source))
                {
                    string kind = attr.Groups[1].Value;
                    bool xrefKind = kind.EndsWith("HandlerFor", StringComparison.OrdinalIgnoreCase) || kind.Equals("SubscribesTo", StringComparison.OrdinalIgnoreCase);
                    if (!includeXRefKinds && xrefKind) continue;   // the reference scan adds only what XRef does not record
                    string target = TargetPath(attr.Groups[2].Value);
                    if (target == null) continue;
                    snap.Handlers.Add(new Handler
                    {
                        Class = className, Method = m.Name, Target = target,
                        Kind = kind.StartsWith("Post", StringComparison.OrdinalIgnoreCase) ? "Post"
                            : kind.StartsWith("Pre", StringComparison.OrdinalIgnoreCase) ? "Pre"
                            : kind.Equals("SubscribesTo", StringComparison.OrdinalIgnoreCase) ? "Delegate"
                            : kind.Replace("EventHandler", ""),
                        Event = EventArg.Match(attr.Groups[2].Value).Groups[1].Value,
                    });
                }
        }

        /// <summary>Element path from intrinsic function arguments, e.g. formDataSourceStr(F, DS) -> /Forms/F/DataSources/DS.</summary>
        internal static string TargetPath(string args)
        {
            string best = null;
            foreach (Match m in IntrinsicArg.Matches(args))
            {
                var a = m.Groups[2].Value.Split(',').Select(x => x.Trim()).ToArray();
                string p = null;
                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "class": p = "/Classes/" + a[0]; break;
                    case "table": p = "/Tables/" + a[0]; break;
                    case "form": p = "/Forms/" + a[0]; break;
                    case "view": p = "/Views/" + a[0]; break;
                    case "dataentityview": p = "/DataEntityViews/" + a[0]; break;
                    case "map": p = "/Maps/" + a[0]; break;
                    case "query": p = "/Queries/" + a[0]; break;
                    case "method": case "staticmethod": case "delegate": if (a.Length > 1) p = "/Classes/" + a[0] + "/Methods/" + a[1]; break;
                    case "tablemethod": case "tablestaticmethod": if (a.Length > 1) p = "/Tables/" + a[0] + "/Methods/" + a[1]; break;
                    case "formmethod": if (a.Length > 1) p = "/Forms/" + a[0] + "/Methods/" + a[1]; break;
                    case "formdatasource": if (a.Length > 1) p = "/Forms/" + a[0] + "/DataSources/" + a[1]; break;
                    case "formcontrol": if (a.Length > 1) p = "/Forms/" + a[0] + "/Controls/" + a[1]; break;
                    case "formdatafield": if (a.Length > 2) p = "/Forms/" + a[0] + "/DataSources/" + a[1] + "/Fields/" + a[2]; break;
                }
                if (p != null && (best == null || p.Length > best.Length)) best = p;
            }
            return best;
        }
    }
}
