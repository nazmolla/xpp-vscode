using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>
    /// Routes JSON requests from the tool router to <see cref="IMetaModelBridge"/>, one at a time.
    /// </summary>
    public sealed class MetaModelBridgeServer
    {
        private readonly IMetaModelBridge _bridge;
        private readonly object _gate = new object();

        private static readonly string BridgeLogPath =
            Path.Combine(Path.GetTempPath(), "XppVsCodeBridge.log");

        private static void BridgeLog(string msg)
        {
            try
            {
                File.AppendAllText(BridgeLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        public MetaModelBridgeServer(IMetaModelBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public string Dispatch(string action, string body)
        {
            // The metadata provider is not thread-safe.
            lock (_gate)
            {
                try
                {
                    return DispatchOnMainThread(action, body);
                }
                catch (Exception ex)
                {
                    BridgeLog("Dispatch " + action + " failed: " + ex);
                    return "{\"success\":false,\"message\":\"" + EscapeJson(ex.Message) + "\"}";
                }
            }
        }

        private string DispatchOnMainThread(string action, string body)
        {
            switch (action)
            {
                case "create_object":
                    return HandleCreateObject(body);
                case "read_object":
                    return HandleReadObject(body);
                case "read_object_by_path":
                    return HandleReadObjectByPath(body);
                case "update_object":
                    return HandleUpdateObject(body);
                case "find_object":
                    return HandleFindObject(body);
                case "list_objects":
                    return HandleListObjects(body);
                case "find_references":
                    return HandleFindReferences(body);
                case "get_model_info":
                    return HandleGetModelInfo(body);
                case "list_models":
                    return HandleListModels(body);
                case "read_label":
                    return HandleReadLabel(body);
                case "create_label":
                    return HandleCreateLabel(body);
                case "add_to_project":
                    return HandleAddToProject(body);
                case "list_project_items":
                    return HandleListProjectItems(body);
                case "remove_from_project":
                    return _bridge.RemoveFromProjectJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"));
                case "select_project":
                    return string.IsNullOrWhiteSpace(ExtractJsonString(body, "projectFile"))
                        ? _bridge.ListProjectsJson(ExtractJsonString(body, "searchFolder"))
                        : _bridge.SelectProjectJson(ExtractJsonString(body, "projectFile"));
                case "delete_object":
                    return _bridge.DeleteObjectJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"),
                        "true".Equals(ExtractJsonString(body, "checkOnly"), StringComparison.OrdinalIgnoreCase));
                case "label_file_entries":
                    return _bridge.LabelFileEntriesJson(ExtractJsonString(body, "labelFile"), ExtractJsonString(body, "search"),
                        ExtractJsonStringArray(body, "languages")?.ToList(), int.TryParse(ExtractJsonString(body, "limit"), out int labelLimit) ? labelLimit : 500);
                case "save_label":
                    return _bridge.SaveLabelJson(ExtractJsonString(body, "labelFile"), ExtractJsonString(body, "labelId"),
                        ExtractJsonObject(body, "texts"), ExtractJsonString(body, "comment"));
                case "delete_label":
                    return _bridge.DeleteLabelJson(ExtractJsonString(body, "labelFile"), ExtractJsonString(body, "labelId"));
                case "build":
                    return _bridge.BuildJson(ExtractJsonString(body, "modelName"),
                        string.Equals(ExtractJsonString(body, "scope"), "project", StringComparison.OrdinalIgnoreCase),
                        "true".Equals(ExtractJsonString(body, "syncDatabase"), StringComparison.OrdinalIgnoreCase));
                case "get_environment":
                    return HandleGetEnvironment(body);
                case "validate_object":
                    return HandleValidateObject(body);
                case "get_object_type_schema":
                    return HandleGetObjectTypeSchema(body);
                case "get_form_layout":
                    return HandleGetFormLayout(body);
                case "get_form_tree":
                    return _bridge.GetFormTreeJson(ExtractJsonString(body, "formName"));
                case "get_table_fields":
                    return _bridge.GetTableFieldsJson(ExtractJsonString(body, "tableName"));
                case "get_element_tree":
                    return _bridge.GetElementTreeJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"));
                case "get_element_properties":
                    return _bridge.GetElementPropertiesJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"),
                        ExtractJsonStringArray(body, "path"));
                case "set_element_property":
                    return _bridge.SetElementPropertyJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"),
                        ExtractJsonStringArray(body, "path"), ExtractJsonString(body, "property"), ExtractJsonString(body, "value"));
                case "get_node_types":
                    return _bridge.GetNodeTypesJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"), ExtractJsonStringArray(body, "path"));
                case "add_element_node":
                    return _bridge.AddElementNodeJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"),
                        ExtractJsonStringArray(body, "path"), ExtractJsonString(body, "typeName"), ExtractJsonString(body, "name"), ExtractJsonObject(body, "properties"));
                case "delete_element_node":
                    return _bridge.DeleteElementNodeJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"), ExtractJsonStringArray(body, "path"));
                case "get_element_method":
                    return _bridge.GetElementMethodJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"), ExtractJsonStringArray(body, "path"));
                case "set_element_method":
                    return _bridge.SetElementMethodJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"),
                        ExtractJsonStringArray(body, "path"), ExtractJsonString(body, "originalName"), ExtractJsonString(body, "source"));
                case "get_overrides":
                    return _bridge.GetOverridesJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"), ExtractJsonStringArray(body, "path"));
                case "get_model":
                    return _bridge.GetModelJson(ExtractJsonString(body, "name"));
                case "create_model":
                    return _bridge.CreateModelJson(ExtractJsonString(body, "name"), ExtractJsonString(body, "displayName"), ExtractJsonString(body, "publisher"),
                        ExtractJsonString(body, "layer"), ExtractJsonString(body, "description"), ExtractJsonStringArray(body, "references"));
                case "update_model":
                    return _bridge.UpdateModelJson(ExtractJsonString(body, "name"), ExtractJsonString(body, "displayName"), ExtractJsonString(body, "publisher"),
                        ExtractJsonString(body, "description"), body.Contains("\"references\"") ? ExtractJsonStringArray(body, "references") : null);
                case "list_packages":
                    return _bridge.ListPackagesJson();
                case "dump_element":
                    return _bridge.DumpElementJson(ExtractJsonString(body, "objectType"), ExtractJsonString(body, "objectName"));
                case "export_project":
                    return _bridge.ExportProjectJson(ExtractJsonString(body, "projectFile"), ExtractJsonString(body, "outputFile"));
                case "inspect_project_package":
                    return _bridge.InspectProjectPackageJson(ExtractJsonString(body, "file"));
                case "import_project":
                    return _bridge.ImportProjectJson(ExtractJsonString(body, "file"), ExtractJsonString(body, "targetFolder"),
                        "true".Equals(ExtractJsonString(body, "overwrite"), StringComparison.OrdinalIgnoreCase));
                default:
                    return "{\"success\":false,\"message\":\"Unknown action: " + EscapeJson(action ?? "") + "\"}";
            }
        }

        // ── Action handlers ──

        private string HandleCreateObject(string body)
        {
            BridgeLog("HandleCreateObject body=" + body);

            var req = new CreateObjectRequest
            {
                ObjectType = ExtractJsonString(body, "objectType"),
                ObjectName = ExtractJsonString(body, "objectName"),
                TypedMetadataJson = ExtractJsonObjectRaw(body, "typedMetadata"),
                Declaration = ExtractJsonString(body, "declaration"),
                Methods = ExtractMethodSources(body),
                ModelName = ExtractJsonString(body, "modelName"),
                FormatCode = !"false".Equals(ExtractJsonString(body, "formatCode"), StringComparison.OrdinalIgnoreCase),
                Properties = ExtractJsonObject(body, "properties"),
                EnumValues = ParseEnumValues(body),
                Fields = ParseFields(body),
                Indexes = ParseIndexes(body),
                FieldGroups = ParseFieldGroups(body),
                Relations = ParseRelations(body),
                EntryPoints = ParseEntryPoints(body),
                DataSources = ParseDataSources(body),
                Controls = ParseFormControls(body, "controls"),
                ControlModifications = ParseControlModifications(body),
                Privileges = ExtractJsonStringArray(body, "privileges")?.ToList(),
                Duties = ExtractJsonStringArray(body, "duties")?.ToList(),
                DataEntityPermissions = ParseDataEntityPermissions(body),
                EntityFields = ParseEntityFields(body),
                MenuElements = ParseMenuElements(body),
                SkipStagingTable = "true".Equals(ExtractJsonString(body, "skipStagingTable"), StringComparison.OrdinalIgnoreCase)
            };

            // Reject any formatted markup in declaration or methods
            string error = RejectIfFormattedContent(req.Declaration, "declaration")
                        ?? RejectIfFormattedContentArray(req.Methods, "methods");
            if (error != null)
                return SerializeResult(new MetaModelResult { Success = false, Message = error });

            // Fallback: Copilot may stuff typed metadata into "metadata" as a JSON string
            error = NormalizeFromMetadata(body, req);
            if (req.TypedMetadataJson == null) req.TypedMetadataJson = RootDataEntitiesAsTypedMetadata(body);
            if (error != null)
                return SerializeResult(new MetaModelResult { Success = false, Message = error });

            BridgeLog("create_object type=" + req.ObjectType + " name=" + req.ObjectName
                + " props=" + (req.Properties?.Count ?? 0)
                + " enumValues=" + (req.EnumValues?.Count ?? 0)
                + " fields=" + (req.Fields?.Count ?? 0));

            var result = _bridge.CreateObject(req);
            result.Message += MetaModelBridge.IgnoredPropertiesReport();
            MetaModelBridge.ClearIgnoredProperties();
            if (!result.Success)
            {
                // Enhance error message with object context
                result.Message = "[CreateObject " + req.ObjectType + " '" + req.ObjectName + "'] " + result.Message;
                return SerializeResult(result);
            }

            // A label file is saved as one AxLabelFile per language ('<Id>_<lang>'); the create result lists them.
            if (req.ObjectType == "AxLabelFile")
                return SerializeResult(result);

            // Post-creation validation: verify object exists in project and metadata applied
            var createValidateReq = BuildValidateRequest(
                req.ObjectType, req.ObjectName, req.TypedMetadataJson,
                req.Properties, req.Fields, req.EnumValues, req.Indexes,
                req.Relations, req.FieldGroups, req.EntryPoints, req.DataSources);
            createValidateReq.Controls = req.Controls;
            var validation = _bridge.ValidateObject(createValidateReq);

            return SerializeResultWithValidation(result, validation);
        }

        private string HandleReadObject(string body)
        {
            string objectType = ExtractJsonString(body, "objectType");
            string objectName = ExtractJsonString(body, "objectName");
            BridgeLog("HandleReadObject type=" + objectType + " name=" + objectName);
            var result = _bridge.ReadObject(objectType, objectName);
            BridgeLog("HandleReadObject DONE type=" + objectType + " name=" + objectName + " success=" + result?.Success);
            return SerializeReadResult(result);
        }

        private string HandleReadObjectByPath(string body)
        {
            string filePath = ExtractJsonString(body, "filePath");
            var result = _bridge.ReadObjectByPath(filePath);
            return SerializeReadResult(result);
        }

        private string HandleUpdateObject(string body)
        {
            var req = new UpdateObjectRequest
            {
                ObjectType = ExtractJsonString(body, "objectType"),
                ObjectName = ExtractJsonString(body, "objectName"),
                TypedMetadataJson = ExtractJsonObjectRaw(body, "typedMetadata"),
                Declaration = ExtractJsonString(body, "declaration"),
                Methods = ExtractMethodSources(body),
                RemoveMethodNames = ExtractJsonStringArray(body, "removeMethodNames"),
                FormatCode = !"false".Equals(ExtractJsonString(body, "formatCode"), StringComparison.OrdinalIgnoreCase),
                Properties = ExtractJsonObject(body, "properties"),
                EnumValues = ParseEnumValues(body),
                Fields = ParseFields(body),
                Indexes = ParseIndexes(body),
                FieldGroups = ParseFieldGroups(body),
                Relations = ParseRelations(body),
                EntryPoints = ParseEntryPoints(body),
                DataSources = ParseDataSources(body),
                Controls = ParseFormControls(body, "controls"),
                ControlModifications = ParseControlModifications(body),
                Privileges = ExtractJsonStringArray(body, "privileges")?.ToList(),
                Duties = ExtractJsonStringArray(body, "duties")?.ToList(),
                DataEntityPermissions = ParseDataEntityPermissions(body),
                EntityFields = ParseEntityFields(body),
                MenuElements = ParseMenuElements(body),
                SkipStagingTable = "true".Equals(ExtractJsonString(body, "skipStagingTable"), StringComparison.OrdinalIgnoreCase),
                RemoveSecurityItems = ExtractJsonStringArray(body, "removeSecurityItems")?.ToList(),
                UpdateControls = ParseFormControls(body, "updateControls"),
                RemoveControls = ExtractJsonStringArray(body, "removeControls")?.ToList(),
                RemoveControlModifications = ExtractJsonStringArray(body, "removeControlModifications")?.ToList()
            };

            // Reject any formatted markup in declaration or methods
            string error = RejectIfFormattedContent(req.Declaration, "declaration")
                        ?? RejectIfFormattedContentArray(req.Methods, "methods");
            if (error != null)
                return SerializeResult(new MetaModelResult { Success = false, Message = error });

            // Fallback: Copilot may stuff typed metadata into "metadata" as a JSON string
            error = NormalizeFromMetadata(body, req);
            if (req.TypedMetadataJson == null) req.TypedMetadataJson = RootDataEntitiesAsTypedMetadata(body);
            if (error != null)
                return SerializeResult(new MetaModelResult { Success = false, Message = error });

            var result = _bridge.UpdateObject(req);
            result.Message += MetaModelBridge.IgnoredPropertiesReport();
            MetaModelBridge.ClearIgnoredProperties();
            if (!result.Success)
            {
                // Enhance error message with object context
                result.Message = "[UpdateObject " + req.ObjectType + " '" + req.ObjectName + "'] " + result.Message;
                return SerializeResult(result);
            }

            // Post-update validation: verify metadata was applied
            var updateValidateReq = BuildValidateRequest(
                req.ObjectType, req.ObjectName, req.TypedMetadataJson,
                req.Properties, req.Fields, req.EnumValues, req.Indexes,
                req.Relations, req.FieldGroups, req.EntryPoints, req.DataSources);
            updateValidateReq.Controls = req.Controls;
            var validation = _bridge.ValidateObject(updateValidateReq);

            return SerializeResultWithValidation(result, validation);
        }

        private ValidateObjectRequest BuildValidateRequest(string objectType, string objectName,
            string typedMetadataJson,
            Dictionary<string, string> properties,
            List<FieldDto> fields,
            List<EnumValueDto> enumValues,
            List<IndexDto> indexes,
            List<RelationDto> relations,
            List<FieldGroupDto> fieldGroups,
            List<EntryPointDto> entryPoints,
            List<QueryDataSourceDto> dataSources)
        {
            return new ValidateObjectRequest
            {
                ObjectType = objectType,
                ObjectName = objectName,
                TypedMetadataJson = typedMetadataJson,
                Properties = properties,
                Fields = fields,
                EnumValues = enumValues,
                Indexes = indexes,
                Relations = relations,
                FieldGroups = fieldGroups,
                EntryPoints = entryPoints,
                DataSources = dataSources
            };
        }

        private string HandleValidateObject(string body)
        {
            string objectType = ExtractJsonString(body, "objectType");
            string objectName = ExtractJsonString(body, "objectName");
            BridgeLog("HandleValidateObject type=" + objectType + " name=" + objectName);
            var req = BuildValidateRequest(
                objectType,
                objectName,
                ExtractJsonObjectRaw(body, "typedMetadata"),
                ExtractJsonObject(body, "properties"),
                ParseFields(body, defaultFieldType: false),
                ParseEnumValues(body),
                ParseIndexes(body),
                ParseRelations(body),
                ParseFieldGroups(body),
                ParseEntryPoints(body),
                ParseDataSources(body));
            req.Controls = ParseFormControls(body, "controls");
            var result = _bridge.ValidateObject(req);
            BridgeLog("HandleValidateObject DONE type=" + objectType + " name=" + objectName + " valid=" + result?.Valid);
            return SerializeValidateResult(result);
        }

        private string HandleGetObjectTypeSchema(string body)
        {
            string objectType = ExtractJsonString(body, "objectType");
            var result = _bridge.GetObjectTypeSchema(objectType);
            return SerializeObjectTypeSchemaResult(result);
        }

        private string HandleGetFormLayout(string body)
        {
            string formName = ExtractJsonString(body, "formName") ?? ExtractJsonString(body, "objectName");
            string rootControl = ExtractJsonString(body, "rootControl");
            int.TryParse(ExtractJsonString(body, "maxDepth"), out int maxDepth);
            BridgeLog("HandleGetFormLayout form=" + formName + " root=" + rootControl + " depth=" + maxDepth);

            var result = _bridge.GetFormLayout(formName, rootControl, maxDepth);
            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (result.Success ? "true" : "false"));
            if (result.Message != null)
                sb.Append(",\"message\":\"" + EscapeJson(result.Message) + "\"");
            if (result.Layout != null)
                sb.Append(",\"layout\":\"" + EscapeJson(result.Layout) + "\"");
            sb.Append("}");
            return sb.ToString();
        }

        private string HandleFindReferences(string body)
        {
            string objectType = ExtractJsonString(body, "objectType");
            string objectName = ExtractJsonString(body, "objectName");
            string referenceKind = ExtractJsonString(body, "referenceKind");
            string maxStr = ExtractJsonString(body, "maxResults");
            int maxResults = 25;
            if (!string.IsNullOrEmpty(maxStr)) int.TryParse(maxStr, out maxResults);
            if (maxResults < 1) maxResults = 1;
            if (maxResults > 100) maxResults = 100;

            var result = _bridge.FindReferences(objectType, objectName, ExtractJsonString(body, "memberName"), referenceKind, maxResults);

            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (result.Success ? "true" : "false"));
            if (result.Message != null)
                sb.Append(",\"message\":\"" + EscapeJson(result.Message) + "\"");
            if (result.TargetPath != null)
                sb.Append(",\"targetPath\":\"" + EscapeJson(result.TargetPath) + "\"");
            sb.Append(",\"count\":" + result.References.Count);
            sb.Append(",\"references\":[");
            for (int i = 0; i < result.References.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var r = result.References[i];
                sb.Append("{");
                sb.Append("\"sourcePath\":\"" + EscapeJson(r.SourcePath ?? "") + "\"");
                if (r.SourceObjectType != null)
                    sb.Append(",\"sourceObjectType\":\"" + EscapeJson(r.SourceObjectType) + "\"");
                if (r.SourceObjectName != null)
                    sb.Append(",\"sourceObjectName\":\"" + EscapeJson(r.SourceObjectName) + "\"");
                if (r.SourceMember != null)
                    sb.Append(",\"sourceMember\":\"" + EscapeJson(r.SourceMember) + "\"");
                sb.Append(",\"kind\":\"" + EscapeJson(r.Kind ?? "") + "\"");
                sb.Append(",\"lineNumber\":" + r.LineNumber);
                if (r.SourceModule != null)
                    sb.Append(",\"sourceModule\":\"" + EscapeJson(r.SourceModule) + "\"");
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string HandleFindObject(string body)
        {
            string objectName = ExtractJsonString(body, "objectName");
            string objectType = ExtractJsonString(body, "objectType");
            string exactStr = ExtractJsonString(body, "exactMatch");
            bool exact = "true".Equals(exactStr, StringComparison.OrdinalIgnoreCase);
            string maxStr = ExtractJsonString(body, "maxResults");
            int maxResults = 10;
            if (!string.IsNullOrEmpty(maxStr)) int.TryParse(maxStr, out maxResults);
            if (maxResults < 1) maxResults = 1;
            if (maxResults > 50) maxResults = 50;

            if (!exact && (objectName ?? "").Trim().Length < 3)
                return "{\"success\":false,\"message\":\"objectName must be at least 3 characters for partial matching (or set exactMatch=true). Search for a specific object name.\",\"matches\":[]}";

            var result = _bridge.FindObject(objectName, objectType, exact, maxResults);

            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (result.Success ? "true" : "false"));
            if (result.Message != null)
                sb.Append(",\"message\":\"" + EscapeJson(result.Message) + "\"");
            sb.Append(",\"matches\":[");
            for (int i = 0; i < result.Matches.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var m = result.Matches[i];
                sb.Append("{\"objectType\":\"" + EscapeJson(m.ObjectType) + "\"");
                sb.Append(",\"objectName\":\"" + EscapeJson(m.ObjectName) + "\"");
                sb.Append(",\"modelName\":\"" + EscapeJson(m.ModelName ?? "") + "\"");
                if (!string.IsNullOrEmpty(m.Label)) sb.Append(",\"label\":\"" + EscapeJson(m.Label) + "\"");
                sb.Append(",\"isCustom\":" + (m.IsCustom ? "true" : "false") + "}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string HandleListObjects(string body)
        {
            string modelName = ExtractJsonString(body, "modelName");
            string objectType = ExtractJsonString(body, "objectType");
            string nameFilter = ExtractJsonString(body, "nameFilter");
            string maxStr = ExtractJsonString(body, "maxResults");
            int maxResults = 10;
            if (!string.IsNullOrEmpty(maxStr)) int.TryParse(maxStr, out maxResults);
            if (maxResults < 1) maxResults = 1;
            if (maxResults > 50) maxResults = 50;

            // Broad filters force a full metadata scan on the VS UI thread and freeze VS
            if (string.IsNullOrWhiteSpace(nameFilter) || nameFilter.Trim().Length < 3)
                return "{\"success\":false,\"message\":\"nameFilter must be at least 3 characters (e.g. 'CustTable'). To see what a model contains, use xpp_list_model_objects.\",\"objects\":[]}";
            if (!string.IsNullOrWhiteSpace(modelName) && string.IsNullOrWhiteSpace(objectType))
                return "{\"success\":false,\"message\":\"objectType is required when modelName is set (model filtering is expensive per object).\",\"objects\":[]}";

            var result = _bridge.ListObjects(modelName, objectType, nameFilter, maxResults);

            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (result.Success ? "true" : "false"));
            if (result.Message != null)
                sb.Append(",\"message\":\"" + EscapeJson(result.Message) + "\"");
            sb.Append(",\"objects\":[");
            for (int i = 0; i < result.Objects.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var o = result.Objects[i];
                sb.Append("{\"objectType\":\"" + EscapeJson(o.ObjectType) + "\"");
                sb.Append(",\"objectName\":\"" + EscapeJson(o.ObjectName) + "\"");
                sb.Append(",\"modelName\":\"" + EscapeJson(o.ModelName ?? "") + "\"");
                sb.Append(",\"isCustom\":" + (o.IsCustom ? "true" : "false") + "}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string HandleGetModelInfo(string body)
        {
            string modelName = ExtractJsonString(body, "modelName");
            var result = _bridge.GetModelInfo(modelName);
            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (result.Success ? "true" : "false"));
            if (result.Message != null) sb.Append(",\"message\":\"" + EscapeJson(result.Message) + "\"");
            if (result.Name != null) sb.Append(",\"name\":\"" + EscapeJson(result.Name) + "\"");
            if (result.DisplayName != null) sb.Append(",\"displayName\":\"" + EscapeJson(result.DisplayName) + "\"");
            if (result.Publisher != null) sb.Append(",\"publisher\":\"" + EscapeJson(result.Publisher) + "\"");
            if (result.Version != null) sb.Append(",\"version\":\"" + EscapeJson(result.Version) + "\"");
            if (result.Layer != null) sb.Append(",\"layer\":\"" + EscapeJson(result.Layer) + "\"");
            if (result.Package != null) sb.Append(",\"package\":\"" + EscapeJson(result.Package) + "\"");
            if (result.Dependencies.Count > 0) sb.Append(",\"dependencies\":\"" + EscapeJson(string.Join(", ", result.Dependencies)) + "\"");
            sb.Append(",\"modelId\":" + result.ModelId);
            sb.Append("}");
            return sb.ToString();
        }

        private string HandleListModels(string body)
        {
            var models = _bridge.ListModels();
            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"models\":[");
            for (int i = 0; i < models.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var m = models[i];
                sb.Append("{\"name\":\"" + EscapeJson(m.Name) + "\"");
                sb.Append(",\"displayName\":\"" + EscapeJson(m.DisplayName ?? "") + "\"");
                sb.Append(",\"isCustom\":" + (m.IsCustom ? "true" : "false") + "}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string HandleReadLabel(string body)
        {
            string labelFileId = ExtractJsonString(body, "labelFileId");
            string language = ExtractJsonString(body, "language");
            string labelId = ExtractJsonString(body, "labelId");
            string searchText = ExtractJsonString(body, "searchText");
            string maxStr = ExtractJsonString(body, "maxResults");
            int maxResults = 25;
            if (!string.IsNullOrEmpty(maxStr)) int.TryParse(maxStr, out maxResults);
            if (maxResults < 1) maxResults = 1;
            if (maxResults > 100) maxResults = 100;
            var result = _bridge.ReadLabel(labelFileId, language, labelId, searchText, maxResults);

            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (result.Success ? "true" : "false"));
            if (result.Message != null) sb.Append(",\"message\":\"" + EscapeJson(result.Message) + "\"");
            sb.Append(",\"labels\":[");
            for (int i = 0; i < result.Labels.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var l = result.Labels[i];
                sb.Append("{\"id\":\"" + EscapeJson(l.Id) + "\"");
                sb.Append(",\"text\":\"" + EscapeJson(l.Text) + "\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string HandleCreateLabel(string body)
        {
            string labelFileId = ExtractJsonString(body, "labelFileId");
            string language = ExtractJsonString(body, "language");
            var labels = new List<LabelRequestDto>();
            string single = ExtractJsonString(body, "labelId");
            if (!string.IsNullOrEmpty(single))
                labels.Add(new LabelRequestDto
                {
                    LabelId = single,
                    Text = ExtractJsonString(body, "text"),
                    Comment = ExtractJsonString(body, "comment"),
                    Translations = ExtractJsonObject(body, "translations")
                });
            string array = ExtractJsonArray(body, "labels");
            if (array != null)
                foreach (string obj in SplitJsonObjects(array))
                    labels.Add(new LabelRequestDto
                    {
                        LabelId = ExtractJsonString(obj, "labelId") ?? ExtractJsonString(obj, "id"),
                        Text = ExtractJsonString(obj, "text"),
                        Comment = ExtractJsonString(obj, "comment"),
                        Translations = ExtractJsonObject(obj, "translations")
                    });
            var result = _bridge.CreateLabels(labelFileId, language, labels);
            return SerializeResult(result);
        }

        private string HandleAddToProject(string body)
        {
            string objectType = ExtractJsonString(body, "objectType");
            string objectName = ExtractJsonString(body, "objectName");
            var result = _bridge.AddToProject(objectType, objectName);
            return SerializeResult(result);
        }

        private string HandleListProjectItems(string body)
        {
            var items = _bridge.ListProjectItems();
            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"hasProject\":" + (_bridge.HasActiveProject ? "true" : "false") + ",\"items\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":\"" + EscapeJson(items[i].Name) + "\"");
                if (items[i].ObjectType != null)
                    sb.Append(",\"objectType\":\"" + EscapeJson(items[i].ObjectType) + "\"");
                if (items[i].FilePath != null)
                    sb.Append(",\"filePath\":\"" + EscapeJson(items[i].FilePath) + "\"");
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string HandleGetEnvironment(string body)
        {
            var info = _bridge.GetEnvironmentInfo();
            var sb = new StringBuilder();
            sb.Append("{\"success\":true");
            sb.Append(",\"customMetadataFolder\":\"" + EscapeJson(info.CustomMetadataFolder ?? "") + "\"");
            sb.Append(",\"referenceMetadataFolders\":[");
            for (int i = 0; i < info.ReferenceMetadataFolders.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"" + EscapeJson(info.ReferenceMetadataFolders[i]) + "\"");
            }
            sb.Append("]");
            sb.Append(",\"activeProjectName\":\"" + EscapeJson(info.ActiveProjectName ?? "") + "\"");
            sb.Append(",\"activeModelName\":\"" + EscapeJson(info.ActiveModelName ?? "") + "\"");
            sb.Append(",\"activeModelId\":" + info.ActiveModelId);
            sb.Append(",\"activeModelLayer\":\"" + EscapeJson(info.ActiveModelLayer ?? "") + "\"");
            sb.Append("}");
            return sb.ToString();
        }

        // ── Serialization helpers ──

        private string SerializeResultWithValidation(MetaModelResult r, ValidateObjectResult v)
        {
            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (r.Success ? "true" : "false"));
            sb.Append(",\"message\":\"" + EscapeJson(r.Message ?? "") + "\"");
            if (r.FilePath != null)
                sb.Append(",\"filePath\":\"" + EscapeJson(r.FilePath) + "\"");
            sb.Append(",\"validation\":");
            sb.Append(BuildValidationJson(v));
            sb.Append("}");
            return sb.ToString();
        }

        private string SerializeValidateResult(ValidateObjectResult v)
        {
            return BuildValidationJson(v);
        }

        private static string BuildValidationJson(ValidateObjectResult v)
        {
            var sb = new StringBuilder();
            sb.Append("{\"valid\":" + (v.Valid ? "true" : "false"));
            sb.Append(",\"exists\":" + (v.Exists ? "true" : "false"));
            sb.Append(",\"inProject\":" + (v.InProject ? "true" : "false"));
            if (v.ObjectType != null) sb.Append(",\"objectType\":\"" + EscapeJson(v.ObjectType) + "\"");
            if (v.ObjectName != null) sb.Append(",\"objectName\":\"" + EscapeJson(v.ObjectName) + "\"");
            if (v.ModelName != null) sb.Append(",\"modelName\":\"" + EscapeJson(v.ModelName) + "\"");
            sb.Append(",\"message\":\"" + EscapeJson(v.Message ?? "") + "\"");
            if (v.Mismatches != null && v.Mismatches.Count > 0)
            {
                sb.Append(",\"mismatches\":[");
                for (int i = 0; i < v.Mismatches.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"" + EscapeJson(v.Mismatches[i]) + "\"");
                }
                sb.Append("]");
            }
            sb.Append("}");
            return sb.ToString();
        }

        private string SerializeResult(MetaModelResult r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (r.Success ? "true" : "false"));
            sb.Append(",\"message\":\"" + EscapeJson(r.Message ?? "") + "\"");
            if (r.FilePath != null)
                sb.Append(",\"filePath\":\"" + EscapeJson(r.FilePath) + "\"");
            sb.Append("}");
            return sb.ToString();
        }

        private static string SerializeObjectTypeSchemaResult(ObjectTypeSchemaResult r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (r.Success ? "true" : "false"));
            if (r.ObjectType != null)
                sb.Append(",\"objectType\":\"" + EscapeJson(r.ObjectType) + "\"");
            if (r.SchemaJson != null)
                sb.Append(",\"schema\":" + r.SchemaJson);
            if (r.Message != null)
                sb.Append(",\"message\":\"" + EscapeJson(r.Message) + "\"");
            sb.Append("}");
            return sb.ToString();
        }

        private string SerializeReadResult(ReadObjectResult r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"success\":" + (r.Success ? "true" : "false"));
            if (r.Message != null) sb.Append(",\"message\":\"" + EscapeJson(r.Message) + "\"");
            if (r.ObjectType != null) sb.Append(",\"objectType\":\"" + EscapeJson(r.ObjectType) + "\"");
            if (r.ObjectName != null) sb.Append(",\"objectName\":\"" + EscapeJson(r.ObjectName) + "\"");
            if (r.TypedMetadataJson != null) sb.Append(",\"typedMetadata\":" + r.TypedMetadataJson);
            if (r.Declaration != null) sb.Append(",\"declaration\":\"" + EscapeJson(r.Declaration) + "\"");
            if (r.ModelName != null) sb.Append(",\"modelName\":\"" + EscapeJson(r.ModelName) + "\"");
            sb.Append(",\"isCustom\":" + (r.IsCustom ? "true" : "false"));

            // Methods
            sb.Append(",\"methods\":[");
            for (int i = 0; i < r.Methods.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":\"" + EscapeJson(r.Methods[i].Name) + "\"");
                sb.Append(",\"source\":\"" + EscapeJson(r.Methods[i].Source) + "\"}");
            }
            sb.Append("]");

            // Properties
            if (r.Properties != null && r.Properties.Count > 0)
            {
                sb.Append(",\"properties\":{");
                bool first = true;
                foreach (var kvp in r.Properties)
                {
                    if (!first) sb.Append(",");
                    sb.Append("\"" + EscapeJson(kvp.Key) + "\":\"" + EscapeJson(kvp.Value ?? "") + "\"");
                    first = false;
                }
                sb.Append("}");
            }

            // EnumValues
            SerializeEnumValues(sb, r.EnumValues);
            // Fields
            SerializeFields(sb, r.Fields);
            // Indexes
            SerializeIndexes(sb, r.Indexes);
            // FieldGroups
            SerializeFieldGroups(sb, r.FieldGroups);
            // Relations
            SerializeRelations(sb, r.Relations);
            // EntryPoints
            SerializeEntryPoints(sb, r.EntryPoints);
            // Query DataSources
            SerializeDataSources(sb, r.DataSources);
            if (r.EntityFields != null && r.EntityFields.Count > 0)
            {
                sb.Append(",\"entityFields\":[");
                sb.Append(string.Join(",", r.EntityFields.Select(f => "{\"name\":\"" + EscapeJson(f.Name ?? "") + "\",\"dataSource\":\""
                    + EscapeJson(f.DataSource ?? "") + "\",\"dataField\":\"" + EscapeJson(f.DataField ?? "") + "\"}")));
                sb.Append("]");
            }
            // Form extension controls
            SerializeFormControls(sb, "controls", r.Controls);
            SerializeControlModifications(sb, r.ControlModifications);

            sb.Append("}");
            return sb.ToString();
        }

        private static void SerializeFormControls(StringBuilder sb, string key, System.Collections.Generic.List<FormControlDto> controls)
        {
            if (controls == null || controls.Count == 0) return;
            sb.Append(",\"" + key + "\":[");
            for (int i = 0; i < controls.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var c = controls[i];
                sb.Append("{\"name\":\"" + EscapeJson(c.Name) + "\"");
                AppendOptionalJsonString(sb, "controlType", c.ControlType);
                AppendOptionalJsonString(sb, "parent", c.Parent);
                AppendOptionalJsonString(sb, "position", c.Position);
                AppendOptionalJsonString(sb, "previousSibling", c.PreviousSibling);
                AppendOptionalJsonString(sb, "dataSource", c.DataSource);
                AppendOptionalJsonString(sb, "dataField", c.DataField);
                AppendOptionalJsonString(sb, "dataGroup", c.DataGroup);
                AppendOptionalJsonString(sb, "label", c.Label);
                if (c.Controls.Count > 0)
                {
                    var nested = new StringBuilder();
                    SerializeFormControls(nested, "controls", c.Controls);
                    sb.Append(nested);
                }
                sb.Append("}");
            }
            sb.Append("]");
        }

        private static void SerializeControlModifications(StringBuilder sb, System.Collections.Generic.List<ControlModificationDto> mods)
        {
            if (mods == null || mods.Count == 0) return;
            sb.Append(",\"controlModifications\":[");
            for (int i = 0; i < mods.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":\"" + EscapeJson(mods[i].Name) + "\",\"properties\":{");
                bool first = true;
                foreach (var kv in mods[i].Properties ?? new System.Collections.Generic.Dictionary<string, string>())
                {
                    if (!first) sb.Append(",");
                    sb.Append("\"" + EscapeJson(kv.Key) + "\":\"" + EscapeJson(kv.Value ?? "") + "\"");
                    first = false;
                }
                sb.Append("}}");
            }
            sb.Append("]");
        }

        private static void AppendOptionalJsonString(StringBuilder sb, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
                sb.Append(",\"" + key + "\":\"" + EscapeJson(value) + "\"");
        }

        private static void SerializeEnumValues(StringBuilder sb, System.Collections.Generic.List<EnumValueDto> values)
        {
            if (values == null || values.Count == 0) return;
            sb.Append(",\"enumValues\":[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var v = values[i];
                sb.Append("{\"name\":\"" + EscapeJson(v.Name) + "\",\"value\":" + v.Value);
                if (!string.IsNullOrEmpty(v.Label))
                    sb.Append(",\"label\":\"" + EscapeJson(v.Label) + "\"");
                sb.Append("}");
            }
            sb.Append("]");
        }

        private static void SerializeFields(StringBuilder sb, System.Collections.Generic.List<FieldDto> fields)
        {
            if (fields == null || fields.Count == 0) return;
            sb.Append(",\"fields\":[");
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var f = fields[i];
                sb.Append("{\"name\":\"" + EscapeJson(f.Name) + "\",\"fieldType\":\"" + EscapeJson(f.FieldType ?? "String") + "\"");
                if (!string.IsNullOrEmpty(f.ExtendedDataType))
                    sb.Append(",\"extendedDataType\":\"" + EscapeJson(f.ExtendedDataType) + "\"");
                if (!string.IsNullOrEmpty(f.EnumType))
                    sb.Append(",\"enumType\":\"" + EscapeJson(f.EnumType) + "\"");
                if (!string.IsNullOrEmpty(f.Label))
                    sb.Append(",\"label\":\"" + EscapeJson(f.Label) + "\"");
                sb.Append("}");
            }
            sb.Append("]");
        }

        private static void SerializeIndexes(StringBuilder sb, System.Collections.Generic.List<IndexDto> indexes)
        {
            if (indexes == null || indexes.Count == 0) return;
            sb.Append(",\"indexes\":[");
            for (int i = 0; i < indexes.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var idx = indexes[i];
                sb.Append("{\"name\":\"" + EscapeJson(idx.Name) + "\",\"allowDuplicates\":" + (idx.AllowDuplicates ? "true" : "false"));
                sb.Append(",\"fields\":[");
                for (int j = 0; j < idx.Fields.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    sb.Append("\"" + EscapeJson(idx.Fields[j]) + "\"");
                }
                sb.Append("]}");
            }
            sb.Append("]");
        }

        private static void SerializeFieldGroups(StringBuilder sb, System.Collections.Generic.List<FieldGroupDto> groups)
        {
            if (groups == null || groups.Count == 0) return;
            sb.Append(",\"fieldGroups\":[");
            for (int i = 0; i < groups.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var fg = groups[i];
                sb.Append("{\"name\":\"" + EscapeJson(fg.Name) + "\"");
                if (!string.IsNullOrEmpty(fg.Label))
                    sb.Append(",\"label\":\"" + EscapeJson(fg.Label) + "\"");
                sb.Append(",\"fields\":[");
                for (int j = 0; j < fg.Fields.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    sb.Append("\"" + EscapeJson(fg.Fields[j]) + "\"");
                }
                sb.Append("]}");
            }
            sb.Append("]");
        }

        private static void SerializeRelations(StringBuilder sb, System.Collections.Generic.List<RelationDto> relations)
        {
            if (relations == null || relations.Count == 0) return;
            sb.Append(",\"relations\":[");
            for (int i = 0; i < relations.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var rel = relations[i];
                sb.Append("{\"name\":\"" + EscapeJson(rel.Name) + "\",\"relatedTable\":\"" + EscapeJson(rel.RelatedTable ?? "") + "\"");
                if (rel.Constraints != null && rel.Constraints.Count > 0)
                {
                    sb.Append(",\"constraints\":[");
                    for (int j = 0; j < rel.Constraints.Count; j++)
                    {
                        if (j > 0) sb.Append(",");
                        var c = rel.Constraints[j];
                        sb.Append("{\"field\":\"" + EscapeJson(c.Field ?? "") + "\",\"relatedField\":\"" + EscapeJson(c.RelatedField ?? "") + "\"}");
                    }
                    sb.Append("]");
                }
                sb.Append("}");
            }
            sb.Append("]");
        }

        private static void SerializeEntryPoints(StringBuilder sb, System.Collections.Generic.List<EntryPointDto> entryPoints)
        {
            if (entryPoints == null || entryPoints.Count == 0) return;
            sb.Append(",\"entryPoints\":[");
            for (int i = 0; i < entryPoints.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var ep = entryPoints[i];
                sb.Append("{\"name\":\"" + EscapeJson(ep.Name) + "\"");
                if (!string.IsNullOrEmpty(ep.ObjectType))
                    sb.Append(",\"objectType\":\"" + EscapeJson(ep.ObjectType) + "\"");
                if (!string.IsNullOrEmpty(ep.ObjectName))
                    sb.Append(",\"objectName\":\"" + EscapeJson(ep.ObjectName) + "\"");
                if (!string.IsNullOrEmpty(ep.Grant))
                    sb.Append(",\"grant\":\"" + EscapeJson(ep.Grant) + "\"");
                sb.Append("}");
            }
            sb.Append("]");
        }

        private static void SerializeDataSources(StringBuilder sb, System.Collections.Generic.List<QueryDataSourceDto> dataSources)
        {
            if (dataSources == null || dataSources.Count == 0) return;
            sb.Append(",\"dataSources\":[");
            for (int i = 0; i < dataSources.Count; i++)
            {
                if (i > 0) sb.Append(",");
                SerializeDataSourceNode(sb, dataSources[i]);
            }
            sb.Append("]");
        }

        private static void SerializeDataSourceNode(StringBuilder sb, QueryDataSourceDto ds)
        {
            sb.Append("{\"name\":\"" + EscapeJson(ds.Name ?? "") + "\"");
            if (!string.IsNullOrEmpty(ds.Table))
                sb.Append(",\"table\":\"" + EscapeJson(ds.Table) + "\"");
            if (!string.IsNullOrEmpty(ds.ParentDataSource))
                sb.Append(",\"parentDataSource\":\"" + EscapeJson(ds.ParentDataSource) + "\"");
            if (!string.IsNullOrEmpty(ds.JoinMode))
                sb.Append(",\"joinMode\":\"" + EscapeJson(ds.JoinMode) + "\"");
            if (!string.IsNullOrEmpty(ds.LinkType))
                sb.Append(",\"linkType\":\"" + EscapeJson(ds.LinkType) + "\"");
            if (ds.DynamicFields.HasValue)
                sb.Append(",\"dynamicFields\":" + (ds.DynamicFields.Value ? "true" : "false"));
            if (ds.Fields != null && ds.Fields.Count > 0)
                sb.Append(",\"fields\":[" + string.Join(",", ds.Fields.Select(f => "\"" + EscapeJson(f) + "\"")) + "]");
            if (ds.Relations.HasValue)
                sb.Append(",\"relations\":" + (ds.Relations.Value ? "true" : "false"));
            if (ds.FirstOnly.HasValue)
                sb.Append(",\"firstOnly\":" + (ds.FirstOnly.Value ? "true" : "false"));

            if (ds.Ranges != null && ds.Ranges.Count > 0)
            {
                sb.Append(",\"ranges\":[");
                for (int j = 0; j < ds.Ranges.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    var r = ds.Ranges[j];
                    sb.Append("{");
                    bool wrote = false;
                    if (!string.IsNullOrEmpty(r.Name))
                    {
                        sb.Append("\"name\":\"" + EscapeJson(r.Name) + "\"");
                        wrote = true;
                    }
                    if (!string.IsNullOrEmpty(r.Field))
                    {
                        if (wrote) sb.Append(",");
                        sb.Append("\"field\":\"" + EscapeJson(r.Field) + "\"");
                        wrote = true;
                    }
                    if (!string.IsNullOrEmpty(r.Value))
                    {
                        if (wrote) sb.Append(",");
                        sb.Append("\"value\":\"" + EscapeJson(r.Value) + "\"");
                    }
                    sb.Append("}");
                }
                sb.Append("]");
            }

            // Recurse into child datasources
            if (ds.ChildDataSources != null && ds.ChildDataSources.Count > 0)
            {
                sb.Append(",\"dataSources\":[");
                for (int k = 0; k < ds.ChildDataSources.Count; k++)
                {
                    if (k > 0) sb.Append(",");
                    SerializeDataSourceNode(sb, ds.ChildDataSources[k]);
                }
                sb.Append("]");
            }

            sb.Append("}");
        }

        // ── Formatted content rejection helpers ──

        private static readonly string FormattedContentRejectionMessage =
            "ERROR: Formatted markup is not supported. You MUST use the strongly-typed JSON parameters "
          + "(objectType, objectName, properties, fields, indexes, relations, fieldGroups, enumValues, "
          + "entryPoints, declaration, methods). Do NOT send markup, CDATA, or angle-bracketed tags. "
          + "Call xpp_read_object on an existing object to see the correct JSON format.";

        /// <summary>Returns an error message if the value looks like formatted markup, null otherwise.</summary>
        private static string RejectIfFormattedContent(string value, string paramName)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string t = value.TrimStart();
            if (t.StartsWith("<![CDATA[") || t.StartsWith("<?") || t.StartsWith("<Ax"))
                return "ERROR: The '" + paramName + "' parameter contains markup. " + FormattedContentRejectionMessage;
            return null;
        }

        /// <summary>Returns an error if any array element looks like formatted markup.</summary>
        private static string RejectIfFormattedContentArray(string[] values, string paramName)
        {
            if (values == null) return null;
            foreach (string v in values)
            {
                string err = RejectIfFormattedContent(v, paramName);
                if (err != null) return err;
            }
            return null;
        }

        // ── metadata normalization fallback (JSON only) ──

        /// <summary>
        /// If Copilot sends a "metadata" parameter it must contain JSON.
        /// Formatted markup is rejected with a clear error message.
        /// </summary>
        private string NormalizeFromMetadata(string body, CreateObjectRequest req)
        {
            if (req.Properties != null || req.EnumValues != null || req.Fields != null)
                return null; // Already have typed metadata, skip fallback

            string metadataStr = ExtractJsonString(body, "metadata");
            if (string.IsNullOrEmpty(metadataStr)) {
                BridgeLog("NormalizeFromMetadata (Create): metadata string not found — will use typed parameters");
                return null;
            }

            // Reject formatted markup — this app is JSON-only
            string trimmed = metadataStr.TrimStart();
            if (trimmed.StartsWith("<"))
            {
                BridgeLog("REJECTED formatted markup in metadata — app is JSON-only");
                return "ERROR: Formatted markup is not supported. You MUST use the strongly-typed JSON parameters "
                     + "(objectType, objectName, properties, fields, indexes, relations, fieldGroups, enumValues, "
                     + "entryPoints, declaration, methods). Do NOT send markup, CDATA, or angle-bracketed tags. "
                     + "Call xpp_read_object on an existing object to see the correct JSON format.";
            }

            // metadataStr is a JSON string — parse typed arrays from it
            int parsedCount = 0;
            var beforeValues = req.EnumValues;
            if (req.EnumValues == null) { req.EnumValues = ParseEnumValues(metadataStr); if (req.EnumValues != null) parsedCount++; }
            var beforeFields = req.Fields;
            if (req.Fields == null) { req.Fields = ParseFields(metadataStr); if (req.Fields != null) parsedCount++; }
            var beforeIndexes = req.Indexes;
            if (req.Indexes == null) { req.Indexes = ParseIndexes(metadataStr); if (req.Indexes != null) parsedCount++; }
            var beforeFieldGroups = req.FieldGroups;
            if (req.FieldGroups == null) { req.FieldGroups = ParseFieldGroups(metadataStr); if (req.FieldGroups != null) parsedCount++; }
            var beforeRelations = req.Relations;
            if (req.Relations == null) { req.Relations = ParseRelations(metadataStr); if (req.Relations != null) parsedCount++; }
            var beforeEntryPoints = req.EntryPoints;
            if (req.EntryPoints == null) { req.EntryPoints = ParseEntryPoints(metadataStr); if (req.EntryPoints != null) parsedCount++; }
            if (req.DataSources == null) { req.DataSources = ParseDataSources(metadataStr); if (req.DataSources != null) parsedCount++; }

            if (req.Properties == null)
                req.Properties = ExtractJsonObject(metadataStr, "properties") ?? ExtractFlatProperties(metadataStr);

            if (string.IsNullOrEmpty(req.ObjectType))
                req.ObjectType = ExtractJsonString(metadataStr, "objectType");
            if (string.IsNullOrEmpty(req.ObjectName))
                req.ObjectName = ExtractJsonString(metadataStr, "objectName");
            if (string.IsNullOrEmpty(req.ModelName))
                req.ModelName = ExtractJsonString(metadataStr, "modelName");
            if (string.IsNullOrEmpty(req.Declaration))
                req.Declaration = ExtractJsonString(metadataStr, "declaration");

            BridgeLog("NormalizeFromMetadata (Create): parsed " + parsedCount + " metadata sections from fallback");
            return null; // success
        }

        private string NormalizeFromMetadata(string body, UpdateObjectRequest req)
        {
            if (req.Properties != null || req.EnumValues != null || req.Fields != null)
                return null;

            string metadataStr = ExtractJsonString(body, "metadata");
            if (string.IsNullOrEmpty(metadataStr)) {
                BridgeLog("NormalizeFromMetadata (Update): metadata string not found — will use typed parameters");
                return null;
            }

            string trimmed = metadataStr.TrimStart();
            if (trimmed.StartsWith("<"))
            {
                BridgeLog("REJECTED formatted markup in metadata (update) — app is JSON-only");
                return "ERROR: Formatted markup is not supported. You MUST use the strongly-typed JSON parameters "
                     + "(objectType, objectName, properties, fields, indexes, relations, fieldGroups, enumValues, "
                     + "entryPoints, declaration, methods). Do NOT send markup, CDATA, or angle-bracketed tags. "
                     + "Call xpp_read_object on an existing object to see the correct JSON format.";
            }

            int parsedCount = 0;
            if (req.EnumValues == null) { req.EnumValues = ParseEnumValues(metadataStr); if (req.EnumValues != null) parsedCount++; }
            if (req.Fields == null) { req.Fields = ParseFields(metadataStr); if (req.Fields != null) parsedCount++; }
            if (req.Indexes == null) { req.Indexes = ParseIndexes(metadataStr); if (req.Indexes != null) parsedCount++; }
            if (req.FieldGroups == null) { req.FieldGroups = ParseFieldGroups(metadataStr); if (req.FieldGroups != null) parsedCount++; }
            if (req.Relations == null) { req.Relations = ParseRelations(metadataStr); if (req.Relations != null) parsedCount++; }
            if (req.EntryPoints == null) { req.EntryPoints = ParseEntryPoints(metadataStr); if (req.EntryPoints != null) parsedCount++; }
            if (req.DataSources == null) { req.DataSources = ParseDataSources(metadataStr); if (req.DataSources != null) parsedCount++; }

            if (req.Properties == null)
                req.Properties = ExtractJsonObject(metadataStr, "properties") ?? ExtractFlatProperties(metadataStr);

            if (string.IsNullOrEmpty(req.ObjectType))
                req.ObjectType = ExtractJsonString(metadataStr, "objectType");
            if (string.IsNullOrEmpty(req.ObjectName))
                req.ObjectName = ExtractJsonString(metadataStr, "objectName");
            if (string.IsNullOrEmpty(req.Declaration))
                req.Declaration = ExtractJsonString(metadataStr, "declaration");

            BridgeLog("NormalizeFromMetadata (Update): parsed " + parsedCount + " metadata sections from fallback");
            return null; // success
        }

        // ── Typed metadata parsing helpers ──

        /// <summary>
        /// Find a JSON key only at the top level of the outermost object (depth 1).
        /// Prevents matching keys nested inside child objects/arrays.
        /// Returns the index of the opening quote of the key, or -1 if not found.
        /// </summary>
        private static int FindTopLevelKey(string json, string key)
        {
            if (json == null) return -1;
            string marker = "\"" + key + "\"";
            int depth = 0;
            bool inStr = false;
            bool esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                // Check for marker match BEFORE toggling string state
                if (!inStr && depth == 1 && c == '"'
                    && i + marker.Length <= json.Length
                    && string.CompareOrdinal(json, i, marker, 0, marker.Length) == 0)
                {
                    return i;
                }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{' || c == '[') depth++;
                if (c == '}' || c == ']') depth--;
            }
            return -1;
        }

        private static System.Collections.Generic.Dictionary<string, string> ExtractJsonObject(string json, string key)
        {
            if (json == null) return null;
            string marker = "\"" + key + "\"";
            int idx = FindTopLevelKey(json, key);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + marker.Length);
            if (colon < 0) return null;
            int braceStart = json.IndexOf('{', colon);
            if (braceStart < 0) return null;

            // Find matching closing brace
            int depth = 0;
            bool inStr = false; bool esc = false; int braceEnd = -1;
            for (int i = braceStart; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                if (c == '}') { depth--; if (depth == 0) { braceEnd = i; break; } }
            }
            if (braceEnd < 0) return null;

            string content = json.Substring(braceStart, braceEnd - braceStart + 1);
            var dict = new System.Collections.Generic.Dictionary<string, string>();

            // Parse key-value pairs from flat object
            int pos = 1; // skip opening brace
            while (pos < content.Length - 1)
            {
                string k = ReadJsonString(content, ref pos);
                if (k == null) break;
                SkipWhitespace(content, ref pos);
                if (pos < content.Length && content[pos] == ':') pos++;
                SkipWhitespace(content, ref pos);
                string v = ReadJsonValue(content, ref pos);
                if (v != null) dict[k] = v;
                SkipWhitespace(content, ref pos);
                if (pos < content.Length && content[pos] == ',') pos++;
            }
            return dict.Count > 0 ? dict : null;
        }

        private static string ExtractJsonArray(string json, string key)
        {
            if (json == null) return null;
            string marker = "\"" + key + "\"";
            int idx = FindTopLevelKey(json, key);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + marker.Length);
            if (colon < 0) return null;
            int bracketStart = json.IndexOf('[', colon);
            if (bracketStart < 0) return null;

            int depth = 0; bool inStr = false; bool esc = false; int bracketEnd = -1;
            for (int i = bracketStart; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[') depth++;
                if (c == ']') { depth--; if (depth == 0) { bracketEnd = i; break; } }
            }
            if (bracketEnd < 0) return null;
            return json.Substring(bracketStart, bracketEnd - bracketStart + 1);
        }

        // rootDataEntities (AxCompositeDataEntityView) is the typed graph's RootDataEntities with camelCase keys.
        private static string RootDataEntitiesAsTypedMetadata(string body)
        {
            string array = ExtractJsonArray(body, "rootDataEntities");
            if (array == null) return null;
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
            object PascalCase(object node)
            {
                if (node is Dictionary<string, object> dict)
                    return dict.ToDictionary(kv => kv.Key.Length > 0 && kv.Key[0] != '$' ? char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1) : kv.Key,
                        kv => PascalCase(kv.Value));
                if (node is object[] items) return items.Select(PascalCase).ToArray();
                if (node is System.Collections.ArrayList list) return list.Cast<object>().Select(PascalCase).ToArray();
                return node;
            }
            return serializer.Serialize(new Dictionary<string, object> { ["RootDataEntities"] = PascalCase(serializer.DeserializeObject(array)) });
        }

        private static string ExtractJsonObjectRaw(string json, string key)
        {
            if (json == null) return null;
            string marker = "\"" + key + "\"";
            int idx = FindTopLevelKey(json, key);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + marker.Length);
            if (colon < 0) return null;
            int braceStart = json.IndexOf('{', colon);
            if (braceStart < 0) return null;

            int depth = 0;
            bool inStr = false;
            bool esc = false;
            int braceEnd = -1;

            for (int i = braceStart; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                if (c == '}')
                {
                    depth--;
                    if (depth == 0) { braceEnd = i; break; }
                }
            }

            if (braceEnd < 0) return null;
            return json.Substring(braceStart, braceEnd - braceStart + 1);
        }

        private static System.Collections.Generic.List<string> SplitJsonObjects(string arrayJson)
        {
            var objects = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(arrayJson) || arrayJson.Length < 2) return objects;
            string content = arrayJson.Substring(1, arrayJson.Length - 2); // strip [ ]

            int pos = 0;
            while (pos < content.Length)
            {
                int objStart = content.IndexOf('{', pos);
                if (objStart < 0) break;
                int depth = 0; bool inStr = false; bool esc = false; int objEnd = -1;
                for (int i = objStart; i < content.Length; i++)
                {
                    char c = content[i];
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == '"') { inStr = !inStr; continue; }
                    if (inStr) continue;
                    if (c == '{') depth++;
                    if (c == '}') { depth--; if (depth == 0) { objEnd = i; break; } }
                }
                if (objEnd < 0) break;
                objects.Add(content.Substring(objStart, objEnd - objStart + 1));
                pos = objEnd + 1;
            }
            return objects;
        }

        private static string ReadJsonString(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] != '"') return null;
            var sb = new StringBuilder();
            pos++; // skip opening quote
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == '\\' && pos + 1 < json.Length)
                {
                    char next = json[pos + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 5 < json.Length)
                            {
                                string hex = json.Substring(pos + 2, 4);
                                int cp;
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out cp))
                                {
                                    sb.Append((char)cp);
                                    pos += 6;
                                    continue;
                                }
                            }
                            sb.Append('u'); break;
                        default: sb.Append(next); break;
                    }
                    pos += 2; continue;
                }
                if (c == '"') { pos++; return sb.ToString(); }
                sb.Append(c); pos++;
            }
            return sb.ToString();
        }

        private static string ReadJsonValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length) return null;
            if (json[pos] == '"') return ReadJsonString(json, ref pos);
            // Read number, bool, or null token
            int start = pos;
            while (pos < json.Length && json[pos] != ',' && json[pos] != '}' && json[pos] != ']' && !char.IsWhiteSpace(json[pos]))
                pos++;
            return json.Substring(start, pos - start);
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private System.Collections.Generic.List<EnumValueDto> ParseEnumValues(string body)
        {
            string arrayJson = ExtractJsonArray(body, "enumValues");
            if (arrayJson == null) {
                BridgeLog("ParseEnumValues: enumValues array not found in body");
                return null;
            }
            var result = new System.Collections.Generic.List<EnumValueDto>();
            int skipped = 0;
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) { skipped++; continue; }
                var dto = new EnumValueDto { Name = name };
                string valStr = ExtractJsonString(obj, "value");
                if (!string.IsNullOrEmpty(valStr) && int.TryParse(valStr, out int v))
                    dto.Value = v;
                dto.Label = ExtractJsonString(obj, "label");
                result.Add(dto);
            }
            if (skipped > 0) BridgeLog("ParseEnumValues: skipped " + skipped + " items with missing name");
            BridgeLog("ParseEnumValues: parsed " + result.Count + " enum values");
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<FieldDto> ParseFields(string body, bool defaultFieldType = true)
        {
            string arrayJson = ExtractJsonArray(body, "fields");
            if (arrayJson == null) {
                BridgeLog("ParseFields: fields array not found in body");
                return null;
            }
            var result = new System.Collections.Generic.List<FieldDto>();
            int skipped = 0;
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) { skipped++; continue; }
                string enumType = ExtractJsonString(obj, "enumType");
                string fieldType = ExtractJsonString(obj, "fieldType");
                if (fieldType == null && defaultFieldType)
                    fieldType = string.IsNullOrEmpty(enumType) ? "String" : "Enum";
                result.Add(new FieldDto
                {
                    Name = name,
                    FieldType = fieldType,
                    ExtendedDataType = ExtractJsonString(obj, "extendedDataType"),
                    EnumType = enumType,
                    Label = ExtractJsonString(obj, "label")
                });
            }
            if (skipped > 0) BridgeLog("ParseFields: skipped " + skipped + " items with missing name");
            BridgeLog("ParseFields: parsed " + result.Count + " fields");
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<IndexDto> ParseIndexes(string body)
        {
            string arrayJson = ExtractJsonArray(body, "indexes");
            if (arrayJson == null) {
                BridgeLog("ParseIndexes: indexes array not found in body");
                return null;
            }
            var result = new System.Collections.Generic.List<IndexDto>();
            int skipped = 0;
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) { skipped++; continue; }
                var dto = new IndexDto { Name = name };
                string dup = ExtractJsonString(obj, "allowDuplicates");
                dto.AllowDuplicates = "true".Equals(dup, StringComparison.OrdinalIgnoreCase);
                dto.AllowDuplicatesSpecified = dup != null;
                string[] fields = ExtractJsonStringArray(obj, "fields");
                if (fields != null) dto.Fields.AddRange(fields);
                result.Add(dto);
            }
            if (skipped > 0) BridgeLog("ParseIndexes: skipped " + skipped + " items with missing name");
            BridgeLog("ParseIndexes: parsed " + result.Count + " indexes");
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<FieldGroupDto> ParseFieldGroups(string body)
        {
            string arrayJson = ExtractJsonArray(body, "fieldGroups");
            if (arrayJson == null) {
                BridgeLog("ParseFieldGroups: fieldGroups array not found in body");
                return null;
            }
            var result = new System.Collections.Generic.List<FieldGroupDto>();
            int skipped = 0;
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) { skipped++; continue; }
                var dto = new FieldGroupDto { Name = name, Label = ExtractJsonString(obj, "label") };
                string[] fields = ExtractJsonStringArray(obj, "fields");
                if (fields != null) dto.Fields.AddRange(fields);
                result.Add(dto);
            }
            if (skipped > 0) BridgeLog("ParseFieldGroups: skipped " + skipped + " items with missing name");
            BridgeLog("ParseFieldGroups: parsed " + result.Count + " field groups");
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<RelationDto> ParseRelations(string body)
        {
            string arrayJson = ExtractJsonArray(body, "relations");
            if (arrayJson == null) {
                BridgeLog("ParseRelations: relations array not found in body");
                return null;
            }
            var result = new System.Collections.Generic.List<RelationDto>();
            int skipped = 0;
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) { skipped++; continue; }
                var dto = new RelationDto
                {
                    Name = name,
                    RelatedTable = ExtractJsonString(obj, "relatedTable")
                };
                string constraintsJson = ExtractJsonArray(obj, "constraints");
                if (constraintsJson != null)
                {
                    foreach (string cObj in SplitJsonObjects(constraintsJson))
                    {
                        dto.Constraints.Add(new RelationConstraintDto
                        {
                            Field = ExtractJsonString(cObj, "field"),
                            RelatedField = ExtractJsonString(cObj, "relatedField")
                        });
                    }
                }
                result.Add(dto);
            }
            if (skipped > 0) BridgeLog("ParseRelations: skipped " + skipped + " items with missing name");
            BridgeLog("ParseRelations: parsed " + result.Count + " relations");
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<EntryPointDto> ParseEntryPoints(string body)
        {
            string arrayJson = ExtractJsonArray(body, "entryPoints");
            if (arrayJson == null) return null;
            var result = new System.Collections.Generic.List<EntryPointDto>();
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new EntryPointDto
                {
                    Name = name,
                    ObjectType = ExtractJsonString(obj, "objectType"),
                    ObjectName = ExtractJsonString(obj, "objectName"),
                    Grant = ExtractJsonString(obj, "grant")
                });
            }
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<DataEntityPermissionDto> ParseDataEntityPermissions(string body)
        {
            string arrayJson = ExtractJsonArray(body, "dataEntityPermissions");
            if (arrayJson == null) return null;
            var result = new System.Collections.Generic.List<DataEntityPermissionDto>();
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new DataEntityPermissionDto
                {
                    Name = name,
                    Grant = ExtractJsonString(obj, "grant"),
                    IntegrationMode = ExtractJsonString(obj, "integrationMode")
                });
            }
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<MenuElementDto> ParseMenuElements(string body)
        {
            string arrayJson = ExtractJsonArray(body, "menuElements");
            if (arrayJson == null) return null;
            var result = new System.Collections.Generic.List<MenuElementDto>();
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new MenuElementDto
                {
                    Name = name,
                    MenuItemName = ExtractJsonString(obj, "menuItemName"),
                    MenuItemType = ExtractJsonString(obj, "menuItemType"),
                    Parent = ExtractJsonString(obj, "parent"),
                    Position = ExtractJsonString(obj, "position"),
                    PreviousSibling = ExtractJsonString(obj, "previousSibling"),
                });
            }
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<EntityFieldDto> ParseEntityFields(string body)
        {
            string arrayJson = ExtractJsonArray(body, "entityFields");
            if (arrayJson == null) return null;
            var result = new System.Collections.Generic.List<EntityFieldDto>();
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) continue;
                string mandatory = ExtractJsonString(obj, "mandatory");
                result.Add(new EntityFieldDto
                {
                    Name = name,
                    DataSource = ExtractJsonString(obj, "dataSource"),
                    DataField = ExtractJsonString(obj, "dataField"),
                    Label = ExtractJsonString(obj, "label"),
                    Mandatory = mandatory == null ? (bool?)null : "true".Equals(mandatory, StringComparison.OrdinalIgnoreCase)
                });
            }
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<FormControlDto> ParseFormControls(string json, string key)
        {
            string arrayJson = ExtractJsonArray(json, key);
            if (arrayJson == null) return null;

            var result = new System.Collections.Generic.List<FormControlDto>();
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new FormControlDto
                {
                    Name = name,
                    ControlType = ExtractJsonString(obj, "controlType") ?? ExtractJsonString(obj, "type"),
                    Parent = ExtractJsonString(obj, "parent"),
                    Position = ExtractJsonString(obj, "position"),
                    PreviousSibling = ExtractJsonString(obj, "previousSibling"),
                    DataSource = ExtractJsonString(obj, "dataSource"),
                    DataField = ExtractJsonString(obj, "dataField"),
                    DataGroup = ExtractJsonString(obj, "dataGroup"),
                    Label = ExtractJsonString(obj, "label"),
                    Properties = ExtractJsonObject(obj, "properties"),
                    Controls = ParseFormControls(obj, "controls") ?? new System.Collections.Generic.List<FormControlDto>()
                });
            }
            BridgeLog("ParseFormControls(" + key + "): parsed " + result.Count + " controls");
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<ControlModificationDto> ParseControlModifications(string body)
        {
            string arrayJson = ExtractJsonArray(body, "controlModifications");
            if (arrayJson == null) return null;

            var result = new System.Collections.Generic.List<ControlModificationDto>();
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                string name = ExtractJsonString(obj, "name");
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new ControlModificationDto { Name = name, Properties = ExtractJsonObject(obj, "properties") });
            }
            return result.Count > 0 ? result : null;
        }

        private System.Collections.Generic.List<QueryDataSourceDto> ParseDataSources(string body)
        {
            string arrayJson = ExtractJsonArray(body, "dataSources");
            if (arrayJson == null) return null;

            var result = new System.Collections.Generic.List<QueryDataSourceDto>();
            foreach (string obj in SplitJsonObjects(arrayJson))
            {
                var ds = ParseDataSourceObject(obj, null);
                if (ds != null) result.Add(ds);
            }

            return result.Count > 0 ? result : null;
        }

        private QueryDataSourceDto ParseDataSourceObject(string objJson, string parentName)
        {
            string table = ExtractJsonString(objJson, "table") ?? ExtractJsonString(objJson, "tableName");
            string name = ExtractJsonString(objJson, "name") ?? table;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var dto = new QueryDataSourceDto
            {
                Name = name,
                Table = table,
                ParentDataSource = ExtractJsonString(objJson, "parentDataSource") ?? parentName,
                JoinMode = ExtractJsonString(objJson, "joinMode"),
                LinkType = ExtractJsonString(objJson, "linkType")
            };

            string dynamicFields = ExtractJsonString(objJson, "dynamicFields");
            if (dynamicFields != null)
                dto.DynamicFields = "true".Equals(dynamicFields, StringComparison.OrdinalIgnoreCase)
                    || "yes".Equals(dynamicFields, StringComparison.OrdinalIgnoreCase);
            dto.Fields = ExtractJsonStringArray(objJson, "fields")?.ToList();

            string relations = ExtractJsonString(objJson, "relations");
            if (relations != null)
                dto.Relations = "true".Equals(relations, StringComparison.OrdinalIgnoreCase);

            string firstOnly = ExtractJsonString(objJson, "firstOnly");
            if (firstOnly != null)
                dto.FirstOnly = "true".Equals(firstOnly, StringComparison.OrdinalIgnoreCase);

            string rangesJson = ExtractJsonArray(objJson, "ranges");
            if (rangesJson != null)
            {
                foreach (string rangeObj in SplitJsonObjects(rangesJson))
                {
                    dto.Ranges.Add(new QueryRangeDto
                    {
                        Name = ExtractJsonString(rangeObj, "name"),
                        Field = ExtractJsonString(rangeObj, "field") ?? ExtractJsonString(rangeObj, "dataField"),
                        Value = ExtractJsonString(rangeObj, "value")
                    });
                }
            }

            string childrenJson = ExtractJsonArray(objJson, "childDataSources");
            if (childrenJson == null)
                childrenJson = ExtractJsonArray(objJson, "dataSources");

            if (childrenJson != null)
            {
                foreach (string childObj in SplitJsonObjects(childrenJson))
                {
                    var child = ParseDataSourceObject(childObj, name);
                    if (child != null) dto.ChildDataSources.Add(child);
                }
            }

            return dto;
        }

        // ── Flat-property extraction for metadata fallback ──

        /// <summary>
        /// Scans a JSON object for all top-level key-value pairs whose values are simple types
        /// (string, number, boolean). Skips arrays, objects, and known typed-array keys.
        /// Returns remaining pairs as a property dictionary.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _reservedKeys =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "objectType", "objectName", "modelName", "declaration", "methods",
                "enumValues", "fields", "indexes", "fieldGroups", "relations",
                "entryPoints", "dataSources", "typedMetadata", "properties", "action",
                "privileges", "duties", "dataEntityPermissions", "entityFields", "skipStagingTable", "removeSecurityItems", "menuElements"
            };

        private static System.Collections.Generic.Dictionary<string, string> ExtractFlatProperties(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var dict = new System.Collections.Generic.Dictionary<string, string>();
            int pos = json.IndexOf('{');
            if (pos < 0) return null;
            pos++; // skip opening brace

            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}') break;

                // Read key
                string key = ReadJsonString(json, ref pos);
                if (key == null) break;
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':') break;
                pos++; // skip colon
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length) break;

                // Skip arrays and objects
                if (json[pos] == '[' || json[pos] == '{')
                {
                    // Skip the entire array/object
                    int depth = 0;
                    char open = json[pos], close = open == '[' ? ']' : '}';
                    bool inStr = false; bool esc = false;
                    for (int i = pos; i < json.Length; i++)
                    {
                        char c = json[i];
                        if (esc) { esc = false; continue; }
                        if (c == '\\') { esc = true; continue; }
                        if (c == '"') { inStr = !inStr; continue; }
                        if (inStr) continue;
                        if (c == open) depth++;
                        if (c == close) { depth--; if (depth == 0) { pos = i + 1; break; } }
                    }
                }
                else
                {
                    // Read simple value
                    string value = ReadJsonValue(json, ref pos);
                    if (value != null && !_reservedKeys.Contains(key))
                        dict[key] = value;
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',') pos++;
            }
            return dict.Count > 0 ? dict : null;
        }

        // ── Minimal JSON helpers (no external deps) ──

        private static string ExtractJsonString(string json, string key)
        {
            if (json == null) return null;
            string pattern = "\"" + key + "\"";
            int idx = FindTopLevelKey(json, key);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;

            // Handle bare numbers
            if (char.IsDigit(json[start]) || json[start] == '-')
            {
                int numEnd = start;
                while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '.' || json[numEnd] == '-'))
                    numEnd++;
                return json.Substring(start, numEnd - start);
            }

            // Handle bare booleans
            if (json[start] == 't' || json[start] == 'f')
            {
                int tokenEnd = start;
                while (tokenEnd < json.Length && char.IsLetter(json[tokenEnd])) tokenEnd++;
                return json.Substring(start, tokenEnd - start);
            }

            if (json[start] == 'n') return null; // null literal
            if (json[start] != '"') return null;

            var sb = new StringBuilder();
            for (int i = start + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i++; continue;
                        case '\\': sb.Append('\\'); i++; continue;
                        case 'n': sb.Append('\n'); i++; continue;
                        case 'r': sb.Append('\r'); i++; continue;
                        case 't': sb.Append('\t'); i++; continue;
                        case 'u':
                            if (i + 5 < json.Length)
                            {
                                string hex = json.Substring(i + 2, 4);
                                int cp;
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out cp))
                                {
                                    sb.Append((char)cp);
                                    i += 5;
                                    continue;
                                }
                            }
                            sb.Append('u'); i++; continue;
                        default: sb.Append(next); i++; continue;
                    }
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The "methods" array: plain X++ source strings, or objects whose "source" holds the code (agents often send
        /// {name, source}). Collecting every string token would turn object keys and values into bogus methods.
        /// </summary>
        private static string[] ExtractMethodSources(string json)
        {
            if (json == null || FindTopLevelKey(json, "methods") < 0) return null;
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (!(serializer.DeserializeObject(json) is Dictionary<string, object> root)
                    || !root.TryGetValue("methods", out object value) || !(value is object[] items))
                    return ExtractJsonStringArray(json, "methods");
                var sources = new List<string>();
                foreach (object item in items)
                {
                    if (item is string text) sources.Add(text);
                    else if (item is Dictionary<string, object> obj
                        && (obj.TryGetValue("source", out object s) || obj.TryGetValue("code", out s)) && s is string code)
                        sources.Add(code);
                    else
                        throw new ArgumentException("Each 'methods' item must be the method's full X++ source as a string (or an object with 'source').");
                }
                return sources.ToArray();
            }
            catch (ArgumentException) { throw; }
            catch { return ExtractJsonStringArray(json, "methods"); }
        }

        private static string[] ExtractJsonStringArray(string json, string key)
        {
            if (json == null) return null;
            string pattern = "\"" + key + "\"";
            int idx = FindTopLevelKey(json, key);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int bracketStart = json.IndexOf('[', colon);
            if (bracketStart < 0) return null;

            int depth = 0;
            int bracketEnd = -1;
            bool inStr = false;
            bool esc = false;
            for (int i = bracketStart; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[') depth++;
                if (c == ']') { depth--; if (depth == 0) { bracketEnd = i; break; } }
            }
            if (bracketEnd < 0) return null;

            string content = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            var result = new System.Collections.Generic.List<string>();
            int pos = 0;
            while (pos < content.Length)
            {
                int qs = content.IndexOf('"', pos);
                if (qs < 0) break;
                var val = new StringBuilder();
                int j = qs + 1;
                while (j < content.Length)
                {
                    char c = content[j];
                    if (c == '\\' && j + 1 < content.Length)
                    {
                        char next = content[j + 1];
                        switch (next)
                        {
                            case '"': val.Append('"'); break;
                            case '\\': val.Append('\\'); break;
                            case '/': val.Append('/'); break;
                            case 'n': val.Append('\n'); break;
                            case 'r': val.Append('\r'); break;
                            case 't': val.Append('\t'); break;
                            case 'u':
                                if (j + 5 < content.Length)
                                {
                                    string hex = content.Substring(j + 2, 4);
                                    int cp;
                                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out cp))
                                    {
                                        val.Append((char)cp);
                                        j += 6;
                                        continue;
                                    }
                                }
                                val.Append('u'); break;
                            default: val.Append(next); break;
                        }
                        j += 2; continue;
                    }
                    if (c == '"') break;
                    val.Append(c); j++;
                }
                result.Add(val.ToString());
                pos = j + 1;
            }
            return result.Count > 0 ? result.ToArray() : null;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

    }
}
