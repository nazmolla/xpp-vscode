using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Upgrade readiness of a package: every place its code uses a class, table or method marked [SysObsolete], from the
    /// cross-reference database (rebuilt by each build with cross references), with the attribute's message.
    /// </summary>
    internal sealed class DeprecationReport
    {
        private const int MaxRows = 500;
        private readonly XRefQuery _xref;
        private readonly CodeExamples _examples;

        public DeprecationReport(XRefQuery xref, CodeExamples examples)
        {
            _xref = xref;
            _examples = examples;
        }

        public string Report(string package)
        {
            if (string.IsNullOrWhiteSpace(package)) return "Pass modelName (or open a project).";
            var obsolete = _examples.Obsolete();
            if (!(obsolete["success"] is bool ok && ok)) return obsolete.TryGetValue("message", out object m) ? m as string : "The obsolete API list is not available.";
            var messages = ((List<Dictionary<string, object>>)obsolete["items"])
                .GroupBy(i => i["path"] as string, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var uses = new List<(string Source, int Line, string Target)>();
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null) return "The cross-reference database is not available: " + problem;
                using (var cmd = new SqlCommand(@"
WITH obs AS (
  SELECT DISTINCT s.Path FROM [References] r JOIN Names t ON t.Id = r.TargetId JOIN Names s ON s.Id = r.SourceId
  WHERE t.Path IN ('/Classes/SysObsoleteAttribute', '/Classes/SysObsolete') AND r.Kind = 7)
SELECT TOP (@max) src.Path, r.Line, tgt.Path
FROM [References] r
JOIN Names tgt ON tgt.Id = r.TargetId
JOIN obs ON obs.Path = tgt.Path
JOIN Names src ON src.Id = r.SourceId
JOIN Modules m ON m.Id = src.ModuleId
WHERE m.Module = @package AND r.Kind <> 7
ORDER BY src.Path, r.Line", conn) { CommandTimeout = 120 })
                {
                    cmd.Parameters.AddWithValue("@max", MaxRows * 4);
                    cmd.Parameters.AddWithValue("@package", package.Trim());
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                            uses.Add((reader.GetString(0), reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)), reader.GetString(2)));
                }
            }
            // An obsolete element's own code refers to itself; that is not a use to replace.
            uses = uses.Where(u => !Owner(u.Source).Equals(Owner(u.Target), StringComparison.OrdinalIgnoreCase)).Distinct().Take(MaxRows + 1).ToList();

            var sb = new StringBuilder();
            bool more = uses.Count > MaxRows;
            if (more) uses.RemoveAt(MaxRows);
            int errors = uses.Count(u => messages.TryGetValue(u.Target, out var i) && i["isError"] is bool e && e);
            sb.Append("Package ").Append(package).Append(": ").Append(uses.Count).Append(more ? "+" : "").Append(" use(s) of obsolete APIs, ")
              .Append(errors).Append(" of them marked as errors (they stop compiling in a later version).");
            if (uses.Count == 0)
                sb.Append(" Nothing to change. The result is as current as the last build with cross references.");
            foreach (var use in uses)
            {
                messages.TryGetValue(use.Target, out var info);
                bool isError = info != null && info["isError"] is bool e && e;
                string message = info?["message"] as string;
                sb.Append("\n- ").Append(isError ? "[error] " : "").Append(Short(use.Source)).Append(" line ").Append(use.Line)
                  .Append(" uses ").Append(Short(use.Target)).Append(string.IsNullOrEmpty(message) ? "" : ": " + message);
            }
            return sb.ToString();
        }

        private static string Owner(string path) => string.Join("/", (path ?? "").TrimStart('/').Split('/').Take(2));

        private static string Short(string path)
        {
            var parts = (path ?? "").TrimStart('/').Split('/');
            return parts.Length >= 4 && parts[2] == "Methods" ? parts[1] + "." + parts[3] : parts.Length >= 2 ? parts[1] : path;
        }
    }
}
