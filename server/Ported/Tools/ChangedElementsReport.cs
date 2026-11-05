using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using XppVsCode.Server.MetaModel;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// xpp_diff_changes: the elements of your models that changed in Git (uncommitted, and optionally on the branch since a
    /// base branch), so an agent can review or summarize exactly what changed.
    /// </summary>
    internal static class ChangedElementsReport
    {
        private static readonly Regex ElementPath = new Regex(@"^(Ax\w+)/([^/]+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Report(string baseBranch)
        {
            string root = MetaModelBridge.CustomFolder;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "No custom metadata folder is configured.";
            var changes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int repos = 0;
            foreach (string package in Directory.EnumerateDirectories(root))
                foreach (string model in Directory.EnumerateDirectories(package))
                {
                    string prefix = Git(model, "rev-parse --show-prefix");
                    if (prefix == null) continue;
                    repos++;
                    prefix = prefix.Trim();
                    string top = Git(model, "rev-parse --show-toplevel")?.Trim();
                    foreach (var c in ParseStatus(prefix, Git(top, "status --porcelain --untracked-files=all -- \"" + prefix.TrimEnd('/') + "\"") ?? ""))
                        changes[Path.GetFileName(model) + " " + c.Key] = c.Value + " (uncommitted)";
                    if (string.IsNullOrWhiteSpace(baseBranch)) continue;
                    if (!Regex.IsMatch(baseBranch, @"^[\w.][\w./-]*$")) return "Invalid base branch name.";
                    string mergeBase = Git(top, "merge-base HEAD " + baseBranch)?.Trim();
                    if (string.IsNullOrEmpty(mergeBase)) continue;
                    foreach (var c in ParseNameStatus(prefix, Git(top, "diff --name-status " + mergeBase + " HEAD -- \"" + prefix.TrimEnd('/') + "\"") ?? ""))
                        if (!changes.ContainsKey(Path.GetFileName(model) + " " + c.Key))
                            changes[Path.GetFileName(model) + " " + c.Key] = c.Value + " (on the branch)";
                }
            if (repos == 0) return "Your models are not in a Git repository, so changes can't be listed.";
            var sb = new StringBuilder();
            sb.Append(changes.Count).Append(" changed element(s)").Append(string.IsNullOrWhiteSpace(baseBranch) ? " (uncommitted)" : " (uncommitted and since " + baseBranch + ")").Append(':');
            foreach (var c in changes.Take(500)) sb.Append("\n- ").Append(c.Key).Append(": ").Append(c.Value);
            return sb.ToString();
        }

        /// <summary>"AxType/Name" to change for porcelain status lines under the model's repository prefix.</summary>
        internal static Dictionary<string, string> ParseStatus(string prefix, string porcelain)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in porcelain.Split('\n'))
            {
                if (line.Length < 4) continue;
                string code = line.Substring(0, 2).Trim();
                string file = line.Substring(3).Trim().Trim('"');
                int arrow = file.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) file = file.Substring(arrow + 4);
                Add(result, prefix, file, code == "??" ? "A" : code.Substring(0, 1));
            }
            return result;
        }

        internal static Dictionary<string, string> ParseNameStatus(string prefix, string nameStatus)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in nameStatus.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length >= 2 && parts[0].Length > 0) Add(result, prefix, parts[parts.Length - 1], parts[0].Substring(0, 1));
            }
            return result;
        }

        private static void Add(Dictionary<string, string> result, string prefix, string file, string status)
        {
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            var m = ElementPath.Match(file.Substring(prefix.Length));
            if (!m.Success) return;
            string word = status == "A" ? "added" : status == "D" ? "deleted" : status == "R" ? "renamed" : "modified";
            result[m.Groups[1].Value + "/" + m.Groups[2].Value] = word;
        }

        private static string Git(string folder, string arguments)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                var info = new ProcessStartInfo("git", "-C \"" + folder + "\" " + arguments)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using (var process = Process.Start(info))
                {
                    // Drain stderr in the background so a full stderr pipe can't block git while stdout is read.
                    process.ErrorDataReceived += (s, e) => { };
                    process.BeginErrorReadLine();
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(60000)) { process.Kill(); return null; }
                    return process.ExitCode == 0 ? output : null;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}
