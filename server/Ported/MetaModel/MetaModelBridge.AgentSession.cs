using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>
    /// Copilot's writes under the user's control (F&O Studio: Copilot Checkpoint / Copilot Scope). The extension writes
    /// %LOCALAPPDATA%\XppVsCode\agentSession.json; the MCP server (the agent's process) reads it before each write: elements
    /// outside the scope are refused, and while a checkpoint is open each element's state before its first change is kept so
    /// the extension can restore it through the metadata API.
    /// </summary>
    public sealed partial class MetaModelBridge : IAgentSession
    {
        private static readonly Regex CheckpointId = new Regex("^[A-Za-z0-9-]{1,64}$");

        internal static string SessionFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode");

        private sealed class AgentSessionState
        {
            public string checkpoint { get; set; }
            public List<string> scope { get; set; }
        }

        private static AgentSessionState ReadAgentSession()
        {
            string file = Path.Combine(SessionFolder, "agentSession.json");
            try
            {
                return File.Exists(file) ? new JavaScriptSerializer().Deserialize<AgentSessionState>(File.ReadAllText(file)) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Why the agent may not change this element (outside the user's scope), or null. New elements are allowed and join the scope.</summary>
        public string AgentScopeDenial(string objectType, string objectName)
        {
            var scope = ReadAgentSession()?.scope;
            if (scope == null || string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName)) return null;
            string key = objectType + "/" + objectName;
            if (scope.Any(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase))) return null;
            if (!ObjectExists(objectType, objectName))
            {
                AddToScope(key);
                return null;
            }
            return objectType + " '" + objectName + "' is outside the elements the user allows Copilot to change (F&O Studio: Set Copilot Scope). "
                + "Ask the user to add it to the scope; the allowed elements are: " + string.Join(", ", scope.Take(30)) + (scope.Count > 30 ? ", ..." : "") + ".";
        }

        private static void AddToScope(string key)
        {
            string file = Path.Combine(SessionFolder, "agentSession.json");
            var state = ReadAgentSession();
            if (state?.scope == null) return;
            state.scope.Add(key);
            try { File.WriteAllText(file, new JavaScriptSerializer().Serialize(state)); }
            catch (IOException) { }
        }

        /// <summary>Keeps the element's state before its first change in the open checkpoint (existed = false for new elements).</summary>
        public void BeforeAgentWrite(string objectType, string objectName)
        {
            string id = ReadAgentSession()?.checkpoint;
            if (id == null || !CheckpointId.IsMatch(id) || string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName)) return;
            string folder = Path.Combine(SessionFolder, "checkpoints", id);
            string file = Path.Combine(folder, SafeName(objectType) + "__" + SafeName(objectName) + ".json");
            if (File.Exists(file)) return;
            Directory.CreateDirectory(folder);
            object element = ReadViaMetadataProvider(objectType, objectName, out string model);
            var entry = new Dictionary<string, object>
            {
                ["objectType"] = objectType,
                ["objectName"] = objectName,
                ["model"] = model ?? (element != null ? GetObjectModelName(objectType, objectName) : null),
                ["existed"] = element != null,
                ["time"] = DateTime.UtcNow.ToString("o"),
            };
            if (element != null)
            {
                entry["clrType"] = element.GetType().AssemblyQualifiedName;
                // A failed snapshot must not block the change itself: the entry records why this element can't be reverted.
                try
                {
                    using (var writer = new StringWriter())
                    {
                        new XmlSerializer(element.GetType()).Serialize(writer, element);
                        entry["xml"] = writer.ToString();
                    }
                }
                catch (Exception ex)
                {
                    var causes = new List<string>();
                    for (Exception e = ex; e != null; e = e.InnerException) causes.Add(e.Message);
                    entry["snapshotError"] = string.Join(" -> ", causes);
                    LogIgnored("Checkpoint snapshot of " + objectType + " " + objectName, ex);
                }
            }
            File.WriteAllText(file, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(entry));
        }

        /// <summary>The elements a checkpoint holds: what Copilot changed since it was opened.</summary>
        public string CheckpointEntriesJson(string id)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (id == null || !CheckpointId.IsMatch(id))
                return json.Serialize(Failure("Invalid checkpoint id."));
            var entries = ReadCheckpoint(id).Select(e => new Dictionary<string, object>
            {
                ["objectType"] = e["objectType"], ["objectName"] = e["objectName"], ["model"] = e.ContainsKey("model") ? e["model"] : null, ["existed"] = e["existed"],
            }).ToList();
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["entries"] = entries });
        }

        /// <summary>Restores every element of the checkpoint: changed ones to their saved state, new ones deleted.</summary>
        public string RevertCheckpointJson(string id)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (id == null || !CheckpointId.IsMatch(id))
                return json.Serialize(Failure("Invalid checkpoint id."));
            var restored = new List<string>();
            var failed = new List<string>();
            foreach (var e in ReadCheckpoint(id))
            {
                string objectType = (string)e["objectType"], objectName = (string)e["objectName"];
                string label = objectType + " " + objectName;
                try
                {
                    if (!(e["existed"] is bool existed && existed))
                    {
                        if (ObjectExists(objectType, objectName))
                        {
                            string result = DeleteObjectJson(objectType, objectName);
                            if (result.Contains("\"success\":true")) restored.Add(label + " (removed)");
                            else failed.Add(label + ": " + result);
                        }
                        continue;
                    }
                    string model = e.ContainsKey("model") ? e["model"] as string : null;
                    if (!IsCustomModel(model))
                    {
                        failed.Add(label + ": not in one of your models.");
                        continue;
                    }
                    if (!e.ContainsKey("xml"))
                    {
                        failed.Add(label + ": its state before the change could not be saved"
                            + (e.ContainsKey("snapshotError") ? " (" + e["snapshotError"] + ")" : "") + "; restore it from source control.");
                        continue;
                    }
                    Type type = Type.GetType((string)e["clrType"], throwOnError: false);
                    if (type == null || !type.Namespace.StartsWith("Microsoft.Dynamics.AX.Metadata", StringComparison.Ordinal))
                    {
                        failed.Add(label + ": unknown element type.");
                        continue;
                    }
                    object element;
                    using (var reader = new StringReader((string)e["xml"]))
                        element = new XmlSerializer(type).Deserialize(reader);
                    var saveInfo = GetModelSaveInfo(model);
                    object sub = SubProvider(objectType, out Type subType);
                    string method = ObjectExists(objectType, objectName) ? "Update" : "Create";
                    var save = sub == null ? null : FindProviderMethod(subType, method, type, typeof(ModelSaveInfo));
                    if (save == null || saveInfo == null)
                    {
                        failed.Add(label + ": cannot be saved through the metadata API.");
                        continue;
                    }
                    save.Invoke(sub, new object[] { element, saveInfo });
                    restored.Add(label);
                }
                catch (Exception ex)
                {
                    failed.Add(label + ": " + Cause(ex));
                }
            }
            _host.Reset();
            InvalidateCaches();
            return json.Serialize(new Dictionary<string, object> { ["success"] = failed.Count == 0, ["restored"] = restored, ["failed"] = failed,
                ["message"] = "Restored " + restored.Count + " element(s)" + (failed.Count > 0 ? "; " + failed.Count + " failed: " + string.Join("; ", failed) : ".") });
        }

        private static IEnumerable<Dictionary<string, object>> ReadCheckpoint(string id)
        {
            string folder = Path.Combine(SessionFolder, "checkpoints", id);
            if (!Directory.Exists(folder)) yield break;
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (string file in Directory.EnumerateFiles(folder, "*.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                Dictionary<string, object> entry;
                try { entry = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file)); }
                catch { continue; }
                if (entry != null && entry.ContainsKey("objectType") && entry.ContainsKey("objectName")) yield return entry;
            }
        }

        private static string SafeName(string name) => Regex.Replace(name, @"[^A-Za-z0-9_.-]", "_");
    }
}
