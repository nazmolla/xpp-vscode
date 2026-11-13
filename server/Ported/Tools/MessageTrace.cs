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
        // Label references by their exact text (short texts too), per language: field labels such as "Site".
        private static readonly Dictionary<string, Dictionary<string, List<string>>> TextIndexes =
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object IndexLock = new object();
        private static readonly Regex Placeholder = new Regex(@"%\d", RegexOptions.Compiled);

        private static string NormalText(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim().TrimEnd('.', ' ');

        /// <summary>Every label text of one language from all packages (custom first), built once per server.</summary>
        private static List<LabelEntry> LabelIndex(string language)
        {
            lock (IndexLock)
            {
                if (Indexes.TryGetValue(language, out var cached)) return cached;
                var list = new List<LabelEntry>();
                var byText = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
                                string reference = id.StartsWith("@", StringComparison.Ordinal) ? id : "@" + file + ":" + id;
                                string normal = NormalText(text);
                                if (normal.Length > 0)
                                {
                                    if (!byText.TryGetValue(normal, out var refs)) byText[normal] = refs = new List<string>();
                                    refs.Add(reference);
                                }
                                var pieces = Placeholder.Split(text).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                                int literal = pieces.Sum(p => p.Length);
                                if (literal < 4) continue;
                                list.Add(new LabelEntry
                                {
                                    Reference = reference,
                                    File = file, Text = text, Pieces = pieces, LiteralLength = literal,
                                });
                            }
                        }
                        catch (Exception ex) { McpLogger.Log("Reading labels of " + file + " failed: " + ex.Message); }
                    }
                }
                Indexes[language] = list;
                TextIndexes[language] = byText;
                return list;
            }
        }

        /// <summary>Labels whose whole text is the given text (any file), e.g. every "Customer group" label.</summary>
        private static List<string> LabelsWithText(string text, string language)
        {
            LabelIndex(language);
            lock (IndexLock)
                return TextIndexes[language].TryGetValue(NormalText(text), out var refs) ? refs.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
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
            Dictionary<string, object> mandatory = null;
            Func<string, bool> related = null;
            try
            {
                // "Field %1 must be filled in." is mostly the platform's own check of fields whose Mandatory property is Yes:
                // the field and its properties explain it, not the code that reuses the text for other fields.
                string fieldLabel = labels.Any(m => m.Whole) ? MandatoryFieldLabel(message, language) : null;
                if (fieldLabel != null && conn != null)
                {
                    var found = FindMandatoryFields(conn, fieldLabel, language);
                    mandatory = found.Result;
                    var words = found.FieldNames.Concat(found.LabelIds).Where(w => w.Length > 1).Select(Regex.Escape).ToList();
                    var rx = words.Count == 0 ? null
                        : new Regex(@"(?<![\w@:])(" + string.Join("|", words) + @")(?![\w:])|" + Regex.Escape("\"" + fieldLabel + "\""), RegexOptions.IgnoreCase);
                    related = context => rx != null && context != null && rx.IsMatch(context);
                }

                // The same text often exists in several label files (copies per module): one result per text, usages merged.
                foreach (var group in labels.GroupBy(m => m.Label.Text.Trim(), StringComparer.OrdinalIgnoreCase).Take(maxLabels * 2))
                {
                    var usages = new List<Dictionary<string, object>>();
                    var ids = group.Select(m => m.Label.Reference).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (conn != null)
                        foreach (string id in ids)
                            usages.AddRange(Usages(conn, id, maxUsages, out _, related).Select(u => { u["label"] = id; return u; }));
                    var m0 = group.First();
                    labelResults.Add(new Dictionary<string, object>
                    {
                        ["label"] = string.Join(", ", ids), ["labels"] = ids, ["text"] = m0.Label.Text, ["file"] = m0.Label.File,
                        ["coverage"] = Math.Round(m0.Coverage, 2), ["whole"] = m0.Whole,
                        ["usages"] = OrderUsages(usages).Take(maxUsages).ToList(), ["usageCount"] = conn == null ? -1 : usages.Count,
                        ["relatedCount"] = related == null ? -1 : usages.Count(u => u["related"] is bool b && b),
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
            bool labelUsed = mandatory != null || labelResults.Any(l => ((List<Dictionary<string, object>>)l["usages"]).Count > 0);
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
                ["labels"] = labelResults, ["literal"] = literal, ["mandatory"] = mandatory, ["xrefProblem"] = problem, ["ms"] = watch.ElapsedMilliseconds,
            };
        }

        /// <summary>The platform's message for an empty mandatory field: Field %1 must be filled in.</summary>
        internal const string PlatformMandatoryLabel = "@SYS26332";

        /// <summary>The field label in a platform mandatory-field message ("Customer group"), or null for other messages.</summary>
        private static string MandatoryFieldLabel(string message, string language)
        {
            var platform = LabelIndex(language).FirstOrDefault(l => l.Reference.Equals(PlatformMandatoryLabel, StringComparison.OrdinalIgnoreCase));
            if (platform == null) return null;
            string pattern = "^" + Regex.Replace(Regex.Escape(NormalText(platform.Text)), @"%\d", m => m.Value == "%1" ? "(?<v>.+?)" : ".+?") + "$";
            var match = Regex.Match(NormalText(message), pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string value = match.Groups["v"].Value.Trim().Trim('\'', '"', '\u2018', '\u2019', '\u201C', '\u201D').Trim();
            return value.Length > 0 ? value : null;
        }

        private sealed class MandatorySearch
        {
            public Dictionary<string, object> Result;
            public HashSet<string> FieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> LabelIds = new List<string>();
        }

        private sealed class FieldRef
        {
            public string Table, Field, Extension;   // Extension: the table extension that adds the field
        }

        /// <summary>Table fields labelled with the field label (their own label or their EDT's, inherited EDTs included) whose
        /// Mandatory property is Yes on the table or in a table extension.</summary>
        private static MandatorySearch FindMandatoryFields(SqlConnection conn, string fieldLabel, string language)
        {
            var search = new MandatorySearch();
            search.LabelIds.AddRange(LabelsWithText(fieldLabel, language).Take(80));
            var labelTargets = new HashSet<string>(search.LabelIds.Select(id => "/Labels/" + id), StringComparer.OrdinalIgnoreCase);
            var fields = new Dictionary<string, FieldRef>(StringComparer.OrdinalIgnoreCase);   // metadata path of the field
            var edts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in Refs(conn, labelTargets, "%?Label", true))
            {
                string source = row.Key.Substring(0, row.Key.Length - "?Label".Length);
                var field = ParseField(source);
                if (field != null) fields[source] = field;
                else if (source.StartsWith("Edt", StringComparison.Ordinal) && source.Split('/').Length == 2) edts.Add(source);
            }
            // EDTs extending a labelled EDT inherit its label unless they set their own.
            var level = edts.ToList();
            for (int depth = 0; depth < 4 && level.Count > 0 && edts.Count < 300; depth++)
            {
                var children = Refs(conn, level, "Edt%?Extends", true).Select(r => r.Key.Substring(0, r.Key.Length - "?Extends".Length))
                    .Where(c => c.Split('/').Length == 2 && !edts.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var relabelled = OwnLabels(conn, children, labelTargets);
                level = children.Where(c => !relabelled.Contains(c)).ToList();
                edts.UnionWith(level);
            }
            var viaEdt = Refs(conn, edts, "%?ExtendedDataType", true)
                .Select(r => r.Key.Substring(0, r.Key.Length - "?ExtendedDataType".Length))
                .Where(s => !fields.ContainsKey(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(800).ToList();
            var ownLabel = OwnLabels(conn, viaEdt, labelTargets);
            foreach (string source in viaEdt.Where(s => !ownLabel.Contains(s)))
            {
                var field = ParseField(source);
                if (field != null) fields[source] = field;
            }
            foreach (var f in fields.Values) search.FieldNames.Add(f.Field);

            var mandatoryFields = MandatoryOf(fields.Values.ToList());
            // The tables users work in first: most referenced, staging (data entity) and temporary tables last.
            var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tablePaths = mandatoryFields.Select(f => "/Tables/" + f["table"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tablePaths.Count > 0)
                using (var cmd = new SqlCommand { Connection = conn, CommandTimeout = 60 })
                {
                    var names = tablePaths.Take(500).Select((p, i) => { cmd.Parameters.AddWithValue("@p" + i, p); return "@p" + i; }).ToList();
                    cmd.CommandText = "SELECT t.Path, COUNT(*) FROM Names t JOIN [References] r ON r.TargetId = t.Id WHERE t.Path IN (" + string.Join(",", names) + ") GROUP BY t.Path";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) uses[r.GetString(0).Substring("/Tables/".Length)] = r.GetInt32(1);
                }
            bool Secondary(string table) => Regex.IsMatch(table, @"(Staging|Tmp|Temp)$", RegexOptions.IgnoreCase);
            mandatoryFields = mandatoryFields
                .OrderBy(f => Secondary((string)f["table"]) ? 1 : 0)
                .ThenByDescending(f => uses.TryGetValue((string)f["table"], out int n) ? n : 0)
                .ThenBy(f => (string)f["table"], StringComparer.OrdinalIgnoreCase).ToList();
            search.Result = new Dictionary<string, object>
            {
                ["field"] = fieldLabel, ["platformLabel"] = PlatformMandatoryLabel, ["labels"] = search.LabelIds.Take(10).ToList(),
                ["edts"] = edts.Select(e => e.Substring(e.IndexOf('/') + 1)).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).Take(20).ToList(),
                ["fields"] = mandatoryFields, ["labelledFields"] = fields.Count,
            };
            return search;
        }

        /// <summary>Table/T/TableFieldString/F or TableExtension/T.Ext/TableFieldString/F, else null.</summary>
        private static FieldRef ParseField(string path)
        {
            var parts = path.Split('/');
            if (parts.Length != 4 || !parts[2].StartsWith("TableField", StringComparison.Ordinal) || parts[2].StartsWith("TableFieldGroup", StringComparison.Ordinal)) return null;
            if (parts[0] == "Table") return new FieldRef { Table = parts[1], Field = parts[3] };
            if (parts[0] == "TableExtension" && parts[1].IndexOf('.') > 0)
                return new FieldRef { Table = parts[1].Substring(0, parts[1].IndexOf('.')), Field = parts[3], Extension = parts[1] };
            return null;
        }

        /// <summary>Elements (metadata paths) among the given ones that have a label of their own outside the given labels.</summary>
        private static HashSet<string> OwnLabels(SqlConnection conn, IEnumerable<string> paths, HashSet<string> labelTargets) =>
            new HashSet<string>(Refs(conn, paths.Select(p => p + "?Label").ToList(), null, false)
                .Where(r => !labelTargets.Contains(r.Value)).Select(r => r.Key.Substring(0, r.Key.Length - "?Label".Length)), StringComparer.OrdinalIgnoreCase);

        /// <summary>(source path, target path) rows of the cross-reference database whose target (byTarget) or source is one of
        /// the given paths, optionally filtered by a LIKE pattern on the source.</summary>
        private static List<KeyValuePair<string, string>> Refs(SqlConnection conn, ICollection<string> paths, string sourceLike, bool byTarget)
        {
            var rows = new List<KeyValuePair<string, string>>();
            var all = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int start = 0; start < all.Count; start += 500)
            {
                using (var cmd = new SqlCommand { Connection = conn, CommandTimeout = 60 })
                {
                    var names = all.Skip(start).Take(500).Select((p, i) => { cmd.Parameters.AddWithValue("@p" + i, p); return "@p" + i; }).ToList();
                    cmd.CommandText = "SELECT TOP 5000 s.Path, t.Path FROM Names t JOIN [References] r ON r.TargetId = t.Id JOIN Names s ON s.Id = r.SourceId WHERE "
                        + (byTarget ? "t" : "s") + ".Path IN (" + string.Join(",", names) + ")" + (sourceLike != null ? " AND s.Path LIKE @like" : "");
                    if (sourceLike != null) cmd.Parameters.AddWithValue("@like", sourceLike);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) rows.Add(new KeyValuePair<string, string>(r.GetString(0), r.GetString(1)));
                }
            }
            return rows;
        }

        /// <summary>The fields whose Mandatory property is Yes in their table or in one of its extensions (read from the
        /// metadata files; the cross-reference database has no properties).</summary>
        private static List<Dictionary<string, object>> MandatoryOf(List<FieldRef> fields)
        {
            var result = new List<Dictionary<string, object>>();
            if (fields.Count == 0) return result;
            var files = TableFiles();
            foreach (var table in fields.GroupBy(f => f.Table, StringComparer.OrdinalIgnoreCase).Take(400))
            {
                if (!files.TryGetValue(table.Key, out var tableFiles)) continue;
                var names = new HashSet<string>(table.Select(f => f.Field), StringComparer.OrdinalIgnoreCase);
                foreach (var file in tableFiles)
                {
                    System.Xml.Linq.XDocument doc;
                    try { doc = System.Xml.Linq.XDocument.Load(file.Path); } catch { continue; }
                    foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "AxTableField" || e.Name.LocalName == "AxTableFieldModification"))
                    {
                        string name = element.Elements().FirstOrDefault(c => c.Name.LocalName == "Name")?.Value;
                        string mandatoryValue = element.Elements().FirstOrDefault(c => c.Name.LocalName == "Mandatory")?.Value;
                        if (name == null || !names.Contains(name) || !"Yes".Equals(mandatoryValue, StringComparison.OrdinalIgnoreCase)) continue;
                        bool added = element.Name.LocalName == "AxTableField";
                        result.Add(new Dictionary<string, object>
                        {
                            ["table"] = table.Key, ["field"] = name, ["model"] = file.Model,
                            ["objectType"] = file.Extension == null ? "AxTable" : "AxTableExtension", ["objectName"] = file.Extension ?? table.Key,
                            ["setBy"] = file.Extension == null ? "table" : added ? "field of extension " + file.Extension : "extension " + file.Extension,
                        });
                    }
                }
            }
            return result.OrderBy(f => (string)f["table"], StringComparer.OrdinalIgnoreCase).ThenBy(f => (string)f["field"], StringComparer.OrdinalIgnoreCase).ToList();
        }

        private sealed class TableFile { public string Path, Model, Extension; }

        private static Dictionary<string, List<TableFile>> _tableFiles;
        private static DateTime _tableFilesAt;

        /// <summary>Table and table extension files by table name (refreshed every few minutes for new custom elements).</summary>
        private static Dictionary<string, List<TableFile>> TableFiles()
        {
            lock (IndexLock)
            {
                if (_tableFiles != null && DateTime.UtcNow - _tableFilesAt < TimeSpan.FromMinutes(5)) return _tableFiles;
                var map = new Dictionary<string, List<TableFile>>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in CodeSearch.Folders(new[] { "AxTable", "AxTableExtension" }, null))
                {
                    bool extension = Path.GetFileName(folder.Key).Equals("AxTableExtension", StringComparison.OrdinalIgnoreCase);
                    IEnumerable<string> paths;
                    try { paths = Directory.EnumerateFiles(folder.Key, "*.xml").ToList(); } catch { continue; }
                    foreach (string path in paths)
                    {
                        string name = Path.GetFileNameWithoutExtension(path);
                        string table = extension && name.IndexOf('.') > 0 ? name.Substring(0, name.IndexOf('.')) : name;
                        if (!map.TryGetValue(table, out var list)) map[table] = list = new List<TableFile>();
                        // Custom folders come first: a table in several roots counts once.
                        if (!extension && list.Any(f => f.Extension == null)) continue;
                        list.Add(new TableFile { Path = path, Model = folder.Value, Extension = extension ? name : null });
                    }
                }
                _tableFiles = map;
                _tableFilesAt = DateTime.UtcNow;
                return map;
            }
        }

        private static readonly Regex Raise = new Regex(@"\b(throw|error|warning|checkFailed|info|Box::\w+|addMessage|Message::)\b", RegexOptions.IgnoreCase);

        /// <summary>Places that use a label (cross-reference database), with the code line; raising code first, tests last.</summary>
        private static List<Dictionary<string, object>> Usages(SqlConnection conn, string reference, int max, out int total, Func<string, bool> related = null)
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
                    if (seen.Add(path))
                    {
                        var meta = Usage(null, path, new string[0], row.Item2, null, null);
                        if (related != null) meta["related"] = related(path);
                        result.Add(meta);
                    }
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
                var usage = Usage(type, name, member, index >= 0 ? index + 1 : row.Item2, index >= 0 ? lines[index].Trim() : null, null);
                // The statement around the label names the field it reports (often split over the next lines).
                if (related != null)
                    usage["related"] = index >= 0 && related(string.Join("\n", lines.Skip(Math.Max(0, index - 1)).Take(4)));
                result.Add(usage);
            }
            return OrderUsages(result).Take(max).ToList();
        }

        private static IEnumerable<Dictionary<string, object>> OrderUsages(IEnumerable<Dictionary<string, object>> usages) =>
            usages
                .OrderBy(u => u.TryGetValue("related", out object r) && r is bool b && !b ? 1 : 0)
                .ThenBy(u => ((string)u["objectName"]).EndsWith("Test", StringComparison.OrdinalIgnoreCase) || ((string)u["objectName"]).Contains("Test_") ? 1 : 0)
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
