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
            // Set by the extension for Copilot's MCP server only: its tool calls feed the Copilot Activity view.
            ActivityLog.Enabled = Array.Exists(args, a => a.Equals("--activity-log", StringComparison.OrdinalIgnoreCase));
            Log.Write("xpp-vscode server " + Version + " | config " + config.ConfigurationFilePath + " | project " + (project ?? "none"));
            Run(config, project, Array.Exists(args, a => a.Equals("--allow-data", StringComparison.OrdinalIgnoreCase)), ArgValue(args, "--rules"));
            return 0;
        }

        // Kept separate so D365FO types are only resolved after AssemblyResolve is registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(DynamicsMetadataConfig config, string projectFile, bool allowData, string rulesFile)
        {
            var labels = new LabelStore(config);
            var bridge = new MetaModelBridge(new MetadataHost(config), labels, projectFile);
            var services = new BridgeServices
            {
                Objects = bridge,
                Discovery = bridge,
                Designer = bridge,
                Projects = bridge,
                Builds = new BuildService(bridge),
                Policy = bridge,
                Models = new ModelService(bridge),
                Labels = new LabelService(bridge, labels),
                Packages = new ProjectPackageService(bridge),
                Relations = new TableRelationsService(bridge),
                DataShape = new TableDataShapeService(bridge),
                Performance = new PerformanceAnalysisService(bridge),
                Compatibility = new ExtensionCompatibilityService(bridge, bridge),
                AgentWrites = ActivityLog.Enabled ? bridge : null,
                Checkpoints = allowData ? bridge : null,
            };
            var client = new BridgeClient(new MetaModelBridgeServer(services));
            new McpStdioServer(new ToolRouter(client), client, Version, allowData ? new DataQueryService() : null, rulesFile).Run();
        }

        private static string ArgValue(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
