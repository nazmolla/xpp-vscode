using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using XppVsCode.Server.Analysis;
using XppVsCode.Server.Tracing;

namespace XppVsCode.Server.Host
{
    /// <summary>
    /// Reads a D365FO trace (.etl, as taken in the web client or by Trace Parser) with the event manifests of the installed
    /// platform (PackagesLocalDirectory\bin\Monitoring) and reports requests with their call trees, hot methods, SQL and pauses.
    /// </summary>
    internal static class TraceService
    {
        private const string AppPrefix = "Dynamics.AX.Application.";
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        private enum Kind { Other, Enter, Exit, Sql }

        public static string AnalyzeJson(string body)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                var args = json.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();
                double minRequestMs = args.TryGetValue("minRequestMs", out object m) && m != null ? Convert.ToDouble(m) : 50;
                double minNodeMs = args.TryGetValue("minNodeMs", out object n) && n != null ? Convert.ToDouble(n) : 5;
                var analyzer = Analyze(args.TryGetValue("path", out object p) ? p as string : null, out var info);
                return json.Serialize(Report(analyzer, info, minRequestMs, minNodeMs));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException)
            {
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message });
            }
        }

        /// <summary>Text for agents: the slowest requests with their hot path, hot methods, SQL and pauses.</summary>
        public static string AnalyzeText(string path, double minRequestMs)
        {
            var analyzer = Analyze(path, out var info);
            var sb = new StringBuilder();
            sb.AppendLine($"Trace {Path.GetFileName(path)}: {info.DurationMs / 1000:F1} s, {info.Events:N0} events, {analyzer.XppCalls:N0} X++ calls, {analyzer.Sql.Values.Sum(s => s.Count):N0} SQL statements.");
            var requests = analyzer.Requests.Where(r => r.Duration >= minRequestMs).OrderByDescending(r => r.Duration).Take(10).ToList();
            sb.AppendLine("\nSlowest requests (thread, start, duration, hot path):");
            foreach (var r in requests.OrderBy(r => r.Start))
            {
                sb.AppendLine($"- T{r.Thread} at {r.Start / 1000:F1} s: {r.Root.Method} {r.Duration:F0} ms{(r.IsBatch ? " [batch]" : "")}{(r.Unfinished ? " [unfinished]" : "")}, SQL {r.SqlCount} / {r.SqlMs:F0} ms");
                sb.AppendLine("  hot path (incl / excl ms, calls):");
                foreach (var node in TraceAnalyzer.HotPath(r.Root, 10))
                    sb.AppendLine($"    {node.Method} {node.Inclusive:F0} / {node.Exclusive:F0} ms, {node.Calls}x");
                sb.AppendLine("  most exclusive time: " + string.Join(", ", TraceAnalyzer.TopExclusive(r.Root, 5).Select(k => $"{k.Key} {k.Value:F0} ms{(IsWait(k.Key) ? " (waiting)" : "")}")));
            }
            sb.AppendLine("\nMethods by exclusive time (excl, incl, calls, SQL ms; waits for other sessions left out):");
            foreach (var kv in analyzer.Methods.Where(k => !IsWait(k.Key)).OrderByDescending(k => k.Value.Exclusive).Take(15))
                sb.AppendLine($"- {kv.Key}: {kv.Value.Exclusive:F0} / {kv.Value.Inclusive:F0} ms, {kv.Value.Calls}x, SQL {kv.Value.SqlMs:F0} ms");
            sb.AppendLine("\nSQL by total time (total, count, max, caller):");
            foreach (var kv in analyzer.Sql.OrderByDescending(k => k.Value.Total).Take(10))
                sb.AppendLine($"- {kv.Value.Total:F0} ms, {kv.Value.Count}x, max {kv.Value.Max:F0} ms, {TopCaller(kv.Value)}: {Trim(kv.Key, 300)}");
            var gaps = analyzer.Gaps.OrderByDescending(g => g.Duration).Take(8).ToList();
            if (gaps.Count > 0)
            {
                sb.AppendLine("\nPauses (no events while a method ran; whole process when no thread is named):");
                foreach (var g in gaps.OrderBy(g => g.Start))
                    sb.AppendLine($"- {g.Duration:F0} ms at {g.Start / 1000:F1} s " + (g.Thread == 0 ? "in the whole process" : $"on T{g.Thread} in {g.Method}"));
            }
            return sb.ToString();
        }

        internal sealed class TraceInfo
        {
            public string Path;
            public long Events;
            public double DurationMs;
            public int Definitions;
            public Dictionary<string, int> EventCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        internal static TraceAnalyzer Analyze(string path, out TraceInfo info)
        {
            if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".etl", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pass the full path of a trace file (.etl).");
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new ArgumentException("Trace file not found: " + path);

            string folder = Path.Combine(DynamicsConfigurationReader.ReadActiveConfiguration().FrameworkDirectory ?? "", "bin", "Monitoring");
            var manifest = EventManifest.Load(folder);
            if (manifest.Count == 0)
                throw new InvalidOperationException("No event manifests found in " + folder + "; they come with the Dynamics 365 platform (PackagesLocalDirectory\\bin\\Monitoring).");

            var analyzer = new TraceAnalyzer();
            var result = new TraceInfo { Path = path, Definitions = manifest.Count };
            var kinds = new Dictionary<(Guid, ushort), (Kind kind, EventDefinition def, int[] fields)>();
            long first = -1, last = 0;
            EtwFileReader.Read(path, e =>
            {
                if (!kinds.TryGetValue((e.Provider, e.Id), out var k))
                    kinds[(e.Provider, e.Id)] = k = Classify(manifest.Find(e.Provider, e.Id));
                if (k.def == null) return;  // the file's own header and providers without a manifest
                if (first < 0) first = e.Timestamp;
                last = e.Timestamp;
                result.Events++;
                result.EventCounts[k.def.Name] = result.EventCounts.TryGetValue(k.def.Name, out int c) ? c + 1 : 1;
                double ms = (e.Timestamp - first) / 10000.0;
                analyzer.Event(e.ThreadId, ms);
                switch (k.kind)
                {
                    case Kind.Enter:
                    case Kind.Exit:
                        var values = EventManifest.Decode(e, k.def, k.fields[0] + 1);
                        string method = Method(values[k.fields[0]] as string);
                        if (k.kind == Kind.Enter) analyzer.Enter(e.ThreadId, ms, method);
                        else analyzer.Exit(e.ThreadId, ms, method);
                        break;
                    case Kind.Sql:
                        var sql = EventManifest.Decode(e, k.def, k.fields.Max() + 1);
                        double seconds = (sql[k.fields[1]] is double prep ? prep : 0) + (sql[k.fields[2]] is double exec ? exec : 0);
                        analyzer.SqlStatement(e.ThreadId, ms, Statement(sql[k.fields[0]] as string), seconds * 1000);
                        break;
                }
            });
            result.DurationMs = first < 0 ? 0 : (last - first) / 10000.0;
            analyzer.Finish(result.DurationMs);
            info = result;
            return analyzer;
        }

        private static (Kind, EventDefinition, int[]) Classify(EventDefinition def)
        {
            if (def == null) return (Kind.Other, null, null);
            switch (def.Name)
            {
                case "XppMethodEnter":
                case "XppMethodExit":
                    int method = def.IndexOf("methodName");
                    return method < 0 ? (Kind.Other, def, null) : (def.Name == "XppMethodEnter" ? Kind.Enter : Kind.Exit, def, new[] { method });
                case "AosSqlStatementExecutionLatency":
                    var fields = new[] { def.IndexOf("sqlStatement"), def.IndexOf("preparationTimeSeconds"), def.IndexOf("executionTimeSeconds") };
                    return fields.Any(f => f < 0) ? (Kind.Other, def, null) : (Kind.Sql, def, fields);
                default:
                    return (Kind.Other, def, null);
            }
        }

        private static Dictionary<string, object> Report(TraceAnalyzer analyzer, TraceInfo info, double minRequestMs, double minNodeMs)
        {
            var requests = analyzer.Requests.Where(r => r.Duration >= minRequestMs).OrderByDescending(r => r.Duration).Take(150).OrderBy(r => r.Start).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["file"] = info.Path,
                ["durationMs"] = Math.Round(info.DurationMs, 1),
                ["events"] = info.Events,
                ["xppCalls"] = analyzer.XppCalls,
                ["eventCounts"] = info.EventCounts.OrderByDescending(k => k.Value).Take(25).Select(k => new Dictionary<string, object> { ["name"] = k.Key, ["count"] = k.Value }).ToList(),
                ["requests"] = requests.Select(r =>
                {
                    int budget = 400;
                    return new Dictionary<string, object>
                    {
                        ["thread"] = r.Thread,
                        ["startMs"] = Math.Round(r.Start, 1),
                        ["ms"] = Math.Round(r.Duration, 1),
                        ["method"] = r.Root.Method,
                        ["batch"] = r.IsBatch,
                        ["unfinished"] = r.Unfinished,
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
