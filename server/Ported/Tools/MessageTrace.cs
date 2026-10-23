using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Where does a message come from? The way developers trace an error: take the message text without its values, find the
    /// label whose text matches (placeholders %1..%9 match any value), then find where that label is used (cross-reference
    /// database: /Labels/@File:Id with the method and line). Messages written as literal strings are found by a code search.
    /// </summary>
    internal sealed class MessageTrace
    {
        private readonly XRefQuery _xref;
        private readonly CodeSearch _search = new CodeSearch();

        public MessageTrace(XRefQuery xref) { _xref = xref; }

        private sealed class LabelEntry
        {
            public string Reference;   // @File:Id or legacy @SYS1234
            public string File, Text;
            public string[] Pieces;    // literal parts between placeholders
            public int LiteralLength;
        }

        private static readonly Dictionary<string, List<LabelEntry>> Indexes = new Dictionary<string, List<LabelEntry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object IndexLock = new object();
        private static readonly Regex Placeholder = new Regex(@"%\d", RegexOptions.Compiled);

        /// <summary>Every label text of one language from all packages (custom first), built once per server.</summary>
        private static List<LabelEntry> LabelIndex(string language)
        {
            lock (IndexLock)
            {
                if (Indexes.TryGetValue(language, out var cached)) return cached;
                var list = new List<LabelEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string suffix = "." + language + ".label.txt";
                foreach (var folder in CodeSearch.Folders(new[] { "AxLabelFile" }, null))
                {
                    string dir = Path.Combine(folder.Key, "LabelResources", language);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string path in Directory.EnumerateFiles(dir, "*.label.txt"))
                    {
                        string name = Path.GetFileName(path);
                        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                        string file = name.Substring(0, name.Length - suffix.Length);
                        if (!seen.Add(file)) continue;
                        try
                        {
                            foreach (string line in File.ReadLines(path))
                            {
                                if (line.Length == 0 || line[0] == ' ' || line[0] == ';') continue;
                                int eq = line.IndexOf('=');
                                if (eq <= 0) continue;
                                string id = line.Substring(0, eq), text = line.Substring(eq + 1);
                                var pieces = Placeholder.Split(text).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                                int literal = pieces.Sum(p => p.Length);
                                if (literal < 4) continue;
                                list.Add(new LabelEntry
                                {
                                    Reference = id.StartsWith("@") ? id : "@" + file + ":" + id,
                                    File = file, Text = text, Pieces = pieces, LiteralLength = literal,
                                });
                            }
                        }
                        catch { }
                    }
                }
                Indexes[language] = list;
                return list;
            }
        }

        private sealed class Match
        {
            public LabelEntry Label;
            public double Coverage;      // share of the message the label's literal text explains
            public bool Whole;           // the label is the whole message (values aside)
        }

        /// <summary>Labels whose literal parts all occur in the message, in order.</summary>
        private static List<Match> MatchLabels(string message, string language, int max)
        {
            string msg = Regex.Replace(message ?? "", @"\s+", " ").Trim();
            var matches = new List<Match>();
            foreach (var l in LabelIndex(language))
            {
                int at = 0, first = -1, last = 0;
                bool ok = true;
                foreach (string piece in l.Pieces)
                {
                    int hit = msg.IndexOf(piece, at, StringComparison.OrdinalIgnoreCase);
                    if (hit < 0) { ok = false; break; }
                    if (first < 0) first = hit;
                    at = hit + piece.Length;
                    last = at;
                }
                if (!ok) continue;
                double coverage = (double)l.LiteralLength / Math.Max(1, msg.Replace(" ", "").Length + (msg.Length - msg.Replace(" ", "").Length));
                matches.Add(new Match { Label = l, Coverage = Math.Min(1, coverage), Whole = first <= 2 && last >= msg.Length - 2 });
            }
            // Longest explanation first; a label equal to the message beats shorter labels found inside it.
            var ranked = matches.OrderByDescending(m => m.Whole).ThenByDescending(m => m.Label.LiteralLength).ToList();
            // Keep labels that explain a real part of the message; short generic words ("Error", "Customer") are noise.
            int minLength = Math.Min(12, Math.Max(4, (int)(msg.Length * 0.3)));
            var useful = ranked.Where(m => m.Whole || m.Label.LiteralLength >= minLength).ToList();
            // Several labels with the same text (copies in other files): keep them, they are all candidates.
            return (useful.Count > 0 ? useful : ranked).Take(max).ToList();
        }

        public Dictionary<string, object> Trace(string message, string language, int maxLabels, int maxUsages)
        {
            if (string.IsNullOrWhiteSpace(message) || message.Trim().Length < 4)
                return Fail("Give the message text (at least 4 characters), as shown in the infolog or the error dialog.");
            language = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
            maxLabels = Math.Max(1, Math.Min(maxLabels <= 0 ? 5 : maxLabels, 20));
            maxUsages = Math.Max(1, Math.Min(maxUsages <= 0 ? 15 : maxUsages, 100));
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var labels = MatchLabels(message, language, 60);
            var labelResults = new List<Dictionary<string, object>>();
            string problem = null;
            SqlConnection conn = labels.Count > 0 ? _xref.TryOpen(out problem) : null;
            try
            {
                // The same text often exists in several label files (copies per module): one result per text, usages merged.
                foreach (var group in labels.GroupBy(m => m.Label.Text.Trim(), StringComparer.OrdinalIgnoreCase).Take(maxLabels * 2))
                {
                    var usages = new List<Dictionary<string, object>>();
                    var ids = group.Select(m => m.Label.Reference).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (conn != null)
                        foreach (string id in ids)
                            usages.AddRange(Usages(conn, id, maxUsages, out _).Select(u => { u["label"] = id; return u; }));
                    var m0 = group.First();
                    labelResults.Add(new Dictionary<string, object>
                    {
                        ["label"] = string.Join(", ", ids), ["labels"] = ids, ["text"] = m0.Label.Text, ["file"] = m0.Label.File,
                        ["coverage"] = Math.Round(m0.Coverage, 2), ["whole"] = m0.Whole,
                        ["usages"] = OrderUsages(usages).Take(maxUsages).ToList(), ["usageCount"] = conn == null ? -1 : usages.Count,
                    });
                }
            }
            finally { conn?.Dispose(); }
            // Texts the code uses come first; labels nothing uses are mostly kernel messages or unrelated words.
            labelResults = labelResults
                .OrderByDescending(l => (bool)l["whole"])
                .ThenByDescending(l => (int)l["usageCount"] > 0)
                .ThenByDescending(l => ((string)l["text"]).Length)
                .ToList();
            // A used label that is the whole message explains it: parts ("Canceled") are noise then.
            if (labelResults.Any(l => (bool)l["whole"] && (int)l["usageCount"] > 0))
                labelResults = labelResults.Where(l => (bool)l["whole"]).ToList();
            labelResults = labelResults.Take(maxLabels).ToList();

            // Literal strings in code ("throw error('...')" without a label): search the longest value-free run of the message.
            var literal = new List<Dictionary<string, object>>();
            bool labelUsed = labelResults.Any(l => ((List<Dictionary<string, object>>)l["usages"]).Count > 0);
            if (!labelUsed)
            {
                string fragment = Regex.Split(message, @"[0-9]+|'[^']*'|""[^""]*""|%\d")
                    .Select(s => s.Trim()).OrderByDescending(s => s.Length).FirstOrDefault();
                if (fragment != null && fragment.Length >= 12)
                {
                    var result = _search.Search(fragment, false, false, null, null, maxUsages * 3, 60000);
                    foreach (var h in result.Hits.Where(h => !Regex.IsMatch(h.Text ?? "", @"^\s*(//|/\*|\*)")).Take(maxUsages))
                        literal.Add(Usage(h.ObjectType, h.ObjectName, h.MemberPath ?? new string[0], h.Line, h.Text, h.Model));
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true, ["message"] = message, ["language"] = language,
                ["labels"] = labelResults, ["literal"] = literal, ["xrefProblem"] = problem, ["ms"] = watch.ElapsedMilliseconds,
            };
        }

        private static readonly Regex Raise = new Regex(@"\b(throw|error|warning|checkFailed|info|Box::\w+|addMessage|Message::)\b", RegexOptions.IgnoreCase);

        /// <summary>Places that use a label (cross-reference database), with the code line; raising code first, tests last.</summary>
        private static List<Dictionary<string, object>> Usages(SqlConnection conn, string reference, int max, out int total)
        {
            var rows = new List<Tuple<string, int>>();
            using (var cmd = new SqlCommand(
                "SELECT TOP 400 s.Path, r.Line FROM Names t JOIN [References] r ON r.TargetId = t.Id JOIN Names s ON s.Id = r.SourceId "
                + "WHERE t.Path = @p ORDER BY s.Path, r.Line", conn) { CommandTimeout = 60 })
            {
                cmd.Parameters.AddWithValue("@p", "/Labels/" + reference);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) rows.Add(Tuple.Create(r.GetString(0), r.IsDBNull(1) ? 0 : (int)Convert.ToInt64(r.GetValue(1))));
            }
            total = rows.Count;
            var token = new Regex(Regex.Escape(reference) + @"(?![\w:])", RegexOptions.IgnoreCase);
            var sourceCache = new Dictionary<string, List<KeyValuePair<string[], string>>>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                // /Classes/X/Methods/m, /Forms/F/DataSources/ds/Methods/m, or metadata (SecurityRole/X?Label).
                string path = row.Item1;
                var parts = path.TrimStart('/').Split('/');
                string type = parts.Length > 1 ? XRefQuery.TypeOfFolder(parts[0]) : null;
                if (type == null)
                {
                    if (seen.Add(path)) result.Add(Usage(null, path, new string[0], row.Item2, null, null));
                    continue;
                }
                string name = parts[1];
                var member = parts.Skip(2).Select(p => p == "DataFields" ? "Fields" : p).ToArray();
                if (!seen.Add(type + "/" + name + "/" + string.Join("/", member))) continue;
                if (!sourceCache.TryGetValue(type + "/" + name, out var sources)) sourceCache[type + "/" + name] = sources = CodeSearch.SourcesOf(type, name);
                var source = sources?.FirstOrDefault(s => member.Length >= 2 && s.Key.Length >= 2
                    && string.Equals(s.Key[s.Key.Length - 1], member[member.Length - 1], StringComparison.OrdinalIgnoreCase)
                    && (member.Length == 2 || s.Key.Length < 3 || string.Equals(s.Key[s.Key.Length - 3], member[member.Length - 3], StringComparison.OrdinalIgnoreCase)));
                var lines = source?.Value?.Replace("\r\n", "\n").Split('\n');
                int index = lines == null ? -1 : Array.FindIndex(lines, l => token.IsMatch(l));
                result.Add(Usage(type, name, member, index >= 0 ? index + 1 : row.Item2, index >= 0 ? lines[index].Trim() : null, null));
            }
            return OrderUsages(result).Take(max).ToList();
        }

        private static IEnumerable<Dictionary<string, object>> OrderUsages(IEnumerable<Dictionary<string, object>> usages) =>
            usages
                .OrderBy(u => ((string)u["objectName"]).EndsWith("Test", StringComparison.OrdinalIgnoreCase) || ((string)u["objectName"]).Contains("Test_") ? 1 : 0)
                .ThenBy(u => u["text"] is string t && Raise.IsMatch(t) ? 0 : 1);

        private static Dictionary<string, object> Usage(string objectType, string objectName, string[] memberPath, int line, string text, string model) =>
            new Dictionary<string, object>
            {
                ["objectType"] = objectType, ["objectName"] = objectName, ["memberPath"] = memberPath,
                ["member"] = memberPath.Length >= 2 && memberPath[memberPath.Length - 2] == "Methods" ? memberPath[memberPath.Length - 1] : null,
                ["line"] = line, ["column"] = 0, ["text"] = text, ["model"] = model,
            };

        private static Dictionary<string, object> Fail(string message) => new Dictionary<string, object> { ["success"] = false, ["message"] = message };
    }
}
