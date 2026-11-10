using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Microsoft.Dynamics.AX.Metadata.Service;
using XppVsCode.Server.Host;
using static XppVsCode.Server.MetaModel.MetaModelBridge;

namespace XppVsCode.Server.MetaModel
{
    // Build (labels + X++) the way Visual Studio and the extension's Build command do, for agents.
    internal sealed class BuildService : IBuildService
    {
        private readonly IMetadataCore _core;

        public BuildService(IMetadataCore core)
        {
            _core = core;
        }

        private IMetaModelService MetaService => _core.MetaService;
        private bool IsCustomModel(string modelName) => _core.IsCustomModel(modelName);
        private XppProject GetActiveProject() => _core.ActiveProject;

        private const int MaxReportedDiagnostics = 80;

        public sealed class BuildDiagnostic
        {
            public string Severity;
            public string ObjectType;
            public string ObjectName;
            public string Member;
            public int? Line;
            public int? Column;
            public string Moniker;
            public string Message;
            public string Path;
        }

        /// <summary>The best-practice rules (xppbp) on a custom model; projectOnly reports only the active project's elements.</summary>
        public string BestPracticeJson(string modelName, bool projectOnly)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            var project = GetActiveProject();
            if (string.IsNullOrWhiteSpace(modelName)) modelName = project?.ModelName;
            if (string.IsNullOrWhiteSpace(modelName))
                return json.Serialize(Failure("Pass modelName, or select a project with xpp_select_project."));
            var model = MetaService.GetModel(modelName);
            if (model == null || !IsCustomModel(model.Name))
                return json.Serialize(Failure("Model '" + modelName + "' is not one of your custom models."));
            if (projectOnly && project == null)
                return json.Serialize(Failure("scope 'project' needs an active project (xpp_select_project)."));

            string logDir = Path.Combine(config.CustomMetadataFolder, model.Module, "BuildLogs");
            Directory.CreateDirectory(logDir);
            string xmlLog = Path.Combine(logDir, "Dynamics.AX." + model.Module + ".xppbp.xml");
            if (File.Exists(xmlLog)) File.Delete(xmlLog);
            var log = new StringBuilder();
            var timer = Stopwatch.StartNew();
            // Custom metadata as the source and the reference packages for what it uses (verified: other combinations fail to load).
            int exit = RunTool(Path.Combine(config.FrameworkDirectory, "bin", "xppbp.exe"), new[]
            {
                "-metadata=" + config.CustomMetadataFolder, "-packagesRoot=" + config.FrameworkDirectory,
                "-module=" + model.Module, "-model=" + model.Name, "-all", "-xmlLog=" + xmlLog,
            }, log);
            if (!File.Exists(xmlLog))
                return json.Serialize(Failure("The best-practice checker wrote no results (exit " + exit + ").\n" + Tail(log.ToString(), 2000)));

            HashSet<string> projectKeys = projectOnly
                ? new HashSet<string>(project.GetItems().Select(i => i.ObjectType + "/" + i.Name), StringComparer.OrdinalIgnoreCase) : null;
            var reported = ParseBuildDiagnostics(xmlLog).Where(d => projectKeys == null || d.ObjectType == null || projectKeys.Contains(d.ObjectType + "/" + d.ObjectName)).ToList();
            int errors = reported.Count(d => IsBlocking(d.Severity));
            var text = new StringBuilder();
            text.Append("BEST PRACTICE: ").Append(model.Name).Append(projectOnly ? " (project " + project.Caption + " only)" : "")
                .Append(" in ").Append((int)timer.Elapsed.TotalSeconds).Append("s - ").Append(errors).Append(" error(s), ")
                .Append(reported.Count - errors).AppendLine(" warning(s).");
            foreach (var d in reported.OrderBy(d => IsBlocking(d.Severity) ? 0 : 1).Take(MaxReportedDiagnostics))
                text.AppendLine(FormatDiagnostic(d));
            if (reported.Count > MaxReportedDiagnostics)
                text.AppendLine("... " + (reported.Count - MaxReportedDiagnostics) + " more.");
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = text.ToString() });
        }

        /// <summary>A deployable package of custom models, with the script Visual Studio runs (RunPackageCreationAll.ps1).</summary>
        public string PackageJson(string[] modelNames, string version)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            if (string.IsNullOrWhiteSpace(version)) version = "1.0.0.0";
            if (!Regex.IsMatch(version, @"^\d+(\.\d+){3}$"))
                return json.Serialize(Failure("version must look like 1.0.0.0."));
            var names = (modelNames ?? new string[0]).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (names.Count == 0 && GetActiveProject()?.ModelName is string projectModel) names.Add(projectModel);
            if (names.Count == 0) return json.Serialize(Failure("Pass modelNames, or select a project."));
            var packages = new List<string>();
            foreach (string name in names)
            {
                var model = MetaService.GetModel(name);
                if (model == null || !IsCustomModel(model.Name))
                    return json.Serialize(Failure("Model '" + name + "' is not one of your custom models."));
                if (!packages.Contains(model.Module, StringComparer.OrdinalIgnoreCase)) packages.Add(model.Module);
            }
            string bin = Path.Combine(config.FrameworkDirectory, "bin");
            string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode", "packages", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(output);
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            // Names come from the metadata API and the version is validated, so the command line holds no free text.
            string command = "& " + Q(Path.Combine(bin, "RunPackageCreationAll.ps1")) + " -packageNames @(" + string.Join(",", packages.Select(Q)) + ")"
                + " -metadataDir " + Q(config.CustomMetadataFolder) + " -modelDir " + Q(config.CustomMetadataFolder) + " -binDir " + Q(bin)
                + " -packageVersion " + Q(version) + " -outputDir " + Q(output);
            var log = new StringBuilder();
            int exit = RunTool(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command }, log);
            var zips = Directory.GetFiles(output, "*.zip", SearchOption.AllDirectories);
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = exit == 0 && zips.Length > 0,
                ["message"] = (exit == 0 && zips.Length > 0 ? "PACKAGE CREATED: " + string.Join(", ", zips) : "PACKAGE FAILED (exit " + exit + ").")
                    + " Packages: " + string.Join(", ", packages) + ", version " + version + ". Build the models first; the package contains their last build.\n" + Tail(log.ToString(), 2500),
            });
        }

        /// <summary>Synchronizes only the given tables (SyncEngine partiallist) on a cloud-hosted or VHD environment.</summary>
        public string SyncTablesJson(string[] tables)
        {
            var json = new JavaScriptSerializer();
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            if (config.IsUnifiedDeveloperEnvironment)
                return json.Serialize(Failure("Unified developer environments synchronize the database when the package is deployed; there is no local database to synchronize."));
            var names = (tables ?? new string[0]).Select(t => MetaService.GetTable(t?.Trim() ?? "")?.Name).ToList();
            if (names.Count == 0 || names.Any(n => n == null))
                return json.Serialize(Failure("Pass existing table names."));
            if (string.IsNullOrEmpty(config.DatabaseServer) || string.IsNullOrEmpty(config.BusinessDatabaseName))
                return json.Serialize(Failure("The configuration names no database server/business database."));
            var log = new StringBuilder();
            string connection = "Data Source=" + config.DatabaseServer + ";Initial Catalog=" + config.BusinessDatabaseName + ";Integrated Security=True;Enlist=True;Application Name=SyncEngine";
            int exit = RunTool(Path.Combine(config.FrameworkDirectory, "bin", "SyncEngine.exe"), new[]
            {
                "-syncmode=partiallist", "-synclist=" + string.Join(",", names),
                "-metadatabinaries=" + config.FrameworkDirectory, "-connect=" + connection, "-fallbacktonative=False",
            }, log);
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = exit == 0,
                ["message"] = (exit == 0 ? "Synchronized " : "Synchronization FAILED (exit " + exit + ") for ") + string.Join(", ", names) + ".\n" + Tail(log.ToString(), 2000),
            });
        }

        public string BuildJson(string modelName, bool projectOnly, bool syncDatabase)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            var project = GetActiveProject();

            if (string.IsNullOrWhiteSpace(modelName))
                modelName = project?.ModelName;
            if (string.IsNullOrWhiteSpace(modelName))
                return json.Serialize(Failure("Pass modelName, or select a project with xpp_select_project."));
            var model = MetaService.GetModel(modelName);
            if (model == null || !IsCustomModel(model.Name))
                return json.Serialize(Failure("Model '" + modelName + "' is not one of your custom models; only your own models can be built."));
            if (projectOnly && project == null)
                return json.Serialize(Failure("scope 'project' needs an active project (xpp_select_project)."));

            string package = model.Module;
            string store = config.CustomMetadataFolder;
            string bin = Path.Combine(config.FrameworkDirectory, "bin");
            string packageFolder = Path.Combine(store, package);
            string logDir = Path.Combine(packageFolder, "BuildLogs");
            Directory.CreateDirectory(logDir);
            var log = new StringBuilder();
            var timer = Stopwatch.StartNew();

            var labelIds = LabelFileIds(Path.Combine(packageFolder, model.Name, "AxLabelFile"));
            if (labelIds.Count > 0)
            {
                int labelExit = RunTool(Path.Combine(bin, "LabelC.exe"), new[]
                {
                    "-metadata=" + store,
                    "-output=" + Path.Combine(packageFolder, "Resources"),
                    "-modelmodule=" + package,
                    "-names=" + string.Join(",", labelIds),
                }, log);
                if (labelExit != 0)
                    return json.Serialize(new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["message"] = "BUILD FAILED: label compilation (LabelC) exited with " + labelExit + ".\n" + Tail(log.ToString(), 3000),
                    });
            }

            string xmlLog = Path.Combine(logDir, "Dynamics.AX." + package + ".xppc.xml");
            string assembly = Path.Combine(packageFolder, "bin", "Dynamics.AX." + package + ".dll");
            DateTime assemblyBefore = File.Exists(assembly) ? File.GetLastWriteTimeUtc(assembly) : DateTime.MinValue;
            if (File.Exists(xmlLog)) File.Delete(xmlLog);

            var args = new List<string>
            {
                "-metadata=" + store,
                "-compilermetadata=" + (string.IsNullOrEmpty(config.RuntimePackagesDirectory) ? config.FrameworkDirectory : config.RuntimePackagesDirectory),
                "-modelmodule=" + package,
                "-output=" + Path.Combine(packageFolder, "bin"),
                "-xmllog=" + xmlLog,
                "-log=" + Path.Combine(logDir, "Dynamics.AX." + package + ".xppc.log"),
                "-appBase=" + bin,
                "-refPath=" + bin,
            };
            args.AddRange(config.ReferenceMetadataFolders.Select(r => "-referenceFolder=" + r));
            args.Add("-referenceFolder=" + store);
            if (!string.IsNullOrEmpty(config.CrossReferencesDbServerName) && !string.IsNullOrEmpty(config.CrossReferencesDatabaseName))
            {
                args.Add("-xref");
                args.Add("-xrefSqlServer=" + config.CrossReferencesDbServerName);
                args.Add("-xrefDbName=" + config.CrossReferencesDatabaseName);
            }

            int exit = RunTool(Path.Combine(bin, "xppc.exe"), args.ToArray(), log);
            var diagnostics = File.Exists(xmlLog) ? ParseBuildDiagnostics(xmlLog) : new List<BuildDiagnostic>();
            bool assemblyWritten = File.Exists(assembly) && File.GetLastWriteTimeUtc(assembly) > assemblyBefore;

            var blocking = diagnostics.Where(d => IsBlocking(d.Severity)).ToList();
            bool success = exit == 0 && blocking.Count == 0 && assemblyWritten;

            HashSet<string> projectKeys = null;
            if (projectOnly)
                projectKeys = new HashSet<string>(project.GetItems().Select(i => i.ObjectType + "/" + i.Name), StringComparer.OrdinalIgnoreCase);
            Func<BuildDiagnostic, bool> inScope = d => projectKeys == null || d.ObjectType == null
                || projectKeys.Contains(d.ObjectType + "/" + d.ObjectName);
            var reported = diagnostics.Where(inScope).ToList();

            var text = new StringBuilder();
            int errors = reported.Count(d => IsBlocking(d.Severity));
            int warnings = reported.Count - errors;
            text.Append(success ? "BUILD SUCCEEDED" : "BUILD FAILED").Append(": ").Append(package)
                .Append(projectOnly ? " (diagnostics of project " + project.Caption + " only; xppc always compiles the whole package)" : "")
                .Append(" in ").Append((int)timer.Elapsed.TotalSeconds).Append("s - ")
                .Append(errors).Append(" error(s), ").Append(warnings).Append(" warning(s)");
            if (!success && blocking.Count == 0)
                text.Append(exit != 0 ? ". xppc exited with " + exit : "").Append(!assemblyWritten ? ". No assembly was produced" : "");
            text.AppendLine(".");

            foreach (var d in reported.OrderBy(d => IsBlocking(d.Severity) ? 0 : 1).Take(MaxReportedDiagnostics))
                text.AppendLine(FormatDiagnostic(d));
            if (reported.Count > MaxReportedDiagnostics)
                text.AppendLine("... " + (reported.Count - MaxReportedDiagnostics) + " more in " + xmlLog);
            if (!success && blocking.Count == 0)
                text.AppendLine(Tail(log.ToString(), 2000));

            if (syncDatabase)
                text.AppendLine(success ? SyncDatabase(config, package, log) : "Database sync skipped because the build failed.");

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = success,
                ["message"] = text.ToString().TrimEnd(),
                ["diagnostics"] = reported.Select(d => new Dictionary<string, object>
                {
                    ["severity"] = d.Severity, ["objectType"] = d.ObjectType, ["objectName"] = d.ObjectName, ["member"] = d.Member,
                    ["line"] = d.Line, ["column"] = d.Column, ["moniker"] = d.Moniker, ["message"] = d.Message, ["path"] = d.Path,
                }).ToList(),
            });
        }

        private static bool IsBlocking(string severity) =>
            string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase) || string.Equals(severity, "Fatal", StringComparison.OrdinalIgnoreCase);

        private static string FormatDiagnostic(BuildDiagnostic d)
        {
            string where = d.ObjectType != null ? d.ObjectType + " " + d.ObjectName + (d.Member != null ? "." + d.Member : "") : d.Path;
            string message = d.Message ?? "";
            int stack = message.IndexOf("\n   at ", StringComparison.Ordinal);
            if (stack > 0) message = message.Substring(0, stack);
            return "  " + d.Severity.ToUpperInvariant() + " " + where + (d.Line.HasValue ? " line " + d.Line + (d.Column.HasValue ? ":" + d.Column : "") : "")
                + " [" + d.Moniker + "] " + message.Replace("\r", "").Replace("\n", " ");
        }

        public static List<BuildDiagnostic> ParseBuildDiagnostics(string xmlLog)
        {
            var result = new List<BuildDiagnostic>();
            XDocument doc;
            try { doc = XDocument.Load(xmlLog); }
            catch { return result; }
            foreach (var e in doc.Descendants().Where(x => x.Name.LocalName == "Diagnostic"))
            {
                string Get(string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value?.Trim();
                int? Num(string name) => int.TryParse(Get(name), out int v) ? v : (int?)null;
                var d = new BuildDiagnostic
                {
                    Severity = Get("Severity") ?? "Error",
                    Path = Get("Path"),
                    Moniker = Get("Moniker"),
                    Message = Get("Message"),
                    Line = Num("Line"),
                    Column = Num("Column"),
                };
                // dynamics://Class/Name/Method/m or /Query/Name
                var segments = (d.Path ?? "").Replace("dynamics://", "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    d.ObjectType = segments[0].StartsWith("Ax", StringComparison.Ordinal) ? segments[0] : "Ax" + segments[0];
                    d.ObjectName = segments[1];
                    if (segments.Length >= 4 && segments[segments.Length - 2].Equals("Method", StringComparison.OrdinalIgnoreCase))
                        d.Member = segments[segments.Length - 1];
                }
                result.Add(d);
            }
            return result;
        }

        private static List<string> LabelFileIds(string labelFolder)
        {
            if (!Directory.Exists(labelFolder)) return new List<string>();
            return Directory.GetFiles(labelFolder, "*.xml")
                .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int RunTool(string exe, string[] args, StringBuilder log)
        {
            if (!File.Exists(exe))
            {
                log.AppendLine("Not found: " + exe);
                return -1;
            }
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
            {
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            log.AppendLine("> " + System.IO.Path.GetFileName(exe) + " " + psi.Arguments);
            using (var process = Process.Start(psi))
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string Quote(string arg)
        {
            int eq = arg.IndexOf('=');
            if (arg.IndexOf(' ') < 0) return arg;
            // xppc expects -name="value with spaces".
            return eq > 0 ? arg.Substring(0, eq + 1) + "\"" + arg.Substring(eq + 1) + "\"" : "\"" + arg + "\"";
        }

        private static string Tail(string text, int max) => text.Length <= max ? text : "..." + text.Substring(text.Length - max);

        private static string SyncDatabase(DynamicsMetadataConfig config, string package, StringBuilder log)
        {
            if (config.IsUnifiedDeveloperEnvironment)
                return "Database sync: not available locally. This is a unified developer environment (cloud runtime, no local business database); "
                    + "the database is synchronized when the package is deployed to " + (config.CloudInstanceUrl ?? "the environment")
                    + " (Dynamics 365 > Deploy).";
            if (string.IsNullOrEmpty(config.DatabaseServer) || string.IsNullOrEmpty(config.BusinessDatabaseName))
                return "Database sync: the configuration names no database server/business database.";

            string bin = System.IO.Path.Combine(config.FrameworkDirectory, "bin");
            string connection = "Data Source=" + config.DatabaseServer + ";Initial Catalog=" + config.BusinessDatabaseName
                + ";Integrated Security=True;Enlist=True;Application Name=SyncEngine";
            int exit = RunTool(System.IO.Path.Combine(bin, "SyncEngine.exe"), new[]
            {
                "-syncmode=fullall",
                "-metadatabinaries=" + config.FrameworkDirectory,
                "-connect=" + connection,
                "-fallbacktonative=False",
            }, log);
            return exit == 0 ? "Database sync: succeeded." : "Database sync FAILED (exit " + exit + "):\n" + Tail(log.ToString(), 2000);
        }
    }
}
