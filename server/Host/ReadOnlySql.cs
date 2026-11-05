using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace XppVsCode.Server.Host
{
    /// <summary>
    /// Decides whether a SQL batch only reads. A first line of defence for the read-only query panel and the agent tool;
    /// the data service also runs read-only batches in a transaction it always rolls back.
    /// </summary>
    internal static class ReadOnlySql
    {
        private static readonly HashSet<string> WriteKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "EXEC", "EXECUTE", "SP_EXECUTESQL",
            "GRANT", "REVOKE", "DENY", "INTO", "BACKUP", "RESTORE", "DBCC", "KILL", "SHUTDOWN", "RECONFIGURE", "BULK",
            "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML", "WRITETEXT", "UPDATETEXT", "COMMIT", "ROLLBACK", "SAVE",
            "BEGIN", "USE", "SET", "DECLARE", "WAITFOR", "CHECKPOINT", "SETUSER", "REVERT",
        };

        private static readonly Regex Word = new Regex(@"[A-Za-z_][A-Za-z0-9_@$#]*", RegexOptions.Compiled);

        /// <summary>Null when the batch is a single SELECT (or WITH … SELECT); otherwise why it is refused.</summary>
        public static string Refusal(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return "The query is empty.";
            string code = StripLiteralsAndComments(sql, out string problem);
            if (problem != null) return problem;
            string trimmed = code.Trim().TrimEnd(';').Trim();
            if (trimmed.Contains(";")) return "Run one statement at a time.";
            var first = Word.Match(trimmed);
            if (!first.Success || !(first.Value.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Value.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
                return "Only SELECT queries run in read-only mode.";
            foreach (Match m in Word.Matches(trimmed))
                if (WriteKeywords.Contains(m.Value))
                    return "'" + m.Value.ToUpperInvariant() + "' is not allowed in read-only mode.";
            return null;
        }

        /// <summary>The batch with string literals, quoted identifiers and comments blanked out, so keywords inside them don't count.</summary>
        internal static string StripLiteralsAndComments(string sql, out string problem)
        {
            problem = null;
            var sb = new StringBuilder(sql.Length);
            int i = 0;
            while (i < sql.Length)
            {
                char c = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
                if (c == '-' && next == '-')
                {
                    while (i < sql.Length && sql[i] != '\n') i++;
                    sb.Append(' ');
                }
                else if (c == '/' && next == '*')
                {
                    // T-SQL block comments nest.
                    int depth = 0;
                    do
                    {
                        if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                        else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    } while (depth > 0 && i < sql.Length);
                    if (depth > 0) { problem = "A comment is not closed."; return ""; }
                    sb.Append(' ');
                }
                else if (c == '\'' || c == '"' || c == '[')
                {
                    char close = c == '[' ? ']' : c;
                    i++;
                    bool closed = false;
                    while (i < sql.Length)
                    {
                        if (sql[i] == close)
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                            i++;
                            closed = true;
                            break;
                        }
                        i++;
                    }
                    if (!closed) { problem = c == '\'' ? "A string is not closed." : "A quoted name is not closed."; return ""; }
                    sb.Append(c == '\'' ? " '' " : " x ");
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }
    }
}
