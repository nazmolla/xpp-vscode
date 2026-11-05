using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XppVsCode.Server.Analysis
{
    /// <summary>A performance finding in one method; Line is 1-based within the method's source.</summary>
    public sealed class PerformanceFinding
    {
        public string Rule { get; set; }
        public string Severity { get; set; }
        public string Method { get; set; }
        public int Line { get; set; }
        public string Message { get; set; }
        public string Fix { get; set; }
    }

    /// <summary>What the rules need to know about a table (from the metadata).</summary>
    public sealed class TableFacts
    {
        public string Name { get; set; }
        public bool OverridesInsert { get; set; }
        public bool OverridesUpdate { get; set; }
        public bool OverridesDelete { get; set; }
        public bool HasDeleteActions { get; set; }
        public bool DateEffective { get; set; }
        /// <summary>Each index's fields in order (RecId, the surrogate key, is always indexed).</summary>
        public List<string[]> Indexes { get; set; } = new List<string[]>();
    }

    /// <summary>
    /// Static performance rules over X++ method source (no environment needed): queries and row operations in loops,
    /// nested single-row lookups, forupdate without a write, unbalanced transactions, cross-company queries, loops over
    /// every field, and set-based statements that quietly fall back to row-by-row
    /// (learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-data-perf).
    /// </summary>
    public static class PerformanceRules
    {
        private static readonly Regex Token = new Regex(
            @"\bwhile\s+select\b|\bwhile\b|\bfor\b|\bdo\b|\bselect\b|\binsert_recordset\b|\bupdate_recordset\b|\bdelete_from\b|[{};]|\.\s*(insert|update|delete|doInsert|doUpdate|doDelete|write)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Declaration = new Regex(@"\b([A-Za-z_]\w*)\s+([A-Za-z_]\w*)\s*(?=[;,=)])", RegexOptions.Compiled);
        private static readonly HashSet<string> SelectHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "firstonly", "firstonly1", "firstonly10", "firstonly100", "firstonly1000", "firstfast", "forupdate", "nofetch", "crosscompany",
            "forceselectorder", "forcenestedloop", "forceplaceholders", "forceliterals", "pessimisticlock", "optimisticlock",
            "repeatableread", "reverse", "generateonly", "validtimestate",
        };

        /// <summary>Findings for one method. classDeclaration supplies member variable types; tables answers table facts or null.</summary>
        public static List<PerformanceFinding> Analyze(string methodName, string source, string classDeclaration, Func<string, TableFacts> tables)
        {
            var findings = new List<PerformanceFinding>();
            if (string.IsNullOrWhiteSpace(source)) return findings;
            string code = Blank(source);
            var types = VariableTypes(Blank(classDeclaration ?? "") + "\n" + code);

            // Blocks: true for loop bodies. A loop keyword makes the next '{' a loop body; ';' ends a header-less loop.
            var blocks = new Stack<bool>();
            bool pendingLoop = false;
            int loops = 0;
            int skipUntil = -1;
            foreach (Match m in Token.Matches(code))
            {
                if (m.Index < skipUntil) continue;
                string t = m.Value.ToLowerInvariant();
                int line = LineOf(code, m.Index);
                if (t == "{")
                {
                    blocks.Push(pendingLoop);
                    if (pendingLoop) loops++;
                    pendingLoop = false;
                    continue;
                }
                if (t == "}")
                {
                    if (blocks.Count > 0 && blocks.Pop()) loops--;
                    continue;
                }
                if (t == ";")
                {
                    pendingLoop = false;
                    continue;
                }
                if (t.StartsWith("while", StringComparison.Ordinal) && Regex.IsMatch(t, @"^while\s+select$"))
                {
                    string statement = Statement(code, m.Index);
                    if (loops > 0)
                        Add(findings, "QueryInLoop", "Warning", methodName, line,
                            "while select inside a loop: one query per row of the outer loop.",
                            "Join the tables in one while select (join / exists join / notexists join).");
                    CheckSelect(findings, methodName, line, statement, code, types);
                    CheckIndex(findings, methodName, line, statement, types, tables);
                    pendingLoop = true;
                    continue;
                }
                if (t == "while" || t == "for" || t == "do")
                {
                    pendingLoop = true;
                    // for (i = 0; i < n; i++) has semicolons in its header.
                    if (t == "for") skipUntil = MatchingParen(code, m.Index + m.Length);
                    continue;
                }
                if (t == "select")
                {
                    string statement = Statement(code, m.Index);
                    if (loops > 0)
                    {
                        bool single = Regex.IsMatch(statement, @"^select\s+(\w+\s+)*?firstonly", RegexOptions.IgnoreCase);
                        Add(findings, "QueryInLoop", "Warning", methodName, line,
                            single ? "select firstonly inside a loop: one query per row of the loop." : "select inside a loop: one query per row of the loop.",
                            single ? "Join the looked-up table into the outer while select, or use exists join / notexists join when only its existence matters."
                                   : "Move the query out of the loop, or join it into the outer while select.");
                    }
                    CheckSelect(findings, methodName, line, statement, code, types);
                    CheckIndex(findings, methodName, line, statement, types, tables);
                    continue;
                }
                if (t == "insert_recordset" || t == "update_recordset" || t == "delete_from")
                {
                    string statement = Statement(code, m.Index);
                    if (loops > 0)
                        Add(findings, "QueryInLoop", "Warning", methodName, line, t + " inside a loop: one set-based statement per row.",
                            "Widen the statement's where clause (or join) so one statement covers every row.");
                    CheckSetBased(findings, methodName, line, t, statement, code, types, tables);
                    continue;
                }
                if (t.StartsWith(".", StringComparison.Ordinal) && loops > 0)
                {
                    string op = m.Groups[1].Value;
                    string buffer = BufferBefore(code, m.Index);
                    if (buffer != null && types.TryGetValue(buffer, out string type) && tables(type) != null)
                        Add(findings, "RowOperationInLoop", "Info", methodName, line,
                            buffer + "." + op + "() in a loop: one database round trip per row.",
                            op.IndexOf("insert", StringComparison.OrdinalIgnoreCase) >= 0
                                ? "Use insert_recordset, or collect rows in a RecordInsertList / RecordSortedList and insert them at once."
                                : "Use " + (op.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0 ? "delete_from" : "update_recordset") + " when every row gets the same change.");
                }
            }

            int begins = Regex.Matches(code, @"\bttsbegin\b", RegexOptions.IgnoreCase).Count;
            int ends = Regex.Matches(code, @"\btts(commit|abort)\b", RegexOptions.IgnoreCase).Count;
            if (begins != ends && begins > 0)
                Add(findings, "UnbalancedTransaction", "Warning", methodName, LineOf(code, Regex.Match(code, @"\bttsbegin\b", RegexOptions.IgnoreCase).Index),
                    "ttsbegin (" + begins + ") and ttscommit/ttsabort (" + ends + ") don't balance in this method.",
                    "Open and close the transaction in the same method, around the smallest unit of work.");
            return findings;
        }

        private static void CheckSelect(List<PerformanceFinding> findings, string method, int line, string statement, string code, Dictionary<string, string> types)
        {
            var hints = new List<string>();
            var words = Regex.Matches(statement, @"[A-Za-z_]\w*").Cast<Match>().Select(w => w.Value).ToList();
            int i = words.FindIndex(w => w.Equals("select", StringComparison.OrdinalIgnoreCase)) + 1;
            while (i > 0 && i < words.Count && SelectHints.Contains(words[i])) hints.Add(words[i++].ToLowerInvariant());

            if (hints.Contains("crosscompany") && !Regex.IsMatch(statement, @"\bcrosscompany\s*:", RegexOptions.IgnoreCase))
                Add(findings, "CrossCompany", "Info", method, line, "crossCompany query: reads every company's rows.",
                    "Limit it to the companies needed with a container: select crossCompany : companies ...");

            bool fieldList = Regex.IsMatch(statement, @"\bfrom\b", RegexOptions.IgnoreCase);
            if (statement.StartsWith("while", StringComparison.OrdinalIgnoreCase) && !fieldList && i < words.Count)
                Add(findings, "AllFields", "Info", method, line, "while select of " + words[i] + " reads every field of every row.",
                    "List the fields the loop uses: while select Field1, Field2 from " + words[i]);

            if (hints.Contains("forupdate") && i < words.Count && !fieldList)
            {
                string buffer = words[i];
                // find(…, _forUpdate) methods return the buffer for the caller to change.
                bool returned = Regex.IsMatch(code, @"\breturn\s+" + Regex.Escape(buffer) + @"\s*;", RegexOptions.IgnoreCase);
                if (!returned && !Regex.IsMatch(code, @"\b" + Regex.Escape(buffer) + @"\s*\.\s*(update|delete|write|doUpdate|doDelete)\s*\(", RegexOptions.IgnoreCase)
                    && types.ContainsKey(buffer))
                    Add(findings, "ForUpdateWithoutWrite", "Warning", method, line,
                        "select forupdate " + buffer + " but the method never updates or deletes " + buffer + ".",
                        "Drop forupdate: it takes update locks for nothing (or the write happens elsewhere; then select forupdate there).");
            }
        }

        private static void CheckSetBased(List<PerformanceFinding> findings, string method, int line, string keyword, string statement, string code,
            Dictionary<string, string> types, Func<string, TableFacts> tables)
        {
            var target = Regex.Match(statement, @"^\w+\s+(\w+)");
            if (!target.Success || !types.TryGetValue(target.Groups[1].Value, out string type)) return;
            var facts = tables(type);
            if (facts == null) return;
            string buffer = target.Groups[1].Value;
            bool Skips(string what) => Regex.IsMatch(code, @"\b" + Regex.Escape(buffer) + @"\s*\.\s*" + what + @"\s*\(\s*(true)?\s*\)", RegexOptions.IgnoreCase);

            var causes = new List<string>();
            bool overridden = keyword == "insert_recordset" ? facts.OverridesInsert : keyword == "update_recordset" ? facts.OverridesUpdate : facts.OverridesDelete;
            string data = keyword == "insert_recordset" ? "insert" : keyword == "update_recordset" ? "update" : "delete";
            if (overridden && !Skips("skipDataMethods"))
                causes.Add(facts.Name + " overrides " + data + "() (call " + buffer + ".skipDataMethods(true) if its logic may be skipped)");
            if (keyword == "delete_from" && facts.HasDeleteActions && !Skips("skipDeleteActions"))
                causes.Add(facts.Name + " has delete actions (" + buffer + ".skipDeleteActions(true) if they may be skipped)");
            if (facts.DateEffective)
                causes.Add(facts.Name + " is date-effective (ValidTimeState), which cannot be skipped");
            if (causes.Count == 0) return;
            Add(findings, "RowByRowFallback", "Warning", method, line,
                keyword + " on " + facts.Name + " runs row by row: " + string.Join("; ", causes) + ".",
                "Remove the cause where its logic may be skipped. Database log and alerts on the table also force row-by-row (skipDatabaseLog, skipEvents).");
        }

        /// <summary>A where clause comparing fields with == that no index of the table starts with: the query may scan the table.</summary>
        private static void CheckIndex(List<PerformanceFinding> findings, string method, int line, string statement, Dictionary<string, string> types, Func<string, TableFacts> tables)
        {
            int where = statement.IndexOf(" where ", StringComparison.OrdinalIgnoreCase);
            if (where < 0) return;
            string clause = statement.Substring(where);
            foreach (var group in Regex.Matches(clause, @"\b(\w+)\s*\.\s*(\w+)\s*==").Cast<Match>().GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
            {
                if (!types.TryGetValue(group.Key, out string type)) continue;
                var facts = tables(type);
                if (facts == null || facts.Indexes.Count == 0) continue;
                var fields = new HashSet<string>(group.Select(m => m.Groups[2].Value), StringComparer.OrdinalIgnoreCase);
                if (fields.Contains("RecId") || facts.Indexes.Any(ix => ix.Length > 0 && fields.Contains(ix[0]))) continue;
                Add(findings, "NoMatchingIndex", "Info", method, line,
                    "No index of " + facts.Name + " starts with " + string.Join(", ", fields) + " (the fields compared with == here), so the query may scan the table.",
                    "Filter on the leading field of an existing index, or add an index that starts with these fields (consider how often the table is written).");
            }
        }

        /// <summary>Variable name to declared type, from declarations "Type name;" "Type a, b;" and parameters.</summary>
        internal static Dictionary<string, string> VariableTypes(string code)
        {
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Declaration.Matches(code))
            {
                string type = m.Groups[1].Value;
                if (type.Equals("return", StringComparison.OrdinalIgnoreCase) || type.Equals("new", StringComparison.OrdinalIgnoreCase)) continue;
                types[m.Groups[2].Value] = type;
            }
            return types;
        }

        /// <summary>The statement starting at index, up to the first ';' or '{' at the same parenthesis depth.</summary>
        private static string Statement(string code, int index)
        {
            int depth = 0;
            for (int i = index; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth <= 0 && (c == ';' || c == '{')) return Regex.Replace(code.Substring(index, i - index), @"\s+", " ").Trim();
            }
            return Regex.Replace(code.Substring(index), @"\s+", " ").Trim();
        }

        private static int MatchingParen(string code, int from)
        {
            int open = code.IndexOf('(', from);
            if (open < 0) return -1;
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static string BufferBefore(string code, int dotIndex)
        {
            int end = dotIndex;
            while (end > 0 && char.IsWhiteSpace(code[end - 1])) end--;
            int start = end;
            while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_')) start--;
            return start < end ? code.Substring(start, end - start) : null;
        }

        private static int LineOf(string code, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < code.Length; i++) if (code[i] == '\n') line++;
            return line;
        }

        private static void Add(List<PerformanceFinding> findings, string rule, string severity, string method, int line, string message, string fix)
        {
            if (findings.Any(f => f.Rule == rule && f.Line == line)) return;
            findings.Add(new PerformanceFinding { Rule = rule, Severity = severity, Method = method, Line = line, Message = message, Fix = fix });
        }

        /// <summary>Source with comments, strings and labels blanked (same length and line breaks), so their text never matches.</summary>
        internal static string Blank(string source)
        {
            var sb = new StringBuilder(source);
            int i = 0;
            while (i < sb.Length)
            {
                char c = sb[i], next = i + 1 < sb.Length ? sb[i + 1] : '\0';
                if (c == '/' && next == '/')
                    while (i < sb.Length && sb[i] != '\n') sb[i++] = ' ';
                else if (c == '/' && next == '*')
                {
                    while (i < sb.Length && !(sb[i] == '*' && i + 1 < sb.Length && sb[i + 1] == '/')) { if (sb[i] != '\n') sb[i] = ' '; i++; }
                    for (int k = 0; k < 2 && i < sb.Length; k++) sb[i++] = ' ';
                }
                else if (c == '"' || c == '\'')
                {
                    char quote = c;
                    bool verbatim = i > 0 && sb[i - 1] == '@';
                    i++;
                    while (i < sb.Length && sb[i] != quote)
                    {
                        if (!verbatim && sb[i] == '\\' && i + 1 < sb.Length) { sb[i++] = ' '; }
                        if (sb[i] != '\n') sb[i] = ' ';
                        i++;
                    }
                    i++;
                }
                else i++;
            }
            return sb.ToString();
        }
    }
}
