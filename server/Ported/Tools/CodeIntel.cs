using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Framework.Xlnt.XppParser;
using Microsoft.Dynamics.AX.Metadata.XppCompiler;
using XppVsCode.Server.Host;
using XppVsCode.Server.MetaModel;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Language features for the VS Code extension (JSON-RPC method xpp/lang): implementations (overrides, Chain of
    /// Command, event handlers, subclasses, metadata extensions), call hierarchy, code lens counts, workspace symbols,
    /// type members for completion, label search and syntax checks with the compiler's own parser.
    /// Runs in a server process of its own, so slow requests never block the designers or the agent tools.
    /// </summary>
    internal sealed class CodeIntel
    {
        private readonly XRefQuery _xref;
        private readonly BridgeClient _bridge;
        private readonly ExtensionIndex _index;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly Dictionary<string, KeyValuePair<DateTime, Dictionary<string, object>>> _membersCache =
            new Dictionary<string, KeyValuePair<DateTime, Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KeyValuePair<DateTime, Dictionary<string, LabelEntry>>> _labelCache =
            new Dictionary<string, KeyValuePair<DateTime, Dictionary<string, LabelEntry>>>(StringComparer.OrdinalIgnoreCase);

        private const int KindClassExtended = 4, KindOverride = 10;

        // Metadata extensions are named "<Type>Extension/<Base>.<Suffix>" in the cross-reference database (no leading slash).
        private static readonly string[] MetadataExtensionKinds =
        {
            "TableExtension", "FormExtension", "EnumExtension", "EdtExtension", "DataEntityViewExtension", "ViewExtension",
            "MenuExtension", "MenuItemDisplayExtension", "MenuItemActionExtension", "MenuItemOutputExtension",
            "SecurityRoleExtension", "SecurityDutyExtension", "QuerySimpleExtension",
        };

        public CodeIntel(XRefQuery xref, BridgeClient bridge)
        {
            _xref = xref;
            _bridge = bridge;
            _index = new ExtensionIndex(xref);
            _examples = new CodeExamples(xref);
            _messages = new MessageTrace(xref);
        }

        private readonly MessageTrace _messages;

        private readonly CodeExamples _examples;
        private readonly CodeSearch _search = new CodeSearch();
        private readonly SecurityAnalysis _security = new SecurityAnalysis();

        public string Handle(string action, string body)
        {
            try
            {
                switch (action)
                {
                    case "status": return Status(body);
                    case "refresh_index": return RefreshIndex(body);
                    case "implementations": return Implementations(body);
                    case "extension_info": return ExtensionInfo(body);
                    case "calls": return Calls(body);
                    case "subtypes": return Subtypes(body);
                    case "code_lens": return CodeLens(body);
                    case "symbols": return Symbols(body);
                    case "members": return Members(body);
                    case "syntax": return Syntax(body);
                    case "labels": return Labels(body);
                    case "event_handlers": return EventHandlers(body);
                    case "search_code": return SearchCode(body);
                    case "find_examples": return _json.Serialize(_examples.Find(Str(body, "query"), Str(body, "objectType"), Str(body, "objectName"), Int(body, "maxResults", 15)));
                    case "obsolete": return _json.Serialize(_examples.Obsolete());
                    case "security": return _json.Serialize(_security.Handle(Str(body, "op"), k => Str(body, k),
                        k => JsonHelpers.ExtractJsonStringArray(body, k), k => Flag(body, k)));
                    case "message_source": return _json.Serialize(_messages.Trace(Str(body, "message"), Str(body, "language"),
                        Int(body, "maxLabels", 5), Int(body, "maxResults", 30)));
                    default: return Fail("Unknown language action '" + action + "'.");
                }
            }
            catch (Exception ex)
            {
                McpLogger.Log("xpp/lang " + action + " failed: " + ex);
                return Fail(ex.Message);
            }
        }

        private static int Int(string body, string key, int fallback) =>
            int.TryParse(JsonHelpers.ExtractJsonValueToken(body, key) ?? Str(body, key), out int v) ? v : fallback;

        private string SearchCode(string body)
        {
            var result = _search.Search(Str(body, "pattern"), Flag(body, "regex"), Flag(body, "caseSensitive"),
                JsonHelpers.ExtractJsonStringArray(body, "objectTypes"), Str(body, "model"), Int(body, "maxResults", 200));
            if (result.Error != null && result.Hits.Count == 0) return Fail(result.Error);
            return Ok(new Dictionary<string, object>
            {
                ["items"] = result.Hits.Select(h => new Dictionary<string, object>
                {
                    ["objectType"] = h.ObjectType, ["objectName"] = h.ObjectName, ["model"] = h.Model, ["memberPath"] = h.MemberPath,
                    ["member"] = h.Member, ["line"] = h.Line, ["column"] = h.Column, ["text"] = h.Text,
                }).ToList(),
                ["truncated"] = result.Truncated, ["scanned"] = result.Scanned, ["ms"] = result.Ms, ["message"] = result.Error,
            });
        }

        private string Ok(Dictionary<string, object> data)
        {
            data["success"] = true;
            return _json.Serialize(data);
        }

        private string Fail(string message) => _json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = message });

        private static string Str(string body, string key) => JsonHelpers.ExtractJsonString(body, key);
        private static bool Flag(string body, string key) => "true".Equals(JsonHelpers.ExtractJsonValueToken(body, key) ?? Str(body, key), StringComparison.OrdinalIgnoreCase);

        private bool ScanEvents(string body) => !"false".Equals(JsonHelpers.ExtractJsonValueToken(body, "scanEventHandlers") ?? "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Element path of the request: /Folder/Name[/memberPath...] or /Folder/Name/Methods/member.</summary>
        private static string PathOf(string body, bool includeMember = true)
        {
            string folder = XRefQuery.FolderOf(Str(body, "objectType"));
            string name = Str(body, "objectName");
            if (folder == null || string.IsNullOrEmpty(name)) return null;
            string path = "/" + folder + "/" + name;
            if (!includeMember) return path;
            var memberPath = JsonHelpers.ExtractJsonStringArray(body, "memberPath");
            if (memberPath != null && memberPath.Length > 0) return path + "/" + string.Join("/", memberPath);
            string member = Str(body, "member");
            return string.IsNullOrEmpty(member) ? path : path + "/Methods/" + member;
        }

        private static Dictionary<string, object> Element(string path, string kind, string detail)
        {
            var parts = (path ?? "").TrimStart('/').Split('/');
            var d = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["path"] = path,
                ["objectType"] = parts.Length > 0 ? XRefQuery.TypeOfFolder(parts[0]) : null,
                ["objectName"] = parts.Length > 1 ? parts[1] : null,
                ["memberPath"] = parts.Skip(2).ToArray(),
                ["member"] = parts.Length > 3 && parts[parts.Length - 2] == "Methods" ? parts[parts.Length - 1] : null,
            };
            if (detail != null) d["detail"] = detail;
            return d;
        }

        // ── Index ──

        private string Status(string body)
        {
            _index.EnsureBuilt(ScanEvents(body), false);
            return Ok(new Dictionary<string, object> { ["ready"] = _index.Ready, ["status"] = _index.Status });
        }

        private string RefreshIndex(string body)
        {
            _index.EnsureBuilt(ScanEvents(body), Flag(body, "force"));
            return Ok(new Dictionary<string, object> { ["ready"] = _index.Ready, ["status"] = _index.Status });
        }

        private bool IndexReady(string body)
        {
            _index.EnsureBuilt(ScanEvents(body), false);
            return _index.WaitReady(15000);
        }

        // ── Implementations ──

        private string Implementations(string body)
        {
            string path = PathOf(body);
            if (path == null) return Fail("objectType and objectName are required.");
            string objectPath = PathOf(body, includeMember: false);
            bool isObject = path == objectPath;
            var items = new List<Dictionary<string, object>>();
            bool ready = IndexReady(body);

            if (ready)
            {
                if (isObject)
                {
                    foreach (var e in _index.ExtensionsOf(path, includeChildren: true))
                        items.Add(Element("/Classes/" + e.Name, "Chain of Command class",
                            "extends " + Short(e.Target) + (e.Custom ? " (your model, from source)" : "")));
                    foreach (var h in _index.HandlersOf(path, includeChildren: true).Where(h => !h.Target.Contains("/Methods/")))
                        items.Add(Element("/Classes/" + h.Class + "/Methods/" + h.Method, "Event handler", HandlerDetail(h)));
                }
                else
                {
                    foreach (var w in _index.WrappersOf(path))
                        items.Add(Element("/Classes/" + w.ExtensionClass + "/Methods/" + w.Method, "Chain of Command", "wraps " + Short(w.Wrapped)));
                    foreach (var h in _index.HandlersOf(path, includeChildren: false))
                        items.Add(Element("/Classes/" + h.Class + "/Methods/" + h.Method, "Event handler", HandlerDetail(h)));
                }
            }

            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn != null)
                {
                    foreach (string src in Sources(conn, path, isObject ? KindClassExtended : KindOverride, 500))
                        items.Add(Element(src, isObject ? "Subclass" : "Override", null));
                    if (isObject)
                    {
                        string name = Str(body, "objectName");
                        foreach (string kind in MetadataExtensionKinds)
                            foreach (string ext in Column(conn, "SELECT TOP 50 Path FROM Names WHERE Path LIKE @p AND Path NOT LIKE @nested",
                                         ("@p", kind + "/" + Like(name) + ".%"), ("@nested", kind + "/" + Like(name) + ".%/%")))
                                items.Add(new Dictionary<string, object>
                                {
                                    ["kind"] = "Metadata extension", ["path"] = ext,
                                    ["objectType"] = "Ax" + kind, ["objectName"] = ext.Substring(kind.Length + 1),
                                    ["memberPath"] = new string[0], ["member"] = null,
                                });
                    }
                }
            }
            return Ok(new Dictionary<string, object> { ["items"] = items, ["indexReady"] = ready, ["indexStatus"] = _index.Status });
        }

        /// <summary>
        /// Handlers of an element's events (designer Events node): path is the handled element, e.g. /Tables/CustTable,
        /// /Forms/F/DataSources/DS, /Forms/F/DataSources/DS/Fields/F, /Forms/F/Controls/C or /Classes/C/Methods/m;
        /// event narrows to one event (Inserted, Clicked, ...) or Pre / Post / Delegate.
        /// </summary>
        private string EventHandlers(string body)
        {
            string path = Str(body, "path");
            if (string.IsNullOrEmpty(path)) return Fail("path is required.");
            string ev = Str(body, "event");
            bool ready = IndexReady(body);
            var items = new List<Dictionary<string, object>>();
            if (ready)
                foreach (var h in _index.HandlersOf(path, Flag(body, "includeChildren")))
                {
                    if (!string.IsNullOrEmpty(ev) && !ev.Equals(h.Event, StringComparison.OrdinalIgnoreCase) && !ev.Equals(h.Kind, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var item = Element("/Classes/" + h.Class + "/Methods/" + h.Method, "Event handler", HandlerDetail(h));
                    item["event"] = string.IsNullOrEmpty(h.Event) ? h.Kind : h.Event;
                    item["target"] = h.Target;
                    items.Add(item);
                }
            return Ok(new Dictionary<string, object> { ["items"] = items, ["indexReady"] = ready, ["indexStatus"] = _index.Status });
        }

        private static string HandlerDetail(ExtensionIndex.Handler h) =>
            h.Kind + (string.IsNullOrEmpty(h.Event) ? "" : " " + h.Event) + " handler of " + Short(h.Target);

        private static string Short(string path)
        {
            var parts = (path ?? "").TrimStart('/').Split('/');
            if (parts.Length < 2) return path;
            var rest = parts.Skip(2).Where(p => p != "Methods" && p != "DataSources" && p != "Controls" && p != "Fields");
            return string.Join(".", new[] { parts[1] }.Concat(rest));
        }

        private string ExtensionInfo(string body)
        {
            string cls = Str(body, "className");
            if (string.IsNullOrEmpty(cls)) return Fail("className is required.");
            bool ready = IndexReady(body);
            var e = ready ? _index.ExtensionClassInfo(cls) : null;
            var wrappers = ready ? _index.WrappersIn(cls) : new List<ExtensionIndex.Wrapper>();
            var handlers = ready ? _index.HandlersIn(cls) : new List<ExtensionIndex.Handler>();
            return Ok(new Dictionary<string, object>
            {
                ["indexReady"] = ready,
                ["target"] = e == null ? null : Element(e.Target, "Extended element", null),
                ["wrappers"] = wrappers.Select(w => new Dictionary<string, object> { ["method"] = w.Method, ["wrapped"] = Element(w.Wrapped, "Wrapped method", null) }).ToList(),
                ["handlers"] = handlers.Select(h => new Dictionary<string, object> { ["method"] = h.Method, ["target"] = Element(h.Target, "Handled", HandlerDetail(h)) }).ToList(),
            });
        }

        // ── Calls and hierarchy ──

        private string Calls(string body)
        {
            string path = PathOf(body);
            if (path == null) return Fail("objectType and objectName are required.");
            bool outgoing = "out".Equals(Str(body, "direction"), StringComparison.OrdinalIgnoreCase);
            int max = int.TryParse(Str(body, "max"), out int m) ? Math.Max(1, Math.Min(m, 1000)) : 300;
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null) return Fail(problem);
                string sql = outgoing
                    ? "SELECT TOP (@max) t.Path, r.Line, r.[Column] FROM Names s JOIN [References] r ON r.SourceId = s.Id JOIN Names t ON t.Id = r.TargetId WHERE s.Path = @p AND r.Kind IN (1,5,8) ORDER BY r.Line, r.[Column]"
                    : "SELECT TOP (@max) s.Path, r.Line, r.[Column] FROM Names t JOIN [References] r ON r.TargetId = t.Id JOIN Names s ON s.Id = r.SourceId WHERE t.Path = @p AND r.Kind IN (1,5,8) ORDER BY s.Path, r.Line";
                var calls = new List<Dictionary<string, object>>();
                using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
                {
                    cmd.Parameters.AddWithValue("@p", path);
                    cmd.Parameters.AddWithValue("@max", max);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var item = Element(r.GetString(0), outgoing ? "Callee" : "Caller", null);
                            item["line"] = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
                            item["column"] = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
                            calls.Add(item);
                        }
                }
                return Ok(new Dictionary<string, object> { ["calls"] = calls });
            }
        }

        private string Subtypes(string body)
        {
            string path = PathOf(body, includeMember: false);
            if (path == null) return Fail("objectType and objectName are required.");
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null) return Fail(problem);
                return Ok(new Dictionary<string, object> { ["subtypes"] = Sources(conn, path, KindClassExtended, 500).Select(s => Element(s, "Subclass", null)).ToList() });
            }
        }

        // ── Code lens ──

        private string CodeLens(string body)
        {
            string objectPath = PathOf(body, includeMember: false);
            if (objectPath == null) return Fail("objectType and objectName are required.");
            string name = Str(body, "objectName");
            bool ready = IndexReady(body);
            var methods = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> M(string method)
            {
                if (!methods.TryGetValue(method, out var d))
                    methods[method] = d = new Dictionary<string, object> { ["references"] = 0, ["overrides"] = 0, ["chainOfCommand"] = 0, ["handlers"] = 0 };
                return d;
            }

            int subclasses = 0;
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn != null)
                {
                    using (var cmd = new SqlCommand(
                        "SELECT t.Path, SUM(CASE WHEN r.Kind IN (1,5,8) THEN 1 ELSE 0 END), SUM(CASE WHEN r.Kind = 10 THEN 1 ELSE 0 END) "
                        + "FROM Names t JOIN [References] r ON r.TargetId = t.Id WHERE t.Path LIKE @p GROUP BY t.Path", conn) { CommandTimeout = 60 })
                    {
                        cmd.Parameters.AddWithValue("@p", Like(objectPath) + "/Methods/%");
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                string method = r.GetString(0).Substring(r.GetString(0).LastIndexOf('/') + 1);
                                M(method)["references"] = Convert.ToInt32(r.GetValue(1));
                                M(method)["overrides"] = Convert.ToInt32(r.GetValue(2));
                            }
                    }
                    subclasses = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM Names t JOIN [References] r ON r.TargetId = t.Id AND r.Kind = 4 WHERE t.Path = @p", ("@p", objectPath)));
                }
            }

            var wraps = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var handles = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int extensionClasses = 0, objectHandlers = 0;
            object extends = null;
            if (ready)
            {
                foreach (var e in _index.ExtensionsOf(objectPath, includeChildren: false)) extensionClasses++;
                foreach (var h in _index.HandlersOf(objectPath, includeChildren: true).Where(h => !h.Target.Contains("/Methods/"))) objectHandlers++;
                foreach (var w in _index.ExtensionsOf(objectPath, includeChildren: false).SelectMany(e => _index.WrappersIn(e.Name)))
                    M(w.Wrapped.Substring(w.Wrapped.LastIndexOf('/') + 1));
                foreach (var kv in methods)
                {
                    kv.Value["chainOfCommand"] = _index.WrappersOf(objectPath + "/Methods/" + kv.Key).Count;
                    kv.Value["handlers"] = _index.HandlersOf(objectPath + "/Methods/" + kv.Key, false).Count;
                }
                // The other way round: this class is an extension or a handler class.
                if (objectPath.StartsWith("/Classes/"))
                {
                    var info = _index.ExtensionClassInfo(name);
                    if (info != null) extends = Element(info.Target, "Extended element", null);
                    foreach (var w in _index.WrappersIn(name)) wraps[w.Method] = Element(w.Wrapped, "Wrapped method", null);
                    foreach (var h in _index.HandlersIn(name)) handles[h.Method] = Element(h.Target, "Handled", HandlerDetail(h));
                }
            }
            return Ok(new Dictionary<string, object>
            {
                ["indexReady"] = ready,
                ["methods"] = methods,
                ["subclasses"] = subclasses,
                ["extensionClasses"] = extensionClasses,
                ["objectHandlers"] = objectHandlers,
                ["extends"] = extends,
                ["wraps"] = wraps,
                ["handles"] = handles,
            });
        }

        // ── Workspace symbols ──

        private static readonly string[] SymbolFolders = { "Classes", "Tables", "Forms", "Enums", "Edts", "Views", "DataEntityViews", "Maps", "Queries", "MenuItemDisplays", "MenuItemActions", "MenuItemOutputs" };

        private string Symbols(string body)
        {
            string query = (Str(body, "query") ?? "").Trim();
            if (query.Length < 2) return Ok(new Dictionary<string, object> { ["symbols"] = new List<object>() });
            int max = int.TryParse(Str(body, "max"), out int m) ? Math.Max(1, Math.Min(m, 200)) : 100;
            var symbols = new List<Dictionary<string, object>>();
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null) return Fail(problem);
                // Prefix seeks on the Path index; "Class.method" searches methods of matching objects.
                string objectPart = query, memberPart = null;
                int dot = query.IndexOfAny(new[] { '.', ':' });
                if (dot > 0) { objectPart = query.Substring(0, dot); memberPart = query.Substring(dot).TrimStart('.', ':'); }
                foreach (string folder in SymbolFolders)
                {
                    if (symbols.Count >= max) break;
                    string pattern = "/" + folder + "/" + Like(objectPart) + "%";
                    string sql = memberPart == null
                        ? "SELECT TOP (@max) Path FROM Names WHERE Path LIKE @p AND Path NOT LIKE @nested ORDER BY Path"
                        : "SELECT TOP (@max) Path FROM Names WHERE Path LIKE @p AND Path LIKE @member ORDER BY Path";
                    using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 })
                    {
                        cmd.Parameters.AddWithValue("@p", pattern);
                        cmd.Parameters.AddWithValue("@nested", pattern + "/%");
                        cmd.Parameters.AddWithValue("@member", "/" + folder + "/" + Like(objectPart) + "%/Methods/" + Like(memberPart ?? "") + "%");
                        cmd.Parameters.AddWithValue("@max", max - symbols.Count);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) symbols.Add(Element(r.GetString(0), "Symbol", null));
                    }
                }
            }
            return Ok(new Dictionary<string, object> { ["symbols"] = symbols });
        }

        // ── Members (completion, signature help) ──

        private string Members(string body)
        {
            string type = Str(body, "objectType"), name = Str(body, "objectName");
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name)) return Fail("objectType and objectName are required.");
            string key = type + "/" + name;
            lock (_membersCache)
                if (_membersCache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.Key).TotalSeconds < 60)
                    return Ok(new Dictionary<string, object>(cached.Value));

            var result = _json.DeserializeObject(_bridge.Call("type_members", "{\"objectType\":\"" + JsonHelpers.EscapeJsonString(type)
                + "\",\"objectName\":\"" + JsonHelpers.EscapeJsonString(name) + "\"}")) as Dictionary<string, object>;
            if (result == null || !true.Equals(result.TryGetValue("success", out var s) ? s : null))
                return Fail(result != null && result.TryGetValue("message", out var msg) ? Convert.ToString(msg) : "Members not available.");

            // Methods that Chain of Command classes add to the type (and its bases) are members too.
            var members = Items(result["members"]).Cast<Dictionary<string, object>>().ToList();
            var chain = Items(result["chain"]).Select(Convert.ToString).ToList();
            string folder = XRefQuery.FolderOf(type);
            if (folder != null && IndexReady(body))
            {
                var known = new HashSet<string>(members.Select(x => Convert.ToString(x["kind"]) + ":" + Convert.ToString(x["name"])), StringComparer.OrdinalIgnoreCase);
                foreach (string owner in chain)
                    foreach (var ext in _index.ExtensionsOf("/" + folder + "/" + owner, includeChildren: false))
                    {
                        if (!(_json.DeserializeObject(_bridge.Call("type_members", "{\"objectType\":\"AxClass\",\"objectName\":\""
                                + JsonHelpers.EscapeJsonString(ext.Name) + "\"}")) is Dictionary<string, object> extMembers)
                            || !true.Equals(extMembers.TryGetValue("success", out var ok) ? ok : null)) continue;
                        foreach (var em in Items(extMembers["members"]).Cast<Dictionary<string, object>>())
                        {
                            if (!string.Equals(Convert.ToString(em["owner"]), ext.Name, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!known.Add(Convert.ToString(em["kind"]) + ":" + Convert.ToString(em["name"]))) continue;
                            if (Convert.ToString(em.TryGetValue("access", out var acc) ? acc : "public") == "private") continue;
                            em["owner"] = ext.Name;
                            em["extension"] = true;
                            members.Add(em);
                        }
                    }
            }
            var data = new Dictionary<string, object> { ["chain"] = chain, ["members"] = members };
            lock (_membersCache) _membersCache[key] = new KeyValuePair<DateTime, Dictionary<string, object>>(DateTime.UtcNow, data);
            return Ok(new Dictionary<string, object>(data));
        }

        // ── Syntax check with the compiler's parser ──

        private string Syntax(string body)
        {
            string source = Str(body, "source") ?? "";
            string kind = Str(body, "kind") ?? "method";
            var sink = new DiagnosticsHandler();
            var context = new ParserContext(Str(body, "elementType") ?? "Class", Str(body, "elementName") ?? "Code");
            try
            {
                if (kind == "declaration")
                    Pass1.ParseClassOrInterface(context, source, null, sink, new MacroLibrary(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                else
                    Pass1.ParseMethod(context, source, null, sink, new MacroLibrary());
            }
            catch (Exception ex)
            {
                // The parser reports syntax errors before it throws on its own missing context (it expects a metadata
                // provider further on); only a syntax exception without diagnostics is reported as such.
                if (!sink.Diagnostics().Any() && ex is SyntaxErrorException)
                    return Ok(new Dictionary<string, object> { ["diagnostics"] = new[] { new Dictionary<string, object> {
                        ["line"] = 1, ["column"] = 1, ["endLine"] = 1, ["endColumn"] = 1, ["severity"] = "Error", ["message"] = ex.Message } } });
            }
            var diagnostics = sink.Diagnostics().Where(d => !d.Ignored).Select(d =>
            {
                string message = d.Message ?? "";
                try { var args = d.Args?.Cast<object>().ToArray(); if (args != null && args.Length > 0) message = string.Format(message, args); } catch { }
                return new Dictionary<string, object>
                {
                    ["line"] = d.Position.StartLine, ["column"] = d.Position.StartCol,
                    ["endLine"] = d.Position.EndLine, ["endColumn"] = d.Position.EndCol,
                    ["severity"] = d.Severity.ToString(), ["code"] = d.Moniker, ["message"] = message,
                };
            }).ToList();
            return Ok(new Dictionary<string, object> { ["diagnostics"] = diagnostics });
        }

        // ── Labels of your models (completion of "@...") ──

        private string Labels(string body)
        {
            string search = (Str(body, "search") ?? "").Trim().TrimStart('@');
            string language = Str(body, "language") ?? "en-US";
            int max = int.TryParse(Str(body, "max"), out int m) ? Math.Max(1, Math.Min(m, 300)) : 100;
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            var store = new LabelStore(config);
            var result = new List<Dictionary<string, object>>();
            foreach (string file in config.CustomPackageFolders().SelectMany(p => Directory.EnumerateFiles(p, "*." + language + ".label.txt", SearchOption.AllDirectories)))
            {
                string fileId = Path.GetFileName(file);
                fileId = fileId.Substring(0, fileId.Length - ("." + language + ".label.txt").Length);
                var mtime = File.GetLastWriteTimeUtc(file);
                Dictionary<string, LabelEntry> labels;
                lock (_labelCache)
                {
                    if (!_labelCache.TryGetValue(file, out var cached) || cached.Key != mtime)
                        _labelCache[file] = cached = new KeyValuePair<DateTime, Dictionary<string, LabelEntry>>(mtime, store.ReadAll(file));
                    labels = cached.Value;
                }
                foreach (var l in labels.Values)
                {
                    string reference = "@" + fileId + ":" + l.Id;
                    if (search.Length > 0 && reference.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                        && (l.Text ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result.Add(new Dictionary<string, object> { ["reference"] = reference, ["text"] = l.Text, ["comment"] = l.Comment ?? "" });
                    if (result.Count >= max) break;
                }
                if (result.Count >= max) break;
            }
            return Ok(new Dictionary<string, object> { ["labels"] = result });
        }

        // ── SQL helpers ──

        private static IEnumerable<object> Items(object value) =>
            value is System.Collections.IEnumerable e && !(value is string) ? e.Cast<object>() : Enumerable.Empty<object>();

        private static string Like(string s) => (s ?? "").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        private static IEnumerable<string> Sources(SqlConnection conn, string targetPath, int kind, int max) =>
            Column(conn, "SELECT TOP " + max + " s.Path FROM Names t JOIN [References] r ON r.TargetId = t.Id AND r.Kind = " + kind
                + " JOIN Names s ON s.Id = r.SourceId WHERE t.Path = @p ORDER BY s.Path", ("@p", targetPath));

        private static List<string> Column(SqlConnection conn, string sql, params (string, object)[] args)
        {
            var list = new List<string>();
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(r.GetString(0));
            }
            return list;
        }

        private static object Scalar(SqlConnection conn, string sql, params (string, object)[] args)
        {
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
                return cmd.ExecuteScalar();
            }
        }
    }
}
