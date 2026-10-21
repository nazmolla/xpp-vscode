// Minimal schema for local X++ tests: the kernel tables, the ID tables and only the application tables the
// tests use, created with the database synchronizer's managed table worker (no kernel start, no full sync).
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.AX.Data.Management;
using Microsoft.Dynamics.AX.Data.Management.Enum;

internal static class SchemaSync
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static int Run(string[] args)
    {
        string tables = args[0].Contains(":") ? args[0].Substring(args[0].IndexOf(':') + 1) : "";
        string packages = Setting("Common.BinDir");
        string connect = args.Where(a => a.StartsWith("/connect:", StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(9)).FirstOrDefault()
            ?? "Data Source=" + Setting("DataAccess.DbServer") + ";Initial Catalog=" + Setting("DataAccess.Database")
               + ";Integrated Security=True;Enlist=True;Application Name=XppLocalSchema";

        var options = new SyncOptions(new DirectoryInfo(packages), new DirectoryInfo(packages));
        options.Logger = (type, message) =>
        {
            if (type != LogEntryType.Diagnostic) Console.WriteLine("[" + type + "] " + message);
        };
        options.ContinueOnError = true;
        foreach (var prop in typeof(SyncOptions).GetProperties().Where(p => p.CanWrite && p.GetValue(options) == null
                     && p.PropertyType.IsClass && p.PropertyType.GetConstructor(Type.EmptyTypes) != null && p.PropertyType != typeof(string)))
            prop.SetValue(options, Activator.CreateInstance(prop.PropertyType));

        var dbAssembly = Assembly.Load("Microsoft.Dynamics.AX.Framework.Database");
        var engineType = dbAssembly.GetType("Microsoft.Dynamics.AX.Framework.Database.Tools.SyncEngine", true);
        object engine = engineType.GetConstructors(All).First(c => c.GetParameters().Length == 3).Invoke(new object[] { packages, connect, options });
        object provider = engineType.GetField("metadataProvider", All).GetValue(engine);
        string cs = (string)engineType.GetField("sqlConnectionString", All).GetValue(engine);

        Step("ConfigureDatabase", () => engineType.GetMethod("ConfigureDatabase", All).Invoke(engine, null));
        Step("Kernel tables", () =>
        {
            var legacy = dbAssembly.GetType("Microsoft.Dynamics.AX.Framework.Database.Tools.LegacyCodepath", true);
            var bootstrap = legacy.GetMethod("BootstrapKernelSync", All);
            bootstrap.Invoke(null, new object[] { options, cs, provider, false, true });
        });
        Step("ID tables", () => engineType.GetMethod("FullIdSync", All).Invoke(engine, null));

        var names = tables.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (names.Count > 0)
        {
            Step("Tables " + string.Join(", ", names), () =>
            {
                // The kernel-bootstrap mode of the worker is the path that runs without native kernel state.
                string offline = ConfigurationManager.AppSettings.Get("IsInOfflineMode");
                ConfigurationManager.AppSettings.Set("IsInOfflineMode", "true");
                var settings = Type.GetType("Microsoft.Dynamics.AX.Data.Management.SqlDatabaseSettings, Microsoft.Dynamics.AX.Data.Management")
                    ?? typeof(SyncOptions).Assembly.GetTypes().First(t => t.Name == "SqlDatabaseSettings");
                settings.GetMethod("Initialize", All, null, new[] { typeof(string) }, null)?.Invoke(null, new object[] { cs });
                try
                {
                    var worker = new ManagedSyncTableWorker(cs, (Microsoft.Dynamics.AX.Metadata.Providers.IMetadataProvider)provider,
                        options.Logger, options, false, true, false, false, true);
                    worker.SyncIncremental(names.Select(n => new SyncRequest(SyncRequestType.AddOrModifyTable, n.ToUpperInvariant())).ToList(), new List<string>());
                }
                finally
                {
                    ConfigurationManager.AppSettings.Set("IsInOfflineMode", offline);
                }
            });
        }
        Console.WriteLine("SCHEMA DONE");
        if (args.Any(a => a.Equals("/security", StringComparison.OrdinalIgnoreCase)))
        {
            // Role/privilege permission tables; without them the kernel denies table access even to admins.
            var modeProp = typeof(SyncOptions).GetProperty("SyncMode");
            modeProp.SetValue(options, Enum.Parse(modeProp.PropertyType, "FullSecurity"));
            Step("Security", () => engineType.GetMethod("FullSecuritySync", All).Invoke(engine, null));
            Console.WriteLine("SECURITY DONE");
        }
        return 0;
    }

    // Settings come from the ECA_ConfigurationFile the runtime uses (appSettings), before the runtime loads it.
    private static string Setting(string key)
    {
        string file = Environment.GetEnvironmentVariable("ECA_ConfigurationFile");
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            var config = ConfigurationManager.OpenMappedExeConfiguration(new ExeConfigurationFileMap { ExeConfigFilename = file }, ConfigurationUserLevel.None);
            string value = config.AppSettings.Settings[key]?.Value;
            if (value != null) return value;
        }
        return ConfigurationManager.AppSettings[key];
    }

    private static void Step(string name, Action action)
    {
        Console.WriteLine("SCHEMA STEP " + name);
        try
        {
            action();
        }
        catch (System.Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            Console.WriteLine("SCHEMA STEP FAILED " + name + ": " + inner.GetType().Name + ": " + inner.Message);
            Console.WriteLine(inner.StackTrace?.Split('\n').Take(8).Aggregate((a, b) => a + "\n" + b));
        }
    }
}


