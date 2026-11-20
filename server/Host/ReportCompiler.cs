using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XppVsCode.Server.Host
{
    /// <summary>
    /// Compiles a package's reports into the RDL files the AOS reads at run time (<c>&lt;package&gt;\Reports</c>), as Visual
    /// Studio's build does, with Microsoft's reportsc.exe. src/reports/reportCompiler.ts follows the same rules; keep them alike.
    /// </summary>
    internal static class ReportCompiler
    {
        /// <summary>The package's report names (AxReport elements of every model in the package folder).</summary>
        public static List<string> ReportNames(string packageFolder)
        {
            var names = new List<string>();
            if (!Directory.Exists(packageFolder)) return names;
            foreach (string modelFolder in Directory.GetDirectories(packageFolder))
            {
                string reports = Path.Combine(modelFolder, "AxReport");
                if (Directory.Exists(reports))
                    names.AddRange(Directory.GetFiles(reports, "*.xml").Select(Path.GetFileNameWithoutExtension));
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// reportsc reads one metadata root, so the standard packages and the custom ones must sit side by side. A shared
        /// store (cloud-hosted and VHD environments) already is that root; otherwise a folder of junctions to every package
        /// is kept under %LOCALAPPDATA%\FOStudio\ReportMetadata.
        /// </summary>
        public static string MetadataRoot(DynamicsMetadataConfig config, StringBuilder log)
        {
            if (config.SharedStore) return config.CustomMetadataFolder;
            string name = string.Concat((config.ConfigurationName ?? "default").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_'));
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FOStudio", "ReportMetadata", name);
            Directory.CreateDirectory(root);

            // The custom folder wins over a reference package of the same name.
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in new[] { config.CustomMetadataFolder }.Concat(config.ReferenceMetadataFolders))
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                foreach (string package in Directory.GetDirectories(folder))
                    if (Directory.Exists(Path.Combine(package, "Descriptor")) && !wanted.ContainsKey(Path.GetFileName(package)))
                        wanted[Path.GetFileName(package)] = package;
            }

            // Links are recorded in a manifest (name|target) so a build checks them without asking Windows for each target.
            string manifest = Path.Combine(root, "links.txt");
            var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(manifest))
                foreach (string line in File.ReadAllLines(manifest))
                {
                    int bar = line.IndexOf('|');
                    if (bar > 0) recorded[line.Substring(0, bar)] = line.Substring(bar + 1);
                }

            foreach (string link in Directory.GetDirectories(root))
            {
                string package = Path.GetFileName(link);
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) continue;
                if (!wanted.TryGetValue(package, out string target) || !recorded.TryGetValue(package, out string was)
                    || !string.Equals(was, target, StringComparison.OrdinalIgnoreCase))
                    // Not recursive: removes the junction itself, never what it points to.
                    Directory.Delete(link, false);
            }
            var missing = wanted.Where(p => !Directory.Exists(Path.Combine(root, p.Key))).ToList();
            if (missing.Count > 0)
            {
                string script = Path.Combine(root, "links.cmd");
                File.WriteAllLines(script, new[] { "@echo off" }.Concat(missing.Select(p => "mklink /J \"" + Path.Combine(root, p.Key) + "\" \"" + p.Value + "\" >nul")));
                RunCmd("/c \"" + script + "\"");
                File.Delete(script);
                foreach (var p in missing.Where(p => !Directory.Exists(Path.Combine(root, p.Key))))
                    log.AppendLine("Could not link " + p.Value + " into " + root + ".");
            }
            File.WriteAllLines(manifest, wanted.Where(p => Directory.Exists(Path.Combine(root, p.Key))).Select(p => p.Key + "|" + p.Value));
            return root;
        }

        /// <summary>Runs reportsc for the package's reports; returns the exit code (0 when there is nothing to compile).</summary>
        public static int Compile(DynamicsMetadataConfig config, string package, string packageFolder, string logDir, StringBuilder log, out string xmlLog)
        {
            xmlLog = Path.Combine(logDir, "Dynamics.AX." + package + ".reportsc.xml");
            var names = ReportNames(packageFolder);
            RemoveStale(Path.Combine(packageFolder, "Reports"), names, log);
            if (names.Count == 0) return 0;
            if (File.Exists(xmlLog)) File.Delete(xmlLog);
            string root = MetadataRoot(config, log);
            string bin = Path.Combine(config.FrameworkDirectory, "bin");
            string output = Path.Combine(packageFolder, "Reports");
            Directory.CreateDirectory(output);
            return MetaModel.BuildService.RunTool(Path.Combine(bin, "reportsc.exe"), new[]
            {
                "-metadata=" + root,
                "-modelmodule=" + package,
                // Always by name: without it reportsc first deletes every .rdl in the output folder.
                "-name=" + string.Join(",", names),
                "-output=" + output,
                "-LabelsPath=" + root,
                "-log=" + Path.Combine(logDir, "Dynamics.AX." + package + ".reportsc.log"),
                "-xmllog=" + xmlLog,
            }, log);
        }

        /// <summary>Compiled reports (Report.Design.rdl) of reports the package no longer has would still be deployed; they go.</summary>
        public static void RemoveStale(string reportsFolder, List<string> names, StringBuilder log)
        {
            if (!Directory.Exists(reportsFolder)) return;
            var current = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(reportsFolder, "*.rdl"))
            {
                string report = Path.GetFileName(file).Split('.')[0];
                if (current.Contains(report)) continue;
                File.Delete(file);
                log.AppendLine("Removed " + Path.GetFileName(file) + ": its report is no longer in the package.");
            }
        }

        private static void RunCmd(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = System.Diagnostics.Process.Start(psi))
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
            }
        }
    }
}
