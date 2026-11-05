using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace XppVsCode.Server.Host
{
    /// <summary>
    /// Business data for the extension's data browser and query panel (xpp/data): the local AxDB of a cloud-hosted or
    /// VHD environment with Windows authentication, or a unified environment's Azure SQL database with just-in-time
    /// credentials. Credentials arrive with each request and are never stored or logged here.
    /// </summary>
    internal sealed class DataQueryService
    {
        private const int DefaultRows = 200;
        private const int MaxRows = 10000;
        private const int MaxCellChars = 4000;
        private const int MaxResultSets = 5;
        private const int DefaultTimeoutSeconds = 60;
        private const int MaxTimeoutSeconds = 600;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly ConcurrentDictionary<string, SqlCommand> _running = new ConcurrentDictionary<string, SqlCommand>();

        public string Handle(string action, string body)
        {
            try
            {
                var request = _json.Deserialize<Dictionary<string, object>>(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new Dictionary<string, object>();
                switch (action)
                {
                    case "test": return Serialize(Test(request));
                    case "browse": return Serialize(Browse(request));
                    case "query": return Serialize(Query(request));
                    case "plan": return Serialize(Plan(request));
                    case "cancel": return Serialize(Cancel(request));
                    default: return Failure("Unknown data action: " + action);
                }
            }
            catch (SqlException ex)
            {
                return Failure(ex.Message, ex.Number);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
            {
                return Failure(ex.Message);
            }
        }

        private Dictionary<string, object> Test(Dictionary<string, object> request)
        {
            using (var conn = Open(request))
            using (var cmd = new SqlCommand("SELECT SUSER_SNAME(), DB_NAME(), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64))", conn))
            using (var reader = cmd.ExecuteReader())
            {
                reader.Read();
                return Success(new Dictionary<string, object>
                {
                    ["user"] = reader.GetValue(0) as string,
                    ["database"] = reader.GetValue(1) as string,
                    ["version"] = reader.GetValue(2) as string,
                });
            }
        }

        /// <summary>Rows of one table: the statement is built here from validated names, values are parameters.</summary>
        private Dictionary<string, object> Browse(Dictionary<string, object> request)
        {
            string table = Text(request, "table");
            using (var conn = Open(request))
            using (var cmd = new SqlCommand())
            {
                var columns = TableColumns(conn, table);
                if (columns.Count == 0)
                    return Fail("Table " + table + " is not in the database. Is it synchronized (or a temporary table)?");
                string Column(string name) => columns.Keys.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("Table " + table + " has no column " + name + ".");

                var selected = (List(request, "columns") ?? new List<string>()).Select(Column).ToList();
                var where = new List<string>();
                int p = 0;
                string company = Text(request, "company");
                if (!string.IsNullOrEmpty(company) && columns.ContainsKey("DATAAREAID"))
                {
                    where.Add("[DATAAREAID] = @p" + p);
                    cmd.Parameters.AddWithValue("@p" + p++, company.ToLowerInvariant());
                }
                // Date-effective tables: the rows valid on that date, as X++'s validtimestate(date) selects them.
                string asOf = Text(request, "asOf");
                if (!string.IsNullOrEmpty(asOf) && columns.ContainsKey("VALIDFROM") && columns.ContainsKey("VALIDTO"))
                {
                    if (!DateTime.TryParse(asOf, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
                        throw new ArgumentException("As-of date '" + asOf + "' is not a date.");
                    where.Add("[VALIDFROM] <= @p" + p + " AND [VALIDTO] >= @p" + p);
                    cmd.Parameters.AddWithValue("@p" + p++, date);
                }
                foreach (var filter in Objects(request, "filters"))
                {
                    string column = Column(Text(filter, "column"));
                    string op = Text(filter, "operator") ?? "=";
                    string value = Text(filter, "value") ?? "";
                    switch (op)
                    {
                        case "=": case "<>": case "<": case "<=": case ">": case ">=":
                            where.Add(Quote(column) + " " + op + " @p" + p);
                            cmd.Parameters.AddWithValue("@p" + p++, value);
                            break;
                        case "contains":
                            where.Add(Quote(column) + " LIKE @p" + p + " ESCAPE '\\'");
                            cmd.Parameters.AddWithValue("@p" + p++, "%" + EscapeLike(value) + "%");
                            break;
                        case "startsWith":
                            where.Add(Quote(column) + " LIKE @p" + p + " ESCAPE '\\'");
                            cmd.Parameters.AddWithValue("@p" + p++, EscapeLike(value) + "%");
                            break;
                        default:
                            throw new ArgumentException("Unknown filter operator " + op + ".");
                    }
                }
                string orderBy = Text(request, "orderBy");
                int top = Rows(request);
                cmd.CommandText = "SELECT TOP (" + (top + 1).ToString(CultureInfo.InvariantCulture) + ") "
                    + (selected.Count > 0 ? string.Join(", ", selected.Select(Quote)) : "*")
                    + " FROM [dbo]." + Quote(table.Trim())
                    + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                    + (string.IsNullOrEmpty(orderBy) ? "" : " ORDER BY " + Quote(Column(orderBy)) + (Flag(request, "descending") ? " DESC" : ""));
                cmd.Connection = conn;
                return Execute(cmd, request, top, readOnly: true);
            }
        }

        private Dictionary<string, object> Query(Dictionary<string, object> request)
        {
            string sql = Text(request, "sql");
            bool readOnly = !Flag(request, "allowWrite");
            if (readOnly)
            {
                string refusal = ReadOnlySql.Refusal(sql);
                if (refusal != null) return Fail(refusal);
            }
            else if (string.IsNullOrWhiteSpace(sql))
                return Fail("The query is empty.");
            using (var conn = Open(request))
            using (var cmd = new SqlCommand(sql, conn))
                return Execute(cmd, request, Rows(request), readOnly);
        }

        /// <summary>The estimated plan of a SELECT (SHOWPLAN: the query is compiled, not run), summarized.</summary>
        private Dictionary<string, object> Plan(Dictionary<string, object> request)
        {
            string sql = Text(request, "sql");
            string refusal = ReadOnlySql.Refusal(sql);
            if (refusal != null) return Fail(refusal);
            string xml;
            using (var conn = Open(request))
            {
                using (var on = new SqlCommand("SET SHOWPLAN_XML ON", conn)) on.ExecuteNonQuery();
                try
                {
                    using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
                        xml = cmd.ExecuteScalar() as string;
                }
                finally
                {
                    using (var off = new SqlCommand("SET SHOWPLAN_XML OFF", conn)) off.ExecuteNonQuery();
                }
            }
            if (string.IsNullOrEmpty(xml)) return Fail("The server returned no plan.");
            var summary = QueryPlan.Summarize(xml);
            summary["xml"] = xml.Length > 200000 ? null : xml;
            return Success(summary);
        }

        private Dictionary<string, object> Cancel(Dictionary<string, object> request)
        {
            string id = Text(request, "queryId");
            if (id != null && _running.TryGetValue(id, out var cmd))
                cmd.Cancel();
            return Success(new Dictionary<string, object>());
        }

        /// <summary>Runs a command in a transaction: rolled back for read-only work, committed only for allowed writes.</summary>
        private Dictionary<string, object> Execute(SqlCommand cmd, Dictionary<string, object> request, int maxRows, bool readOnly)
        {
            string queryId = Text(request, "queryId");
            cmd.CommandTimeout = Math.Max(1, Math.Min(MaxTimeoutSeconds, Int(request, "timeoutSeconds") ?? DefaultTimeoutSeconds));
            var messages = new List<string>();
            cmd.Connection.InfoMessage += (s, e) => { if (messages.Count < 50) messages.Add(e.Message); };
            var watch = Stopwatch.StartNew();
            using (var transaction = cmd.Connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                cmd.Transaction = transaction;
                if (queryId != null) _running[queryId] = cmd;
                try
                {
                    var sets = new List<Dictionary<string, object>>();
                    int affected;
                    using (var reader = cmd.ExecuteReader())
                    {
                        do
                        {
                            if (reader.FieldCount == 0) continue;
                            if (sets.Count == MaxResultSets) { messages.Add("Only the first " + MaxResultSets + " result sets are shown."); break; }
                            sets.Add(ReadSet(reader, maxRows));
                        } while (reader.NextResult());
                        affected = reader.RecordsAffected;
                    }
                    if (readOnly) transaction.Rollback(); else transaction.Commit();
                    return Success(new Dictionary<string, object>
                    {
                        ["resultSets"] = sets,
                        ["rowsAffected"] = readOnly ? -1 : affected,
                        ["messages"] = messages,
                        ["elapsedMs"] = watch.ElapsedMilliseconds,
                        ["committed"] = !readOnly,
                    });
                }
                catch
                {
                    // The server may already have rolled back; the original error is the one to report.
                    try { transaction.Rollback(); } catch (Exception rollback) when (rollback is InvalidOperationException || rollback is SqlException) { }
                    throw;
                }
                finally
                {
                    if (queryId != null) _running.TryRemove(queryId, out _);
                }
            }
        }

        private static Dictionary<string, object> ReadSet(SqlDataReader reader, int maxRows)
        {
            var columns = new List<Dictionary<string, object>>();
            for (int c = 0; c < reader.FieldCount; c++)
                columns.Add(new Dictionary<string, object> { ["name"] = reader.GetName(c), ["type"] = reader.GetDataTypeName(c) });
            var rows = new List<object[]>();
            bool truncated = false;
            while (reader.Read())
            {
                if (rows.Count == maxRows) { truncated = true; break; }
                var row = new object[reader.FieldCount];
                for (int c = 0; c < reader.FieldCount; c++)
                    row[c] = Cell(reader.GetValue(c));
                rows.Add(row);
            }
            return new Dictionary<string, object> { ["columns"] = columns, ["rows"] = rows, ["truncated"] = truncated };
        }

        /// <summary>A value JSON keeps exactly: 64-bit integers (RecIds) and decimals as text, dates as ISO 8601, long text cut.</summary>
        internal static object Cell(object value)
        {
            switch (value)
            {
                case null:
                case DBNull _:
                    return null;
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case decimal d: return d.ToString(CultureInfo.InvariantCulture);
                case DateTime dt: return dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
                case DateTimeOffset dto: return dto.ToString("o", CultureInfo.InvariantCulture);
                case TimeSpan ts: return ts.ToString("c", CultureInfo.InvariantCulture);
                case Guid g: return g.ToString();
                case byte[] bytes:
                    string hex = "0x" + BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 64)).Replace("-", "");
                    return bytes.Length > 64 ? hex + "… (" + bytes.Length + " bytes)" : hex;
                case string s: return s.Length > MaxCellChars ? s.Substring(0, MaxCellChars) + "…" : s;
                case int _: case short _: case byte _: case bool _: case double _: case float _: return value;
                default:
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    return text.Length > MaxCellChars ? text.Substring(0, MaxCellChars) + "…" : text;
            }
        }

        private static SqlConnection Open(Dictionary<string, object> request)
        {
            var connection = request.TryGetValue("connection", out object c) ? c as Dictionary<string, object> : null;
            if (connection == null) throw new ArgumentException("No database connection given.");
            string server = Text(connection, "server"), database = Text(connection, "database");
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
                throw new ArgumentException("The database connection needs a server and a database.");
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                ConnectTimeout = 20,
                ApplicationName = "F&O Studio",
                PersistSecurityInfo = false,
            };
            if (Flag(connection, "windowsAuthentication"))
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = Text(connection, "user") ?? throw new ArgumentException("The database connection needs a user name.");
                builder.Password = Text(connection, "password") ?? throw new ArgumentException("The database connection needs a password.");
                builder.Encrypt = true;
                builder.TrustServerCertificate = false;
            }
            var conn = new SqlConnection(builder.ConnectionString);
            try
            {
                conn.Open();
            }
            catch
            {
                conn.Dispose();
                throw;
            }
            return conn;
        }

        private static Dictionary<string, string> TableColumns(SqlConnection conn, string table)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("No table given.");
            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqlCommand("SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION", conn))
            {
                cmd.Parameters.AddWithValue("@t", table.Trim());
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        columns[reader.GetString(0)] = reader.GetString(1);
            }
            return columns;
        }

        internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

        private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

        private static int Rows(Dictionary<string, object> request) => Math.Max(1, Math.Min(MaxRows, Int(request, "maxRows") ?? DefaultRows));

        private static string Text(Dictionary<string, object> obj, string name) =>
            obj != null && obj.TryGetValue(name, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        private static bool Flag(Dictionary<string, object> obj, string name) => obj != null && obj.TryGetValue(name, out object v) && v is bool b && b;

        private static int? Int(Dictionary<string, object> obj, string name) =>
            obj != null && obj.TryGetValue(name, out object v) && v != null && int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out int i) ? i : (int?)null;

        private static List<string> List(Dictionary<string, object> obj, string name) =>
            obj != null && obj.TryGetValue(name, out object v) && v is System.Collections.IEnumerable items && !(v is string)
                ? items.Cast<object>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToList() : null;

        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> obj, string name) =>
            obj != null && obj.TryGetValue(name, out object v) && v is System.Collections.IEnumerable items && !(v is string)
                ? items.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();

        private static Dictionary<string, object> Success(Dictionary<string, object> result)
        {
            result["success"] = true;
            return result;
        }

        private static Dictionary<string, object> Fail(string message) =>
            new Dictionary<string, object> { ["success"] = false, ["message"] = message };

        private string Serialize(Dictionary<string, object> result) => _json.Serialize(result);

        private string Failure(string message, int sqlError = 0)
        {
            var result = Fail(message);
            if (sqlError != 0) result["sqlError"] = sqlError;
            return _json.Serialize(result);
        }
    }
}
