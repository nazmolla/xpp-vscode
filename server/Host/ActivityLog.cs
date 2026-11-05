using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using XppVsCode.Server.MetaModel;

namespace XppVsCode.Server.Host
{
    /// <summary>
    /// What the agent asked the MCP server (one JSON line per tool call) for the extension's Copilot Activity view, plus a
    /// copy of each of your elements before an agent changes or deletes it, so the change can be reviewed as a diff.
    /// Only the MCP server Copilot starts writes it; the extension's own server processes don't.
    /// </summary>
    internal static class ActivityLog
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private const int MaxArgumentChars = 400;
        private const int MaxSnapshots = 200;
        private static readonly object Gate = new object();
        private static readonly HashSet<string> WriteTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "xpp_create_object", "xpp_update_object", "xpp_update_current_object", "xpp_delete_object",
        };

        public static bool Enabled { get; set; }

        private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode");

        public static bool IsWrite(string tool) => WriteTools.Contains(tool);

        /// <summary>Copies an element file of your models before a write; null when it doesn't exist yet (create).</summary>
        public static string Snapshot(string objectType, string objectName)
        {
            if (!Enabled || string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName))
                return null;
            try
            {
                string file = ElementFile(objectType, objectName);
                if (file == null) return null;
                string folder = Path.Combine(Folder, "snapshots");
                Directory.CreateDirectory(folder);
                string copy = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + objectType + "_" + objectName + ".xml");
                File.Copy(file, copy, true);
                foreach (var old in new DirectoryInfo(folder).GetFiles("*.xml").OrderByDescending(f => f.Name).Skip(MaxSnapshots))
                    old.Delete();
                return copy;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("Activity snapshot failed: " + ex.Message);
                return null;
            }
        }

        public static void Write(string tool, string arguments, long ms, bool ok, string objectType, string objectName, string snapshot)
        {
            if (!Enabled) return;
            var entry = new Dictionary<string, object>
            {
                ["time"] = DateTime.UtcNow.ToString("o"),
                ["tool"] = tool,
                ["ms"] = ms,
                ["ok"] = ok,
                ["arguments"] = arguments == null ? null : arguments.Length > MaxArgumentChars ? arguments.Substring(0, MaxArgumentChars) + "…" : arguments,
                ["objectType"] = objectType,
                ["objectName"] = objectName,
                ["write"] = IsWrite(tool),
                ["before"] = snapshot,
                ["after"] = IsWrite(tool) && ok ? ElementFile(objectType, objectName) : null,
            };
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    string file = Path.Combine(Folder, "activity.jsonl");
                    if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                    {
                        var lines = File.ReadAllLines(file);
                        File.WriteAllLines(file, lines.Skip(lines.Length / 2));
                    }
                    File.AppendAllText(file, new JavaScriptSerializer().Serialize(entry) + "\n");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("Activity log failed: " + ex.Message);
            }
        }

        /// <summary>The element's file in the custom metadata folder: &lt;folder&gt;\&lt;package&gt;\&lt;model&gt;\&lt;type&gt;\&lt;name&gt;.xml.</summary>
        private static string ElementFile(string objectType, string objectName)
        {
            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName)) return null;
            // Element names (extensions have a dot: CustTable.MyExtension); nothing that can leave the type folder.
            if (!System.Text.RegularExpressions.Regex.IsMatch(objectType, @"^Ax\w+$") || !System.Text.RegularExpressions.Regex.IsMatch(objectName, @"^\w+(\.\w+)?$")) return null;
            string root = MetaModelBridge.CustomFolder;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            foreach (string package in Directory.EnumerateDirectories(root))
                foreach (string model in Directory.EnumerateDirectories(package))
                {
                    string file = Path.Combine(model, objectType, objectName + ".xml");
                    if (File.Exists(file)) return file;
                }
            return null;
        }
    }
}
