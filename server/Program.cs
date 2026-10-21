using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using XppVsCode.Server.Host;
using XppVsCode.Server.MetaModel;
using XppVsCode.Server.Tools;

namespace XppVsCode.Server
{
    internal static class Program
    {
        public const string Version = "0.1.0";

        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--sql-check" || args[0] == "--restore-xref"))
                return SqlSetup.Run(args);

            DynamicsConfigurationReader.ConfigFileOverride = ArgValue(args, "--config");
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            if (string.IsNullOrEmpty(config.FrameworkDirectory) || !config.HasReference)
            {
                Log.Write("No usable D365FO configuration (" + (config.ConfigurationFilePath ?? "none found") + ").");
                return 1;
            }

            string bin = Path.Combine(config.FrameworkDirectory, "bin");
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string path = Path.Combine(bin, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };

            string project = ArgValue(args, "--project");
            Log.Write("xpp-vscode server " + Version + " | config " + config.ConfigurationFilePath + " | project " + (project ?? "none"));
            Run(config, project);
            return 0;
        }

        // Kept separate so D365FO types are only resolved after AssemblyResolve is registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(DynamicsMetadataConfig config, string projectFile)
        {
            var bridge = new MetaModelBridge(new MetadataHost(config), new LabelStore(config), projectFile);
            var client = new BridgeClient(new MetaModelBridgeServer(bridge));
            new McpStdioServer(new ToolRouter(client), client, Version).Run();
        }

        private static string ArgValue(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
