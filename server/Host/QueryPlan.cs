using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace XppVsCode.Server.Host
{
    /// <summary>Plain-language summary of a SQL Server showplan: costliest operators, scans, missing indexes and warnings.</summary>
    internal static class QueryPlan
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

        public static Dictionary<string, object> Summarize(string xml)
        {
            var doc = XDocument.Parse(xml);
            var statement = doc.Descendants(Ns + "StmtSimple").FirstOrDefault();
            double total = Number(statement?.Attribute("StatementSubTreeCost")?.Value);
            var ops = doc.Descendants(Ns + "RelOp").Select(op => new
            {
                Physical = (string)op.Attribute("PhysicalOp"),
                Logical = (string)op.Attribute("LogicalOp"),
                Cost = Number(op.Attribute("EstimatedTotalSubtreeCost")?.Value)
                    - op.Elements().SelectMany(e => e.Elements(Ns + "RelOp")).Sum(c => Number(c.Attribute("EstimatedTotalSubtreeCost")?.Value)),
                Rows = Number(op.Attribute("EstimateRows")?.Value),
                Object = op.Descendants(Ns + "Object").Select(o => ((string)o.Attribute("Table"))?.Trim('[', ']') + (o.Attribute("Index") != null ? "." + ((string)o.Attribute("Index")).Trim('[', ']') : "")).FirstOrDefault(),
            }).ToList();

            var notes = new List<string>();
            foreach (var op in ops.OrderByDescending(o => o.Cost).Take(5))
                notes.Add($"{op.Physical}{(op.Logical != op.Physical ? " (" + op.Logical + ")" : "")}{(op.Object != null ? " on " + op.Object : "")}: "
                    + $"{Percent(op.Cost, total)} of the cost, about {op.Rows.ToString("N0", CultureInfo.InvariantCulture)} row(s).");
            foreach (var scan in ops.Where(o => (o.Physical == "Table Scan" || o.Physical == "Clustered Index Scan" || o.Physical == "Index Scan") && o.Rows > 1000))
                notes.Add($"{scan.Physical} on {scan.Object} reads about {scan.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows: an index on the filtered columns may avoid it.");

            var missing = doc.Descendants(Ns + "MissingIndexGroup").Select(g =>
            {
                var index = g.Element(Ns + "MissingIndex");
                string Columns(string usage) => string.Join(", ", index?.Elements(Ns + "ColumnGroup").Where(c => (string)c.Attribute("Usage") == usage)
                    .SelectMany(c => c.Elements(Ns + "Column")).Select(c => ((string)c.Attribute("Name")).Trim('[', ']')) ?? Enumerable.Empty<string>());
                return $"Missing index on {((string)index?.Attribute("Table"))?.Trim('[', ']')} (impact {Number(g.Attribute("Impact")?.Value):0}%): "
                    + $"equality [{Columns("EQUALITY")}], inequality [{Columns("INEQUALITY")}], include [{Columns("INCLUDE")}]. In F&O, add it as a table index in your model, not in SQL.";
            }).ToList();

            var warnings = doc.Descendants(Ns + "Warnings").SelectMany(w => w.Elements()).Select(w =>
                w.Name.LocalName == "PlanAffectingConvert" ? "Implicit conversion: " + (string)w.Attribute("Expression") + " (compare with values of the column's type)."
                : w.Name.LocalName == "NoJoinPredicate" ? "A join without a join condition (cross join)."
                : w.Name.LocalName + ".").Distinct().ToList();

            return new Dictionary<string, object>
            {
                ["estimatedCost"] = Math.Round(total, 4),
                ["operators"] = ops.Count,
                ["notes"] = notes,
                ["missingIndexes"] = missing,
                ["warnings"] = warnings,
            };
        }

        private static string Percent(double part, double total) => total > 0 ? (part / total).ToString("P0", CultureInfo.InvariantCulture) : "n/a";

        private static double Number(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
    }
}
