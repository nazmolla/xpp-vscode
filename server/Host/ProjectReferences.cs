using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using XppVsCode.Server.MetaModel;

namespace XppVsCode.Server.Host
{
    /// <summary>
    /// What Visual Studio's build does before it compiles X++: builds the C# projects the X++ project references and copies
    /// what they need at run time (referenced assemblies with dependencies, PDB/XML files and satellites, files marked
    /// "Copy to Output Directory") into the package's bin folder. MSBuild runs on the .rnrproj with Visual Studio's build
    /// tasks replaced by a target that only resolves the references. The extension's build does the same
    /// (src/dotnetReferences.ts); keep the two alike.
    /// </summary>
    internal static class ProjectReferences
    {
        private const string TargetName = "XppStudioResolveReferences";
        private static readonly Regex BuildTasksImport = new Regex(@"<Import\s+Project=""\$\(BuildTasksDirectory\)\\([^""\\/]+\.targets)""", RegexOptions.IgnoreCase);

        public static bool HasReferences(string projectXml) =>
            Regex.IsMatch(projectXml, @"<(ProjectReference|Reference)\s") || Regex.IsMatch(projectXml, @"<CopyToOutputDirectory>\s*(Always|PreserveNewest)\s*<", RegexOptions.IgnoreCase);

        /// <summary>Builds the references of the project and copies their files into packageFolder\bin; false when that failed (see the log).</summary>
        public static bool Prepare(string projectFile, string packageFolder, string platformBin, StringBuilder log)
        {
            string xml = File.ReadAllText(projectFile);
            if (!HasReferences(xml)) return true;
            log.AppendLine("Building the projects and assemblies " + Path.GetFileName(projectFile) + " references...");

            string folder = Path.Combine(Path.GetTempPath(), "xpp-vscode-msbuild");
            string stubs = Path.Combine(folder, "BuildTasks");
            Directory.CreateDirectory(stubs);
            foreach (Match m in BuildTasksImport.Matches(xml))
                WriteIfChanged(Path.Combine(stubs, m.Groups[1].Value), "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />\r\n");
            string targets = Path.Combine(folder, "XppStudio.References.targets");
            WriteIfChanged(targets, "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n"
                + "  <Target Name=\"" + TargetName + "\" DependsOnTargets=\"BuildOnlySettings;ResolveReferences;GetCopyToOutputDirectoryItems\" />\n</Project>\n");
            string resultFile = Path.Combine(folder, "result-" + Process.GetCurrentProcess().Id + "-" + DateTime.UtcNow.Ticks + ".json");

            var (exe, prefix) = FindMsBuild();
            if (exe == null)
            {
                log.AppendLine("No MSBuild found: install Visual Studio, the Visual Studio Build Tools or the .NET SDK.");
                return false;
            }
            var args = new List<string>(prefix)
            {
                projectFile, "-nologo", "-v:minimal", "-nr:false", "-restore", "-p:RestorePackagesConfig=true",
                "-p:BuildTasksDirectory=" + stubs, "-p:CustomAfterMicrosoftCommonTargets=" + targets,
                "-t:" + TargetName, "-getItem:ReferenceCopyLocalPaths", "-getItem:AllItemsFullPathWithTargetPath", "-getResultOutputFile:" + resultFile,
            };
            try
            {
                if (BuildService.RunTool(exe, args.ToArray(), log) != 0 || !File.Exists(resultFile))
                {
                    log.AppendLine("Building the references failed.");
                    return false;
                }
                var result = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(resultFile));
                return Copy(Files(result), Path.Combine(packageFolder, "bin"), platformBin, log);
            }
            finally
            {
                try { File.Delete(resultFile); } catch (IOException) { }
            }
        }

        private sealed class ReferenceFile
        {
            public string Source, Target;
            public bool Always;
        }

        private static List<ReferenceFile> Files(Dictionary<string, object> result)
        {
            var files = new Dictionary<string, ReferenceFile>(StringComparer.OrdinalIgnoreCase);
            var items = result.TryGetValue("Items", out object i) ? i as Dictionary<string, object> : null;
            IEnumerable<Dictionary<string, object>> List(string name) =>
                items != null && items.TryGetValue(name, out object l) && l is System.Collections.ArrayList a ? a.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
            string Get(Dictionary<string, object> d, string key) => d.TryGetValue(key, out object v) ? v as string ?? "" : "";
            foreach (var item in List("ReferenceCopyLocalPaths"))
            {
                string source = Get(item, "FullPath").Length > 0 ? Get(item, "FullPath") : Get(item, "Identity");
                string target = Path.Combine(Get(item, "DestinationSubDirectory"), Path.GetFileName(source));
                files[target] = new ReferenceFile { Source = source, Target = target };
            }
            foreach (var item in List("AllItemsFullPathWithTargetPath"))
            {
                string mode = Get(item, "CopyToOutputDirectory");
                string target = Get(item, "TargetPath");
                bool always = mode.Equals("Always", StringComparison.OrdinalIgnoreCase);
                if ((always || mode.Equals("PreserveNewest", StringComparison.OrdinalIgnoreCase)) && target.Length > 0)
                    files[target] = new ReferenceFile { Source = Get(item, "FullPath").Length > 0 ? Get(item, "FullPath") : Get(item, "Identity"), Target = target, Always = always };
            }
            return files.Values.ToList();
        }

        /// <summary>Compiled X++ packages and Finance and Operations' own assemblies are never copied into a package.</summary>
        public static bool IsPlatformFile(string fileName, string platformBin) =>
            fileName.StartsWith("Dynamics.AX.", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(platformBin, fileName));

        private static bool Copy(List<ReferenceFile> files, string binFolder, string platformBin, StringBuilder log)
        {
            string root = Path.GetFullPath(binFolder).TrimEnd('\\') + "\\";
            var copied = new List<string>();
            bool ok = true;
            foreach (var file in files)
            {
                string target = Path.GetFullPath(Path.Combine(root, file.Target));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    log.AppendLine("Not copied: " + file.Target + " is outside the package's bin folder.");
                    ok = false;
                    continue;
                }
                if (IsPlatformFile(Path.GetFileName(target), platformBin))
                {
                    log.AppendLine("Not copied: " + file.Target + " is part of Finance and Operations (reference it with Copy Local = False).");
                    continue;
                }
                try
                {
                    var source = new FileInfo(file.Source);
                    var existing = new FileInfo(target);
                    // Newer than the source is up to date: the build converts copied PDBs to portable ones in place.
                    if (!file.Always && existing.Exists && existing.LastWriteTimeUtc >= source.LastWriteTimeUtc)
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source.FullName, target, true);
                    copied.Add(file.Target);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    log.AppendLine("Could not copy " + file.Source + " to " + target + ": " + ex.Message);
                    ok = false;
                }
            }
            if (copied.Count > 0)
                log.AppendLine("Copied " + copied.Count + " referenced file(s) into " + binFolder + ": " + string.Join(", ", copied));
            return ok;
        }

        // Referenced projects import these files too (global properties flow to them) and imported files are compile inputs.
        private static void WriteIfChanged(string file, string text)
        {
            if (!File.Exists(file) || File.ReadAllText(file) != text)
                File.WriteAllText(file, text);
        }

        /// <summary>Visual Studio's or the Build Tools' MSBuild (vswhere), else the .NET SDK's.</summary>
        private static (string exe, string[] prefix) FindMsBuild()
        {
            string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
            {
                var output = new StringBuilder();
                var psi = new ProcessStartInfo(vswhere, "-latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                using (var process = Process.Start(psi))
                {
                    output.Append(process.StandardOutput.ReadToEnd());
                    process.WaitForExit(30000);
                }
                string found = output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(File.Exists);
                if (found != null) return (found, new string[0]);
            }
            string dotnet = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
                .Where(p => p.Trim().Length > 0).Select(p => Path.Combine(p.Trim(), "dotnet.exe")).FirstOrDefault(File.Exists);
            return dotnet != null ? (dotnet, new[] { "msbuild" }) : (null, null);
        }
    }
}
