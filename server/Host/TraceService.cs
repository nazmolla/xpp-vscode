using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using XppVsCode.Server.Analysis;
using XppVsCode.Server.Tracing;

namespace XppVsCode.Server.Host
{
    /// <summary>What to keep from a trace: one AOS, session or user, requests that ran a method, a time window.</summary>
    internal sealed class TraceFilter
    {
        /// <summary>Machine name of the AOS (contains, case-insensitive).</summary>
        public string Aos;
        /// <summary>X++ session id (exact).</summary>
        public string Session;
        /// <summary>User id or user GUID (contains, case-insensitive).</summary>
        public string User;
        /// <summary>Requests whose call tree has a method containing this text.</summary>
        public string Method;
        /// <summary>Seconds from the start of the trace, or a UTC time (HH:mm:ss or a full date and time).</summary>
        public string From, To;

        public bool HasSessionFilter => !string.IsNullOrWhiteSpace(Aos) || !string.IsNullOrWhiteSpace(Session) || !string.IsNullOrWhiteSpace(User);
        public bool IsEmpty => !HasSessionFilter && string.IsNullOrWhiteSpace(Method) && string.IsNullOrWhiteSpace(From) && string.IsNullOrWhiteSpace(To);

        public string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Aos)) parts.Add("AOS " + Aos);
            if (!string.IsNullOrWhiteSpace(Session)) parts.Add("session " + Session);
            if (!string.IsNullOrWhiteSpace(User)) parts.Add("user " + User);
            if (!string.IsNullOrWhiteSpace(Method)) parts.Add("method " + Method);
            if (!string.IsNullOrWhiteSpace(From)) parts.Add("from " + From);
            if (!string.IsNullOrWhiteSpace(To)) parts.Add("to " + To);
            return string.Join(", ", parts);
        }

        public static TraceFilter Create(Func<string, string> get) => new TraceFilter
        {
            Aos = Clean(get("aos")),
            Session = Clean(get("session")),
            User = Clean(get("user")),
            Method = Clean(get("method")),
            From = Clean(get("from")),
            To = Clean(get("to")),
        };

        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// Reads a D365FO trace (.etl, as taken in the web client or by Trace Parser) with the event manifests of the installed
    /// platform (PackagesLocalDirectory\bin\Monitoring) and reports AOS instances, sessions and users, requests with their call
    /// trees, errors and exceptions with their call stacks, hot methods, SQL and pauses.
    /// </summary>
    internal static class TraceService
    {
        private const string AppPrefix = "Dynamics.AX.Application.";
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private const int MaxMessages = 2000;

        private enum Kind { Other, Enter, Exit, Sql }

        /// <summary>Indexes of the fields every Dynamics event carries (-1 when the event has none).</summary>
        private sealed class EventShape
        {
            public Kind Kind;
            public EventDefinition Def;
            public int Method = -1, SqlText = -1, SqlPrep = -1, SqlExec = -1;
            public int Machine = -1, Session = -1, UserGuid = -1, UserId = -1, RequestId = -1, Message = -1;
            public int DecodeCount;
        }

        internal sealed class SessionInfo
        {
            public string Aos, Session, UserId, UserGuid;
            public double First = double.MaxValue, Last;
            public long Events, XppCalls;
            public int Requests, Errors, Exceptions;
        }

        internal sealed class MessageInfo
        {
            public string Event, Message, Aos, Session, UserId;
            public double FirstMs;
            public int Count;
        }

        internal sealed class TraceInfo
        {
            public string Path;
            public long Events;
            public double DurationMs;
            public int Definitions;
            public DateTime StartUtc;
            public Dictionary<string, int> EventCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<(string, string), SessionInfo> Sessions = new Dictionary<(string, string), SessionInfo>();
            public Dictionary<string, long> AosEvents = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<(string, string), MessageInfo> Messages = new Dictionary<(string, string), MessageInfo>();
            public TraceFilter Filter = new TraceFilter();
            /// <summary>Requests after the method and time filters.</summary>
            public List<TraceAnalyzer.Request> Requests = new List<TraceAnalyzer.Request>();
            public List<TraceAnalyzer.ErrorHit> Errors = new List<TraceAnalyzer.ErrorHit>();

            public string Utc(double ms) => StartUtc == default
                ? (ms / 1000).ToString("F1", CultureInfo.InvariantCulture) + " s"
                : StartUtc.AddMilliseconds(ms).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            public SessionInfo SessionOf(TraceAnalyzer.Request r) => r.Session != null && Sessions.TryGetValue((r.Aos, r.Session), out var s) ? s : null;

            public string UserOf(TraceAnalyzer.Request r)
            {
                var s = SessionOf(r);
                return !string.IsNullOrEmpty(s?.UserId) ? s.UserId : r.UserGuid;
            }
        }

        public static string AnalyzeJson(string body)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                var args = json.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();
                string Get(string key) => args.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
                double minRequestMs = args.TryGetValue("minRequestMs", out object m) && m != null ? Convert.ToDouble(m, CultureInfo.InvariantCulture) : 50;
                double minNodeMs = args.TryGetValue("minNodeMs", out object n) && n != null ? Convert.ToDouble(n, CultureInfo.InvariantCulture) : 5;
                var filter = TraceFilter.Create(Get);
                var analyzer = Analyze(Get("path"), filter, out var info);
                return json.Serialize(Report(analyzer, info, filter.IsEmpty ? minRequestMs : 0, minNodeMs));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException || ex is FormatException)
            {
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message });
            }
        }

        public static string AnalyzeText(string path, double minRequestMs) => AnalyzeText(path, minRequestMs, new TraceFilter());

        /// <summary>Text for agents: AOS and sessions, errors with their stacks, the slowest (or filtered) requests, hot methods, SQL and pauses.</summary>
        public static string AnalyzeText(string path, double minRequestMs, TraceFilter filter)
        {
            filter = filter ?? new TraceFilter();
            var analyzer = Analyze(path, filter, out var info);
            var sb = new StringBuilder();
            sb.AppendLine($"Trace {Path.GetFileName(path)}: {info.Utc(0)} to {info.Utc(info.DurationMs)} UTC ({info.DurationMs / 1000:F1} s), {info.Events:N0} events, {analyzer.XppCalls:N0} X++ calls, {analyzer.Sql.Values.Sum(s => s.Count):N0} SQL statements.");
            if (!filter.IsEmpty) sb.AppendLine("Filter: " + filter.Describe() + ". The AOS and session lists cover the whole trace; everything else only what matches.");

            sb.AppendLine($"\nAOS instances ({info.AosEvents.Count}):");
            foreach (var kv in info.AosEvents.OrderByDescending(k => k.Value))
                sb.AppendLine($"- {(kv.Key.Length == 0 ? "(unnamed)" : kv.Key)}: {kv.Value:N0} events, {info.Sessions.Values.Count(s => s.Aos == kv.Key)} sessions");

            var sessions = info.Sessions.Values.OrderByDescending(s => s.Errors + s.Exceptions > 0).ThenByDescending(s => s.XppCalls).ThenBy(s => s.First).ToList();
            sb.AppendLine($"\nSessions ({sessions.Count}; filter with session, user or aos):");
            foreach (var s in sessions.Take(40))
                sb.AppendLine($"- {AosLabel(s.Aos, info)}session {SessionLabel(s.Session)}, user {UserLabel(s)}, {info.Utc(s.First)}-{info.Utc(s.Last)}, {s.Requests} requests, {s.XppCalls:N0} X++ calls{(s.Errors + s.Exceptions > 0 ? $", {s.Errors} errors / {s.Exceptions} exceptions" : "")}");
            if (sessions.Count > 40) sb.AppendLine($"  ... {sessions.Count - 40} more with less activity");

            var errors = info.Errors.Where(e => e.Kind != "warning").Concat(info.Errors.Where(e => e.Kind == "warning")).Take(25).OrderBy(e => e.Ms).ToList();
            sb.AppendLine(errors.Count == 0
                ? "\nErrors and exceptions: none found (no call to Global::error/checkFailed/warning, no frames unwound by an exception)."
                : $"\nErrors and exceptions ({info.Errors.Count} distinct; call stacks outermost first):");
            foreach (var e in errors)
            {
                var r = e.Request;
                sb.AppendLine($"- {info.Utc(e.Ms)} {e.Kind.ToUpperInvariant()} {(e.Kind == "exception" ? $"thrown in {e.Method}, caught by {e.CaughtBy}" : e.Method)}{(e.Count > 1 ? $" ({e.Count}x)" : "")}");
                sb.AppendLine($"  {AosLabel(r.Aos, info)}session {SessionLabel(r.Session)}, user {info.UserOf(r)}, T{r.Thread}, request {r.Root.Method} at {info.Utc(r.Start)}");
                foreach (string frame in Frames(e.Stack, 18)) sb.AppendLine("    > " + frame);
            }

            var messages = info.Messages.Values.Where(m => m.Event != "AosReflectionInfo").OrderBy(m => m.FirstMs).Take(25).ToList();
            if (messages.Count > 0)
            {
                sb.AppendLine("\nPlatform messages (first UTC time, count, event, session and user: message):");
                foreach (var m in messages)
                    sb.AppendLine($"- {info.Utc(m.FirstMs)} {m.Count}x {m.Event}, {AosLabel(m.Aos, info)}session {SessionLabel(m.Session)} {m.UserId}: {Trim(m.Message, 300)}");
            }

            bool filtered = !filter.IsEmpty;
            var requests = filtered
                ? info.Requests.OrderBy(r => r.Start).Take(30).ToList()
                : info.Requests.Where(r => r.Duration >= minRequestMs).OrderByDescending(r => r.Duration).Take(10).OrderBy(r => r.Start).ToList();
            sb.AppendLine(filtered
                ? $"\nRequests matching the filter ({info.Requests.Count}{(info.Requests.Count > 30 ? ", first 30" : "")}; UTC start, AOS, thread, session, user, duration, hot path):"
                : "\nSlowest requests (UTC start, AOS, thread, session, user, duration, hot path):");
            foreach (var r in requests)
            {
                sb.AppendLine($"- {info.Utc(r.Start)} {AosLabel(r.Aos, info)}T{r.Thread} session {SessionLabel(r.Session)}, user {info.UserOf(r)}: {r.Root.Method} {r.Duration:F0} ms{(r.IsBatch ? " [batch]" : "")}{(r.Unfinished ? " [unfinished]" : "")}{(r.Errors + r.Unwinds > 0 ? $" [{r.Errors} errors, {r.Unwinds} exceptions]" : "")}, SQL {r.SqlCount} / {r.SqlMs:F0} ms");
                if (!string.IsNullOrWhiteSpace(filter.Method))
                {
                    sb.AppendLine($"  path to {filter.Method}:");
                    foreach (var node in PathTo(r.Root, filter.Method, 15)) sb.AppendLine($"    {node.Method} {node.Inclusive:F0} / {node.Exclusive:F0} ms, {node.Calls}x");
                }
                sb.AppendLine("  hot path (incl / excl ms, calls):");
                foreach (var node in TraceAnalyzer.HotPath(r.Root, 10))
                    sb.AppendLine($"    {node.Method} {node.Inclusive:F0} / {node.Exclusive:F0} ms, {node.Calls}x");
                sb.AppendLine("  most exclusive time: " + string.Join(", ", TraceAnalyzer.TopExclusive(r.Root, 5).Select(k => $"{k.Key} {k.Value:F0} ms{(IsWait(k.Key) ? " (waiting)" : "")}")));
            }
            if (requests.Count == 0) sb.AppendLine("- none");

            sb.AppendLine("\nMethods by exclusive time (excl, incl, calls, SQL ms; waits for other sessions left out):");
            foreach (var kv in analyzer.Methods.Where(k => !IsWait(k.Key)).OrderByDescending(k => k.Value.Exclusive).Take(15))
                sb.AppendLine($"- {kv.Key}: {kv.Value.Exclusive:F0} / {kv.Value.Inclusive:F0} ms, {kv.Value.Calls}x, SQL {kv.Value.SqlMs:F0} ms");
            sb.AppendLine("\nSQL by total time (total, count, max, caller):");
            foreach (var kv in analyzer.Sql.OrderByDescending(k => k.Value.Total).Take(10))
                sb.AppendLine($"- {kv.Value.Total:F0} ms, {kv.Value.Count}x, max {kv.Value.Max:F0} ms, {TopCaller(kv.Value)}: {Trim(kv.Key, 300)}");
            var gaps = analyzer.Gaps.OrderByDescending(g => g.Duration).Take(8).ToList();
            if (gaps.Count > 0)
            {
                sb.AppendLine("\nPauses (no events while a method ran; whole AOS when no thread is named):");
                foreach (var g in gaps.OrderBy(g => g.Start))
                    sb.AppendLine($"- {g.Duration:F0} ms at {info.Utc(g.Start)} {AosLabel(g.Aos, info)}" + (g.Thread == 0 ? "in the whole AOS" : $"on T{g.Thread} in {g.Method}"));
            }
            return sb.ToString();
        }

        internal static TraceAnalyzer Analyze(string path, out TraceInfo info) => Analyze(path, new TraceFilter(), out info);

        internal static TraceAnalyzer Analyze(string path, TraceFilter filter, out TraceInfo info)
        {
            filter = filter ?? new TraceFilter();
            if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".etl", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pass the full path of a trace file (.etl).");
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new ArgumentException("Trace file not found: " + path);

            string folder = Path.Combine(DynamicsConfigurationReader.ReadActiveConfiguration().FrameworkDirectory ?? "", "bin", "Monitoring");
            var manifest = EventManifest.Load(folder);
            if (manifest.Count == 0)
                throw new InvalidOperationException("No event manifests found in " + folder + "; they come with the Dynamics 365 platform (PackagesLocalDirectory\\bin\\Monitoring).");

            // The user id is only on some events of a session, so the sessions to keep are found in a first pass.
            HashSet<(string, string)> keep = null;
            TraceInfo scan = null;
            if (filter.HasSessionFilter)
            {
                scan = Read(path, manifest, null, null);
                keep = new HashSet<(string, string)>(scan.Sessions.Values.Where(s => Matches(s, filter)).Select(s => (s.Aos, s.Session)));
                if (keep.Count == 0)
                    throw new ArgumentException($"No session matches {filter.Describe()}. Sessions in the trace: " +
                        string.Join("; ", scan.Sessions.Values.OrderByDescending(s => s.XppCalls).Take(20).Select(s => $"{AosLabel(s.Aos, scan)}session {SessionLabel(s.Session)} user {UserLabel(s)}")));
            }

            var analyzer = new TraceAnalyzer();
            var result = Read(path, manifest, analyzer, keep);
            if (scan != null)
            {
                // The lists of AOS, sessions and messages stay complete so other sessions can be picked next.
                foreach (var s in scan.Sessions) if (!result.Sessions.ContainsKey(s.Key)) result.Sessions[s.Key] = s.Value;
                result.AosEvents = scan.AosEvents;
                result.Messages = scan.Messages;
            }
            result.Filter = filter;
            analyzer.Finish(result.DurationMs);

            foreach (var r in analyzer.Requests)
            {
                var s = result.SessionOf(r);
                if (s == null) continue;
                s.Requests++;
                s.Errors += r.Errors;
                s.Exceptions += r.Unwinds;
            }
            double from = ParseTime(filter.From, result, double.MinValue), to = ParseTime(filter.To, result, double.MaxValue);
            result.Requests = analyzer.Requests.Where(r => r.Start + r.Duration >= from && r.Start <= to
                && (string.IsNullOrWhiteSpace(filter.Method) || Contains(r.Root, filter.Method))).ToList();
            var kept = new HashSet<TraceAnalyzer.Request>(result.Requests);
            result.Errors = analyzer.Errors.Where(e => kept.Contains(e.Request)).ToList();
            info = result;
            return analyzer;
        }

        private static TraceInfo Read(string path, EventManifest manifest, TraceAnalyzer analyzer, HashSet<(string, string)> keep)
        {
            var result = new TraceInfo { Path = path, Definitions = manifest.Count };
            var shapes = new Dictionary<(Guid, ushort), EventShape>();
            long first = -1, last = 0;
            EtwFileReader.Read(path, e =>
            {
                if (!shapes.TryGetValue((e.Provider, e.Id), out var k))
                    shapes[(e.Provider, e.Id)] = k = Classify(manifest.Find(e.Provider, e.Id));
                if (k.Def == null) return;  // the file's own header and providers without a manifest
                if (first < 0)
                {
                    first = e.Timestamp;
                    result.StartUtc = DateTime.FromFileTimeUtc(first);
                }
                last = e.Timestamp;
                double ms = (e.Timestamp - first) / 10000.0;
                var values = k.DecodeCount > 0 ? EventManifest.Decode(e, k.Def, k.DecodeCount) : new object[0];
                string aos = Str(values, k.Machine) ?? "";
                string session = Str(values, k.Session);
                if (keep != null && !keep.Contains((aos, session ?? ""))) return;

                result.Events++;
                result.EventCounts[k.Def.Name] = result.EventCounts.TryGetValue(k.Def.Name, out int c) ? c + 1 : 1;
                result.AosEvents[aos] = result.AosEvents.TryGetValue(aos, out long ac) ? ac + 1 : 1;
                string userId = Str(values, k.UserId), userGuid = Str(values, k.UserGuid);
                if (session != null || k.Kind == Kind.Enter)
                {
                    session = session ?? "";
                    if (!result.Sessions.TryGetValue((aos, session), out var s))
                        result.Sessions[(aos, session)] = s = new SessionInfo { Aos = aos, Session = session };
                    s.Events++;
                    s.First = Math.Min(s.First, ms);
                    s.Last = Math.Max(s.Last, ms);
                    if (!string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(s.UserId)) s.UserId = userId;
                    if (!string.IsNullOrEmpty(userGuid) && string.IsNullOrEmpty(s.UserGuid)) s.UserGuid = userGuid;
                    if (k.Kind == Kind.Enter) s.XppCalls++;
                }
                string message = Str(values, k.Message);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    var key = (k.Def.Name, message);
                    if (result.Messages.TryGetValue(key, out var mi)) mi.Count++;
                    else if (result.Messages.Count < MaxMessages)
                        result.Messages[key] = new MessageInfo { Event = k.Def.Name, Message = message, Aos = aos, Session = session, UserId = userId, FirstMs = ms, Count = 1 };
                }
                if (analyzer == null) return;
                analyzer.Event(aos, e.ThreadId, ms);
                switch (k.Kind)
                {
                    case Kind.Enter:
                        analyzer.Enter(aos, e.ThreadId, ms, Method(Str(values, k.Method)),
                            new TraceAnalyzer.CallContext { Session = session, UserGuid = userGuid, RequestId = Str(values, k.RequestId) });
                        break;
                    case Kind.Exit:
                        analyzer.Exit(aos, e.ThreadId, ms, Method(Str(values, k.Method)));
                        break;
                    case Kind.Sql:
                        double seconds = (At(values, k.SqlPrep) is double prep ? prep : 0) + (At(values, k.SqlExec) is double exec ? exec : 0);
                        analyzer.SqlStatement(aos, e.ThreadId, ms, Statement(Str(values, k.SqlText)), seconds * 1000);
                        break;
                }
            });
            result.DurationMs = first < 0 ? 0 : (last - first) / 10000.0;
            return result;
        }

        private static EventShape Classify(EventDefinition def)
        {
            var shape = new EventShape { Def = def };
            if (def == null) return shape;
            shape.Machine = def.IndexOf("machineName");
            shape.Session = def.IndexOf("sessionId");
            shape.UserGuid = def.IndexOf("userGuid");
            shape.UserId = def.IndexOf("userId");
            shape.RequestId = def.IndexOf("requestId");
            shape.Message = def.IndexOf("message");
            switch (def.Name)
            {
                case "XppMethodEnter":
                case "XppMethodExit":
                    shape.Method = def.IndexOf("methodName");
                    if (shape.Method >= 0) shape.Kind = def.Name == "XppMethodEnter" ? Kind.Enter : Kind.Exit;
                    break;
                case "AosSqlStatementExecutionLatency":
                    shape.SqlText = def.IndexOf("sqlStatement");
                    shape.SqlPrep = def.IndexOf("preparationTimeSeconds");
                    shape.SqlExec = def.IndexOf("executionTimeSeconds");
                    if (shape.SqlText >= 0 && shape.SqlPrep >= 0 && shape.SqlExec >= 0) shape.Kind = Kind.Sql;
                    break;
            }
            var needed = new[] { shape.Machine, shape.Session, shape.UserGuid, shape.UserId, shape.RequestId, shape.Message, shape.Method, shape.SqlText, shape.SqlPrep, shape.SqlExec };
            shape.DecodeCount = needed.Max() + 1;
            return shape;
        }

        private static object At(object[] values, int index) => index >= 0 && index < values.Length ? values[index] : null;

        private static string Str(object[] values, int index)
        {
            object v = At(values, index);
            if (v == null) return null;
            return v is Guid g ? (g == Guid.Empty ? null : g.ToString()) : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static bool Matches(SessionInfo s, TraceFilter f)
        {
            if (!string.IsNullOrWhiteSpace(f.Aos) && s.Aos.IndexOf(f.Aos, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrWhiteSpace(f.Session) && !string.Equals(SessionLabel(s.Session), f.Session, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(f.User)
                && (s.UserId ?? "").IndexOf(f.User, StringComparison.OrdinalIgnoreCase) < 0
                && (s.UserGuid ?? "").IndexOf(f.User, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        /// <summary>Seconds from the start, or a UTC time of day / date and time; <paramref name="fallback"/> when empty.</summary>
        private static double ParseTime(string text, TraceInfo info, double fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) return seconds * 1000;
            if (text.IndexOf(':') > 0 && text.IndexOf('-') < 0 && text.IndexOf('/') < 0 && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time))
                return (info.StartUtc.Date + time - info.StartUtc).TotalMilliseconds;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                return (date - info.StartUtc).TotalMilliseconds;
            throw new FormatException("Can't read the time '" + text + "': use seconds from the start of the trace, a UTC time (20:05:53) or a UTC date and time.");
        }

        private static bool Contains(TraceAnalyzer.Node root, string text)
        {
            var pending = new Stack<TraceAnalyzer.Node>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (node.Method.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                foreach (var child in node.Children.Values) pending.Push(child);
            }
            return false;
        }

        /// <summary>The heaviest path from the root to a method containing <paramref name="text"/>.</summary>
        private static List<TraceAnalyzer.Node> PathTo(TraceAnalyzer.Node root, string text, int maxLines)
        {
            var path = new List<TraceAnalyzer.Node>();
            bool Search(TraceAnalyzer.Node node, int depth)
            {
                path.Add(node);
                if (node.Method.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (depth < 300)
                    foreach (var child in node.Children.Values.OrderByDescending(c => c.Inclusive))
                        if (Search(child, depth + 1)) return true;
                path.RemoveAt(path.Count - 1);
                return false;
            }
            Search(root, 0);
            return path.Count <= maxLines ? path : path.Take(2).Concat(path.Skip(path.Count - (maxLines - 2))).ToList();
        }

        /// <summary>The stack with the middle left out: the two outermost frames, then the innermost ones.</summary>
        private static IEnumerable<string> Frames(string[] stack, int max)
        {
            if (stack.Length <= max) return stack;
            return stack.Take(2).Concat(new[] { $"... {stack.Length - max + 1} frames ..." }).Concat(stack.Skip(stack.Length - (max - 3)));
        }

        private static string SessionLabel(string session) => string.IsNullOrEmpty(session) ? "(none)" : session;

        private static string AosLabel(string aos, TraceInfo info) => info.AosEvents.Count > 1 ? $"AOS {(aos.Length == 0 ? "(unnamed)" : aos)}, " : "";

        private static string UserLabel(SessionInfo s) =>
            !string.IsNullOrEmpty(s.UserId) ? s.UserId + (string.IsNullOrEmpty(s.UserGuid) ? "" : $" ({s.UserGuid})") : (s.UserGuid ?? "?");

        private static Dictionary<string, object> Report(TraceAnalyzer analyzer, TraceInfo info, double minRequestMs, double minNodeMs)
        {
            var requests = info.Requests.Where(r => r.Duration >= minRequestMs || r.Errors + r.Unwinds > 0)
                .OrderByDescending(r => r.Errors + r.Unwinds > 0).ThenByDescending(r => r.Duration).Take(150).OrderBy(r => r.Start).ToList();
            var index = new Dictionary<TraceAnalyzer.Request, int>();
            for (int i = 0; i < requests.Count; i++) index[requests[i]] = i;
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["file"] = info.Path,
                ["startUtc"] = info.StartUtc == default ? null : info.StartUtc.ToString("o", CultureInfo.InvariantCulture),
                ["durationMs"] = Math.Round(info.DurationMs, 1),
                ["events"] = info.Events,
                ["xppCalls"] = analyzer.XppCalls,
                ["filter"] = info.Filter.IsEmpty ? null : info.Filter.Describe(),
                ["eventCounts"] = info.EventCounts.OrderByDescending(k => k.Value).Take(25).Select(k => new Dictionary<string, object> { ["name"] = k.Key, ["count"] = k.Value }).ToList(),
                ["aos"] = info.AosEvents.OrderByDescending(k => k.Value).Select(k => new Dictionary<string, object> { ["name"] = k.Key, ["events"] = k.Value }).ToList(),
                ["sessions"] = info.Sessions.Values.OrderByDescending(s => s.XppCalls).Take(300).Select(s => new Dictionary<string, object>
                {
                    ["aos"] = s.Aos,
                    ["session"] = s.Session,
                    ["user"] = s.UserId,
                    ["userGuid"] = s.UserGuid,
                    ["startMs"] = Math.Round(s.First, 1),
                    ["endMs"] = Math.Round(s.Last, 1),
                    ["requests"] = s.Requests,
                    ["xppCalls"] = s.XppCalls,
                    ["errors"] = s.Errors,
                    ["exceptions"] = s.Exceptions,
                }).ToList(),
                ["errors"] = info.Errors.Take(300).Select(e => new Dictionary<string, object>
                {
                    ["kind"] = e.Kind,
                    ["ms"] = Math.Round(e.Ms, 1),
                    ["method"] = e.Method,
                    ["caughtBy"] = e.CaughtBy,
                    ["count"] = e.Count,
                    ["aos"] = e.Request.Aos,
                    ["session"] = e.Request.Session,
                    ["user"] = info.UserOf(e.Request),
                    ["thread"] = e.Request.Thread,
                    ["requestMethod"] = e.Request.Root.Method,
                    ["request"] = index.TryGetValue(e.Request, out int ri) ? ri : -1,
                    ["stack"] = e.Stack.Length <= 60 ? e.Stack : e.Stack.Take(5).Concat(new[] { "..." }).Concat(e.Stack.Skip(e.Stack.Length - 54)).ToArray(),
                }).ToList(),
                ["messages"] = info.Messages.Values.Where(m => m.Event != "AosReflectionInfo").OrderBy(m => m.FirstMs).Take(200).Select(m => new Dictionary<string, object>
                {
                    ["event"] = m.Event,
                    ["message"] = Trim(m.Message, 2000),
                    ["ms"] = Math.Round(m.FirstMs, 1),
                    ["count"] = m.Count,
                    ["aos"] = m.Aos,
                    ["session"] = m.Session,
                    ["user"] = m.UserId,
                }).ToList(),
                ["requests"] = requests.Select(r =>
                {
                    int budget = 400;
                    return new Dictionary<string, object>
                    {
                        ["aos"] = r.Aos,
                        ["thread"] = r.Thread,
                        ["session"] = r.Session,
                        ["user"] = info.UserOf(r),
                        ["requestId"] = r.RequestId,
                        ["startMs"] = Math.Round(r.Start, 1),
                        ["ms"] = Math.Round(r.Duration, 1),
                        ["method"] = r.Root.Method,
                        ["batch"] = r.IsBatch,
                        ["unfinished"] = r.Unfinished,
                        ["errors"] = r.Errors,
                        ["exceptions"] = r.Unwinds,
                        ["sqlN"] = r.SqlCount,
                        ["sqlMs"] = Math.Round(r.SqlMs, 1),
                        ["tree"] = TraceAnalyzer.TreeJson(r.Root, Math.Max(minNodeMs, r.Duration * 0.002), ref budget),
                        ["topExclusive"] = TraceAnalyzer.TopExclusive(r.Root, 8).Select(k => new Dictionary<string, object> { ["m"] = k.Key, ["ms"] = Math.Round(k.Value, 1), ["wait"] = IsWait(k.Key) }).ToList(),
                    };
                }).ToList(),
                ["methods"] = analyzer.Methods.OrderByDescending(k => k.Value.Exclusive).Take(300).Select(k => new Dictionary<string, object>
                {
                    ["m"] = k.Key,
                    ["n"] = k.Value.Calls,
                    ["incl"] = Math.Round(k.Value.Inclusive, 1),
                    ["excl"] = Math.Round(k.Value.Exclusive, 1),
                    ["wait"] = IsWait(k.Key),
                    ["max"] = Math.Round(k.Value.Max, 1),
                    ["sqlN"] = k.Value.SqlCount,
                    ["sqlMs"] = Math.Round(k.Value.SqlMs, 1),
                }).ToList(),
                ["sqlTotal"] = new Dictionary<string, object> { ["n"] = analyzer.Sql.Values.Sum(s => s.Count), ["ms"] = Math.Round(analyzer.Sql.Values.Sum(s => s.Total), 1) },
                ["sql"] = analyzer.Sql.OrderByDescending(k => k.Value.Total).Take(200).Select(k => new Dictionary<string, object>
                {
                    ["sql"] = Trim(k.Key, 4000),
                    ["n"] = k.Value.Count,
                    ["ms"] = Math.Round(k.Value.Total, 1),
                    ["max"] = Math.Round(k.Value.Max, 1),
                    ["callers"] = k.Value.Callers.OrderByDescending(c => c.Value).Take(5).Select(c => new Dictionary<string, object> { ["m"] = c.Key, ["n"] = c.Value }).ToList(),
                }).ToList(),
                ["gaps"] = analyzer.Gaps.OrderByDescending(g => g.Duration).Take(50).OrderBy(g => g.Start).Select(g => new Dictionary<string, object>
                {
                    ["aos"] = g.Aos,
                    ["thread"] = g.Thread,
                    ["startMs"] = Math.Round(g.Start, 1),
                    ["ms"] = Math.Round(g.Duration, 1),
                    ["method"] = g.Method,
                }).ToList(),
            };
        }

        /// <summary>Methods that wait for another session (the operation sandbox), so their own time is not work.</summary>
        internal static bool IsWait(string method) => method.EndsWith("::wait", StringComparison.Ordinal) || method.EndsWith("WaitForCompletion", StringComparison.Ordinal);

        private static string Method(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            return name.StartsWith(AppPrefix, StringComparison.Ordinal) ? name.Substring(AppPrefix.Length) : name;
        }

        private static string Statement(string sql) => string.IsNullOrEmpty(sql) ? "?" : Trim(Whitespace.Replace(sql, " ").Trim(), 8000);

        private static string TopCaller(TraceAnalyzer.SqlStat stat) => stat.Callers.OrderByDescending(c => c.Value).First().Key;

        private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
