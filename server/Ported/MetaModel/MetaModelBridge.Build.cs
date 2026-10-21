using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace XppVsCode.Server.MetaModel
{
    // Build (labels + X++) the way Visual Studio and the extension's Build command do, for agents.
    public sealed partial class MetaModelBridge
    {
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
                    d.ObjectType = segments[0].StartsWith("Ax") ? segments[0] : "Ax" + segments[0];
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
                .Select(f => Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(f), "_[^_]+$", ""))
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
