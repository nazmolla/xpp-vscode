using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Where-used queries against the cross-reference database named in the active D365FO configuration.
    /// Runs in the MCP server process (never on the VS UI thread) and reports exactly why the database is unusable.
    /// </summary>
    internal sealed class XRefQuery
    {
        private const int DefaultMax = 25;
        private const int MaxMax = 100;
        private const string KnownSchemaVersionPrefix = "1.0.";
        private static readonly TimeSpan HealthTtl = TimeSpan.FromMinutes(2);

        private static readonly Dictionary<string, string> TypeToFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AxClass", "Classes" }, { "AxTable", "Tables" }, { "AxForm", "Forms" }, { "AxEnum", "Enums" },
            { "AxEdt", "Edts" }, { "AxView", "Views" }, { "AxQuery", "Queries" }, { "AxDataEntityView", "DataEntityViews" },
            { "AxMap", "Maps" }, { "AxMenu", "Menus" }, { "AxMenuItemDisplay", "MenuItemDisplays" },
            { "AxMenuItemAction", "MenuItemActions" }, { "AxMenuItemOutput", "MenuItemOutputs" },
            { "AxTableExtension", "TableExtensions" }, { "AxEnumExtension", "EnumExtensions" }, { "AxReport", "Reports" },
            { "AxSecurityRole", "SecurityRoles" }, { "AxSecurityPrivilege", "SecurityPrivileges" },
            { "AxSecurityDuty", "SecurityDuties" }, { "AxTile", "Tiles" }, { "AxConfigurationKey", "ConfigurationKeys" }
        };

        // Values of Microsoft.Dynamics.AX.Framework.Xlnt.XReference.CrossReferenceKind.
        private static readonly string[] KindNames =
            { "Any", "MethodCall", "TypeReference", "InterfaceImplementation", "ClassExtended", "TestCall",
              "Property", "Attribute", "TestHelperCall", "Tag", "MethodOverride" };

        private sealed class Health
        {
            public bool Usable;
            public string Server;
            public string Database;
            public string Summary;
            public string Problem;
            public List<string> Warnings = new List<string>();
            public DateTime CheckedUtc;
        }

        private readonly object _lock = new object();
        private Health _health;

        /// <summary>An open connection to the cross-reference database, or null with the reason (for the language service).</summary>
        internal SqlConnection TryOpen(out string problem)
        {
            var health = GetHealth();
            problem = health.Usable ? null : health.Problem;
            if (!health.Usable) return null;
            try { return Open(health.Server, health.Database); }
            catch (Exception ex) { problem = ex.Message; return null; }
        }

        /// <summary>Database name, for cache file names.</summary>
        internal string DatabaseName => GetHealth().Database;

        internal static string FolderOf(string objectType) =>
            objectType != null && TypeToFolder.TryGetValue(objectType, out string folder) ? folder : null;

        internal static string TypeOfFolder(string folder) =>
            TypeToFolder.FirstOrDefault(kv => kv.Value.Equals(folder ?? "", StringComparison.OrdinalIgnoreCase)).Key;

        /// <summary>Returns a tool result, or null when the database is unusable and the caller should fall back to the in-VS provider.</summary>
        public string TryHandle(string idToken, string json, out string unusableReason)
        {
            unusableReason = null;
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(args, "objectType")?.Trim();
            string objectName = JsonHelpers.ExtractArgString(args, "objectName")?.Trim();
            string memberName = JsonHelpers.ExtractArgString(args, "memberName", "methodName")?.Trim();
            string kindArg = JsonHelpers.ExtractArgString(args, "referenceKind")?.Trim();
            int max = ParseInt(JsonHelpers.ExtractArgString(args, "maxResults"), DefaultMax);
            if (max < 1) max = 1;
            if (max > MaxMax) max = MaxMax;

            if (!TypeToFolder.TryGetValue(objectType ?? "", out string folder))
                return JsonHelpers.BuildToolResult(idToken, "Unsupported objectType '" + objectType + "' for where-used. Supported: "
                    + string.Join(", ", TypeToFolder.Keys), isError: true);

            int kind = 0;
            if (!string.IsNullOrEmpty(kindArg))
            {
                kind = Array.FindIndex(KindNames, k => k.Equals(kindArg, StringComparison.OrdinalIgnoreCase));
                if (kind < 0)
                    return JsonHelpers.BuildToolResult(idToken, "Unknown referenceKind '" + kindArg + "'. Valid: "
                        + string.Join(", ", KindNames), isError: true);
            }

            var health = GetHealth();
            if (!health.Usable)
            {
                unusableReason = health.Problem;
                return null;
            }

            string objectPath = "/" + folder + "/" + objectName;
            try
            {
                using (var conn = Open(health.Server, health.Database))
                {
                    if (!TargetExists(conn, objectPath, memberName))
                        return JsonHelpers.BuildToolResult(idToken, NotIndexedMessage(conn, health, objectType, objectName, objectPath, memberName));

                    var rows = QueryReferences(conn, objectPath, memberName, kind, max + 1);
                    return JsonHelpers.BuildToolResult(idToken, Render(health, objectType, objectName, memberName, kind, rows, max));
                }
            }
            catch (SqlException ex)
            {
                InvalidateHealth();
                unusableReason = "Cross-reference query failed on " + health.Server + "/" + health.Database + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>Where-used as JSON for the VS Code extension's references list.</summary>
        /// <summary>
        /// Removes a deleted object's own cross-reference entries: the references from the object and its members, and
        /// its names that nothing references any more. References to it from other objects stay (they still use it).
        /// xppc only drops these on a full model build, so where-used would otherwise list deleted objects.
        /// </summary>
        public string RemoveObject(string objectType, string objectName)
        {
            if (!TypeToFolder.TryGetValue(objectType ?? "", out string folder)) return null;
            var health = GetHealth();
            if (!health.Usable) return null;
            string objectPath = "/" + folder + "/" + objectName;
            string like = objectPath.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "/%";
            try
            {
                using (var conn = Open(health.Server, health.Database))
                using (var tx = conn.BeginTransaction())
                using (var cmd = new SqlCommand(
                    "SELECT Id INTO #ids FROM Names WHERE Path = @p OR Path LIKE @like; "
                    + "DELETE r FROM [References] r JOIN #ids i ON r.SourceId = i.Id; SELECT @@ROWCOUNT; "
                    + "DELETE n FROM Names n JOIN #ids i ON n.Id = i.Id WHERE NOT EXISTS (SELECT 1 FROM [References] r WHERE r.TargetId = n.Id); SELECT @@ROWCOUNT; "
                    + "DROP TABLE #ids;", conn, tx) { CommandTimeout = 120 })
                {
                    cmd.Parameters.AddWithValue("@p", objectPath);
                    cmd.Parameters.AddWithValue("@like", like);
                    var counts = new List<int>();
                    using (var reader = cmd.ExecuteReader())
                    {
                        do { while (reader.Read()) counts.Add(reader.GetInt32(0)); } while (reader.NextResult());
                    }
                    tx.Commit();
                    return counts.Count == 2 && counts[0] + counts[1] > 0
                        ? "Cross-reference entries removed: " + counts[0] + " reference(s) from it and " + counts[1] + " name(s)."
                        : null;
                }
            }
            catch (Exception ex)
            {
                return "Cross-reference entries not removed (" + ex.Message + "); they go away with the next full build of the model.";
            }
        }

        public string QueryJson(string objectType, string objectName, string memberName, int max, string kindName = null)
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (!TypeToFolder.TryGetValue(objectType ?? "", out string folder))
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Where-used is not available for " + objectType + "." });
            int kind = string.IsNullOrEmpty(kindName) ? 0 : Array.FindIndex(KindNames, k => k.Equals(kindName, StringComparison.OrdinalIgnoreCase));
            if (kind < 0)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Unknown reference kind '" + kindName + "'." });

            var health = GetHealth();
            if (!health.Usable)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = health.Problem });

            string objectPath = "/" + folder + "/" + objectName;
            try
            {
                using (var conn = Open(health.Server, health.Database))
                {
                    if (!TargetExists(conn, objectPath, memberName))
                        return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = NotIndexedMessage(conn, health, objectType, objectName, objectPath, memberName) });

                    var rows = QueryReferences(conn, objectPath, memberName, kind, max + 1);
                    var references = rows.Take(max).Select(r =>
                    {
                        var parts = r.Source.TrimStart('/').Split('/');
                        string type = parts.Length >= 2 ? TypeToFolder.FirstOrDefault(kv => kv.Value.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).Key : null;
                        return new Dictionary<string, object>
                        {
                            ["objectType"] = type,
                            ["objectName"] = parts.Length >= 2 ? parts[1] : r.Source,
                            ["memberPath"] = parts.Skip(2).ToArray(),
                            ["member"] = Member(r.Source),
                            ["target"] = Member(r.Target, objectLevel: "(type)"),
                            ["kind"] = KindName(r.Kind),
                            ["line"] = r.Line,
                            ["module"] = r.Module,
                        };
                    }).ToList();
                    return json.Serialize(new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["references"] = references,
                        ["more"] = rows.Count > max,
                        ["warnings"] = health.Warnings,
                    });
                }
            }
            catch (SqlException ex)
            {
                InvalidateHealth();
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Cross-reference query failed: " + ex.Message });
            }
        }

        // ── Tags (Dynamics 365 > Find Tag References) ──

        // Tag sources use singular kinds ("Table/X/TableFieldInt/F", "Class/X/Method/m"); map them to element types.
        private static readonly Dictionary<string, string> SourceKindToType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Class", "AxClass" }, { "Table", "AxTable" }, { "Form", "AxForm" }, { "View", "AxView" }, { "Query", "AxQuery" },
            { "DataEntityView", "AxDataEntityView" }, { "Map", "AxMap" }, { "Enum", "AxEnum" }, { "Menu", "AxMenu" },
            { "MenuItemDisplay", "AxMenuItemDisplay" }, { "MenuItemAction", "AxMenuItemAction" }, { "MenuItemOutput", "AxMenuItemOutput" },
            { "TableExtension", "AxTableExtension" }, { "FormExtension", "AxFormExtension" }, { "SecurityPrivilege", "AxSecurityPrivilege" },
        };

        public string TagsJson(string filter, int max)
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var health = GetHealth();
            if (!health.Usable)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = health.Problem });
            var tags = new List<string>();
            using (var conn = Open(health.Server, health.Database))
            using (var cmd = new SqlCommand("SELECT TOP (@take) Path FROM Names WHERE Path LIKE @p ORDER BY Path", conn) { CommandTimeout = 30 })
            {
                cmd.Parameters.AddWithValue("@take", max);
                cmd.Parameters.AddWithValue("@p", "/Tags/%" + EscapeLike(filter ?? "") + "%");
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) tags.Add(r.GetString(0).Substring("/Tags/".Length));
            }
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["tags"] = tags });
        }

        public string TagReferencesJson(string tag, int max)
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var health = GetHealth();
            if (!health.Usable)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = health.Problem });
            var refs = new List<Dictionary<string, object>>();
            using (var conn = Open(health.Server, health.Database))
            using (var cmd = new SqlCommand("SELECT TOP (@take) s.Path, r.Line, m.Module FROM Names t JOIN [References] r ON r.TargetId = t.Id "
                + "JOIN Names s ON s.Id = r.SourceId LEFT JOIN Modules m ON m.Id = s.ModuleId WHERE t.Path = @p ORDER BY m.Module, s.Path", conn) { CommandTimeout = 30 })
            {
                cmd.Parameters.AddWithValue("@take", max);
                cmd.Parameters.AddWithValue("@p", "/Tags/" + tag);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        string source = r.GetString(0);
                        var parts = source.TrimStart('/').Split('/');
                        string type = parts.Length >= 2 && SourceKindToType.TryGetValue(parts[0], out string t) ? t
                            : parts.Length >= 2 ? TypeToFolder.FirstOrDefault(kv => kv.Value.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).Key : null;
                        refs.Add(new Dictionary<string, object>
                        {
                            ["objectType"] = type,
                            ["objectName"] = parts.Length >= 2 ? parts[1] : source,
                            ["member"] = string.Join("/", parts.Skip(2)),
                            ["line"] = r.IsDBNull(1) ? 0 : (int)r.GetInt16(1),
                            ["module"] = r.IsDBNull(2) ? "?" : r.GetString(2),
                        });
                    }
            }
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["references"] = refs });
        }

        // ── Health / deployment checks ──

        private Health GetHealth()
        {
            lock (_lock)
            {
                if (_health != null && DateTime.UtcNow - _health.CheckedUtc < HealthTtl) return _health;
                _health = CheckHealth();
                McpLogger.Log("XRef health: usable=" + _health.Usable + " " + (_health.Problem ?? _health.Summary));
                return _health;
            }
        }

        private void InvalidateHealth()
        {
            lock (_lock) _health = null;
        }

        private static Health CheckHealth()
        {
            var h = new Health { CheckedUtc = DateTime.UtcNow };
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            string configFile = config.ConfigurationFilePath ?? "(no active D365FO configuration found in the registry)";
            h.Server = config.CrossReferencesDbServerName;
            h.Database = config.CrossReferencesDatabaseName;

            if (string.IsNullOrEmpty(h.Server) || string.IsNullOrEmpty(h.Database))
            {
                h.Problem = "The active D365FO configuration does not define the cross-reference database "
                    + "(CrossReferencesDbServerName / CrossReferencesDatabaseName missing) in " + configFile
                    + ". Re-run the environment configuration for this version so the XRef database is created and registered.";
                return h;
            }

            try
            {
                using (var conn = Open(h.Server, "master"))
                {
                    var state = Scalar(conn, "SELECT state_desc FROM sys.databases WHERE name = @db", ("@db", h.Database)) as string;
                    if (state == null)
                    {
                        h.Problem = "Cross-reference database '" + h.Database + "' is referenced by " + configFile
                            + " but does not exist on server '" + h.Server + "'. It was not deployed: build the models with "
                            + "'Build cross reference data' enabled (Dynamics 365 > Build models), or re-run the environment configuration.";
                        return h;
                    }
                    if (!state.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
                    {
                        h.Problem = "Cross-reference database '" + h.Database + "' on '" + h.Server + "' is " + state + ", not ONLINE.";
                        return h;
                    }

                    conn.ChangeDatabase(h.Database);
                    var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var cmd = new SqlCommand("SELECT name FROM sys.tables", conn) { CommandTimeout = 30 })
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) tables.Add(r.GetString(0));
                    foreach (string required in new[] { "Names", "References", "Modules", "DBVersion" })
                        if (!tables.Contains(required))
                        {
                            h.Problem = "Cross-reference database '" + h.Database + "' exists but is not initialized (table '" + required
                                + "' missing). Build the models with 'Build cross reference data' enabled.";
                            return h;
                        }

                    string version = Scalar(conn, "SELECT TOP 1 Version FROM DBVersion") as string ?? "?";
                    if (!version.StartsWith(KnownSchemaVersionPrefix, StringComparison.Ordinal))
                    {
                        h.Problem = "Cross-reference database schema version " + version + " is not one this tool understands.";
                        return h;
                    }

                    long names = Convert.ToInt64(Scalar(conn,
                        "SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID('Names') AND p.index_id IN (0,1)") ?? 0L);
                    long refs = Convert.ToInt64(Scalar(conn,
                        "SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID('References') AND p.index_id IN (0,1)") ?? 0L);
                    if (names == 0 || refs == 0)
                    {
                        h.Problem = "Cross-reference database '" + h.Database + "' is empty. Build the models with 'Build cross reference data' enabled.";
                        return h;
                    }

                    var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var cmd = new SqlCommand("SELECT Module FROM Modules", conn) { CommandTimeout = 30 })
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) modules.Add(r.GetString(0));

                    var missing = config.CustomPackageFolders().Select(Path.GetFileName).Where(p => !modules.Contains(p)).ToList();
                    if (missing.Count > 0)
                        h.Warnings.Add("Custom package(s) not in the cross-reference database: " + string.Join(", ", missing)
                            + ". References from their code are NOT included; build them with 'Build cross reference data' enabled.");

                    h.Usable = true;
                    h.Summary = h.Server + "/" + h.Database + " (schema " + version + ", " + modules.Count + " modules, "
                        + refs.ToString("N0") + " references)";
                    return h;
                }
            }
            catch (SqlException ex)
            {
                h.Problem = "Cannot query cross-reference server '" + h.Server + "' (database '" + h.Database + "'): " + ex.Message
                    + (h.Server.StartsWith("(LocalDB)", StringComparison.OrdinalIgnoreCase)
                        ? " If LocalDB is stopped, run: sqllocaldb start " + h.Server.Substring(h.Server.IndexOf('\\') + 1)
                        : "");
                return h;
            }
        }

        // ── Queries ──

        private static SqlConnection Open(string server, string database)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                IntegratedSecurity = true,
                ConnectTimeout = 15,
                ApplicationName = "XppMcpServer"
            };
            var conn = new SqlConnection(builder.ConnectionString);
            conn.Open();
            return conn;
        }

        private static object Scalar(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
        {
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 })
            {
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value);
                object v = cmd.ExecuteScalar();
                return v == DBNull.Value ? null : v;
            }
        }

        // Object-level search covers the object and all its members; member search covers one method or field.
        private static string TargetFilter(string memberName) => string.IsNullOrEmpty(memberName)
            ? "(t.Path = @p OR t.Path LIKE @prefix)"
            // Fields added by table extensions are indexed under the extension (/TableExtensions/Table.Ext/Fields/Field).
            : "(t.Path IN (@p + '/Methods/' + @m, @p + '/Fields/' + @m) OR (@ext <> '' AND t.Path LIKE @ext))";

        private static void AddTargetParameters(SqlCommand cmd, string objectPath, string memberName)
        {
            cmd.Parameters.AddWithValue("@p", objectPath);
            cmd.Parameters.AddWithValue("@prefix", EscapeLike(objectPath) + "/%");
            cmd.Parameters.AddWithValue("@m", memberName ?? "");
            cmd.Parameters.AddWithValue("@ext", objectPath.StartsWith("/Tables/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(memberName)
                ? "/TableExtensions/" + EscapeLike(objectPath.Substring(8)) + ".%/Fields/" + EscapeLike(memberName) : "");
        }

        private static bool TargetExists(SqlConnection conn, string objectPath, string memberName)
        {
            using (var cmd = new SqlCommand("SELECT TOP 1 1 FROM Names t WHERE " + TargetFilter(memberName), conn) { CommandTimeout = 30 })
            {
                AddTargetParameters(cmd, objectPath, memberName);
                return cmd.ExecuteScalar() != null;
            }
        }

        private sealed class RefRow
        {
            public string Source;
            public string Target;
            public int Kind;
            public int Line;
            public string Module;
        }

        private static List<RefRow> QueryReferences(SqlConnection conn, string objectPath, string memberName, int kind, int take)
        {
            var rows = new List<RefRow>();
            string sql = "SELECT TOP (@take) s.Path, t.Path, r.Kind, r.Line, m.Module "
                + "FROM Names t JOIN [References] r ON r.TargetId = t.Id JOIN Names s ON s.Id = r.SourceId "
                + "LEFT JOIN Modules m ON m.Id = s.ModuleId "
                + "WHERE " + TargetFilter(memberName)
                // A member's callers include other methods of the same object; an object's callers exclude the object itself.
                + (string.IsNullOrEmpty(memberName) ? " AND s.Path <> @p AND s.Path NOT LIKE @prefix " : " AND s.Path <> t.Path ")
                + (kind > 0 ? "AND r.Kind = @kind " : "")
                + "ORDER BY m.Module, s.Path, r.Line";
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 })
            {
                AddTargetParameters(cmd, objectPath, memberName);
                cmd.Parameters.AddWithValue("@take", take);
                cmd.Parameters.AddWithValue("@kind", kind);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        rows.Add(new RefRow
                        {
                            Source = r.GetString(0),
                            Target = r.GetString(1),
                            Kind = r.GetByte(2),
                            Line = r.IsDBNull(3) ? 0 : r.GetInt16(3),
                            Module = r.IsDBNull(4) ? "?" : r.GetString(4)
                        });
            }
            return rows;
        }

        private static string NotIndexedMessage(SqlConnection conn, Health health, string objectType, string objectName,
            string objectPath, string memberName)
        {
            bool objectKnown = Scalar(conn, "SELECT TOP 1 1 FROM Names WHERE Path = @p", ("@p", objectPath)) != null;
            var sb = new StringBuilder();
            if (objectKnown && !string.IsNullOrEmpty(memberName))
                sb.Append(objectType + " '" + objectName + "' is indexed, but it has no method or field '" + memberName
                    + "'. Check the member name with xpp_read_object.");
            else
                sb.Append("No cross-reference entries for " + objectType + " '" + objectName + "': nothing in the indexed packages "
                    + "refers to it, the name is wrong (check it with xpp_find_object), or its package was not built with 'Build cross reference data'."
                    + (objectType.StartsWith("AxMenuItem", StringComparison.Ordinal)
                        ? " The privileges, duties and roles that grant a menu item are listed by xpp_analyze_security op=access." : ""));
            AppendWarnings(sb, health);
            return sb.ToString();
        }

        // ── Rendering ──

        private static string Render(Health health, string objectType, string objectName, string memberName, int kind,
            List<RefRow> rows, int max)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Where-used: " + objectType + " " + objectName + (string.IsNullOrEmpty(memberName) ? "" : "." + memberName)
                + (kind > 0 ? "  (kind " + KindNames[kind] + ")" : "") + "  [source: " + health.Summary + "]");
            AppendWarnings(sb, health);

            if (rows.Count == 0)
            {
                sb.AppendLine("No references from other objects.");
                return sb.ToString().TrimEnd();
            }

            bool more = rows.Count > max;
            var shown = rows.Take(max).ToList();
            sb.AppendLine((more ? "First " + max : shown.Count.ToString()) + " reference(s), grouped by referencing object:");
            var loaded = LoadedPackages();
            bool anyUnreadable = false;
            foreach (var group in shown.GroupBy(r => ObjectKey(r.Source)))
            {
                var first = group.First();
                bool readable = loaded.Count == 0 || loaded.Contains(first.Module ?? "");
                anyUnreadable |= !readable;
                sb.AppendLine("  " + DescribeObject(first.Source) + "  [" + first.Module + "]"
                    + (readable ? "" : "  (package not in the local metadata: cannot be read with xpp_read_object)"));
                foreach (var r in group)
                    sb.AppendLine("    " + Member(r.Source) + " line " + r.Line + ": " + KindName(r.Kind)
                        + " -> " + Member(r.Target, objectLevel: "(type)"));
            }
            if (anyUnreadable)
                sb.AppendLine("Packages marked as not local (typically test packages) are indexed in the cross-reference database but their source is not installed with the metadata.");
            if (more)
                sb.AppendLine("More references exist. Narrow with memberName or referenceKind, or raise maxResults (max " + MaxMax + ").");
            return sb.ToString().TrimEnd();
        }

        private static HashSet<string> _loadedPackages;

        // Packages whose metadata is on this machine (folders under the custom and reference metadata roots).
        private static HashSet<string> LoadedPackages()
        {
            if (_loadedPackages != null) return _loadedPackages;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var config = DynamicsConfigurationReader.ReadActiveConfiguration();
                foreach (string root in config.MetadataRoots().Where(System.IO.Directory.Exists))
                    foreach (string dir in System.IO.Directory.GetDirectories(root))
                        if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Descriptor")))
                            set.Add(System.IO.Path.GetFileName(dir));
            }
            catch { }
            return _loadedPackages = set;
        }

        private static void AppendWarnings(StringBuilder sb, Health health)
        {
            foreach (string w in health.Warnings)
                sb.AppendLine("\nWARNING: " + w);
        }

        private static string KindName(int kind) => kind >= 0 && kind < KindNames.Length ? KindNames[kind] : "Kind" + kind;

        // "/Classes/X/Methods/m" -> "/Classes/X"
        private static string ObjectKey(string path)
        {
            var parts = path.TrimStart('/').Split('/');
            return parts.Length >= 2 ? "/" + parts[0] + "/" + parts[1] : path;
        }

        private static string DescribeObject(string path)
        {
            var parts = path.TrimStart('/').Split('/');
            if (parts.Length < 2) return path;
            string type = TypeToFolder.FirstOrDefault(kv => kv.Value.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).Key ?? parts[0];
            return type + " " + parts[1];
        }

        // "/Classes/X/Methods/m" -> "m"; "/Forms/F/Controls/B/Methods/clicked" -> "Controls/B/Methods/clicked"
        private static string Member(string path, string objectLevel = "(declaration)")
        {
            var parts = path.TrimStart('/').Split('/');
            if (parts.Length <= 2) return objectLevel;
            if (parts.Length == 4 && parts[2] == "Methods") return parts[3] + "()";
            if (parts.Length == 4 && parts[2] == "Fields") return parts[3];
            return string.Join("/", parts.Skip(2));
        }

        private static string EscapeLike(string value) =>
            value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        private static int ParseInt(string s, int fallback) => int.TryParse(s, out int v) ? v : fallback;
    }
}
