using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Routes MCP tool calls to the MetaModel bridge running inside the VSIX.
    /// All metadata operations go through the strongly-typed MetaModel API.
    /// </summary>
    internal sealed class ToolRouter
    {
        private readonly BridgeClient _bridge;
        private readonly DocSearchHandler _docSearch;
        private readonly ModelObjectLister _modelLister = new ModelObjectLister();
        private readonly XRefQuery _xref = new XRefQuery();
        internal XRefQuery XRef => _xref;
        private readonly CodeSearch _codeSearch = new CodeSearch();
        private CodeExamples _examples;
        private CodeExamples Examples => _examples ?? (_examples = new CodeExamples(_xref));
        private readonly SecurityAnalysis _security = new SecurityAnalysis();
        private MessageTrace _messageTrace;
        private MessageTrace Messages => _messageTrace ?? (_messageTrace = new MessageTrace(_xref));

        /// <summary>The bridge actions served from the cross-reference database; they don't use the metadata API and run at once.</summary>
        public void AddXRefActions(MetaModel.BridgeActionRegistry actions)
        {
            actions.Add(new MetaModel.BridgeAction("where_used", MetaModel.WriteKind.Read, WhereUsedJson, usesMetadata: false));
            actions.Add(new MetaModel.BridgeAction("list_tags", MetaModel.WriteKind.Read, TagsJson, usesMetadata: false));
            actions.Add(new MetaModel.BridgeAction("tag_references", MetaModel.WriteKind.Read, TagReferencesJson, usesMetadata: false));
            actions.Add(new MetaModel.BridgeAction("label_usage", MetaModel.WriteKind.Read, LabelUsageJson, usesMetadata: false));
            actions.Add(new MetaModel.BridgeAction("element_dependencies", MetaModel.WriteKind.Read, DependenciesJson, usesMetadata: false));
        }

        /// <summary>Structured where-used for the extension's references list.</summary>
        public string WhereUsedJson(string body)
        {
            int max = int.TryParse(JsonHelpers.ExtractJsonString(body, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 1000)) : 500;
            return _xref.QueryJson(JsonHelpers.ExtractJsonString(body, "objectType"), JsonHelpers.ExtractJsonString(body, "objectName"),
                JsonHelpers.ExtractJsonString(body, "memberName"), max, JsonHelpers.ExtractJsonString(body, "referenceKind"));
        }

        public string TagsJson(string body) => _xref.TagsJson(JsonHelpers.ExtractJsonString(body, "filter"), 500);

        public string TagReferencesJson(string body) => _xref.TagReferencesJson(JsonHelpers.ExtractJsonString(body, "tag"), 1000);

        public string DependenciesJson(string body) => _xref.DependenciesJson(JsonHelpers.ExtractJsonString(body, "objectType"),
            JsonHelpers.ExtractJsonString(body, "objectName"), int.TryParse(JsonHelpers.ExtractJsonString(body, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 500)) : 200);

        /// <summary>The label IDs of a label file that the cross-reference database records a use of.</summary>
        public string LabelUsageJson(string body)
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string file = JsonHelpers.ExtractJsonString(body, "labelFileId");
            if (string.IsNullOrWhiteSpace(file) || !System.Text.RegularExpressions.Regex.IsMatch(file, @"^\w+$"))
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "labelFileId is required." });
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null)
                    return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "The cross-reference database is not available: " + problem });
                var used = new List<string>();
                using (var cmd = new System.Data.SqlClient.SqlCommand("SELECT t.Path FROM Names t WHERE t.Path LIKE @p AND EXISTS (SELECT 1 FROM [References] r WHERE r.TargetId = t.Id)", conn) { CommandTimeout = 120 })
                {
                    cmd.Parameters.AddWithValue("@p", "/Labels/@" + file + ":%");
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) used.Add(r.GetString(0).Substring(("/Labels/@" + file + ":").Length));
                }
                return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["used"] = used });
            }
        }

        // Above this total method source size, reads return signatures only unless methodNames is given.
        private const int MaxInlineMethodSourceChars = 40000;


        public ToolRouter(BridgeClient bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _docSearch = new DocSearchHandler();
            RegisterTools();
            _tools.AttachDefinitions(McpToolRegistry.LoadDefinitions());
        }

        private readonly McpToolRegistry _tools = new McpToolRegistry();

        /// <summary>The agent tools: handlers, traits and definitions.</summary>
        internal McpToolRegistry Tools => _tools;

        /// <summary>Read-only tools run in parallel with other requests.</summary>
        public bool IsParallelRead(string toolName) => _tools.Find(toolName)?.Is(ToolTraits.ParallelRead) == true;

        private void RegisterTools()
        {
            const ToolTraits Read = ToolTraits.ParallelRead, Write = ToolTraits.WritesElement, None = ToolTraits.None;
            var t = _tools;
            // Elements
            t.Add("xpp_create_object", Write, (id, json) => DelegateToBridge(id, json, "create_object"));
            t.Add("xpp_read_object", Read, HandleReadObject);
            t.Add("xpp_update_object", Write, (id, json) => DelegateToBridge(id, json, "update_object"));
            t.Add("xpp_update_current_object", Write | ToolTraits.Unlisted, (id, json) => DelegateToBridge(id, json, "update_object"));
            t.Add("xpp_delete_object", Write, HandleDeleteObject);
            t.Add("xpp_validate_object", Read, HandleValidateObject);
            t.Add("xpp_get_object_type_schema", Read, HandleGetObjectTypeSchema);
            t.Add("xpp_get_form_layout", Read, HandleGetFormLayout);
            // Discovery
            t.Add("xpp_find_object", Read, HandleFindObject);
            t.Add("xpp_list_objects", Read, HandleListObjects);
            t.Add("xpp_list_model_objects", Read, _modelLister.Handle);
            t.Add("xpp_find_references", Read, HandleFindReferences);
            t.Add("xpp_find_examples", Read, HandleFindExamples);
            t.Add("xpp_search_code", Read, HandleSearchCode);
            t.Add("xpp_find_message_source", Read, HandleFindMessageSource);
            t.Add("xpp_get_model_info", Read, HandleGetModelInfo);
            t.Add("xpp_list_models", Read, (id, json) => DelegateToBridge(id, json, "list_models"));
            t.Add("xpp_get_environment", Read, (id, json) => DelegateToBridge(id, json, "get_environment"));
            t.Add("xpp_search_docs", Read, _docSearch.Handle);
            t.Add("xpp_get_guide", Read, (id, json) => JsonHelpers.BuildToolResult(id,
                AgentGuide.Get("XppVsCode.Server.Guide.txt", JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "topic"))));
            // Labels
            t.Add("xpp_read_label", Read, HandleReadLabel);
            t.Add("xpp_create_label", None, HandleCreateLabel);
            // Project
            t.Add("xpp_add_to_project", None, HandleAddToProject);
            t.Add("xpp_list_project_items", Read, (id, json) => DelegateToBridge(id, json, "list_project_items"));
            t.Add("xpp_remove_from_project", None, (id, json) => DelegateToBridge(id, json, "remove_from_project"));
            t.Add("xpp_select_project", None, (id, json) => DelegateToBridge(id, json, "select_project"));
            // Build, best practice, packages, changes
            t.Add("xpp_build", None, (id, json) => DelegateToBridge(id, json, "build"));
            t.Add("xpp_check_best_practice", None, (id, json) => DelegateToBridge(id, json, "best_practice"));
            t.Add("xpp_package", None, (id, json) => DelegateToBridge(id, json, "create_package"));
            t.Add("xpp_diff_changes", None, (id, json) => JsonHelpers.BuildToolResult(id,
                ChangedElementsReport.Report(JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "baseBranch"))));
            // Analysis
            t.Add("xpp_analyze_security", Read, HandleAnalyzeSecurity);
            t.Add("xpp_analyze_performance", Read, HandleAnalyzePerformance);
            t.Add("xpp_find_deprecations", Read, HandleFindDeprecations);
            t.Add("xpp_analyze_trace", Read, HandleAnalyzeTrace);
        }

        /// <summary>tools/list: the registered tools with their definitions (Resources/Tools.json).</summary>
        public string BuildToolsListResponse(string idToken) => JsonHelpers.BuildResult(idToken, _tools.ListJson());

        public string HandleToolCall(string idToken, string json)
        {
            string toolName = JsonHelpers.ExtractNestedString(json, "params", "name");
            if (!Host.ActivityLog.Enabled)
                return DispatchTool(idToken, json, toolName);
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractJsonString(args ?? "{}", "objectType");
            string objectName = JsonHelpers.ExtractJsonString(args ?? "{}", "objectName");
            bool write = _tools.Find(toolName)?.Is(ToolTraits.WritesElement) == true;
            string snapshot = write ? Host.ActivityLog.Snapshot(objectType, objectName) : null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string response = DispatchTool(idToken, json, toolName);
            bool failed = response == null || response.Contains("\"isError\":true") || response.Contains("\"error\":{");
            Host.ActivityLog.Write(toolName, args, watch.ElapsedMilliseconds, !failed, objectType, objectName, snapshot, write);
            return response;
        }

        private string DispatchTool(string idToken, string json, string toolName)
        {
            McpLogger.Log("tools/call name=" + (toolName ?? "<null>"));
            McpTool tool = _tools.Find(toolName);
            return tool != null ? tool.Handler(idToken, json) : JsonHelpers.BuildError(idToken, -32602, "Unknown tool: " + toolName);
        }

        private static string HandleAnalyzeTrace(string idToken, string json)
        {
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            string min = JsonHelpers.ExtractJsonValueToken(args, "minRequestMs");
            double minRequestMs = double.TryParse(min, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 50;
            try
            {
                var filter = Host.TraceFilter.Create(key => JsonHelpers.ExtractArgString(args, key));
                return JsonHelpers.BuildToolResult(idToken, Host.TraceService.AnalyzeText(JsonHelpers.ExtractArgString(args, "path"), minRequestMs, filter));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is System.IO.IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException || ex is FormatException)
            {
                return JsonHelpers.BuildToolResult(idToken, ex.Message, isError: true);
            }
        }

        private string HandleFindDeprecations(string idToken, string json)
        {
            string model = JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "modelName");
            string previous = JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "previousPackagesFolder");
            if (string.IsNullOrWhiteSpace(model))
                return JsonHelpers.BuildToolResult(idToken, "Pass modelName.", isError: true);
            // The cross-reference database groups code by package; a model's package usually has its name.
            string body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string, string> { ["modelName"] = model });
            string info = _bridge.IsAvailable() ? _bridge.Call("get_model_info", body) : null;
            string package = (info == null ? null : JsonHelpers.ExtractJsonString(info, "package") ?? JsonHelpers.ExtractJsonString(info, "Package")) ?? model;
            string report = new DeprecationReport(_xref, Examples).Report(package);
            if (_bridge.IsAvailable())
                report += "\n\n" + CompatibilityText(_bridge.Call("extension_compatibility", body))
                    + "\n\n" + CollisionsText(_bridge.Call("extension_collisions", body));
            if (_bridge.IsAvailable() && !string.IsNullOrWhiteSpace(previous))
                report += "\n\n" + DriftText(_bridge.Call("wrapper_drift", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
                    new Dictionary<string, string> { ["modelName"] = model, ["previousFolder"] = Environment.ExpandEnvironmentVariables(previous) })));
            return JsonHelpers.BuildToolResult(idToken, report);
        }

        /// <summary>Clashes with other models' extensions of the same elements.</summary>
        internal static string CollisionsText(string resultJson)
        {
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(resultJson);
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return "Collisions with other models: " + (result.TryGetValue("message", out object m) ? m : "not checked") + ".";
            var findings = ((System.Collections.ArrayList)result["findings"]).Cast<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append("Collisions with other models: ").Append(result["extensions"]).Append(" extension(s) compared with the other installed models' extensions of the same elements; ")
              .Append(findings.Count == 0 ? "no clashes." : findings.Count + " finding(s):");
            foreach (var f in findings.Take(200))
                sb.Append("\n- ").Append(f["severity"]).Append(" ").Append(f["rule"]).Append(": ").Append(f["objectName"])
                  .Append(f["member"] is string member && member.Length > 0 ? "." + member : "").Append(": ").Append(f["message"]);
            return sb.ToString();
        }

        /// <summary>Wrapped standard methods whose code changed since the earlier version (names and size of the change; no code).</summary>
        internal static string DriftText(string resultJson)
        {
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(resultJson);
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return "Standard code drift: " + (result.TryGetValue("message", out object m) ? m : "not checked") + ".";
            var changed = ((System.Collections.ArrayList)result["changed"]).Cast<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append("Standard code drift: ").Append(result["wrappers"]).Append(" wrapper(s); ").Append(changed.Count).Append(" target method(s) changed since the earlier version, ")
              .Append(result["unchanged"]).Append(" unchanged").Append(Convert.ToInt32(result["notFound"]) > 0 ? ", " + result["notFound"] + " not found in one of the versions" : "").Append('.');
            foreach (var c in changed.Take(100))
                sb.Append("\n- ").Append(c["className"]).Append('.').Append(c["method"]).Append(" wraps ").Append(c["target"]).Append('.').Append(c["method"])
                  .Append(": ").Append(c["changedLines"]).Append(" line(s) differ; read both versions and check the logic around next.");
            return sb.ToString();
        }

        /// <summary>Readable extension compatibility findings (Chain of Command, handlers, metadata extensions).</summary>
        internal static string CompatibilityText(string resultJson)
        {
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(resultJson);
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return "Extension compatibility: " + (result.TryGetValue("message", out object m) ? m : "not checked") + ".";
            var findings = ((System.Collections.ArrayList)result["findings"]).Cast<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append("Extension compatibility: ").Append(result["wrappers"]).Append(" Chain of Command wrapper(s), ").Append(result["handlers"])
                .Append(" event handler(s) and ").Append(result["extensions"]).Append(" metadata extension(s) checked against the installed code; ")
                .Append(findings.Count == 0 ? "no problems found." : findings.Count + " finding(s):");
            foreach (var f in findings.Take(300))
                sb.Append("\n- ").Append(f["severity"]).Append(" ").Append(f["rule"]).Append(": ").Append(f["objectName"])
                    .Append(f["member"] is string member && member.Length > 0 ? "." + member : "").Append(": ").Append(f["message"]);
            return sb.ToString();
        }

        private string HandleAnalyzePerformance(string idToken, string json)
        {
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            if (!_bridge.IsAvailable())
                return JsonHelpers.BuildToolResult(idToken, "MetaModel bridge is not available.", isError: true);
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Deserialize<Dictionary<string, object>>(_bridge.Call("analyze_performance", args));
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return JsonHelpers.BuildToolResult(idToken, result.TryGetValue("message", out object m) ? m as string : "Analysis failed.", isError: true);
            var findings = (result["findings"] as System.Collections.ArrayList ?? new System.Collections.ArrayList()).OfType<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append(result["objectName"]).Append(": ").Append(findings.Count).Append(" performance finding(s) in ").Append(result["methods"]).Append(" method(s).");
            foreach (var f in findings.Take(100))
                sb.Append("\n- [").Append(f["severity"]).Append("] ").Append(f["rule"]).Append(" in ").Append(f["method"]).Append(" line ").Append(f["line"])
                  .Append(": ").Append(f["message"]).Append(" Fix: ").Append(f["fix"]);
            if (findings.Count > 100) sb.Append("\n... ").Append(findings.Count - 100).Append(" more.");
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
        }

        private string DelegateToBridge(string idToken, string json, string action, Func<string, string> postText = null)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);

            // Log full arguments for create/update to aid debugging
            if (action == "create_object" || action == "update_object")
                McpLogger.Log("bridge args (" + action + "): " + argsJson);

            if (!_bridge.IsAvailable())
            {
                return JsonHelpers.BuildToolResult(idToken,
                    "MetaModel bridge is not available. Check that the active D365FO configuration points to an extracted PackagesLocalDirectory.",
                    isError: true);
            }

            string bridgeResponse = _bridge.Call(action, argsJson);

            // Parse bridge response and format as MCP tool result
            string message = JsonHelpers.ExtractJsonString(bridgeResponse, "message");

            // validate_object reports its outcome as "valid" rather than "success".
            bool isSuccess = ReadBool(bridgeResponse, action == "validate_object" ? "valid" : "success");
            bool isError = !isSuccess;

            // For rich results, pass through relevant data
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(message))
                sb.AppendLine(message);

            string validation = JsonHelpers.ExtractNestedObject(bridgeResponse, "validation");
            if (isSuccess && !string.IsNullOrEmpty(validation))
            {
                bool valid = ReadBool(validation, "valid");
                sb.AppendLine("Validation: " + (valid ? "PASSED" : "FAILED") + " - " + JsonHelpers.ExtractJsonString(validation, "message"));
                if (!valid)
                {
                    // The object is saved but does not match the request: report it as an error so it is not missed.
                    isError = true;
                    sb.AppendLine("The object was saved, but it does not contain everything requested (see the mismatches above). "
                        + "Fix it with xpp_update_object, or delete it with xpp_delete_object.");
                }
            }

            if (isSuccess && (action == "list_objects" || action == "find_object")
                && ReadObjectArray(bridgeResponse, action == "find_object" ? "matches" : "objects").Count == 0
                && string.IsNullOrEmpty(message))
                sb.AppendLine("0 matches.");
            if (isSuccess && action == "list_project_items" && ReadObjectArray(bridgeResponse, "items").Count == 0)
                sb.AppendLine(ReadBool(bridgeResponse, "hasProject")
                    ? "The active project is empty."
                    : "No active project. Select one with xpp_select_project.");

            string layout = JsonHelpers.ExtractJsonString(bridgeResponse, "layout");
            if (!string.IsNullOrEmpty(layout))
                sb.AppendLine(layout);

            // Append matches/objects/items/labels if present
            AppendArrayField(sb, bridgeResponse, "matches", "objectType", "objectName", "modelName", "isCustom", "label");
            AppendArrayField(sb, bridgeResponse, "objects", "objectType", "objectName", "modelName", "isCustom");
            AppendArrayField(sb, bridgeResponse, "models", "name", "displayName", "isCustom");
            AppendArrayField(sb, bridgeResponse, "labels", "id", "text");
            AppendArrayField(sb, bridgeResponse, "items", "name", "filePath");

            // Append read result fields
            string declaration = JsonHelpers.ExtractJsonString(bridgeResponse, "declaration");
            if (!string.IsNullOrEmpty(declaration))
            {
                sb.AppendLine("\n=== Declaration ===");
                sb.AppendLine(declaration);
            }

            if (action == "read_object" || action == "read_object_by_path")
            {
                AppendMethods(sb, bridgeResponse, JsonHelpers.ExtractJsonStringArray(argsJson, "methodNames"));
                AppendGenericMetadata(sb, bridgeResponse);
            }
            if (action == "find_references")
                AppendReferences(sb, bridgeResponse);

            // Append typed metadata (properties, enumValues, fields, indexes, etc.)
            AppendTypedMetadata(sb, bridgeResponse);

            string objectType = JsonHelpers.ExtractJsonString(bridgeResponse, "objectType");
            string objectName = JsonHelpers.ExtractJsonString(bridgeResponse, "objectName");
            string modelName = JsonHelpers.ExtractJsonString(bridgeResponse, "modelName");
            string isCustom = ReadBool(bridgeResponse, "isCustom") ? "true" : "false";
            bool hasEditability = JsonHelpers.ExtractJsonValueToken(bridgeResponse, "isCustom") != null;

            if (!string.IsNullOrEmpty(objectType))
                sb.Insert(0, "ObjectType: " + objectType + "\nName: " + objectName
                    + "\nModel: " + modelName
                    + (hasEditability ? "\nEditable: " + (isCustom == "true" ? "YES" : "NO") : "") + "\n\n");

            // Environment info
            string customFolder = JsonHelpers.ExtractJsonString(bridgeResponse, "customMetadataFolder");
            if (!string.IsNullOrEmpty(customFolder))
            {
                sb.AppendLine("CustomMetadataFolder: " + customFolder);
                string projectName = JsonHelpers.ExtractJsonString(bridgeResponse, "activeProjectName");
                string activeModel = JsonHelpers.ExtractJsonString(bridgeResponse, "activeModelName");
                string layer = JsonHelpers.ExtractJsonString(bridgeResponse, "activeModelLayer");
                if (!string.IsNullOrEmpty(projectName)) sb.AppendLine("ActiveProject: " + projectName);
                else sb.AppendLine("ActiveProject: none. New objects are not added to a project and scope=project builds are unavailable; list and select one with xpp_select_project.");
                if (!string.IsNullOrEmpty(activeModel)) sb.AppendLine("ActiveModel: " + activeModel);
                if (!string.IsNullOrEmpty(layer)) sb.AppendLine("Layer: " + layer);
                foreach (string folder in JsonHelpers.ExtractJsonStringArray(bridgeResponse, "referenceMetadataFolders") ?? new string[0])
                    sb.AppendLine("ReferenceMetadataFolder: " + folder);
            }

            if (action == "get_model_info" && isSuccess)
            {
                foreach (string key in new[] { "name", "displayName", "publisher", "version", "layer", "modelId", "package", "dependencies" })
                {
                    string value = JsonHelpers.ExtractJsonString(bridgeResponse, key)
                        ?? JsonHelpers.ExtractJsonValueToken(bridgeResponse, key);
                    if (!string.IsNullOrEmpty(value))
                        sb.AppendLine(char.ToUpperInvariant(key[0]) + key.Substring(1) + ": " + value);
                }
            }

            string text = sb.ToString().Trim();
            if (string.IsNullOrEmpty(text)) text = isError ? "Operation failed." : "Operation completed.";
            if (postText != null && !isError) text = postText(text);

            return JsonHelpers.BuildToolResult(idToken, text, isError);
        }

        // Bridge returns booleans as JSON true/false; older paths may send them as strings.
        private static bool ReadBool(string json, string key)
        {
            string token = JsonHelpers.ExtractJsonValueToken(json, key);
            if (string.IsNullOrWhiteSpace(token)) return false;
            return "true".Equals(token.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
        }

        private static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> ReadObjectArray(string json, string key)
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (serializer.DeserializeObject(json) is System.Collections.Generic.Dictionary<string, object> root
                    && root.TryGetValue(key, out object value) && value is object[] items)
                    foreach (var item in items)
                        if (item is System.Collections.Generic.Dictionary<string, object> d) result.Add(d);
            }
            catch (Exception ex)
            {
                McpLogger.Log("ReadObjectArray(" + key + ") failed: " + ex.Message);
            }
            return result;
        }

        private static string Str(System.Collections.Generic.Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) ? v?.ToString() : null;

        private static void AppendMethods(StringBuilder sb, string bridgeResponse, string[] methodNames)
        {
            var methods = ReadObjectArray(bridgeResponse, "methods");
            if (methods.Count == 0) return;

            var wanted = methodNames == null || methodNames.Length == 0 ? null
                : new System.Collections.Generic.HashSet<string>(methodNames, StringComparer.OrdinalIgnoreCase);
            // With methodNames the caller already knows the signatures; listing all of them again only floods the context.
            if (wanted == null)
            {
                sb.AppendLine("\n=== Methods (" + methods.Count + ") ===");
                foreach (var m in methods)
                    sb.AppendLine("  " + (MethodSignature(Str(m, "source")) ?? Str(m, "name")));
            }
            else
                sb.AppendLine("\n=== Methods (requested " + wanted.Count + " of " + methods.Count + ") ===");
            var selected = wanted == null ? methods : methods.FindAll(m => wanted.Contains(Str(m, "name") ?? ""));
            int totalChars = 0;
            foreach (var m in selected) totalChars += (Str(m, "source") ?? "").Length;

            if (wanted != null && selected.Count < wanted.Count)
            {
                var found = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in selected) found.Add(Str(m, "name") ?? "");
                sb.AppendLine("\nNot found on this object: " + string.Join(", ", System.Linq.Enumerable.Where(methodNames, n => !found.Contains(n))));
            }

            if (wanted == null && totalChars > MaxInlineMethodSourceChars)
            {
                sb.AppendLine("\nMethod source omitted: " + totalChars + " characters in total. "
                    + "Call xpp_read_object again with methodNames=[...] (from the list above) to get the full source of the methods you need.");
                return;
            }

            foreach (var m in selected)
            {
                sb.AppendLine("\n--- " + Str(m, "name") + " ---");
                sb.AppendLine(Str(m, "source"));
            }
        }

        // First declaration line(s) of a method, without doc comments and attributes, so access modifiers are visible.
        private static string MethodSignature(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            var sig = new StringBuilder();
            int depth = 0;
            bool opened = false;
            foreach (string raw in source.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (sig.Length == 0 && (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal)
                    || line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)))
                    continue;
                if (line.StartsWith("{", StringComparison.Ordinal)) break;
                sig.Append(sig.Length > 0 ? " " : "").Append(line);
                foreach (char c in line)
                {
                    if (c == '(') { depth++; opened = true; }
                    else if (c == ')') depth--;
                }
                if (opened && depth <= 0) break;
            }
            return sig.Length > 0 ? sig.ToString() : null;
        }

        // Types without dedicated extractors (maps, reports, workflows...) would otherwise hide their structure.
        private const int MaxGenericMetadataChars = 30000;
        private static readonly System.Collections.Generic.HashSet<string> TypesWithDedicatedOutput =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AxClass", "AxTable", "AxForm", "AxEnum", "AxEdt", "AxView", "AxQuery", "AxDataEntityView",
                "AxTableExtension", "AxEnumExtension", "AxEdtExtension", "AxFormExtension"
            };

        private static void AppendGenericMetadata(StringBuilder sb, string bridgeResponse)
        {
            string readType = JsonHelpers.ExtractJsonString(bridgeResponse, "objectType") ?? "";
            bool compositeQuery = readType == "AxQuery"
                && !string.IsNullOrEmpty(JsonHelpers.ExtractJsonString(JsonHelpers.ExtractNestedObject(bridgeResponse, "properties") ?? "{}", "Query"));
            if (TypesWithDedicatedOutput.Contains(readType) && !compositeQuery) return;

            string typed = JsonHelpers.ExtractNestedObject(bridgeResponse, "typedMetadata");
            if (string.IsNullOrEmpty(typed) || typed.Length < 3) return;

            sb.AppendLine("\n=== Metadata (typedMetadata) ===");
            if (typed.Length > MaxGenericMetadataChars)
                sb.AppendLine(typed.Substring(0, MaxGenericMetadataChars) + "\n[truncated at " + MaxGenericMetadataChars
                    + " characters; the full graph is available to xpp_update_object via typedMetadata]");
            else
                sb.AppendLine(typed);
        }

        private static void AppendReferences(StringBuilder sb, string bridgeResponse)
        {
            var refs = ReadObjectArray(bridgeResponse, "references");
            if (refs.Count == 0) return;
            sb.AppendLine("\n=== References (" + refs.Count + ", via metadata provider) ===");
            foreach (var r in refs)
                sb.AppendLine("  " + (Str(r, "sourceObjectType") ?? "?") + " " + (Str(r, "sourceObjectName") ?? Str(r, "sourcePath"))
                    + (Str(r, "sourceMember") != null ? "." + Str(r, "sourceMember") + "()" : "")
                    + " line " + Str(r, "lineNumber") + ": " + Str(r, "kind")
                    + (Str(r, "sourceModule") != null ? "  [" + Str(r, "sourceModule") + "]" : ""));
        }

        private string HandleDeleteObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName");
            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken, "Parameters 'objectType' and 'objectName' are required.", isError: true);

            string note = "";
            // Ownership and existence come first, so a standard object is refused for that reason, not for its where-used.
            string check = _bridge.Call("delete_object", "{\"objectType\":\"" + JsonHelpers.EscapeJsonString(objectType)
                + "\",\"objectName\":\"" + JsonHelpers.EscapeJsonString(objectName) + "\",\"checkOnly\":\"true\"}");
            if (!ReadBool(check, "success"))
                return JsonHelpers.BuildToolResult(idToken, JsonHelpers.ExtractJsonString(check, "message") ?? "Delete failed.", isError: true);

            if (!ReadBool(argsJson, "force"))
            {
                string refs = _xref.QueryJson(objectType, objectName, null, 25);
                var users = ReadObjectArray(refs, "references")
                    .Where(r => !(string.Equals(Str(r, "objectType"), objectType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Str(r, "objectName"), objectName, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => Str(r, "objectType") + " " + Str(r, "objectName"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (users.Count > 0)
                    return JsonHelpers.BuildToolResult(idToken, objectType + " '" + objectName + "' is still referenced by "
                        + users.Count + " object(s): " + string.Join(", ", users) + ". Not deleted. Remove the references first, or pass force=true.", isError: true);
                if (!ReadBool(refs, "success"))
                    note = "\n(Where-used could not be checked: " + JsonHelpers.ExtractJsonString(refs, "message") + ")";
            }

            string response = _bridge.Call("delete_object", argsJson);
            bool deleted = ReadBool(response, "success");
            if (deleted)
            {
                string xrefNote = _xref.RemoveObject(objectType, objectName);
                if (xrefNote != null) note += "\n" + xrefNote;
            }
            return JsonHelpers.BuildToolResult(idToken, (JsonHelpers.ExtractJsonString(response, "message") ?? "Delete failed.") + note,
                isError: !deleted);
        }

        private string HandleReadObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string filePath = JsonHelpers.ExtractArgString(argsJson, "filePath", "path");
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            // If filePath provided, use read_object_by_path
            if (!string.IsNullOrEmpty(filePath))
                return DelegateToBridge(idToken, json, "read_object_by_path");

            // Otherwise use typed API
            if (!string.IsNullOrEmpty(objectType) && !string.IsNullOrEmpty(objectName))
            {
                var sections = JsonHelpers.ExtractJsonStringArray(argsJson, "sections")
                    ?? JsonHelpers.ExtractArgString(argsJson, "sections")?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                bool methodsAsked = (JsonHelpers.ExtractJsonStringArray(argsJson, "methodNames")?.Length ?? 0) > 0;
                return DelegateToBridge(idToken, json, "read_object", text => SelectReadSections(text, sections, methodsAsked));
            }

            return JsonHelpers.BuildToolResult(idToken,
                "Either (objectType + objectName) or filePath is required.", isError: true);
        }

        // Reads above this size return the small sections and an index of the large ones (#45).
        private const int ReadSummaryThreshold = 25000;
        private const int SummaryInlineSectionChars = 3000;

        private sealed class ReadSection
        {
            public string Title;
            public string Key;
            public string Body;
        }

        /// <summary>
        /// Filters a formatted read result to the requested sections ("=== Title ===" blocks). Without sections, a result
        /// larger than ReadSummaryThreshold keeps its small sections and lists the large ones with their size.
        /// </summary>
        private static string SelectReadSections(string text, string[] wanted, bool methodsAsked)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var header = new StringBuilder();
            var sections = new System.Collections.Generic.List<ReadSection>();
            foreach (string line in lines)
            {
                if (line.StartsWith("=== ", StringComparison.Ordinal) && line.EndsWith(" ===", StringComparison.Ordinal))
                {
                    string title = line.Substring(4, line.Length - 8);
                    string key = new string(title.Split('(')[0].Where(char.IsLetter).ToArray()).ToLowerInvariant();
                    if (key == "metadatatypedmetadata") key = "typedmetadata";
                    if (key.StartsWith("entityfields", StringComparison.Ordinal)) key = "entityfields";
                    sections.Add(new ReadSection { Title = title, Key = key, Body = "" });
                }
                else if (sections.Count == 0) header.AppendLine(line);
                else sections[sections.Count - 1].Body += line + "\n";
            }

            var requested = (wanted ?? new string[0]).Select(w => new string(w.Where(char.IsLetter).ToArray()).ToLowerInvariant())
                .Where(w => w.Length > 0).ToList();
            if (methodsAsked && requested.Count > 0 && !requested.Contains("methods")) requested.Add("methods");
            if (methodsAsked && requested.Count == 0 && text.Length > ReadSummaryThreshold) requested.Add("methods");
            if (requested.Contains("all") || sections.Count == 0 || (requested.Count == 0 && text.Length <= ReadSummaryThreshold))
                return text;

            string Describe(ReadSection sec)
            {
                string body = sec.Body.Trim();
                int items = body.StartsWith("[", StringComparison.Ordinal) ? CountTopLevelObjects(body) : -1;
                return sec.Key + " (" + sec.Title + "): " + body.Length + " chars" + (items >= 0 ? ", " + items + " item(s)" : "");
            }

            var sb = new StringBuilder(header.ToString().TrimEnd()).AppendLine();
            if (requested.Count == 0 || requested.Contains("summary"))
            {
                bool inlineSmall = requested.Count == 0;
                sb.AppendLine();
                sb.AppendLine(inlineSmall
                    ? "Large object (" + text.Length + " chars): small sections are shown in full, large ones only listed. Call again with sections=[...] (keys below) or sections=[\"all\"]."
                    : "Sections (call again with sections=[...]):");
                foreach (var sec in sections.Where(x => !inlineSmall || x.Body.Trim().Length > SummaryInlineSectionChars))
                    sb.AppendLine("  " + Describe(sec));
                if (inlineSmall)
                    foreach (var sec in sections.Where(x => x.Body.Trim().Length <= SummaryInlineSectionChars))
                        sb.AppendLine().AppendLine("=== " + sec.Title + " ===").Append(sec.Body.TrimEnd()).AppendLine();
                return sb.ToString().TrimEnd();
            }

            var unknown = requested.Where(r => !sections.Any(x => x.Key == r || x.Key.StartsWith(r, StringComparison.Ordinal))).ToList();
            foreach (var sec in sections.Where(x => requested.Any(r => x.Key == r || x.Key.StartsWith(r, StringComparison.Ordinal))))
                sb.AppendLine().AppendLine("=== " + sec.Title + " ===").Append(sec.Body.TrimEnd()).AppendLine();
            if (unknown.Count > 0)
                sb.AppendLine().AppendLine("Not in this object's output: " + string.Join(", ", unknown) + ". Available sections: "
                    + string.Join(", ", sections.Select(x => x.Key)) + ".");
            return sb.ToString().TrimEnd();
        }

        private static int CountTopLevelObjects(string json)
        {
            int depth = 0, count = 0;
            bool inStr = false, esc = false;
            foreach (char c in json)
            {
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[' || c == '{') { if (depth == 1 && c == '{') count++; depth++; }
                else if (c == ']' || c == '}') depth--;
            }
            return count;
        }

        private string HandleFindObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            if (string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectName' is required.", isError: true);

            return DelegateToBridge(idToken, json, "find_object");
        }

        private string HandleListObjects(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string nameFilter = JsonHelpers.ExtractArgString(argsJson, "nameFilter");
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string modelName = JsonHelpers.ExtractArgString(argsJson, "modelName");

            if (string.IsNullOrWhiteSpace(nameFilter) || nameFilter.Trim().Length < 3)
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'nameFilter' must be at least 3 characters and specific (e.g., 'CustTable', 'SalesOrder'). "
                    + "To see what a model contains, use xpp_list_model_objects instead.", isError: true);

            if (!string.IsNullOrWhiteSpace(modelName) && string.IsNullOrWhiteSpace(objectType))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectType' is required when 'modelName' is set (e.g., AxClass, AxTable).", isError: true);

            return DelegateToBridge(idToken, json, "list_objects");
        }

        private string HandleFindReferences(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName");

            if (string.IsNullOrEmpty(objectType))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectType' is required (e.g. AxTable, AxClass).", isError: true);
            if (string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectName' is required.", isError: true);

            if (ArgFlag(argsJson, "includeCode"))
            {
                string withCode = ReferencesWithCode(argsJson, objectType, objectName);
                if (withCode != null) return JsonHelpers.BuildToolResult(idToken, withCode);
            }

            string direct = _xref.TryHandle(idToken, json, out string unusableReason);
            if (direct != null) return direct;

            McpLogger.Log("XRef database unusable: " + unusableReason);
            string viaVs = DelegateToBridge(idToken, json, "find_references");
            if (!viaVs.Contains("\"isError\": true")) return viaVs;

            return JsonHelpers.BuildToolResult(idToken,
                "Where-used is unavailable.\n- Cross-reference database: " + unusableReason
                + "\n- No other cross-reference source is available."
                + "\nTell the user exactly this. Do NOT search metadata XML files or use the terminal as a substitute.",
                isError: true);
        }

        /// <summary>Where-used with the code lines that use the target (how existing code calls the API).</summary>
        private string ReferencesWithCode(string argsJson, string objectType, string objectName)
        {
            string member = JsonHelpers.ExtractArgString(argsJson, "memberName");
            int max = int.TryParse(JsonHelpers.ExtractArgString(argsJson, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 25)) : 15;
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .DeserializeObject(_xref.QueryJson(objectType, objectName, member, max * 4, JsonHelpers.ExtractArgString(argsJson, "referenceKind"))) as Dictionary<string, object>;
            if (parsed == null || !parsed.TryGetValue("success", out object ok) || !(ok is bool okFlag) || !okFlag) return null;
            var refs = (parsed["references"] as object[] ?? new object[0]).Cast<Dictionary<string, object>>().ToList();
            string token = string.IsNullOrEmpty(member) ? objectName : member;
            var tokenRx = new System.Text.RegularExpressions.Regex(@"\b" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Members: accessed as .member / ::member, not a same-named enum or EDT used as Name::Value.
            var strictRx = string.IsNullOrEmpty(member) ? tokenRx : new System.Text.RegularExpressions.Regex(
                @"(\.|::)\s*" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b(?!\s*::)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var sb = new StringBuilder();
            sb.AppendLine("Code using " + objectName + (string.IsNullOrEmpty(member) ? "" : "." + member) + " (" + refs.Count + (parsed.ContainsKey("more") && parsed["more"] is bool more && more ? "+" : "") + " references; showing up to " + max + " places):");
            var sourceCache = new Dictionary<string, List<KeyValuePair<string[], string>>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int shown = 0;
            foreach (var r in refs)
            {
                string type = r.TryGetValue("objectType", out object t) ? t as string : null;
                string name = r.TryGetValue("objectName", out object n) ? n as string : null;
                var path = (r.TryGetValue("memberPath", out object mp) ? mp as object[] : null)?.Select(x => x as string == "DataFields" ? "Fields" : x as string).ToArray() ?? new string[0];
                if (type == null || name == null || !seen.Add(type + "/" + name + "/" + string.Join("/", path))) continue;
                if (!sourceCache.TryGetValue(type + "/" + name, out var sources)) sourceCache[type + "/" + name] = sources = CodeSearch.SourcesOf(type, name);
                var source = sources?.FirstOrDefault(sx => path.Length >= 2 && sx.Key.Length >= 2
                    && string.Equals(sx.Key[sx.Key.Length - 1], path[path.Length - 1], StringComparison.OrdinalIgnoreCase)
                    && (path.Length == 2 || sx.Key.Length < 3 || string.Equals(sx.Key[sx.Key.Length - 3], path[path.Length - 3], StringComparison.OrdinalIgnoreCase)));
                var lines = source?.Value?.Replace("\r\n", "\n").Split('\n');
                var hits = lines == null ? new List<int>() : Enumerable.Range(0, lines.Length).Where(i => strictRx.IsMatch(lines[i])).Take(2).ToList();
                if (hits.Count == 0 && lines != null)
                    hits = Enumerable.Range(0, lines.Length).Where(i => tokenRx.IsMatch(lines[i])).Take(2).ToList();
                sb.AppendLine("- " + type + " " + name + (path.Length > 0 ? " / " + string.Join("/", path) : "") + " [" + (r.TryGetValue("kind", out object k) ? k : "") + "]");
                foreach (int i in hits)
                    sb.AppendLine("    line " + (i + 1) + ": " + lines[i].Trim());
                if (++shown >= max) break;
            }
            sb.AppendLine("Read a whole method with xpp_read_object (methodNames) when you want to copy or adapt it.");
            return sb.ToString();
        }

        // Booleans arrive as JSON tokens (true) or, from some clients, as strings ("true").
        private static bool ArgFlag(string argsJson, string key) =>
            "true".Equals(JsonHelpers.ExtractJsonValueToken(argsJson, key) ?? JsonHelpers.ExtractArgString(argsJson, key), StringComparison.OrdinalIgnoreCase);

        private string HandleFindExamples(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string query = JsonHelpers.ExtractArgString(argsJson, "query");
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName");
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(objectName))
                return JsonHelpers.BuildToolResult(idToken, "Give a query (what you need to implement) and/or objectType + objectName (an object to resemble).", isError: true);
            int max = int.TryParse(JsonHelpers.ExtractArgString(argsJson, "maxResults"), out int m) ? m : 15;
            var result = Examples.Find(query, objectType, objectName, max);
            if (!(result["success"] is bool ok && ok)) return JsonHelpers.BuildToolResult(idToken, result["message"] as string ?? "Search failed.", isError: true);
            var items = (List<Dictionary<string, object>>)result["items"];
            var sb = new StringBuilder();
            sb.AppendLine("Standard examples: " + result["interpretation"]);
            if (items.Count == 0) sb.AppendLine("(none found; rephrase the query with framework words, or give an object to resemble)");
            foreach (var i in items)
                sb.AppendLine("- " + i["objectType"] + " " + i["objectName"] + (i["model"] != null ? " [" + i["model"] + "]" : "") + " - " + i["reason"]);
            sb.AppendLine("Next: xpp_read_object the closest one or two, then reuse (call), extend (CoC / events) or, if neither is possible, copy and adapt; name the precedent in your plan.");
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
        }

        private string HandleFindMessageSource(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            int Num(string key, int fallback) => int.TryParse(JsonHelpers.ExtractArgString(argsJson, key), out int v) ? v : fallback;
            var r = Messages.Trace(JsonHelpers.ExtractArgString(argsJson, "message", "text"), JsonHelpers.ExtractArgString(argsJson, "language"),
                Num("maxLabels", 5), Num("maxResults", 15));
            if (!(r["success"] is bool ok && ok)) return JsonHelpers.BuildToolResult(idToken, r["message"] as string, isError: true);
            var sb = new StringBuilder();
            var labels = (List<Dictionary<string, object>>)r["labels"];
            var literal = (List<Dictionary<string, object>>)r["literal"];
            string Where(Dictionary<string, object> u) => u["objectType"] == null ? "metadata " + u["objectName"]
                : u["objectType"] + " " + u["objectName"] + (((string[])u["memberPath"]).Length > 0 ? " / " + string.Join("/", (string[])u["memberPath"]) : "")
                  + " line " + u["line"] + (u["text"] is string t && t.Length > 0 ? ": " + t : "");
            if (labels.Count == 0) sb.AppendLine("No " + r["language"] + " label text matches this message.");
            foreach (var l in labels)
            {
                var usages = (List<Dictionary<string, object>>)l["usages"];
                var ids = (List<string>)l["labels"];
                sb.AppendLine(string.Join(", ", ids.Take(6)) + (ids.Count > 6 ? " (+" + (ids.Count - 6) + " more with the same text)" : "")
                    + " \"" + l["text"] + "\"" + ((bool)l["whole"] ? " (the whole message)" : " (part of the message)") + ":");
                if ((int)l["usageCount"] < 0) sb.AppendLine("  (where-used unavailable: " + r["xrefProblem"] + ")");
                else if (usages.Count == 0) sb.AppendLine("  not used in X++ or metadata of the indexed packages: the platform (kernel) raises it, or its package was not built with cross-reference data.");
                foreach (var u in usages) sb.AppendLine("  - " + Where(u));
            }
            if (literal.Count > 0)
            {
                sb.AppendLine("Code with the message as literal text:");
                foreach (var u in literal) sb.AppendLine("  - " + Where(u));
            }
            sb.AppendLine("Read the raising method with xpp_read_object (methodNames) to see the condition; the caller chain is in xpp_find_references.");
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
        }

        private string HandleAnalyzeSecurity(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string op = JsonHelpers.ExtractArgString(argsJson, "op");
            var result = _security.Handle(op, k => JsonHelpers.ExtractArgString(argsJson, k),
                k => JsonHelpers.ExtractJsonStringArray(argsJson, k), k => ArgFlag(argsJson, k));
            if (!(result.TryGetValue("success", out object ok) && ok is bool okFlag && okFlag))
                return JsonHelpers.BuildToolResult(idToken, result.TryGetValue("message", out object m) ? m as string : "Failed.", isError: true);
            if (op == "report") return JsonHelpers.BuildToolResult(idToken, result["text"] as string);
            return JsonHelpers.BuildToolResult(idToken, SecurityText(op, result));
        }

        /// <summary>Readable text of a security analysis result for the agent.</summary>
        private static string SecurityText(string op, Dictionary<string, object> r)
        {
            var sb = new StringBuilder();
            switch (op)
            {
                case "license":
                    sb.AppendLine(r["kind"] + " " + r["name"] + " requires license: " + r["license"]);
                    sb.AppendLine("Entry points by license: " + string.Join(", ", ((Dictionary<string, object>)r["byLicense"]).Select(kv => kv.Key + " " + kv.Value))
                        + "; " + r["duties"] + " duties, " + r["privileges"] + " privileges.");
                    var drivers = (List<Dictionary<string, object>>)r["drivers"];
                    if (drivers.Count > 0)
                    {
                        sb.AppendLine("Menu items that require " + r["license"] + " (" + r["driverCount"] + "):");
                        foreach (var d in drivers.Take(30))
                            sb.AppendLine("- " + d["menuItem"] + " [" + d["type"] + ", " + d["grant"] + "; view " + d["viewLicense"] + " / maintain " + d["maintainLicense"] + "] via " + string.Join("; ", (List<string>)d["via"]));
                    }
                    var hints = (List<string>)r["lowerWithReadOnly"];
                    if (hints.Count > 0) { sb.AppendLine("Read-only would lower the license for:"); foreach (string h in hints) sb.AppendLine("- " + h); }
                    var missing = (List<string>)r["missing"];
                    if (missing.Count > 0) sb.AppendLine("Missing objects: " + string.Join("; ", missing));
                    break;
                case "access":
                    foreach (var m in (List<Dictionary<string, object>>)r["menuItems"])
                        sb.AppendLine("Menu item " + m["type"] + " " + m["name"] + ": view " + m["viewLicense"] + ", maintain " + m["maintainLicense"]);
                    var viaForms = r.TryGetValue("viaForms", out object vf) ? (List<string>)vf : new List<string>();
                    if (viaForms.Count > 0) sb.AppendLine("Through the forms using this table: " + string.Join(", ", viaForms.Take(40)) + (viaForms.Count > 40 ? ", ..." : ""));
                    var rows = (List<Dictionary<string, object>>)r["privileges"];
                    sb.AppendLine(rows.Count + " privilege(s), " + r["roleCount"] + " role(s) reach " + r["objectType"] + " " + r["name"] + ":");
                    foreach (var p in rows.Take(80))
                        sb.AppendLine("- " + p["privilege"] + " [" + p["model"] + "] " + p["grant"] + "; duties: " + string.Join(", ", (List<string>)p["duties"])
                            + "; roles: " + string.Join(", ", ((List<string>)p["roles"]).Take(15)) + (((List<string>)p["roles"]).Count > 15 ? ", ..." : ""));
                    break;
                case "placement":
                    sb.AppendLine("Standard roles for this duty (related objects: " + string.Join(", ", (List<string>)r["related"]) + "):");
                    foreach (var role in (List<Dictionary<string, object>>)r["roles"])
                        sb.AppendLine("- " + role["name"] + " '" + role["label"] + "' [" + role["model"] + ", license " + role["license"] + ", score " + role["score"] + "]: "
                            + string.Join("; ", ((List<string>)role["reasons"]).Take(4)));
                    sb.AppendLine("Extend the role(s) that do the job with a role extension (Role.YourSuffix) that adds the duty; if the requirement's role "
                        + "name has no standard role, state the mapping as an assumption in your summary. Check the license with op=license afterwards.");
                    break;
                case "gaps":
                    var findings = (List<Dictionary<string, object>>)r["findings"];
                    sb.AppendLine(findings.Count + " security finding(s) in " + string.Join(", ", (List<string>)r["models"]) + (findings.Count == 0 ? "." : ":"));
                    foreach (var f in findings.Take(150)) sb.AppendLine("- " + f["severity"] + " " + f["kind"] + " " + f["objectType"] + " " + f["name"] + ": " + f["message"]);
                    break;
                case "sod":
                    var conflicts = (List<Dictionary<string, object>>)r["conflicts"];
                    sb.AppendLine("Segregation of duties: " + r["rulesChecked"] + " rule(s) checked on " + ((List<string>)r["roles"]).Count + " role(s); " + conflicts.Count + " conflict(s).");
                    foreach (var c in conflicts) sb.AppendLine("- " + c["severity"] + " " + c["role"] + ": " + c["rule"] + " (" + c["first"] + " + " + c["second"] + "): " + c["reason"]);
                    var skipped = (List<string>)r["rulesSkipped"];
                    if (skipped.Count > 0) sb.AppendLine("Rules skipped (duties not in this version): " + string.Join("; ", skipped));
                    break;
                default:
                    sb.Append(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(r));
                    break;
            }
            return sb.ToString();
        }

        private string HandleSearchCode(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string pattern = JsonHelpers.ExtractArgString(argsJson, "pattern");
            bool regex = ArgFlag(argsJson, "regex");
            bool caseSensitive = ArgFlag(argsJson, "caseSensitive");
            int max = int.TryParse(JsonHelpers.ExtractArgString(argsJson, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 200)) : 50;
            var types = JsonHelpers.ExtractJsonStringArray(argsJson, "objectTypes");
            var result = _codeSearch.Search(pattern, regex, caseSensitive, types, JsonHelpers.ExtractArgString(argsJson, "model"), max);
            if (result.Error != null && result.Hits.Count == 0) return JsonHelpers.BuildToolResult(idToken, result.Error, isError: true);
            var sb = new StringBuilder();
            sb.AppendLine(result.Hits.Count + (result.Truncated ? "+" : "") + " match(es) for '" + pattern + "' (" + result.Scanned + " files, " + result.Ms + " ms)"
                + (result.Truncated ? "; stopped at the first matches found by the parallel scan (which ones, and the file count, vary between calls); "
                    + "more exist: narrow with objectTypes, model (a model or package) or a more specific pattern" : "") + ":");
            foreach (var h in result.Hits)
                sb.AppendLine("- " + h.ObjectType + " " + h.ObjectName + " [" + h.Model + "] " + string.Join("/", h.MemberPath) + " line " + h.Line + ": " + h.Text);
            if (result.Error != null) sb.AppendLine(result.Error);
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
        }

        private string HandleGetModelInfo(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string modelName = JsonHelpers.ExtractArgString(argsJson, "modelName");

            if (string.IsNullOrEmpty(modelName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'modelName' is required.", isError: true);

            return DelegateToBridge(idToken, json, "get_model_info");
        }

        private string HandleReadLabel(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string labelFileId = JsonHelpers.ExtractArgString(argsJson, "labelFileId");

            if (string.IsNullOrEmpty(labelFileId))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'labelFileId' is required.", isError: true);

            return DelegateToBridge(idToken, json, "read_label");
        }

        private string HandleCreateLabel(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string labelFileId = JsonHelpers.ExtractArgString(argsJson, "labelFileId");
            string labelId = JsonHelpers.ExtractArgString(argsJson, "labelId");
            string text = JsonHelpers.ExtractArgString(argsJson, "text");
            bool hasList = argsJson != null && argsJson.Contains("\"labels\"");

            if (string.IsNullOrEmpty(labelFileId) || (!hasList && (string.IsNullOrEmpty(labelId) || string.IsNullOrEmpty(text))))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'labelFileId' is required, plus either 'labels' (array of {labelId, text, comment?, translations?}) or 'labelId' and 'text'.", isError: true);

            return DelegateToBridge(idToken, json, "create_label");
        }

        private string HandleAddToProject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameters 'objectType' and 'objectName' are required.", isError: true);

            return DelegateToBridge(idToken, json, "add_to_project");
        }

        private string HandleValidateObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameters 'objectType' and 'objectName' are required.", isError: true);

            return DelegateToBridge(idToken, json, "validate_object");
        }

        private string HandleGetFormLayout(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string formName = JsonHelpers.ExtractArgString(argsJson, "formName", "objectName");

            if (string.IsNullOrWhiteSpace(formName))
                return JsonHelpers.BuildToolResult(idToken, "Parameter 'formName' is required.", isError: true);
            if (formName.Contains("."))
                return JsonHelpers.BuildToolResult(idToken,
                    "Pass the base form name (e.g. 'CustTable'), not an extension name like '" + formName + "'.", isError: true);

            return DelegateToBridge(idToken, json, "get_form_layout");
        }

        private string HandleGetObjectTypeSchema(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");

            if (string.IsNullOrEmpty(objectType))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectType' is required.", isError: true);

            return DelegateToBridge(idToken, json, "get_object_type_schema");
        }

        private static void AppendArrayField(StringBuilder sb, string json, string arrayKey,
            params string[] fields)
        {
            // Simple heuristic: check if the array key exists and has content
            string marker = "\"" + arrayKey + "\":[";
            int idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return;

            // Find closing bracket
            int start = idx + marker.Length;
            if (start >= json.Length || json[start] == ']') return;

            // Extract items by parsing simple JSON objects
            int depth = 1;
            int pos = start;
            while (pos < json.Length && depth > 0)
            {
                if (json[pos] == '[') depth++;
                if (json[pos] == ']') depth--;
                pos++;
            }
            if (pos <= start) return;

            string arrayContent = json.Substring(start, pos - start - 1);

            // Parse each object in the array
            int objStart = arrayContent.IndexOf('{');
            int count = 0;
            while (objStart >= 0 && count < 200)
            {
                int objEnd = arrayContent.IndexOf('}', objStart);
                if (objEnd < 0) break;
                string obj = arrayContent.Substring(objStart, objEnd - objStart + 1);

                var line = new StringBuilder();
                foreach (string field in fields)
                {
                    string val = JsonHelpers.ExtractJsonString(obj, field);
                    if (val != null)
                    {
                        if (line.Length > 0) line.Append(" | ");
                        line.Append(field + "=" + val);
                    }
                }
                if (line.Length > 0)
                    sb.AppendLine("  " + line);

                objStart = arrayContent.IndexOf('{', objEnd);
                count++;
            }
        }

        /// <summary>
        /// Extracts typed metadata (properties, enumValues, fields, indexes, etc.)
        /// from the bridge JSON response and formats them for display.
        /// The output uses the same JSON parameter names that xpp_create_object and
        /// xpp_update_object accept, enabling round-trip usage.
        /// </summary>
        private static void AppendTypedMetadata(StringBuilder sb, string json)
        {
            // Properties (JSON object)
            string propsObj = JsonHelpers.ExtractNestedObject(json, "properties");
            if (!string.IsNullOrEmpty(propsObj) && propsObj != "{}")
            {
                sb.AppendLine("\n=== Properties (use in \"properties\" parameter) ===");
                sb.AppendLine(propsObj);
            }

            // Enum Values
            AppendJsonArray(sb, json, "enumValues", "Enum Values");
            // Fields
            AppendJsonArray(sb, json, "fields", "Fields");
            // Indexes
            AppendJsonArray(sb, json, "indexes", "Indexes");
            // Field Groups
            AppendJsonArray(sb, json, "fieldGroups", "Field Groups");
            // Relations
            AppendJsonArray(sb, json, "relations", "Relations");
            // Entry Points
            AppendJsonArray(sb, json, "entryPoints", "Entry Points");
            // Query Data Sources
            AppendJsonArray(sb, json, "dataSources", "Data Sources");
            AppendJsonArray(sb, json, "entityFields", "Entity fields (name, dataSource, dataField)");
            // Form extension controls
            AppendJsonArray(sb, json, "controls", "Controls");
            AppendJsonArray(sb, json, "controlModifications", "Control Modifications");
        }

        private static void AppendJsonArray(StringBuilder sb, string json, string key, string label)
        {
            string marker = "\"" + key + "\":[";

            // Find the marker only at the top level of the JSON object (depth <= 1)
            // to avoid matching nested arrays with the same key name (e.g. "fields"
            // inside index or fieldGroup objects).
            int idx = -1;
            {
                int d = 0; bool inS = false; bool es = false;
                for (int i = 0; i < json.Length - marker.Length; i++)
                {
                    char c = json[i];
                    if (es) { es = false; continue; }
                    if (c == '\\') { es = true; continue; }

                    // Check for marker BEFORE toggling string state, so that the
                    // opening quote of a JSON key at the top level is matched.
                    if (!inS && d == 1 && c == '"'
                        && string.CompareOrdinal(json, i, marker, 0, marker.Length) == 0)
                    {
                        idx = i;
                        break;
                    }

                    if (c == '"') { inS = !inS; continue; }
                    if (inS) continue;
                    if (c == '{' || c == '[') d++;
                    if (c == '}' || c == ']') d--;
                }
            }
            if (idx < 0) return;

            // Find the full array including brackets
            int arrStart = idx + marker.Length - 1; // position of [
            int depth = 0; bool inStr = false; bool esc = false; int arrEnd = -1;
            for (int i = arrStart; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[') depth++;
                if (c == ']') { depth--; if (depth == 0) { arrEnd = i; break; } }
            }
            if (arrEnd < 0 || arrEnd == arrStart + 1) return; // empty array

            string arrayJson = json.Substring(arrStart, arrEnd - arrStart + 1);
            sb.AppendLine("\n=== " + label + " (use in \"" + key + "\" parameter) ===");
            sb.AppendLine(arrayJson);
        }
    }
}
