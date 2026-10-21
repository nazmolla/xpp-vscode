using System;
using System.Data.SqlClient;
using System.IO;
using System.Web.Script.Serialization;

namespace XppVsCode.Server.Host
{
    /// <summary>Command-line helpers for environment setup (cross-reference database), used by the extension.</summary>
    internal static class SqlSetup
    {
        public static int Run(string[] args)
        {
            var json = new JavaScriptSerializer();
            try
            {
                switch (args[0])
                {
                    case "--sql-check":
                        Console.WriteLine(json.Serialize(new { ok = true, exists = DatabaseExists(args[1], args.Length > 2 ? args[2] : "master") }));
                        return 0;
                    case "--restore-xref":
                        RestoreXRef(args[1], args[2], args[3], args[4]);
                        Console.WriteLine(json.Serialize(new { ok = true }));
                        return 0;
                    default:
                        throw new ArgumentException("Unknown setup command " + args[0]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(json.Serialize(new { ok = false, message = ex.Message }));
                return 1;
            }
        }

        private static SqlConnection Open(string server)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                ConnectTimeout = 30,
            };
            var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return connection;
        }

        private static bool DatabaseExists(string server, string database)
        {
            using (var connection = Open(server))
            using (var cmd = new SqlCommand("SELECT 1 FROM sys.databases WHERE name = @name", connection))
            {
                cmd.Parameters.AddWithValue("@name", database);
                return cmd.ExecuteScalar() != null;
            }
        }

        /// <summary>Same restore Visual Studio performs: data and log files moved into the configuration folder.</summary>
        private static void RestoreXRef(string server, string database, string backupFile, string dataFolder)
        {
            if (!File.Exists(backupFile)) throw new FileNotFoundException("Backup not found", backupFile);
            if (DatabaseExists(server, database)) throw new InvalidOperationException("Database " + database + " already exists on " + server + ".");
            Directory.CreateDirectory(dataFolder);

            string mdf = Path.Combine(dataFolder, "DYNAMICSXREFDB1.mdf");
            string ldf = Path.Combine(dataFolder, "DYNAMICSXREFDB1_log.ldf");
            for (int i = 1; File.Exists(mdf) && i <= 5; i++)
            {
                mdf = Path.Combine(dataFolder, "DYNAMICSXREFDB1_" + i + ".mdf");
                ldf = Path.Combine(dataFolder, "DYNAMICSXREFDB1_" + i + "_log.ldf");
            }

            using (var connection = Open(server))
            using (var cmd = connection.CreateCommand())
            {
                // Database names cannot be parameters; QUOTENAME-style escaping keeps the name literal.
                cmd.CommandText = "RESTORE DATABASE [" + database.Replace("]", "]]") + "] FROM DISK = @bak WITH REPLACE, "
                    + "MOVE 'DYNAMICSXREFDB' TO @mdf, MOVE 'DYNAMICSXREFDB_log' TO @ldf, RECOVERY";
                cmd.Parameters.AddWithValue("@bak", backupFile);
                cmd.Parameters.AddWithValue("@mdf", mdf);
                cmd.Parameters.AddWithValue("@ldf", ldf);
                cmd.CommandTimeout = 1800;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
