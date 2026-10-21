using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Core.Collections;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Microsoft.Dynamics.AX.Metadata.Service;
using XppVsCode.Server.Host;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>
    /// Wraps the D365FO IMetaModelService loaded headlessly from disk by <see cref="MetadataHost"/>.
    /// All metadata operations use strongly-typed APIs — no raw XML manipulation.
    /// Calls are serialized by <see cref="MetaModelBridgeServer"/>.
    /// </summary>
    public sealed partial class MetaModelBridge : IMetaModelBridge
    {
        private readonly MetadataHost _host;
        private readonly LabelStore _labels;
        private readonly string _projectFile;

        internal MetaModelBridge(MetadataHost host, LabelStore labels, string projectFile)
        {
            _host = host;
            _labels = labels;
            _projectFile = projectFile;
        }
        
        // Cache for GetObjectModelName with 60-second TTL
        private class CachedModelName
        {
            public string ModelName { get; set; }
            public DateTime CachedAt { get; set; }
        }
        private System.Collections.Generic.Dictionary<string, CachedModelName> _objectModelNameCache =
            new System.Collections.Generic.Dictionary<string, CachedModelName>();
        private const int ModelNameCacheTtlSeconds = 60;

        private void InvalidateCaches()
        {
            _objectModelNameCache.Clear();
        }

        private IMetaModelService MetaService => _host.MetaService;

        private IMetadataProvider MetadataProvider => _host.Provider;

        // ── Object CRUD ──

        private static bool HasTypedUpdateSupport(string objectType)
        {
            switch (objectType)
            {
                case "AxClass":
                case "AxTable":
                case "AxForm":
                case "AxEdt":
                case "AxEnum":
                case "AxView":
                case "AxQuery":
                case "AxDataEntityView":
                case "AxMenu":
                case "AxTableExtension":
                case "AxEnumExtension":
                case "AxEdtExtension":
                case "AxFormExtension":
                case "AxSecurityPrivilege":
                case "AxSecurityDuty":
                case "AxSecurityRole":
                case "AxSecurityDutyExtension":
                case "AxSecurityRoleExtension":
                case "AxDataEntityViewExtension":
                    return true;
                default:
                    return false;
            }
        }

        public MetaModelResult CreateObject(CreateObjectRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return Fail("MetaModel service not available. Is D365FO tools extension loaded?");

            var saveInfo = GetModelSaveInfo(request.ModelName);
            if (saveInfo == null)
                return Fail("Could not resolve model '" + request.ModelName + "'. Ensure a D365FO project is open.");

            try
            {
                string preError = PreValidateCreate(request);
                if (preError != null) return Fail(preError);

                MetaModelResult result;
                switch (request.ObjectType)
                {
                    case "AxClass":          result = CreateClass(request, saveInfo); break;
                    case "AxTable":          result = CreateTable(request, saveInfo); break;
                    case "AxForm":           result = CreateForm(request, saveInfo); break;
                    case "AxEdt":            result = CreateEdt(request, saveInfo); break;
                    case "AxEnum":           result = CreateEnum(request, saveInfo); break;
                    case "AxMenuItemDisplay": result = CreateMenuItemDisplay(request, saveInfo); break;
                    case "AxMenuItemOutput":  result = CreateMenuItemOutput(request, saveInfo); break;
                    case "AxMenuItemAction":  result = CreateMenuItemAction(request, saveInfo); break;
                    case "AxQuery":          result = CreateQuery(request, saveInfo); break;
                    case "AxView":           result = CreateView(request, saveInfo); break;
                    case "AxDataEntityView": result = CreateDataEntityView(request, saveInfo); break;
                    case "AxSecurityPrivilege": result = CreateSecurityPrivilege(request, saveInfo); break;
                    case "AxSecurityDuty":   result = CreateSecurityDuty(request, saveInfo); break;
                    case "AxSecurityRole":   result = CreateSecurityRole(request, saveInfo); break;
                    case "AxService":        result = CreateService(request, saveInfo); break;
                    case "AxServiceGroup":   result = CreateServiceGroup(request, saveInfo); break;
                    case "AxMenu":           result = CreateMenu(request, saveInfo); break;
                    case "AxTile":           result = CreateTile(request, saveInfo); break;
                    case "AxConfigurationKey": result = CreateConfigurationKey(request, saveInfo); break;
                    case "AxTableExtension": result = CreateTableExtension(request, saveInfo); break;
                    case "AxEnumExtension":  result = CreateEnumExtension(request, saveInfo); break;
                    case "AxEdtExtension":   result = CreateEdtExtension(request, saveInfo); break;
                    case "AxFormExtension":  result = CreateFormExtension(request, saveInfo); break;
                    case "AxSecurityDutyExtension": result = CreateSecurityDutyExtension(request, saveInfo); break;
                    case "AxSecurityRoleExtension": result = CreateSecurityRoleExtension(request, saveInfo); break;
                    case "AxDataEntityViewExtension": result = CreateDataEntityViewExtension(request, saveInfo); break;

                    default:
                        result = Fail("Unsupported object type: " + request.ObjectType); break;
                }

                if (result.Success)
                {
                    // Force MetaModel refresh so the Update path can find the new object.
                    _host.Reset();
                    InvalidateCaches();

                    // Apply all metadata through UpdateObject so Create and Update
                    // share a single code path for declaration, methods, properties,
                    // fields, indexes, relations, etc.
                    if (HasTypedUpdateSupport(request.ObjectType))
                    {
                        var updateResult = UpdateObject(request.ToUpdateRequest());
                        if (!updateResult.Success)
                            return Fail("Created blank " + request.ObjectType + " '"
                                + request.ObjectName + "' but metadata apply failed: "
                                + updateResult.Message);

                        // Keep the update's report (e.g. form placement verification) visible to the caller.
                        int reportStart = updateResult.Message?.IndexOf('\n') ?? -1;
                        if (reportStart >= 0)
                            result.Message += updateResult.Message.Substring(reportStart);
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                return Fail("Create failed: " + ex.Message);
            }
        }

        public MetaModelResult UpdateObject(UpdateObjectRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return Fail("MetaModel service not available.");

            try
            {
                MetaModelResult result;
                switch (request.ObjectType)
                {
                    case "AxClass":
                        result = UpdateClass(request); break;
                    case "AxTable":
                        result = UpdateTable(request); break;
                    case "AxForm":
                        result = UpdateForm(request); break;
                    case "AxEdt":
                        result = UpdateEdt(request); break;
                    case "AxEnum":
                        result = UpdateEnum(request); break;
                    case "AxView":
                        result = UpdateView(request); break;
                    case "AxQuery":
                        result = UpdateQuery(request); break;
                    case "AxDataEntityView":
                        result = UpdateDataEntityView(request); break;
                    case "AxMenu":
                        result = UpdateMenu(request); break;
                    case "AxTableExtension":
                        result = UpdateTableExtension(request); break;
                    case "AxEnumExtension":
                        result = UpdateEnumExtension(request); break;
                    case "AxEdtExtension":
                        result = UpdateEdtExtension(request); break;
                    case "AxFormExtension":
                        result = UpdateFormExtension(request); break;
                    case "AxSecurityPrivilege":
                        result = UpdateSecurityPrivilege(request); break;
                    case "AxSecurityDuty":
                        result = UpdateSecurityDuty(request); break;
                    case "AxSecurityRole":
                        result = UpdateSecurityRole(request); break;
                    case "AxSecurityDutyExtension":
                        result = UpdateSecurityDutyExtension(request); break;
                    case "AxSecurityRoleExtension":
                        result = UpdateSecurityRoleExtension(request); break;
                    case "AxDataEntityViewExtension":
                        result = UpdateDataEntityViewExtension(request); break;
                    default:
                        result = UpdateGenericByTypedApi(request); break;
                }

                // Force cache refresh so subsequent ReadObject returns updated metadata
                // (MetaService.Update* writes to disk but may not hydrate child collections in cache)
                if (result.Success)
                {
                    _host.Reset();
                    InvalidateCaches();
                }

                return result;
            }
            catch (Exception ex)
            {
                return Fail("Update failed: " + ex.Message);
            }
        }

        public ReadObjectResult ReadObject(string objectType, string objectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new ReadObjectResult { Message = "MetaModel service not available." };

            try
            {
                var result = new ReadObjectResult { ObjectType = objectType, ObjectName = objectName, Success = true };

                switch (objectType)
                {
                    case "AxClass":
                        var cls = MetaService.GetClass(objectName);
                        if (cls == null) return new ReadObjectResult { Message = "Class '" + objectName + "' not found." };
                        result.Declaration = cls.Declaration;
                        if (cls.Methods != null)
                            foreach (var m in cls.Methods)
                                result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
                        result.Properties = ExtractProperties(cls);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, cls);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxTable":
                        var tbl = MetaService.GetTable(objectName);
                        if (tbl == null) return new ReadObjectResult { Message = "Table '" + objectName + "' not found." };
                        result.Declaration = tbl.Declaration;
                        if (tbl.Methods != null)
                            foreach (var m in tbl.Methods)
                                result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
                        result.Properties = ExtractProperties(tbl);
                        result.Fields = ExtractFields(tbl.Fields);
                        result.Indexes = ExtractIndexes(tbl.Indexes);
                        result.FieldGroups = ExtractFieldGroups(tbl.FieldGroups);
                        result.Relations = ExtractRelations(tbl.Relations);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, tbl);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxForm":
                        var frm = MetaService.GetForm(objectName);
                        if (frm == null) return new ReadObjectResult { Message = "Form '" + objectName + "' not found." };
                        result.Declaration = frm.SourceCode?.Declaration;
                        if (frm.Methods != null)
                            foreach (var m in frm.Methods)
                                result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
                        result.Properties = ExtractProperties(frm);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, frm);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxEnum":
                        var enm = MetaService.GetEnum(objectName);
                        if (enm == null) return new ReadObjectResult { Message = "Enum '" + objectName + "' not found." };
                        result.Properties = ExtractProperties(enm);
                        result.EnumValues = ExtractEnumValues(enm.EnumValues);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, enm);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxEdt":
                        var edt = MetaService.GetExtendedDataType(objectName);
                        if (edt == null) return new ReadObjectResult { Message = "EDT '" + objectName + "' not found." };
                        result.Properties = ExtractProperties(edt);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, edt);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxView":
                        var view = MetaService.GetView(objectName);
                        if (view == null) return new ReadObjectResult { Message = "View '" + objectName + "' not found." };
                        result.Declaration = view.Declaration;
                        if (view.Methods != null)
                            foreach (var m in view.Methods)
                                result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
                        result.Properties = ExtractProperties(view);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, view);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxQuery":
                        var qry = MetaService.GetQuery(objectName);
                        if (qry == null) return new ReadObjectResult { Message = "Query '" + objectName + "' not found." };
                        result.Properties = ExtractProperties(qry);
                        result.DataSources = ExtractQueryDataSources(qry);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, qry);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxDataEntityView":
                        var dev = MetaService.GetDataEntityView(objectName);
                        if (dev == null) return new ReadObjectResult { Message = "DataEntityView '" + objectName + "' not found." };
                        result.Declaration = dev.Declaration;
                        if (dev.Methods != null)
                            foreach (var m in dev.Methods)
                                result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
                        result.Properties = ExtractProperties(dev);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, dev);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxMenu":
                        var menu = MetaService.GetMenu(objectName);
                        if (menu == null) return new ReadObjectResult { Message = "Menu '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, menu);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxMenuItemDisplay":
                        var mid = MetaService.GetMenuItemDisplay(objectName);
                        if (mid == null) return new ReadObjectResult { Message = "MenuItemDisplay '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, mid);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxMenuItemOutput":
                        var mio = MetaService.GetMenuItemOutput(objectName);
                        if (mio == null) return new ReadObjectResult { Message = "MenuItemOutput '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, mio);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxMenuItemAction":
                        var mia = MetaService.GetMenuItemAction(objectName);
                        if (mia == null) return new ReadObjectResult { Message = "MenuItemAction '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, mia);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxSecurityPrivilege":
                        var priv = MetaService.GetSecurityPrivilege(objectName);
                        if (priv == null) return new ReadObjectResult { Message = "SecurityPrivilege '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, priv);
                        result.EntryPoints = ExtractEntryPoints(priv);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxSecurityDuty":
                        var sduty = MetaService.GetSecurityDuty(objectName);
                        if (sduty == null) return new ReadObjectResult { Message = "SecurityDuty '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, sduty);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxSecurityRole":
                        var srole = MetaService.GetSecurityRole(objectName);
                        if (srole == null) return new ReadObjectResult { Message = "SecurityRole '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, srole);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxService":
                        var svc = MetaService.GetService(objectName);
                        if (svc == null) return new ReadObjectResult { Message = "Service '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, svc);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxServiceGroup":
                        var sgrp = MetaService.GetServiceGroup(objectName);
                        if (sgrp == null) return new ReadObjectResult { Message = "ServiceGroup '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, sgrp);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxTile":
                        var axtile = MetaService.GetTile(objectName);
                        if (axtile == null) return new ReadObjectResult { Message = "Tile '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, axtile);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxConfigurationKey":
                        var cfgkey = MetaService.GetConfigurationKey(objectName);
                        if (cfgkey == null) return new ReadObjectResult { Message = "ConfigurationKey '" + objectName + "' not found." };
                        PopulateReadResultFromGenericObject(result, objectType, cfgkey);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxTableExtension":
                        var tblExt = MetaService.GetTableExtension(objectName);
                        if (tblExt == null) return new ReadObjectResult { Message = "TableExtension '" + objectName + "' not found." };
                        result.Properties = ExtractPropertyModifications(tblExt.PropertyModifications);
                        result.Fields = ExtractFields(tblExt.Fields);
                        result.Indexes = ExtractIndexes(tblExt.Indexes);
                        result.FieldGroups = ExtractFieldGroups(tblExt.FieldGroups);
                        result.Relations = ExtractRelations(tblExt.Relations);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, tblExt);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxEnumExtension":
                        var enmExt = MetaService.GetEnumExtension(objectName);
                        if (enmExt == null) return new ReadObjectResult { Message = "EnumExtension '" + objectName + "' not found." };
                        result.Properties = ExtractPropertyModifications(enmExt.PropertyModifications);
                        result.EnumValues = ExtractEnumValues(enmExt.EnumValues);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, enmExt);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxEdtExtension":
                        var edtExt = MetaService.GetEdtExtension(objectName);
                        if (edtExt == null) return new ReadObjectResult { Message = "EdtExtension '" + objectName + "' not found." };
                        result.Properties = ExtractPropertyModifications(edtExt.PropertyModifications);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, edtExt);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    case "AxFormExtension":
                        var frmExt = MetaService.GetFormExtension(objectName);
                        if (frmExt == null) return new ReadObjectResult { Message = "FormExtension '" + objectName + "' not found." };
                        result.Properties = ExtractPropertyModifications(frmExt.PropertyModifications);
                        result.DataSources = ExtractFormExtensionDataSources(frmExt);
                        result.Controls = ExtractFormExtensionControls(frmExt);
                        result.ControlModifications = ExtractControlModifications(frmExt);
                        result.TypedMetadataJson = BuildTypedMetadataJson(objectType, frmExt);
                        result.ModelName = GetObjectModelName(objectType, objectName);
                        break;

                    default:
                        var generic = ReadViaMetadataProvider(objectType, objectName, out string genericModel);
                        if (generic == null)
                            return new ReadObjectResult
                            {
                                Message = objectType + " '" + objectName + "' not found, or '" + objectType
                                    + "' is not a D365FO metadata type. Check the type and name with xpp_find_object."
                            };
                        PopulateReadResultFromGenericObject(result, objectType, generic);
                        result.ModelName = genericModel ?? GetObjectModelName(objectType, objectName);
                        break;
                }

                result.IsCustom = IsCustomModel(result.ModelName);
                return result;
            }
            catch (Exception ex)
            {
                return new ReadObjectResult { Message = "Read failed: " + ex.Message };
            }
        }

        public ReadObjectResult ReadObjectByPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return new ReadObjectResult { Message = "filePath is required." };

            // Derive type and name from the path structure:
            // <root>\<Package>\<Model>\AxClass\MyClass.axclass → type=AxClass, name=MyClass
            string objectName = Path.GetFileNameWithoutExtension(filePath);
            string folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "") ?? "";

            if (!string.IsNullOrEmpty(folder) && folder.StartsWith("Ax", StringComparison.OrdinalIgnoreCase))
            {
                var result = ReadObject(folder, objectName);
                if (result != null)
                {
                    result.FilePath = filePath;
                    return result;
                }
            }

            return new ReadObjectResult
            {
                Message = "Cannot read type '" + folder + "' via the MetaModel API. "
                        + "Use xpp_read_object with objectType and objectName instead."
            };
        }




        // Every IMetadataProvider sub-provider has Read(string) returning its Ax type; find the one for objectType.
        private object ReadViaMetadataProvider(string objectType, string objectName, out string modelName)
        {
            modelName = null;
            var provider = MetadataProvider;
            if (provider == null || string.IsNullOrEmpty(objectType)) return null;

            foreach (var prop in typeof(IMetadataProvider).GetProperties())
            {
                var read = prop.PropertyType.GetMethod("Read", new[] { typeof(string) })
                    ?? prop.PropertyType.GetInterfaces().Select(i => i.GetMethod("Read", new[] { typeof(string) })).FirstOrDefault(m => m != null);
                if (read == null || !string.Equals(read.ReturnType.Name, objectType, StringComparison.OrdinalIgnoreCase))
                    continue;

                object subProvider = prop.GetValue(provider);
                if (subProvider == null) return null;
                object axObj = read.Invoke(subProvider, new object[] { objectName });
                if (axObj == null) return null;

                var getModelInfo = subProvider.GetType().GetMethod("GetModelInfo", new[] { typeof(string) });
                if (getModelInfo?.Invoke(subProvider, new object[] { objectName }) is System.Collections.IEnumerable models)
                    modelName = models.OfType<ModelInfo>().FirstOrDefault()?.Name;
                return axObj;
            }
            return null;
        }

        private static void PopulateReadResultFromGenericObject(ReadObjectResult result, string objectType, object axObj)
        {
            if (result == null || axObj == null) return;

            result.Properties = ExtractProperties(axObj);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, axObj);

            var declarationProp = axObj.GetType().GetProperty("Declaration");
            result.Declaration = declarationProp?.GetValue(axObj) as string;

            if (string.IsNullOrEmpty(result.Declaration))
            {
                var sourceCodeProp = axObj.GetType().GetProperty("SourceCode");
                var sourceCode = sourceCodeProp?.GetValue(axObj);
                if (sourceCode != null)
                {
                    var srcDeclProp = sourceCode.GetType().GetProperty("Declaration");
                    result.Declaration = srcDeclProp?.GetValue(sourceCode) as string;
                }
            }

            var methodsProp = axObj.GetType().GetProperty("Methods");
            var methods = methodsProp?.GetValue(axObj) as System.Collections.IEnumerable;
            if (methods != null)
            {
                foreach (var m in methods)
                {
                    if (m == null) continue;
                    string name = m.GetType().GetProperty("Name")?.GetValue(m) as string;
                    string source = m.GetType().GetProperty("Source")?.GetValue(m) as string;
                    if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(source))
                        result.Methods.Add(new MethodInfo { Name = name, Source = source });
                }
            }
        }

        private static string BuildTypedMetadataJson(string objectType, object axObj)
        {
            if (axObj == null) return null;
            try
            {
                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                    RecursionLimit = 64
                };

                Type proxyType = ResolveProxyType(objectType);
                if (proxyType != null)
                {
                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    object proxy = MapSourceToTargetType(axObj, proxyType, 0, visited);
                    return serializer.Serialize(proxy);
                }

                var visitedFallback = new HashSet<object>(ReferenceEqualityComparer.Instance);
                object graph = ExtractMetadataGraph(axObj, 0, visitedFallback);
                return serializer.Serialize(graph);
            }
            catch
            {
                return null;
            }
        }

        private static object ExtractMetadataGraph(object value, int depth, HashSet<object> visited)
        {
            if (value == null) return null;
            if (depth > 12) return null;

            Type t = value.GetType();
            Type nt = Nullable.GetUnderlyingType(t) ?? t;

            if (nt.IsEnum) return value.ToString();
            if (nt.IsPrimitive || nt == typeof(string) || nt == typeof(decimal)
                || nt == typeof(DateTime) || nt == typeof(Guid))
                return value;

            if (!t.IsValueType)
            {
                if (!visited.Add(value)) return null;
            }

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var list = new List<object>();
                foreach (object item in enumerable)
                {
                    object extracted = ExtractMetadataGraph(item, depth + 1, visited);
                    if (extracted != null) list.Add(extracted);
                }
                return list;
            }

            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                if (prop.Name == "Parent" || prop.Name == "Owner") continue;

                object propValue;
                try { propValue = prop.GetValue(value); }
                catch { continue; }

                object extracted = ExtractMetadataGraph(propValue, depth + 1, visited);
                if (extracted != null) dict[prop.Name] = extracted;
            }

            return dict;
        }

        private static Type ResolveProxyType(string objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return null;
            string fullName = "XppVsCode.Server.MetaModel.Generated." + objectType + "Proxy";

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false, true);
                if (t != null) return t;
            }

            return null;
        }

        private static object MapSourceToTargetType(object source, Type targetType, int depth, HashSet<object> visited)
        {
            if (targetType == null || depth > 12) return null;

            Type targetUnderlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (source == null)
            {
                if (targetUnderlying.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    return Activator.CreateInstance(targetUnderlying);
                return null;
            }

            if (IsSimpleType(targetUnderlying))
                return ConvertSimpleValue(source, targetUnderlying) ?? source?.ToString();

            if (!source.GetType().IsValueType)
            {
                if (!visited.Add(source)) return null;
            }

            if (targetUnderlying != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(targetUnderlying))
            {
                Type itemType = GetEnumerableItemType(targetUnderlying) ?? typeof(object);
                var list = new List<object>();
                if (source is System.Collections.IEnumerable srcEnumerable)
                {
                    foreach (object item in srcEnumerable)
                    {
                        object mapped = MapSourceToTargetType(item, itemType, depth + 1, visited);
                        if (mapped != null) list.Add(mapped);
                    }
                }

                object targetList = Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
                var add = targetList.GetType().GetMethod("Add");
                foreach (object item in list) add?.Invoke(targetList, new[] { item });
                return targetList;
            }

            object target = Activator.CreateInstance(targetUnderlying);
            var srcType = source.GetType();
            foreach (var tp in targetUnderlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!tp.CanRead || tp.GetIndexParameters().Length > 0) continue;

                var sp = srcType.GetProperty(tp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (sp == null || !sp.CanRead || sp.GetIndexParameters().Length > 0) continue;

                object srcValue;
                try { srcValue = sp.GetValue(source); }
                catch { continue; }

                object mapped = MapSourceToTargetType(srcValue, tp.PropertyType, depth + 1, visited);
                SetPropertyValue(tp, target, mapped);
            }

            return target;
        }

        private static void SetPropertyValue(PropertyInfo property, object target, object value)
        {
            try
            {
                var setter = property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(target, new[] { value });
                    return;
                }
            }
            catch { }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        // ── Validation ──

        public ValidateObjectResult ValidateObject(ValidateObjectRequest req)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new ValidateObjectResult { ObjectType = req.ObjectType, ObjectName = req.ObjectName };

            // 1. Read the object via typed API
            var readResult = ReadObject(req.ObjectType, req.ObjectName);
            if (!readResult.Success)
            {
                // Retry with reloaded metadata — the disk provider may have cached the old state
                _host.Reset();
                InvalidateCaches();
                readResult = ReadObject(req.ObjectType, req.ObjectName);
            }
            if (!readResult.Success)
            {
                result.Message = "Object not found: " + (readResult.Message ?? "unknown error");
                return result;
            }

            result.Exists = true;
            result.ModelName = readResult.ModelName;

            // 2. Check if in the active project (by name in filePath or item name)
            var projectItems = ListProjectItems();
            bool inProjectByItems = projectItems.Any(p =>
                (p.FilePath != null && p.FilePath.IndexOf(req.ObjectName, StringComparison.OrdinalIgnoreCase) >= 0) ||
                string.Equals(p.Name, req.ObjectName, StringComparison.OrdinalIgnoreCase));

            bool inProjectByModel = false;
            var activeProjectNode = GetActiveProjectNode();
            if (activeProjectNode != null)
            {
                var activeModel = activeProjectNode.GetProjectsModelInfo();
                if (activeModel != null && !string.IsNullOrWhiteSpace(readResult.ModelName))
                {
                    inProjectByModel = string.Equals(activeModel.Name, readResult.ModelName, StringComparison.OrdinalIgnoreCase);
                }
            }

            result.InProject = inProjectByItems || inProjectByModel;

            // 3. Compare expected vs actual properties
            var mismatches = new List<string>();

            if (!string.IsNullOrWhiteSpace(req.TypedMetadataJson))
            {
                if (string.IsNullOrWhiteSpace(readResult.TypedMetadataJson))
                {
                    mismatches.Add("typedMetadata: expected object graph but read result returned none");
                }
                else
                {
                    try
                    {
                        object expected = ParseTypedMetadataToNode(req.ObjectType, req.TypedMetadataJson);
                        object actual = ParseTypedMetadataToNode(req.ObjectType, readResult.TypedMetadataJson);
                        ValidateMetadataSubset(expected, actual, "typedMetadata", mismatches, 0);
                    }
                    catch (Exception ex)
                    {
                        mismatches.Add("typedMetadata: validation parse error: " + ex.Message);
                    }
                }
            }

            if (req.Properties != null)
            {
                foreach (var kv in req.Properties)
                {
                    if (readResult.Properties == null || !readResult.Properties.TryGetValue(kv.Key, out string actual))
                        mismatches.Add("Property '" + kv.Key + "': expected '" + kv.Value + "', not found on object");
                    else if (!string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Property '" + kv.Key + "': expected '" + kv.Value + "', actual '" + actual + "'");
                }
            }

            if (req.Fields != null)
            {
                var actualFieldMap = readResult.Fields != null
                    ? readResult.Fields.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, FieldDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in req.Fields)
                {
                    if (!actualFieldMap.TryGetValue(f.Name ?? "", out var actual))
                    {
                        mismatches.Add("Field '" + f.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(f.FieldType) &&
                        !string.Equals(f.FieldType, actual.FieldType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.FieldType: expected '" + f.FieldType + "', actual '" + (actual.FieldType ?? "") + "'");
                    if (!string.IsNullOrEmpty(f.ExtendedDataType) &&
                        !string.Equals(f.ExtendedDataType, actual.ExtendedDataType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.ExtendedDataType: expected '" + f.ExtendedDataType + "', actual '" + (actual.ExtendedDataType ?? "") + "'");
                    if (!string.IsNullOrEmpty(f.EnumType) &&
                        !string.Equals(f.EnumType, actual.EnumType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.EnumType: expected '" + f.EnumType + "', actual '" + (actual.EnumType ?? "") + "'");
                    if (!string.IsNullOrEmpty(f.Label) &&
                        !string.Equals(f.Label, actual.Label, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.Label: expected '" + f.Label + "', actual '" + (actual.Label ?? "") + "'");
                }
            }

            if (req.EnumValues != null)
            {
                var actualEnumMap = readResult.EnumValues != null
                    ? readResult.EnumValues.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, EnumValueDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var ev in req.EnumValues)
                {
                    if (!actualEnumMap.TryGetValue(ev.Name ?? "", out var actual))
                    {
                        mismatches.Add("EnumValue '" + ev.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (ev.Value != actual.Value)
                        mismatches.Add("EnumValue '" + ev.Name + "'.Value: expected " + ev.Value + ", actual " + actual.Value);
                    if (!string.IsNullOrEmpty(ev.Label) &&
                        !string.Equals(ev.Label, actual.Label, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("EnumValue '" + ev.Name + "'.Label: expected '" + ev.Label + "', actual '" + (actual.Label ?? "") + "'");
                }
            }

            if (req.Indexes != null)
            {
                var actualIndexMap = readResult.Indexes != null
                    ? readResult.Indexes.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, IndexDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var idx in req.Indexes)
                {
                    if (!actualIndexMap.TryGetValue(idx.Name ?? "", out var actual))
                    {
                        mismatches.Add("Index '" + idx.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (idx.AllowDuplicates != actual.AllowDuplicates)
                        mismatches.Add("Index '" + idx.Name + "'.AllowDuplicates: expected " + idx.AllowDuplicates + ", actual " + actual.AllowDuplicates);
                    if (idx.Fields != null && idx.Fields.Count > 0)
                    {
                        var actualFields = actual.Fields ?? new List<string>();
                        var expectedSet = new HashSet<string>(idx.Fields, StringComparer.OrdinalIgnoreCase);
                        var actualSet = new HashSet<string>(actualFields, StringComparer.OrdinalIgnoreCase);
                        foreach (var expField in idx.Fields)
                        {
                            if (!actualSet.Contains(expField))
                                mismatches.Add("Index '" + idx.Name + "'.Fields: expected field '" + expField + "' not found");
                        }
                    }
                }
            }

            if (req.Relations != null)
            {
                var actualRelMap = readResult.Relations != null
                    ? readResult.Relations.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, RelationDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var rel in req.Relations)
                {
                    if (!actualRelMap.TryGetValue(rel.Name ?? "", out var actual))
                    {
                        mismatches.Add("Relation '" + rel.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(rel.RelatedTable) &&
                        !string.Equals(rel.RelatedTable, actual.RelatedTable, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Relation '" + rel.Name + "'.RelatedTable: expected '" + rel.RelatedTable + "', actual '" + (actual.RelatedTable ?? "") + "'");
                    if (rel.Constraints != null && rel.Constraints.Count > 0)
                    {
                        var actualConstraints = actual.Constraints ?? new List<RelationConstraintDto>();
                        foreach (var c in rel.Constraints)
                        {
                            bool found = actualConstraints.Any(ac =>
                                string.Equals(ac.Field, c.Field, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(ac.RelatedField, c.RelatedField, StringComparison.OrdinalIgnoreCase));
                            if (!found)
                                mismatches.Add("Relation '" + rel.Name + "'.Constraint: expected " + (c.Field ?? "") + " -> " + (c.RelatedField ?? "") + ", not found");
                        }
                    }
                }
            }

            if (req.FieldGroups != null)
            {
                var actualFgMap = readResult.FieldGroups != null
                    ? readResult.FieldGroups.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, FieldGroupDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var fg in req.FieldGroups)
                {
                    if (!actualFgMap.TryGetValue(fg.Name ?? "", out var actual))
                    {
                        mismatches.Add("FieldGroup '" + fg.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(fg.Label) &&
                        !string.Equals(fg.Label, actual.Label, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("FieldGroup '" + fg.Name + "'.Label: expected '" + fg.Label + "', actual '" + (actual.Label ?? "") + "'");
                    if (fg.Fields != null && fg.Fields.Count > 0)
                    {
                        var actualFields = actual.Fields ?? new List<string>();
                        var actualSet = new HashSet<string>(actualFields, StringComparer.OrdinalIgnoreCase);
                        foreach (var expField in fg.Fields)
                        {
                            if (!actualSet.Contains(expField))
                                mismatches.Add("FieldGroup '" + fg.Name + "'.Fields: expected field '" + expField + "' not found");
                        }
                    }
                }
            }

            if (req.EntryPoints != null)
            {
                var actualEpMap = readResult.EntryPoints != null
                    ? readResult.EntryPoints.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, EntryPointDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var ep in req.EntryPoints)
                {
                    if (!actualEpMap.TryGetValue(ep.Name ?? "", out var actual))
                    {
                        mismatches.Add("EntryPoint '" + ep.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(ep.ObjectType) &&
                        !string.Equals(ep.ObjectType, actual.ObjectType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("EntryPoint '" + ep.Name + "'.ObjectType: expected '" + ep.ObjectType + "', actual '" + (actual.ObjectType ?? "") + "'");
                    if (!string.IsNullOrEmpty(ep.ObjectName) &&
                        !string.Equals(ep.ObjectName, actual.ObjectName, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("EntryPoint '" + ep.Name + "'.ObjectName: expected '" + ep.ObjectName + "', actual '" + (actual.ObjectName ?? "") + "'");
                    if (!string.IsNullOrEmpty(ep.Grant) && !GrantsEqual(ep.Grant, actual.Grant))
                        mismatches.Add("EntryPoint '" + ep.Name + "'.Grant: expected '" + ep.Grant + "', actual '" + (actual.Grant ?? "") + "'");
                }
            }

            if (req.DataSources != null)
            {
                ValidateDataSources(req.DataSources, readResult.DataSources, "", mismatches);
            }

            if (req.Controls != null)
            {
                var actualControls = new Dictionary<string, FormControlDto>(StringComparer.OrdinalIgnoreCase);
                FlattenControls(readResult.Controls, actualControls);
                var expectedControls = new Dictionary<string, FormControlDto>(StringComparer.OrdinalIgnoreCase);
                FlattenControls(req.Controls, expectedControls);
                foreach (var exp in expectedControls.Values)
                {
                    if (!actualControls.TryGetValue(exp.Name, out var act))
                        mismatches.Add("Control '" + exp.Name + "': expected to exist, not found");
                    else if (!string.IsNullOrEmpty(exp.DataField)
                        && !string.Equals(exp.DataField, act.DataField, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Control '" + exp.Name + "'.DataField: expected '" + exp.DataField + "', actual '" + (act.DataField ?? "") + "'");
                }
            }

            result.Mismatches = mismatches;
            result.Valid = result.InProject && mismatches.Count == 0;
            if (result.Valid)
            {
                result.Message = "Validation passed. Object exists in project and all specified metadata is present.";
            }
            else if (!result.InProject && mismatches.Count == 0)
            {
                result.Message = "Object metadata is correct but it is NOT in the active project.";
            }
            else
            {
                var msgBuilder = new System.Text.StringBuilder();
                msgBuilder.Append("Validation failed (" + mismatches.Count + " mismatch(es), InProject=" + result.InProject + "):");
                foreach (var m in mismatches)
                {
                    msgBuilder.Append("\n  - " + m);
                }
                result.Message = msgBuilder.ToString();
            }

            return result;
        }

        private static void ValidateMetadataSubset(object expected, object actual, string path, List<string> mismatches, int depth)
        {
            if (depth > 10) return;
            if (expected == null) return;
            if (actual == null)
            {
                mismatches.Add(path + ": expected value present, actual is null");
                return;
            }

            if (expected is Dictionary<string, object> expDict)
            {
                var actDict = actual as Dictionary<string, object>;
                if (actDict == null)
                {
                    mismatches.Add(path + ": expected object, actual is " + actual.GetType().Name);
                    return;
                }

                foreach (var kv in expDict)
                {
                    if (!actDict.TryGetValue(kv.Key, out object actVal))
                    {
                        mismatches.Add(path + "." + kv.Key + ": missing");
                        continue;
                    }
                    ValidateMetadataSubset(kv.Value, actVal, path + "." + kv.Key, mismatches, depth + 1);
                }
                return;
            }

            var expArray = ToObjectArray(expected);
            if (expArray != null)
            {
                var actArray = ToObjectArray(actual);
                if (actArray == null)
                {
                    mismatches.Add(path + ": expected array, actual is " + actual.GetType().Name);
                    return;
                }

                if (actArray.Length < expArray.Length)
                    mismatches.Add(path + ": expected at least " + expArray.Length + " items, actual " + actArray.Length);

                int compareCount = Math.Min(expArray.Length, actArray.Length);
                for (int i = 0; i < compareCount; i++)
                    ValidateMetadataSubset(expArray[i], actArray[i], path + "[" + i + "]", mismatches, depth + 1);

                return;
            }

            string expText = Convert.ToString(expected);
            string actText = Convert.ToString(actual);
            if (!string.Equals(expText, actText, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(path + ": expected '" + expText + "', actual '" + actText + "'");
        }

        private static object[] ToObjectArray(object node)
        {
            if (node == null) return null;
            if (node is object[] arr) return arr;
            if (node is System.Collections.ArrayList al) return al.ToArray();
            if (node is System.Collections.IEnumerable en && !(node is string))
            {
                var list = new List<object>();
                foreach (object item in en) list.Add(item);
                return list.ToArray();
            }
            return null;
        }

        private static void ValidateDataSources(List<QueryDataSourceDto> expected, List<QueryDataSourceDto> actual, string prefix, List<string> mismatches)
        {
            if (expected == null || expected.Count == 0) return;

            var actualMap = actual != null
                ? actual.Where(x => x.Name != null)
                    .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, QueryDataSourceDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var ds in expected)
            {
                string dsPath = string.IsNullOrEmpty(prefix)
                    ? "DataSource '" + ds.Name + "'"
                    : prefix + ".DataSource '" + ds.Name + "'";

                if (!actualMap.TryGetValue(ds.Name ?? "", out var act))
                {
                    mismatches.Add(dsPath + ": expected to exist, not found");
                    continue;
                }
                if (!string.IsNullOrEmpty(ds.Table) &&
                    !string.Equals(ds.Table, act.Table, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(dsPath + ".Table: expected '" + ds.Table + "', actual '" + (act.Table ?? "") + "'");
                if (!string.IsNullOrEmpty(ds.JoinMode) &&
                    !string.Equals(ds.JoinMode, act.JoinMode, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(dsPath + ".JoinMode: expected '" + ds.JoinMode + "', actual '" + (act.JoinMode ?? "") + "'");
                if (!string.IsNullOrEmpty(ds.LinkType) &&
                    !string.Equals(ds.LinkType, act.LinkType, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(dsPath + ".LinkType: expected '" + ds.LinkType + "', actual '" + (act.LinkType ?? "") + "'");
                if (ds.DynamicFields.HasValue && ds.DynamicFields != act.DynamicFields)
                    mismatches.Add(dsPath + ".DynamicFields: expected " + ds.DynamicFields + ", actual " + act.DynamicFields);
                if (ds.Relations.HasValue && ds.Relations != act.Relations)
                    mismatches.Add(dsPath + ".Relations: expected " + ds.Relations + ", actual " + act.Relations);
                if (ds.FirstOnly.HasValue && ds.FirstOnly != act.FirstOnly)
                    mismatches.Add(dsPath + ".FirstOnly: expected " + ds.FirstOnly + ", actual " + act.FirstOnly);
                if (ds.Ranges != null && ds.Ranges.Count > 0)
                {
                    var actualRanges = act.Ranges ?? new List<QueryRangeDto>();
                    foreach (var r in ds.Ranges)
                    {
                        bool found = actualRanges.Any(ar =>
                            string.Equals(ar.Field, r.Field, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(ar.Value, r.Value, StringComparison.OrdinalIgnoreCase));
                        if (!found)
                            mismatches.Add(dsPath + ".Range: expected Field='" + (r.Field ?? "") + "' Value='" + (r.Value ?? "") + "', not found");
                    }
                }
                if (ds.ChildDataSources != null && ds.ChildDataSources.Count > 0)
                    ValidateDataSources(ds.ChildDataSources, act.ChildDataSources, dsPath, mismatches);
            }
        }

        // ── Discovery ──

        // Scans run on the VS UI thread; must stay well under the bridge's 12s dispatch timeout.
        private const int SearchTimeBudgetMs = 6000;

        private static string BudgetExceededMessage(int found) =>
            "Search stopped after " + (SearchTimeBudgetMs / 1000) + "s with " + found
            + " result(s); results are PARTIAL. Narrow the search: pass objectType and a longer, more specific name.";

        public FindObjectResult FindObject(string objectName, string objectType, bool exactMatch, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new FindObjectResult { Message = "MetaModel service not available." };

            var result = new FindObjectResult { Success = true };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool budgetExceeded = false;

            try
            {
                var typeSearchers = GetTypeSearchers(objectType);
                foreach (var searcher in typeSearchers)
                {
                    try
                    {
                        foreach (string name in searcher.GetNames())
                        {
                            if (sw.ElapsedMilliseconds > SearchTimeBudgetMs)
                            {
                                budgetExceeded = true;
                                break;
                            }

                            bool match = exactMatch
                                ? name.Equals(objectName, StringComparison.OrdinalIgnoreCase)
                                : name.IndexOf(objectName, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (match)
                            {
                                string modelName = GetObjectModelName(searcher.TypeName, name);
                                result.Matches.Add(new FoundObject
                                {
                                    ObjectType = searcher.TypeName,
                                    ObjectName = name,
                                    ModelName = modelName,
                                    IsCustom = IsCustomModel(modelName)
                                });

                                if (result.Matches.Count >= maxResults) break;
                            }
                        }
                        if (result.Matches.Count >= maxResults || budgetExceeded) break;
                    }
                    catch (Exception typeEx)
                    {
                        // Log per-type error but continue searching other types
                        // This allows partial results even if one metadata extractor fails
                        System.Diagnostics.Debug.WriteLine("FindObject type search error for type '" + searcher.TypeName + "': " + typeEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                string svcContext = MetaService == null ? "[MetaService unavailable]" : "[MetaService active]";
                result.Message = "Search error in FindObject (searching for '" + objectName + "' in type '" + objectType + "'): " + svcContext + " - " + ex.Message;
            }

            if (budgetExceeded)
                result.Message = BudgetExceededMessage(result.Matches.Count);
            else if (result.Matches.Count == 0)
                result.Message = "No objects found matching '" + objectName + "'."
                    + (string.IsNullOrEmpty(objectType) ? "" : " (type: " + objectType + ")");

            return result;
        }

        public ListObjectsResult ListObjects(string modelName, string objectType, string nameFilter, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new ListObjectsResult { Message = "MetaModel service not available." };

            var result = new ListObjectsResult { Success = true };
            bool hasNameFilter = !string.IsNullOrEmpty(nameFilter);
            bool hasModelFilter = !string.IsNullOrEmpty(modelName);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool budgetExceeded = false;

            try
            {
                var typeSearchers = GetTypeSearchers(objectType);
                foreach (var searcher in typeSearchers)
                {
                    foreach (string name in searcher.GetNames())
                    {
                        if (sw.ElapsedMilliseconds > SearchTimeBudgetMs)
                        {
                            budgetExceeded = true;
                            break;
                        }

                        // Apply cheap name filter FIRST to avoid expensive model lookups
                        if (hasNameFilter &&
                            name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        // Only do the expensive model lookup when needed
                        string objModel = null;
                        if (hasModelFilter)
                        {
                            objModel = GetObjectModelName(searcher.TypeName, name);
                            if (!modelName.Equals(objModel, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        // Lazy-resolve model name if not already fetched
                        if (objModel == null)
                            objModel = GetObjectModelName(searcher.TypeName, name);

                        result.Objects.Add(new FoundObject
                        {
                            ObjectType = searcher.TypeName,
                            ObjectName = name,
                            ModelName = objModel,
                            IsCustom = IsCustomModel(objModel)
                        });

                        if (result.Objects.Count >= maxResults) break;
                    }
                    if (result.Objects.Count >= maxResults || budgetExceeded) break;
                }
            }
            catch (Exception ex)
            {
                result.Message = "List error: " + ex.Message;
            }

            if (budgetExceeded)
                result.Message = BudgetExceededMessage(result.Objects.Count);

            return result;
        }

        public ObjectTypeSchemaResult GetObjectTypeSchema(string objectType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(objectType))
                return new ObjectTypeSchemaResult
                {
                    Success = false,
                    ObjectType = objectType,
                    Message = "objectType is required."
                };

            try
            {
                Type metadataType = ResolveMetadataType(objectType);
                if (metadataType == null)
                {
                    return new ObjectTypeSchemaResult
                    {
                        Success = false,
                        ObjectType = objectType,
                        Message = "Unsupported or unknown object type: " + objectType
                    };
                }

                var visited = new HashSet<Type>();
                object schema = BuildTypeSchema(metadataType, 0, visited);

                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                    RecursionLimit = 64
                };

                return new ObjectTypeSchemaResult
                {
                    Success = true,
                    ObjectType = objectType,
                    SchemaJson = serializer.Serialize(schema),
                    Message = "Schema generated for " + objectType
                };
            }
            catch (Exception ex)
            {
                return new ObjectTypeSchemaResult
                {
                    Success = false,
                    ObjectType = objectType,
                    Message = "Schema generation failed: " + ex.Message
                };
            }
        }

        public ProxyGenerationResult GenerateProxies(ProxyGenerationRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var normalized = request ?? new ProxyGenerationRequest();
            var selectedTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            var requested = (normalized.ObjectTypes != null && normalized.ObjectTypes.Count > 0)
                ? normalized.ObjectTypes
                : GetDefaultProxyObjectTypes();

            foreach (var objectType in requested)
            {
                if (string.IsNullOrWhiteSpace(objectType))
                    continue;

                var metadataType = ResolveMetadataType(objectType.Trim());
                if (metadataType != null)
                    selectedTypes[objectType.Trim()] = metadataType;
            }

            if (selectedTypes.Count == 0)
            {
                return new ProxyGenerationResult
                {
                    Success = false,
                    Message = "No valid object types were provided for proxy generation."
                };
            }

            var result = MetaModelProxyGenerator.Generate(selectedTypes, normalized);
            if (!result.Success)
                return result;

            string outputPath = normalized.OutputFilePath;
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = ResolveDefaultProxyOutputPath();

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    File.WriteAllText(outputPath, result.GeneratedCode ?? string.Empty, Encoding.UTF8);
                    result.OutputFilePath = outputPath;
                    result.Message = result.Message + " Output written to '" + outputPath + "'.";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = "Proxy code generated but failed to write output file: " + ex.Message;
                }
            }
            else
            {
                result.Message = result.Message + " Generated in memory only (no writable project path detected).";
            }

            return result;
        }

        private string ResolveDefaultProxyOutputPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var project = GetActiveProject();
                string projectFile = project?.FullName;
                if (string.IsNullOrWhiteSpace(projectFile))
                    return null;

                string projectDirectory = Path.GetDirectoryName(projectFile);
                if (string.IsNullOrWhiteSpace(projectDirectory))
                    return null;

                return Path.Combine(projectDirectory, "MetaModel", "Generated", "MetadataProxies.g.cs");
            }
            catch
            {
                return null;
            }
        }

        private static object BuildTypeSchema(Type type, int depth, HashSet<Type> visited)
        {
            if (type == null || depth > 8) return null;

            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (IsSimpleType(underlying))
                return MapSimpleTypeToSchema(underlying);

            // Prevent cyclic loops in type graph
            if (!underlying.IsValueType && !visited.Add(underlying))
                return new Dictionary<string, object>
                {
                    { "kind", "object" },
                    { "type", underlying.Name },
                    { "recursive", true }
                };

            if (underlying != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying))
            {
                Type itemType = GetEnumerableItemType(underlying) ?? typeof(object);
                return new Dictionary<string, object>
                {
                    { "kind", "array" },
                    { "item", BuildTypeSchema(itemType, depth + 1, visited) }
                };
            }

            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                var propSchema = BuildTypeSchema(prop.PropertyType, depth + 1, new HashSet<Type>(visited));
                if (propSchema != null)
                    properties[prop.Name] = propSchema;
            }

            return new Dictionary<string, object>
            {
                { "kind", "object" },
                { "type", underlying.Name },
                { "properties", properties }
            };
        }

        private static Type GetEnumerableItemType(Type enumerableType)
        {
            if (enumerableType.IsArray)
                return enumerableType.GetElementType();

            if (enumerableType.IsGenericType)
            {
                var args = enumerableType.GetGenericArguments();
                if (args.Length == 1) return args[0];
            }

            Type ienum = enumerableType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return ienum?.GetGenericArguments().FirstOrDefault();
        }

        private static List<string> GetDefaultProxyObjectTypes()
        {
            return new List<string>
            {
                "AxClass",
                "AxTable",
                "AxForm",
                "AxEdt",
                "AxEnum",
                "AxView",
                "AxQuery",
                "AxMenu",
                "AxTile",
                "AxMenuItemDisplay",
                "AxMenuItemOutput",
                "AxMenuItemAction",
                "AxDataEntityView",
                "AxSecurityPrivilege",
                "AxSecurityDuty",
                "AxSecurityRole",
                "AxService",
                "AxServiceGroup",
                "AxConfigurationKey"
            };
        }

        private static Dictionary<string, object> MapSimpleTypeToSchema(Type type)
        {
            if (type == typeof(string))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "string" } };
            if (type == typeof(bool))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "boolean" } };
            if (type == typeof(int) || type == typeof(long) || type == typeof(short))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "integer" } };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "number" } };
            if (type == typeof(DateTime))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "datetime" } };
            if (type == typeof(Guid))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "guid" } };
            if (type.IsEnum)
                return new Dictionary<string, object>
                {
                    { "kind", "enum" },
                    { "type", type.Name },
                    { "values", Enum.GetNames(type) }
                };

            return new Dictionary<string, object> { { "kind", "scalar" }, { "type", type.Name } };
        }

        public ModelInfoResult GetModelInfo(string modelName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new ModelInfoResult { Message = "MetaModel service not available." };

            try
            {
                var modelInfo = MetaService.GetModel(modelName);

                if (modelInfo == null)
                    return new ModelInfoResult { Message = "Model '" + modelName + "' not found." };

                var result = new ModelInfoResult
                {
                    Success = true,
                    Name = modelInfo.Name,
                    DisplayName = modelInfo.DisplayName,
                    Publisher = modelInfo.Publisher,
                    Version = modelInfo.VersionBuild.ToString(),
                    ModelId = modelInfo.Id,
                    Layer = modelInfo.Layer.ToString()
                };

                return result;
            }
            catch (Exception ex)
            {
                return new ModelInfoResult { Message = "Model info failed: " + ex.Message };
            }
        }

        public List<ModelSummary> ListModels()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var models = new List<ModelSummary>();
            if (MetaService == null) return models;

            try
            {
                foreach (var info in MetaService.GetModels())
                {
                    models.Add(new ModelSummary
                    {
                        Name = info.Name,
                        DisplayName = info.DisplayName,
                        IsCustom = IsCustomModel(info.Name)
                    });
                }
            }
            catch { }

            return models;
        }

        // ── Labels ──

        public LabelResult ReadLabel(string labelFileId, string language, string labelId, string searchText, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new LabelResult { Message = "MetaModel service not available." };

            try
            {
                string labelPath = _labels.FindLabelFile(labelFileId, language);
                if (labelPath == null)
                    return new LabelResult { Message = "Label file '" + labelFileId + "' (" + (language ?? "en-US") + ") not found." };

                var result = new LabelResult { Success = true };

                // If specific label requested
                if (!string.IsNullOrEmpty(labelId))
                {
                    result.Labels.AddRange(_labels.Read(labelPath, labelId, null, 1));
                    if (result.Labels.Count == 0)
                        result.Message = "Label '" + labelId + "' not found in " + labelFileId;
                    return result;
                }

                result.Labels.AddRange(_labels.Read(labelPath, null, searchText, maxResults > 0 ? maxResults : 50));

                result.Message = result.Labels.Count > 0
                    ? "Retrieved " + result.Labels.Count + " labels from " + labelFileId + "."
                    : "No labels found in " + labelFileId + ".";
                return result;
            }
            catch (Exception ex)
            {
                return new LabelResult { Message = "Label read failed: " + ex.Message };
            }
        }

        public MetaModelResult CreateLabel(string labelFileId, string language, string labelId, string text, string comment)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return Fail("MetaModel service not available.");

            try
            {
                string labelPath = _labels.FindLabelFile(labelFileId, language);
                if (labelPath == null)
                    return Fail("Label file '" + labelFileId + "' (" + (language ?? "en-US") + ") not found.");
                if (!_labels.IsWritable(labelPath))
                    return Fail("Label file '" + labelFileId + "' is not in your custom metadata folder. Add labels only to label files of your own model.");

                if (_labels.Exists(labelPath, labelId))
                    return Fail("Label '" + labelId + "' already exists in " + labelFileId);

                _labels.Insert(labelPath, labelId, text, comment);

                return new MetaModelResult
                {
                    Success = true,
                    Message = "Created label @" + labelFileId + ":" + labelId + " = " + text
                };
            }
            catch (Exception ex)
            {
                return Fail("Label create failed: " + ex.Message);
            }
        }

        // ── Project ──

        public MetaModelResult AddToProject(string objectType, string objectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var projectNode = GetActiveProjectNode();
                if (projectNode == null)
                    return Fail("No active D365FO project. Open a workspace folder containing a .rnrproj file, or set the 'xpp.project' setting.");

                if (!objectType.StartsWith("Ax", StringComparison.Ordinal))
                    return Fail("Unsupported object type for project add: " + objectType);

                // D365FO projects belong to one model; adding another model's objects causes build/designer errors.
                var projectModel = projectNode.GetProjectsModelInfo();
                string objectModel = GetObjectModelName(objectType, objectName);
                if (projectModel != null && !string.IsNullOrEmpty(objectModel)
                    && !string.Equals(projectModel.Name, objectModel, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(objectType + " '" + objectName + "' belongs to model '" + objectModel
                        + "', but the active project targets model '" + projectModel.Name
                        + "'. Not added. Ask the user to set the project's Model property to '" + objectModel
                        + "' or open a project for that model.");
                }

                bool added = projectNode.AddItem(objectType, objectName);
                InvalidateCaches();

                return new MetaModelResult
                {
                    Success = true,
                    Message = added
                        ? "Added " + objectType + " '" + objectName + "' to project " + projectNode.Caption + "."
                        : objectType + " '" + objectName + "' is already in project " + projectNode.Caption + "."
                };
            }
            catch (Exception ex)
            {
                return Fail("Add to project failed: " + ex.Message);
            }
        }

        public List<ProjectItemInfo> ListProjectItems()
        {
            try
            {
                return GetActiveProject()?.GetItems() ?? new List<ProjectItemInfo>();
            }
            catch
            {
                return new List<ProjectItemInfo>();
            }
        }

        // ── Environment ──

        public EnvironmentInfo GetEnvironmentInfo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var info = new EnvironmentInfo();

            try
            {
                var config = DynamicsConfigurationReader.ReadActiveConfiguration();
                info.CustomMetadataFolder = config.CustomMetadataFolder;
                info.ReferenceMetadataFolders = config.ReferenceMetadataFolders;

                var projectNode = GetActiveProjectNode();
                if (projectNode != null)
                {
                    info.ActiveProjectName = projectNode.Caption;
                    var modelInfo = projectNode.GetProjectsModelInfo();
                    if (modelInfo != null)
                    {
                        info.ActiveModelName = modelInfo.Name;
                        info.ActiveModelId = modelInfo.Id;
                        info.ActiveModelLayer = modelInfo.Layer.ToString();
                    }
                }
            }
            catch { }

            return info;
        }

        // ── Private helpers: Create operations ──

        private MetaModelResult CreateClass(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateClass(new AxClass
            {
                Name = req.ObjectName,
                Declaration = "class " + req.ObjectName + "\n{\n}"
            }, saveInfo);
            return Ok("Created AxClass '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateTable(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateTable(new AxTable { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxTable '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateForm(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateForm(new AxForm { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxForm '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateEdt(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateExtendedDataType(new AxEdtString { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxEdt '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateEnum(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateEnum(new AxEnum { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxEnum '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateMenuItemDisplay(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var item = new AxMenuItemDisplay { Name = req.ObjectName };
            ApplyTypedMetadata(item, req);
            MetaService.CreateMenuItemDisplay(item, saveInfo);
            return Ok("Created AxMenuItemDisplay '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateMenuItemOutput(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var item = new AxMenuItemOutput { Name = req.ObjectName };
            ApplyTypedMetadata(item, req);
            MetaService.CreateMenuItemOutput(item, saveInfo);
            return Ok("Created AxMenuItemOutput '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateMenuItemAction(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var item = new AxMenuItemAction { Name = req.ObjectName };
            ApplyTypedMetadata(item, req);
            MetaService.CreateMenuItemAction(item, saveInfo);
            return Ok("Created AxMenuItemAction '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateQuery(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateQuery(new AxQuerySimple { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxQuery '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateView(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateView(new AxView { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxView '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateDataEntityView(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            // DataEntityView has no Create API; UpdateDataEntityView creates-or-updates.
            MetaService.UpdateDataEntityView(new AxDataEntityView { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxDataEntityView '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateSecurityPrivilege(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateSecurityPrivilege(new AxSecurityPrivilege { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxSecurityPrivilege '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateSecurityDuty(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateSecurityDuty(new AxSecurityDuty { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxSecurityDuty '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateSecurityRole(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateSecurityRole(new AxSecurityRole { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxSecurityRole '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateService(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var svc = new AxService { Name = req.ObjectName };
            ApplyTypedMetadata(svc, req);
            MetaService.CreateService(svc, saveInfo);
            return Ok("Created AxService '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateServiceGroup(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var grp = new AxServiceGroup { Name = req.ObjectName };
            ApplyTypedMetadata(grp, req);
            MetaService.CreateServiceGroup(grp, saveInfo);
            return Ok("Created AxServiceGroup '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateMenu(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            MetaService.CreateMenu(new AxMenu { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxMenu '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateTile(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var tile = new AxTile { Name = req.ObjectName };
            ApplyTypedMetadata(tile, req);
            MetaService.CreateTile(tile, saveInfo);
            return Ok("Created AxTile '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateConfigurationKey(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var key = new AxConfigurationKey { Name = req.ObjectName };
            ApplyTypedMetadata(key, req);
            MetaService.CreateConfigurationKey(key, saveInfo);
            return Ok("Created AxConfigurationKey '" + req.ObjectName + "'.");
        }

        // ── Private helpers: Extension operations ──

        private MetaModelResult CreateTableExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxTable",
                baseName => MetaService.GetTable(baseName) != null,
                () => MetadataProvider.TableExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            MetadataProvider.TableExtensions.Create(new AxTableExtension { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxTableExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateEnumExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxEnum",
                baseName => MetaService.GetEnum(baseName) != null,
                () => MetadataProvider.EnumExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            MetadataProvider.EnumExtensions.Create(new AxEnumExtension { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxEnumExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateEdtExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxEdt",
                baseName => MetaService.GetExtendedDataType(baseName) != null,
                () => MetadataProvider.EdtExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            MetadataProvider.EdtExtensions.Create(new AxEdtExtension { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxEdtExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateTableExtension(UpdateObjectRequest req)
        {
            var ext = MetaService.GetTableExtension(req.ObjectName);
            if (ext == null)
                return Fail("TableExtension '" + req.ObjectName + "' not found.");

            var saveInfo = GetExtensionSaveInfo("AxTableExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);

            ApplyTypedMetadata(ext, req);
            MetadataProvider.TableExtensions.Update(ext, saveInfo);
            return Ok("Updated AxTableExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateEnumExtension(UpdateObjectRequest req)
        {
            var ext = MetaService.GetEnumExtension(req.ObjectName);
            if (ext == null)
                return Fail("EnumExtension '" + req.ObjectName + "' not found.");

            var saveInfo = GetExtensionSaveInfo("AxEnumExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);

            ApplyTypedMetadata(ext, req);
            MetadataProvider.EnumExtensions.Update(ext, saveInfo);
            return Ok("Updated AxEnumExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateEdtExtension(UpdateObjectRequest req)
        {
            var ext = MetaService.GetEdtExtension(req.ObjectName);
            if (ext == null)
                return Fail("EdtExtension '" + req.ObjectName + "' not found.");

            var saveInfo = GetExtensionSaveInfo("AxEdtExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);

            ApplyTypedMetadata(ext, req);
            MetadataProvider.EdtExtensions.Update(ext, saveInfo);
            return Ok("Updated AxEdtExtension '" + req.ObjectName + "'.");
        }

        private string ValidateNewExtension(CreateObjectRequest req, string baseType,
            Func<string, bool> baseExists, Func<bool> extensionExists)
        {
            if (MetadataProvider == null)
                return "D365FO metadata provider not available; cannot create extensions.";

            int dot = req.ObjectName.IndexOf('.');
            if (dot <= 0 || dot == req.ObjectName.Length - 1)
                return req.ObjectType + " name must be '<Base" + baseType.Substring(2)
                    + ">.<Suffix>' (e.g. CustTable.MyModelExtension).";

            string baseName = req.ObjectName.Substring(0, dot);
            if (!baseExists(baseName))
                return baseType + " '" + baseName + "' not found. The part before '.' must be the name of the object being extended.";

            if (extensionExists())
                return req.ObjectType + " '" + req.ObjectName + "' already exists. Use xpp_update_object to change it.";

            string targetModel = ResolveTargetModelName(req.ModelName);
            if (!IsCustomModel(targetModel))
                return "Extensions must be created in a custom model; '" + (targetModel ?? "<unresolved>")
                    + "' is not one. Pass modelName of your own model.";

            return null;
        }

        private ModelSaveInfo GetExtensionSaveInfo(string objectType, string objectName, out string error)
        {
            error = null;
            if (MetadataProvider == null)
            {
                error = "D365FO metadata provider not available; cannot update extensions.";
                return null;
            }

            string modelName = GetObjectModelName(objectType, objectName);
            if (string.IsNullOrEmpty(modelName))
            {
                error = "Cannot resolve model for '" + objectName + "'.";
                return null;
            }

            // Extensions shipped in standard models are not ours to change.
            if (!IsCustomModel(modelName))
            {
                error = objectType + " '" + objectName + "' belongs to standard model '" + modelName
                    + "' and cannot be changed. Create your own extension in a custom model.";
                return null;
            }

            var saveInfo = GetModelSaveInfo(modelName);
            if (saveInfo == null)
                error = "Cannot resolve model for '" + objectName + "'.";
            return saveInfo;
        }

        // ── Private helpers: Update operations ──

        private MetaModelResult UpdateClass(UpdateObjectRequest req)
        {
            var cls = MetaService.GetClass(req.ObjectName);
            if (cls == null)
                return Fail("Class '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxClass", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            if (!string.IsNullOrEmpty(req.Declaration))
                cls.Declaration = PrepareSource(req.Declaration, req.FormatCode);

            UpdateMethods(cls.Methods, req.Methods, req.RemoveMethodNames, req.FormatCode);
            ApplyTypedMetadata(cls, req);
            MetaService.UpdateClass(cls, saveInfo);
            return Ok("Updated AxClass '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateTable(UpdateObjectRequest req)
        {
            var tbl = MetaService.GetTable(req.ObjectName);
            if (tbl == null)
                return Fail("Table '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxTable", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            if (!string.IsNullOrEmpty(req.Declaration))
                tbl.Declaration = PrepareSource(req.Declaration, req.FormatCode);

            UpdateMethods(tbl.Methods, req.Methods, req.RemoveMethodNames, req.FormatCode);
            try
            {
                ApplyTypedMetadata(tbl, req);
            }
            catch (Exception ex)
            {
                return Fail("ApplyTypedMetadata failed for AxTable '" + req.ObjectName + "': " + ex.Message);
            }
            MetaService.UpdateTable(tbl, saveInfo);
            return Ok("Updated AxTable '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateForm(UpdateObjectRequest req)
        {
            var frm = MetaService.GetForm(req.ObjectName);
            if (frm == null)
                return Fail("Form '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxForm", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            if (!string.IsNullOrEmpty(req.Declaration))
                frm.SourceCode.Declaration = PrepareSource(req.Declaration, req.FormatCode);

            UpdateMethods(frm.Methods, req.Methods, req.RemoveMethodNames, req.FormatCode);
            ApplyTypedMetadata(frm, req);
            MetaService.UpdateForm(frm, saveInfo);
            return Ok("Updated AxForm '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateEdt(UpdateObjectRequest req)
        {
            var edt = MetaService.GetExtendedDataType(req.ObjectName);
            if (edt == null)
                return Fail("EDT '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxEdt", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            ApplyTypedMetadata(edt, req);
            MetaService.UpdateExtendedDataType(edt, saveInfo);
            return Ok("Updated AxEdt '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateEnum(UpdateObjectRequest req)
        {
            var enm = MetaService.GetEnum(req.ObjectName);
            if (enm == null)
                return Fail("Enum '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxEnum", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            ApplyTypedMetadata(enm, req);
            MetaService.UpdateEnum(enm, saveInfo);
            return Ok("Updated AxEnum '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateView(UpdateObjectRequest req)
        {
            var view = MetaService.GetView(req.ObjectName);
            if (view == null)
                return Fail("View '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxView", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            if (!string.IsNullOrEmpty(req.Declaration))
                view.Declaration = PrepareSource(req.Declaration, req.FormatCode);

            UpdateMethods(view.Methods, req.Methods, req.RemoveMethodNames, req.FormatCode);
            ApplyTypedMetadata(view, req);
            MetaService.UpdateView(view, saveInfo);
            return Ok("Updated AxView '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateQuery(UpdateObjectRequest req)
        {
            var qry = MetaService.GetQuery(req.ObjectName);
            if (qry == null)
                return Fail("Query '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxQuery", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            ApplyTypedMetadata(qry, req);
            MetaService.UpdateQuery(qry, saveInfo);
            return Ok("Updated AxQuery '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateDataEntityView(UpdateObjectRequest req)
        {
            var entity = MetaService.GetDataEntityView(req.ObjectName);
            if (entity == null)
                return Fail("DataEntityView '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxDataEntityView", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            if (!string.IsNullOrEmpty(req.Declaration))
                entity.Declaration = PrepareSource(req.Declaration, req.FormatCode);

            ApplyTypedMetadata(entity, req);
            MetaService.UpdateDataEntityView(entity, saveInfo);
            return Ok("Updated AxDataEntityView '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateMenu(UpdateObjectRequest req)
        {
            var menu = MetaService.GetMenu(req.ObjectName);
            if (menu == null)
                return Fail("Menu '" + req.ObjectName + "' not found.");

            var saveInfo = GetModelSaveInfoForObject("AxMenu", req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            ApplyTypedMetadata(menu, req);
            MetaService.UpdateMenu(menu, saveInfo);
            return Ok("Updated AxMenu '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateGenericByTypedApi(UpdateObjectRequest req)
        {
            return Fail("Update for type '" + req.ObjectType + "' is not yet supported via the typed API. "
                + "Supported types: AxClass, AxTable, AxForm, AxEdt, AxEnum, AxView, AxQuery, AxDataEntityView, AxMenu, "
                + "AxSecurityPrivilege, AxSecurityDuty, AxSecurityRole, AxTableExtension, AxEnumExtension, AxEdtExtension, "
                + "AxFormExtension, AxDataEntityViewExtension, AxSecurityDutyExtension, AxSecurityRoleExtension.");
        }

        // ── Utility methods ──

        private void AddMethods(IList<AxMethod> target, string[] sources, bool formatCode)
        {
            if (sources == null) return;
            foreach (string src in sources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                string clean = PrepareSource(src, formatCode);
                string name = ExtractMethodName(clean);
                target.Add(new AxMethod { Name = name, Source = clean });
            }
        }

        private void UpdateMethods(IList<AxMethod> existing, string[] upserts, string[] removals, bool formatCode)
        {
            if (removals != null)
            {
                foreach (string removeName in removals)
                {
                    var toRemove = existing.FirstOrDefault(m =>
                        m.Name.Equals(removeName, StringComparison.OrdinalIgnoreCase));
                    if (toRemove != null) existing.Remove(toRemove);
                }
            }

            if (upserts != null)
            {
                foreach (string src in upserts)
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    string clean = PrepareSource(src, formatCode);
                    string name = ExtractMethodName(clean);
                    var found = existing.FirstOrDefault(m =>
                        m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        found.Source = clean;
                    }
                    else
                    {
                        existing.Add(new AxMethod { Name = name, Source = clean });
                    }
                }
            }
        }

        private static string PrepareSource(string source, bool formatCode)
        {
            string clean = StripCData(source);
            return formatCode ? NormalizeXppWhitespace(clean) : clean;
        }

        private static string NormalizeXppWhitespace(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return source;

            string normalized = source.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Replace("\t", "    ");
                lines[i] = line.TrimEnd();
            }
            return string.Join("\n", lines).TrimEnd();
        }

        // The identifier before the first '(' after comments, [attributes] (possibly multi-line) and #macro lines.
        private static string ExtractMethodName(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "unknownMethod";
            string text = source.Replace("\r\n", "\n");
            int i = 0;
            while (true)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                if (string.CompareOrdinal(text, i, "//", 0, 2) == 0 || (text[i] == '#' && IsMacroLine(text, i)))
                {
                    int end = text.IndexOf('\n', i);
                    i = end < 0 ? text.Length : end + 1;
                }
                else if (text[i] == '#')
                {
                    // A #word on the signature line (e.g. #obsolete) acts as a modifier.
                    for (i++; i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'); i++) { }
                }
                else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                }
                else if (text[i] == '[')
                {
                    i = SkipBracketed(text, i);
                }
                else break;
            }
            int paren = text.IndexOf('(', Math.Min(i, text.Length));
            int brace = text.IndexOf('{', Math.Min(i, text.Length));
            // A class-style header ("public class X extends Y {") is how forms store their declaration.
            if (brace >= 0 && (paren < 0 || brace < paren)) return "classDeclaration";
            if (paren <= i) return "unknownMethod";
            string[] tokens = text.Substring(i, paren - i).Trim().Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 ? tokens[tokens.Length - 1] : "unknownMethod";
        }

        // Macro directives (#define.X(..), #if, #localmacro, ...) and macro library lines (#SysAdmin) take the whole line.
        private static bool IsMacroLine(string text, int start)
        {
            int end = text.IndexOf('\n', start);
            string line = text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(line,
                @"^#(define|localmacro|globalmacro|endmacro|if|ifnot|endif|macrolib|undef|globaldefine|localdefine)\b|^#\w+\.\w+|^#\w+\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static int SkipBracketed(string text, int start)
        {
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    for (i++; i < text.Length && text[i] != c; i++)
                        if (text[i] == '\\') i++;
                }
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return i + 1;
            }
            return text.Length;
        }

        private ModelSaveInfo GetModelSaveInfo(string modelName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // If modelName specified, try to resolve it
            if (!string.IsNullOrEmpty(modelName))
            {
                var info = MetaService.GetModel(modelName);
                if (info != null)
                    return new ModelSaveInfo { Id = info.Id, Layer = info.Layer };
            }

            // Fallback: use active project's model
            var projectNode = GetActiveProjectNode();
            if (projectNode != null)
            {
                var projectModel = projectNode.GetProjectsModelInfo();
                if (projectModel != null)
                    return new ModelSaveInfo { Id = projectModel.Id, Layer = projectModel.Layer };
            }

            return null;
        }

        private string ResolveTargetModelName(string modelName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.IsNullOrEmpty(modelName))
            {
                var info = MetaService.GetModel(modelName);
                if (info != null)
                    return info.Name;
            }

            return GetActiveProjectNode()?.GetProjectsModelInfo()?.Name;
        }

        private ModelSaveInfo GetModelSaveInfoForObject(string objectType, string objectName)
        {
            string modelName = GetObjectModelName(objectType, objectName);
            return !string.IsNullOrEmpty(modelName) ? GetModelSaveInfo(modelName) : GetModelSaveInfo(null);
        }

        private string GetObjectModelName(string objectType, string objectName)
        {
            // Create cache key
            string cacheKey = objectType + ":" + objectName;
            
            // Check cache first
            if (_objectModelNameCache.TryGetValue(cacheKey, out CachedModelName cached))
            {
                // Check if cache is still fresh (within TTL)
                if ((DateTime.Now - cached.CachedAt).TotalSeconds < ModelNameCacheTtlSeconds)
                {
                    return cached.ModelName;
                }
                else
                {
                    // Expired cache entry - remove it
                    _objectModelNameCache.Remove(cacheKey);
                }
            }
            
            try
            {
                ModelInfo modelInfo = null;

                string typeKey = objectType.StartsWith("Ax", StringComparison.OrdinalIgnoreCase)
                    ? objectType.Substring(2)
                    : objectType;

                var methodCandidates = new List<string>
                {
                    "Get" + typeKey + "ModelInfo",
                    "Get" + typeKey + "sModelInfo"
                };

                if (string.Equals(objectType, "AxEdt", StringComparison.OrdinalIgnoreCase))
                    methodCandidates.Insert(0, "GetExtendedDataTypeModelInfo");
                if (string.Equals(objectType, "AxQuery", StringComparison.OrdinalIgnoreCase))
                    methodCandidates.Insert(0, "GetQueryModelInfo");
                if (string.Equals(objectType, "AxDataEntityView", StringComparison.OrdinalIgnoreCase))
                    methodCandidates.Insert(0, "GetDataEntityViewModelInfo");

                foreach (string methodName in methodCandidates.Distinct(StringComparer.Ordinal))
                {
                    var method = MetaService.GetType().GetMethod(methodName, new[] { typeof(string) });
                    if (method == null) continue;

                    object raw = method.Invoke(MetaService, new object[] { objectName });
                    if (raw == null) continue;

                    if (raw is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            modelInfo = item as ModelInfo;
                            if (modelInfo != null) break;
                        }
                    }
                    else
                    {
                        modelInfo = raw as ModelInfo;
                    }

                    if (modelInfo != null) break;
                }

                // The service has no model lookup for some types (e.g. data entity extensions); every provider does.
                string modelName = modelInfo?.Name;
                if (modelName == null)
                    ReadViaMetadataProvider(objectType, objectName, out modelName);
                
                // Cache the result
                _objectModelNameCache[cacheKey] = new CachedModelName
                {
                    ModelName = modelName,
                    CachedAt = DateTime.Now
                };
                
                return modelName;
            }
            catch { return null; }
        }

        private XppProject GetActiveProjectNode() => GetActiveProject();

        private XppProject GetActiveProject() =>
            !string.IsNullOrEmpty(_projectFile) && File.Exists(_projectFile) ? new XppProject(_projectFile, MetaService) : null;

        private static Type ResolveMetadataType(string objectType)
        {
            switch (objectType)
            {
                case "AxClass": return typeof(AxClass);
                case "AxTable": return typeof(AxTable);
                case "AxForm": return typeof(AxForm);
                case "AxEdt": return typeof(AxEdt);
                case "AxEnum": return typeof(AxEnum);
                case "AxView": return typeof(AxView);
                case "AxQuery": return typeof(AxQuerySimple);
                case "AxMenu": return typeof(AxMenu);
                case "AxTile": return typeof(AxTile);
                case "AxMenuItemDisplay": return typeof(AxMenuItemDisplay);
                case "AxMenuItemOutput": return typeof(AxMenuItemOutput);
                case "AxMenuItemAction": return typeof(AxMenuItemAction);
                case "AxDataEntityView": return typeof(AxDataEntityView);
                case "AxSecurityPrivilege": return typeof(AxSecurityPrivilege);
                case "AxSecurityDuty": return typeof(AxSecurityDuty);
                case "AxSecurityRole": return typeof(AxSecurityRole);
                case "AxService": return typeof(AxService);
                case "AxServiceGroup": return typeof(AxServiceGroup);
                case "AxConfigurationKey": return typeof(AxConfigurationKey);
                case "AxTableExtension": return typeof(AxTableExtension);
                case "AxEnumExtension": return typeof(AxEnumExtension);
                case "AxEdtExtension": return typeof(AxEdtExtension);
                case "AxFormExtension": return typeof(AxFormExtension);

                default: return null;
            }
        }

        // ── Strongly-typed metadata apply methods ──

        private static void ApplyProperties(object target, Dictionary<string, string> props)
        {
            if (props == null || target == null) return;
            var type = target.GetType();
            foreach (var kvp in props)
            {
                var prop = type.GetProperty(kvp.Key);
                if (prop == null || !prop.CanWrite) continue;
                try
                {
                    var propType = prop.PropertyType;
                    var underlying = Nullable.GetUnderlyingType(propType);
                    object value;

                    if (underlying != null)
                    {
                        if (string.IsNullOrEmpty(kvp.Value))
                            value = null;
                        else if (underlying == typeof(bool))
                            value = bool.Parse(kvp.Value);
                        else if (underlying == typeof(int))
                            value = int.Parse(kvp.Value);
                        else if (underlying == typeof(long))
                            value = long.Parse(kvp.Value);
                        else if (underlying.IsEnum)
                            value = Enum.Parse(underlying, kvp.Value, true);
                        else
                            continue;
                    }
                    else if (propType == typeof(string))
                        value = kvp.Value;
                    else if (propType == typeof(bool))
                        value = "true".Equals(kvp.Value, StringComparison.OrdinalIgnoreCase)
                             || "1".Equals(kvp.Value) || "yes".Equals(kvp.Value, StringComparison.OrdinalIgnoreCase);
                    else if (propType == typeof(int))
                        value = int.Parse(kvp.Value);
                    else if (propType == typeof(long))
                        value = long.Parse(kvp.Value);
                    else if (propType.IsEnum)
                        value = Enum.Parse(propType, kvp.Value, true);
                    else
                        continue;

                    prop.SetValue(target, value);
                }
                catch { }
            }
        }

        private static void ApplyEnumValues(KeyedObjectCollection<AxEnumValue> target, List<EnumValueDto> values)
        {
            if (values == null) return;
            int autoValue = 0;
            foreach (var existing in target)
            {
                if (existing.Value >= autoValue)
                    autoValue = existing.Value + 1;
            }
            foreach (var dto in values)
            {
                if (string.IsNullOrWhiteSpace(dto.Name)) continue;
                if (target.Contains(dto.Name)) continue;
                var ev = new AxEnumValue { Name = dto.Name, Value = dto.Value };
                if (!string.IsNullOrEmpty(dto.Label))
                    ev.Label = dto.Label;
                target.Add(ev);
            }
        }

        private static void ApplyFields(KeyedObjectCollection<AxTableField> target, List<FieldDto> fields)
        {
            if (fields == null) return;
            foreach (var f in fields)
            {
                if (string.IsNullOrEmpty(f.Name)) continue;
                AxTableField axField;
                switch ((f.FieldType ?? "String").ToLowerInvariant())
                {
                    case "int": axField = new AxTableFieldInt(); break;
                    case "real": axField = new AxTableFieldReal(); break;
                    case "date": axField = new AxTableFieldDate(); break;
                    case "datetime": axField = new AxTableFieldUtcDateTime(); break;
                    case "enum": axField = new AxTableFieldEnum(); break;
                    case "int64": axField = new AxTableFieldInt64(); break;
                    case "container": axField = new AxTableFieldContainer(); break;
                    case "guid": axField = new AxTableFieldGuid(); break;
                    case "time": axField = new AxTableFieldTime(); break;
                    default: axField = new AxTableFieldString(); break;
                }
                axField.Name = f.Name;
                if (!string.IsNullOrEmpty(f.Label))
                    axField.Label = f.Label;
                if (!string.IsNullOrEmpty(f.ExtendedDataType))
                {
                    var edtProp = axField.GetType().GetProperty("ExtendedDataType");
                    edtProp?.SetValue(axField, f.ExtendedDataType);
                }
                if (axField is AxTableFieldEnum enumField && !string.IsNullOrEmpty(f.EnumType))
                    enumField.EnumType = f.EnumType;
                target.Add(axField);
            }
        }

        private static void ApplyIndexes(KeyedObjectCollection<AxTableIndex> target, List<IndexDto> indexes)
        {
            if (indexes == null) return;
            foreach (var idx in indexes)
            {
                if (string.IsNullOrEmpty(idx.Name)) continue;
                var axIdx = new AxTableIndex { Name = idx.Name, AllowDuplicates = idx.AllowDuplicates ? NoYes.Yes : NoYes.No };
                if (idx.Fields != null)
                    foreach (string fieldName in idx.Fields)
                        axIdx.Fields.Add(new AxTableIndexField { Name = fieldName, DataField = fieldName });
                target.Add(axIdx);
            }
        }

        private static void ApplyFieldGroups(KeyedObjectCollection<AxTableFieldGroup> target, List<FieldGroupDto> groups)
        {
            if (groups == null) return;
            foreach (var fg in groups)
            {
                if (string.IsNullOrEmpty(fg.Name)) continue;
                var axFg = new AxTableFieldGroup { Name = fg.Name };
                if (!string.IsNullOrEmpty(fg.Label))
                    axFg.Label = fg.Label;
                if (fg.Fields != null)
                    foreach (string fieldName in fg.Fields)
                        axFg.Fields.Add(new AxTableFieldGroupField { Name = fieldName, DataField = fieldName });
                target.Add(axFg);
            }
        }

        private static void ApplyRelations(KeyedObjectCollection<AxTableRelation> target, List<RelationDto> relations)
        {
            if (relations == null) return;
            foreach (var rel in relations)
            {
                if (string.IsNullOrEmpty(rel.Name)) continue;
                var axRel = new AxTableRelation { Name = rel.Name };
                if (!string.IsNullOrEmpty(rel.RelatedTable))
                    axRel.RelatedTable = rel.RelatedTable;
                if (rel.Constraints != null)
                {
                    foreach (var c in rel.Constraints)
                    {
                        axRel.Constraints.Add(new AxTableRelationConstraintField
                        {
                            Name = (c.Field ?? "") + "_" + (c.RelatedField ?? ""),
                            Field = c.Field,
                            RelatedField = c.RelatedField
                        });
                    }
                }
                target.Add(axRel);
            }
        }

        // Extensions cannot set base-object properties directly; D365FO stores them as property modifications.
        private static bool ApplyPropertyModifications(object target, Dictionary<string, string> props)
        {
            KeyedObjectCollection<AxPropertyModification> mods =
                (target as AxTableExtension)?.PropertyModifications
                ?? (target as AxEnumExtension)?.PropertyModifications
                ?? (target as AxEdtExtension)?.PropertyModifications
                ?? (target as AxFormExtension)?.PropertyModifications
                ?? (target as AxDataEntityViewExtension)?.PropertyModifications
                ?? (target as AxSecurityDutyExtension)?.PropertyModifications
                ?? (target as AxSecurityRoleExtension)?.PropertyModifications;
            if (mods == null) return false;
            if (props == null) return true;

            foreach (var kvp in props)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                var existing = mods.FirstOrDefault(m => string.Equals(m.Name, kvp.Key, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    existing.Value = kvp.Value;
                else
                    mods.Add(new AxPropertyModification { Name = kvp.Key, Value = kvp.Value });
            }
            return true;
        }

        private void ApplyTypedMetadata(object target, CreateObjectRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.TypedMetadataJson))
            {
                ApplyTypedMetadataGraph(target, req.ObjectType, req.TypedMetadataJson);
                return;
            }

            // Apply structural changes FIRST so reference properties (ClusterIndex,
            // PrimaryIndex, etc.) find their targets when set in the properties pass.
            if (target is AxEnum axEnum)
                ApplyEnumValues(axEnum.EnumValues, req.EnumValues);
            if (target is AxEnumExtension enumExt)
                ApplyEnumValues(enumExt.EnumValues, req.EnumValues);
            if (target is AxTable axTable)
            {
                ApplyFields(axTable.Fields, req.Fields);
                ApplyIndexes(axTable.Indexes, req.Indexes);
                ApplyFieldGroups(axTable.FieldGroups, req.FieldGroups);
                ApplyRelations(axTable.Relations, req.Relations);
            }
            if (target is AxTableExtension tableExt)
            {
                ApplyFields(tableExt.Fields, req.Fields);
                ApplyIndexes(tableExt.Indexes, req.Indexes);
                ApplyFieldGroups(tableExt.FieldGroups, req.FieldGroups);
                ApplyRelations(tableExt.Relations, req.Relations);
            }
            if (target is AxQuerySimple axQuery)
                ApplyQueryDataSources(axQuery, req.DataSources);
            ApplyEntryPoints(target, req.EntryPoints);

            // Scalar properties last — references to fields/indexes/etc. are now valid.
            if (!ApplyPropertyModifications(target, req.Properties))
                ApplyProperties(target, req.Properties);
        }

        private void ApplyTypedMetadata(object target, UpdateObjectRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.TypedMetadataJson))
            {
                ApplyTypedMetadataGraph(target, req.ObjectType, req.TypedMetadataJson);
                return;
            }

            // Apply structural changes FIRST so reference properties (ClusterIndex,
            // PrimaryIndex, etc.) find their targets when set in the properties pass.
            if (target is AxEnum axEnum)
                ApplyEnumValues(axEnum.EnumValues, req.EnumValues);
            if (target is AxEnumExtension enumExt)
                ApplyEnumValues(enumExt.EnumValues, req.EnumValues);
            if (target is AxTable axTable)
            {
                ApplyFields(axTable.Fields, req.Fields);
                ApplyIndexes(axTable.Indexes, req.Indexes);
                ApplyFieldGroups(axTable.FieldGroups, req.FieldGroups);
                ApplyRelations(axTable.Relations, req.Relations);
            }
            if (target is AxTableExtension tableExt)
            {
                ApplyFields(tableExt.Fields, req.Fields);
                ApplyIndexes(tableExt.Indexes, req.Indexes);
                ApplyFieldGroups(tableExt.FieldGroups, req.FieldGroups);
                ApplyRelations(tableExt.Relations, req.Relations);
            }
            if (target is AxQuerySimple axQuery)
                ApplyQueryDataSources(axQuery, req.DataSources);
            ApplyEntryPoints(target, req.EntryPoints);
            ApplySecurityReferences(target, req);

            // Scalar properties last - references to fields/indexes/etc. are now valid.
            if (!ApplyPropertyModifications(target, req.Properties))
                ApplyProperties(target, req.Properties);
        }

        private static void ApplyTypedMetadataGraph(object target, string objectType, string typedMetadataJson)
        {
            if (target == null || string.IsNullOrWhiteSpace(typedMetadataJson)) return;

            try
            {
                object parsed = ParseTypedMetadataToNode(objectType, typedMetadataJson);
                var dict = parsed as Dictionary<string, object>;
                if (dict == null) return;

                // AxQuery needs concrete root/embedded datasource creation semantics.
                // These keys are handled by the specialized method and must be excluded
                // from the generic ApplyObjectNode pass to prevent double-clear wiping them.
                var handledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (target is AxQuerySimple query)
                {
                    ApplyQueryDataSourcesFromTypedMetadata(query, dict);
                    handledKeys.Add("DataSources");
                    handledKeys.Add("dataSources");
                }

                ApplyObjectNode(target, dict, handledKeys);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("typedMetadata apply failed for " + objectType + ": " + ex.Message, ex);
            }
        }

        private static object ParseTypedMetadataToNode(string objectType, string typedMetadataJson)
        {
            var serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 64
            };

            Type proxyType = ResolveProxyType(objectType);
            if (proxyType != null)
            {
                object proxyObj = serializer.Deserialize(typedMetadataJson, proxyType);
                return ExtractMetadataGraph(proxyObj, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
            }

            return serializer.DeserializeObject(typedMetadataJson);
        }

        private static void ApplyObjectNode(object target, Dictionary<string, object> node, HashSet<string> skipKeys = null)
        {
            if (target == null || node == null) return;

            foreach (var kv in node)
            {
                if (skipKeys != null && skipKeys.Contains(kv.Key)) continue;

                var prop = FindProperty(target.GetType(), kv.Key);
                if (prop == null || !prop.CanRead) continue;

                Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                // Handle collections (including read-only collection properties).
                if (typeof(System.Collections.IEnumerable).IsAssignableFrom(propType) && propType != typeof(string))
                {
                    object collection;
                    try { collection = prop.GetValue(target); }
                    catch { continue; }

                    if (collection == null)
                    {
                        if (!prop.CanWrite) continue;
                        object created = CreateDefaultInstance(propType);
                        if (created == null) continue;
                        prop.SetValue(target, created);
                        collection = created;
                    }

                    ClearCollectionIfPossible(collection);
                    PopulateCollectionFromNode(collection, kv.Value);
                    continue;
                }

                if (!prop.CanWrite) continue;

                if (kv.Value == null)
                {
                    if (Nullable.GetUnderlyingType(prop.PropertyType) != null || !prop.PropertyType.IsValueType)
                        prop.SetValue(target, null);
                    continue;
                }

                if (IsSimpleType(propType))
                {
                    object converted = ConvertSimpleValue(kv.Value, propType);
                    if (converted != null)
                        prop.SetValue(target, converted);
                    continue;
                }

                var childNode = kv.Value as Dictionary<string, object>;
                if (childNode == null) continue;

                object child = null;
                try { child = prop.GetValue(target); } catch { }

                // Support nested metadata on read-only properties when they expose an existing object.
                if (child == null)
                {
                    if (!prop.CanWrite) continue;
                    child = CreateDefaultInstance(propType);
                    if (child == null) continue;
                }

                ApplyObjectNode(child, childNode);
                if (prop.CanWrite)
                    prop.SetValue(target, child);
            }
        }

        private static void PopulateCollectionFromNode(object collection, object node)
        {
            if (collection == null || node == null) return;
            var values = ToObjectArray(node);
            if (values == null) return;

            // Use overload-safe Add resolution — KeyedObjectCollection and similar types
            // expose both typed and object-typed Add overloads; prefer the typed one.
            System.Reflection.MethodInfo addMethod = null;
            foreach (var m in collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Add") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || m.IsGenericMethod) continue;
                if (ps[0].ParameterType != typeof(object)) { addMethod = m; break; }
                if (addMethod == null) addMethod = m;
            }
            if (addMethod == null) return;
            Type itemType = addMethod.GetParameters()[0].ParameterType;

            foreach (object entry in values)
            {
                if (entry == null) continue;
                object addValue = null;

                if (IsSimpleType(itemType))
                {
                    addValue = ConvertSimpleValue(entry, itemType);
                }
                else
                {
                    var childDict = entry as Dictionary<string, object>;
                    if (childDict == null && entry is System.Collections.Hashtable ht)
                    {
                        childDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (System.Collections.DictionaryEntry de in ht)
                            childDict[Convert.ToString(de.Key)] = de.Value;
                    }
                    if (childDict != null)
                    {
                        object item = CreateDefaultInstance(itemType);
                        if (item != null)
                        {
                            ApplyObjectNode(item, childDict);
                            addValue = item;
                        }
                    }
                }

                if (addValue != null)
                    addMethod.Invoke(collection, new[] { addValue });
            }
        }

        private static void ApplyQueryDataSourcesFromTypedMetadata(AxQuerySimple query, Dictionary<string, object> node)
        {
            if (query == null || node == null) return;

            object dsNode = GetNodeValue(node, "DataSources", "dataSources");
            var dsValues = ToObjectArray(dsNode);
            if (dsValues == null) return;

            var dsProp = query.GetType().GetProperty("DataSources", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var rootCollection = dsProp?.GetValue(query);
            if (rootCollection == null) return;

            ClearCollectionIfPossible(rootCollection);

            foreach (var entry in dsValues)
            {
                var dsDict = ToDictionary(entry);
                if (dsDict == null) continue;
                AddQueryDataSourceNode(rootCollection, dsDict);
            }
        }

        private static void AddQueryDataSourceNode(object collection, Dictionary<string, object> node)
        {
            if (collection == null || node == null) return;

            // KeyedObjectCollection has multiple Add overloads (typed + object).
            // Pick the most-specific single-param Add whose parameter is not System.Object.
            System.Reflection.MethodInfo addMethod = null;
            foreach (var m in collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Add") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || m.IsGenericMethod) continue;
                if (ps[0].ParameterType == typeof(object))
                {
                    // Keep as fallback only if nothing better found yet.
                    if (addMethod == null) addMethod = m;
                }
                else
                {
                    addMethod = m;
                    break;
                }
            }

            if (addMethod == null) return;

            Type itemType = addMethod.GetParameters()[0].ParameterType;
            object dataSource = CreateDefaultInstance(itemType);
            if (dataSource == null) return;

            ApplyDictionaryToObject(dataSource, node,
                "DataSources", "dataSources", "ChildDataSources", "childDataSources", "Ranges", "ranges");
            ApplyQueryRangesFromNode(dataSource, node);
            ApplyChildDataSourcesFromNode(dataSource, node);

            addMethod.Invoke(collection, new[] { dataSource });
        }

        private static void ApplyChildDataSourcesFromNode(object dataSource, Dictionary<string, object> node)
        {
            object childrenNode = GetNodeValue(node,
                "DataSources", "dataSources", "ChildDataSources", "childDataSources");
            var children = ToObjectArray(childrenNode);
            if (children == null || children.Length == 0) return;

            var childrenProp = FindProperty(dataSource.GetType(), "DataSources")
                ?? FindProperty(dataSource.GetType(), "ChildDataSources");
            var childCollection = childrenProp?.GetValue(dataSource);
            if (childCollection == null) return;

            ClearCollectionIfPossible(childCollection);
            foreach (var child in children)
            {
                var childDict = ToDictionary(child);
                if (childDict == null) continue;
                AddQueryDataSourceNode(childCollection, childDict);
            }
        }

        private static void ApplyQueryRangesFromNode(object dataSource, Dictionary<string, object> node)
        {
            object rangesNode = GetNodeValue(node, "Ranges", "ranges");
            var ranges = ToObjectArray(rangesNode);
            if (ranges == null || ranges.Length == 0) return;

            var rangesProp = FindProperty(dataSource.GetType(), "Ranges");
            var rangeCollection = rangesProp?.GetValue(dataSource);
            if (rangeCollection == null) return;

            System.Reflection.MethodInfo addMethod = null;
            foreach (var m in rangeCollection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Add") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || m.IsGenericMethod) continue;
                if (ps[0].ParameterType != typeof(object)) { addMethod = m; break; }
                if (addMethod == null) addMethod = m;
            }
            if (addMethod == null) return;
            Type rangeType = addMethod.GetParameters()[0].ParameterType;

            ClearCollectionIfPossible(rangeCollection);
            foreach (var rangeNode in ranges)
            {
                var rangeDict = ToDictionary(rangeNode);
                if (rangeDict == null) continue;

                object range = CreateDefaultInstance(rangeType);
                if (range == null) continue;

                ApplyDictionaryToObject(range, rangeDict);
                addMethod.Invoke(rangeCollection, new[] { range });
            }
        }

        private static void ApplyDictionaryToObject(object target, Dictionary<string, object> source, params string[] excludedKeys)
        {
            if (target == null || source == null) return;

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedKeys != null)
            {
                foreach (var k in excludedKeys)
                    if (!string.IsNullOrWhiteSpace(k)) excluded.Add(k);
            }

            foreach (var kv in source)
            {
                if (excluded.Contains(kv.Key)) continue;

                var prop = FindProperty(target.GetType(), kv.Key);
                if (prop == null || !prop.CanWrite) continue;

                object converted = ConvertValueForProperty(kv.Value, prop.PropertyType);
                if (converted != null)
                    prop.SetValue(target, converted);
            }
        }

        private static object ConvertValueForProperty(object input, Type propertyType)
        {
            if (input == null) return null;

            Type targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            object simple = ConvertSimpleValue(input, targetType);
            if (simple != null) return simple;

            if (targetType == typeof(string))
                return Convert.ToString(input);

            if (input is string text)
            {
                try
                {
                    var parse = targetType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string) }, null);
                    if (parse != null)
                        return parse.Invoke(null, new object[] { text });
                }
                catch { }

                try
                {
                    var ctor = targetType.GetConstructor(new[] { typeof(string) });
                    if (ctor != null)
                        return ctor.Invoke(new object[] { text });
                }
                catch { }

                try
                {
                    object obj = CreateDefaultInstance(targetType);
                    var nameProp = FindProperty(targetType, "Name");
                    if (obj != null && nameProp != null && nameProp.CanWrite)
                    {
                        nameProp.SetValue(obj, text);
                        return obj;
                    }
                }
                catch { }
            }

            return null;
        }

        private static object GetNodeValue(Dictionary<string, object> dict, params string[] keys)
        {
            if (dict == null || keys == null) return null;

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (dict.TryGetValue(key, out object value)) return value;
            }

            return null;
        }

        private static Dictionary<string, object> ToDictionary(object value)
        {
            if (value is Dictionary<string, object> d) return d;
            if (value is System.Collections.Hashtable ht)
            {
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry de in ht)
                    dict[Convert.ToString(de.Key)] = de.Value;
                return dict;
            }
            return null;
        }

        private static void ClearCollectionIfPossible(object collection)
        {
            var clearMethod = collection?.GetType().GetMethod("Clear");
            if (clearMethod == null) return;
            try { clearMethod.Invoke(collection, null); } catch { }
        }

        private static object CreateDefaultInstance(Type type)
        {
            try
            {
                if (type.IsInterface || type.IsAbstract) return null;
                return Activator.CreateInstance(type);
            }
            catch
            {
                return null;
            }
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        }

        private static bool IsSimpleType(Type t)
        {
            Type nt = Nullable.GetUnderlyingType(t) ?? t;
            return nt.IsPrimitive || nt.IsEnum || nt == typeof(string) || nt == typeof(decimal)
                || nt == typeof(DateTime) || nt == typeof(Guid);
        }

        private static object ConvertSimpleValue(object input, Type targetType)
        {
            try
            {
                if (input == null) return null;
                Type nt = Nullable.GetUnderlyingType(targetType) ?? targetType;

                if (nt == typeof(string)) return Convert.ToString(input);
                if (nt.IsEnum)
                {
                    if (input is string es)
                        return Enum.Parse(nt, es, true);
                    return Enum.ToObject(nt, Convert.ToInt32(input));
                }
                if (nt == typeof(bool))
                {
                    if (input is string bs)
                    {
                        if ("1".Equals(bs) || "yes".Equals(bs, StringComparison.OrdinalIgnoreCase)) return true;
                        if ("0".Equals(bs) || "no".Equals(bs, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return Convert.ToBoolean(input);
                }
                if (nt == typeof(Guid))
                    return Guid.Parse(Convert.ToString(input));
                if (nt == typeof(DateTime))
                    return DateTime.Parse(Convert.ToString(input));

                return Convert.ChangeType(input, nt, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyQueryDataSources(AxQuerySimple query, List<QueryDataSourceDto> dataSources)
        {
            if (query == null || dataSources == null || dataSources.Count == 0)
                return;

            var dsProp = query.GetType().GetProperty("DataSources");
            var dsCollection = dsProp?.GetValue(query);
            if (dsCollection == null) return;

            ClearCollectionIfPossible(dsCollection);

            // Preferred shape: nested DTO tree using ChildDataSources.
            bool hasNested = dataSources.Any(ds => ds?.ChildDataSources != null && ds.ChildDataSources.Count > 0);
            if (hasNested)
            {
                foreach (var dto in dataSources)
                    AddQueryDataSourceFromDto(dsCollection, dto);
                return;
            }

            // Backward-compat shape: flattened list using ParentDataSource references.
            var byParent = new Dictionary<string, List<QueryDataSourceDto>>(StringComparer.OrdinalIgnoreCase);
            foreach (var dto in dataSources.Where(d => d != null))
            {
                string parent = string.IsNullOrWhiteSpace(dto.ParentDataSource) ? string.Empty : dto.ParentDataSource;
                if (!byParent.TryGetValue(parent, out var list))
                {
                    list = new List<QueryDataSourceDto>();
                    byParent[parent] = list;
                }
                list.Add(dto);
            }

            if (byParent.TryGetValue(string.Empty, out var roots))
            {
                foreach (var root in roots)
                    AddQueryDataSourceFromFlat(dsCollection, root, byParent);
            }
            else
            {
                // If parent links are absent, treat all as roots.
                foreach (var dto in dataSources)
                    AddQueryDataSourceFromDto(dsCollection, dto);
            }
        }

        private static void AddQueryDataSourceFromFlat(object collection, QueryDataSourceDto dto,
            Dictionary<string, List<QueryDataSourceDto>> byParent)
        {
            object created = AddQueryDataSourceFromDto(collection, dto);
            if (created == null || string.IsNullOrWhiteSpace(dto?.Name)) return;

            if (!byParent.TryGetValue(dto.Name, out var children) || children == null || children.Count == 0)
                return;

            var childCollection = FindProperty(created.GetType(), "DataSources")?.GetValue(created)
                ?? FindProperty(created.GetType(), "ChildDataSources")?.GetValue(created);
            if (childCollection == null) return;

            foreach (var child in children)
                AddQueryDataSourceFromFlat(childCollection, child, byParent);
        }

        private static object AddQueryDataSourceFromDto(object collection, QueryDataSourceDto dto)
        {
            if (collection == null || dto == null) return null;
            if (string.IsNullOrWhiteSpace(dto.Name) && string.IsNullOrWhiteSpace(dto.Table)) return null;

            var addMethod = collection.GetType().GetMethod("Add");
            if (addMethod == null || addMethod.GetParameters().Length != 1) return null;
            Type dsType = addMethod.GetParameters()[0].ParameterType;

            object ds = CreateDefaultInstance(dsType);
            if (ds == null) return null;

            TrySetPropertyValue(ds, dto.Name, "Name", "DataSourceName");
            TrySetPropertyValue(ds, dto.Table, "Table", "TableName");
            TrySetPropertyValue(ds, dto.ParentDataSource, "ParentDataSource", "JoinDataSource", "Parent");
            TrySetPropertyValue(ds, dto.JoinMode, "JoinMode");
            TrySetPropertyValue(ds, dto.LinkType, "LinkType");
            if (dto.DynamicFields.HasValue)
                TrySetPropertyValue(ds, dto.DynamicFields.Value ? "true" : "false", "DynamicFields");
            if (dto.Relations.HasValue)
                TrySetPropertyValue(ds, dto.Relations.Value ? "true" : "false", "Relations");
            if (dto.FirstOnly.HasValue)
                TrySetPropertyValue(ds, dto.FirstOnly.Value ? "true" : "false", "FirstOnly");

            ApplyQueryRanges(ds, dto.Ranges);

            addMethod.Invoke(collection, new object[] { ds });

            if (dto.ChildDataSources != null && dto.ChildDataSources.Count > 0)
            {
                var childCollection = FindProperty(ds.GetType(), "DataSources")?.GetValue(ds)
                    ?? FindProperty(ds.GetType(), "ChildDataSources")?.GetValue(ds);
                if (childCollection != null)
                {
                    ClearCollectionIfPossible(childCollection);
                    foreach (var child in dto.ChildDataSources)
                        AddQueryDataSourceFromDto(childCollection, child);
                }
            }

            return ds;
        }

        private static object FindQueryDataSourceByName(object dsCollection, string name)
        {
            if (dsCollection == null || string.IsNullOrWhiteSpace(name)) return null;
            var enumerable = dsCollection as System.Collections.IEnumerable;
            if (enumerable == null) return null;

            foreach (object item in enumerable)
            {
                var nameProp = item?.GetType().GetProperty("Name")
                    ?? item?.GetType().GetProperty("DataSourceName");
                string existingName = nameProp?.GetValue(item) as string;
                if (!string.IsNullOrWhiteSpace(existingName)
                    && existingName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }

        private static void ApplyQueryRanges(object dataSource, List<QueryRangeDto> ranges)
        {
            if (dataSource == null || ranges == null || ranges.Count == 0) return;

            var rangesProp = dataSource.GetType().GetProperty("Ranges");
            var rangesCollection = rangesProp?.GetValue(dataSource);
            if (rangesCollection == null) return;

            var addMethod = rangesCollection.GetType().GetMethod("Add");
            if (addMethod == null || addMethod.GetParameters().Length != 1) return;
            Type rangeType = addMethod.GetParameters()[0].ParameterType;

            foreach (var dto in ranges)
            {
                if (string.IsNullOrWhiteSpace(dto?.Field) && string.IsNullOrWhiteSpace(dto?.Name))
                    continue;

                object range = Activator.CreateInstance(rangeType);
                TrySetPropertyValue(range, dto.Name, "Name");
                TrySetPropertyValue(range, dto.Field, "Field", "DataField", "Column");
                TrySetPropertyValue(range, dto.Value, "Value", "Range", "Expression");
                addMethod.Invoke(rangesCollection, new object[] { range });
            }
        }

        private static void TrySetPropertyValue(object target, string value, params string[] propertyNames)
        {
            if (target == null || string.IsNullOrWhiteSpace(value) || propertyNames == null) return;

            foreach (string propertyName in propertyNames)
            {
                if (string.IsNullOrWhiteSpace(propertyName)) continue;
                var prop = target.GetType().GetProperty(propertyName);
                if (prop == null || !prop.CanWrite) continue;

                try
                {
                    Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    object converted;

                    if (propType == typeof(string))
                        converted = value;
                    else if (propType == typeof(bool))
                        converted = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
                            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    else if (propType == typeof(int))
                        converted = int.Parse(value);
                    else if (propType == typeof(long))
                        converted = long.Parse(value);
                    else if (propType.IsEnum)
                        converted = Enum.Parse(propType, value, true);
                    else
                        continue;

                    prop.SetValue(target, converted);
                    return;
                }
                catch
                {
                    // Try next matching property name.
                }
            }
        }

        // ── Strongly-typed metadata extraction (for read) ──

        private static Dictionary<string, string> ExtractProperties(object axObj)
        {
            var props = new Dictionary<string, string>();
            if (axObj == null) return props;

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Name", "SourceCode", "Declaration", "Methods", "UnparsableSource",
                "Attributes", "Members", "Fields", "FieldGroups", "Indexes",
                "Relations", "EnumValues", "FormControl", "DataSources",
                "EntryPoints", "Privileges", "Duties", "SubRoles", "Controls",
                "Parts", "FullTextIndexes", "Mappings", "StateMachines",
                "DeleteActions", "FieldReferences", "Events",
                "Parent", "Owner"
            };

            foreach (var prop in axObj.GetType().GetProperties())
            {
                if (!prop.CanRead || excluded.Contains(prop.Name)) continue;
                var propType = prop.PropertyType;
                var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
                if (underlying != typeof(string) && underlying != typeof(bool)
                    && underlying != typeof(int) && underlying != typeof(long)
                    && !underlying.IsEnum)
                    continue;
                try
                {
                    var val = prop.GetValue(axObj);
                    if (val == null) continue;
                    // Skip defaults to keep output clean
                    if (underlying == typeof(bool) && !(bool)val) continue;
                    if (underlying == typeof(int) && (int)val == 0) continue;
                    if (underlying == typeof(long) && (long)val == 0) continue;
                    if (underlying == typeof(string) && string.IsNullOrEmpty((string)val)) continue;
                    if (underlying.IsEnum && Convert.ToInt32(val) == 0) continue;
                    props[prop.Name] = val.ToString();
                }
                catch { }
            }
            return props;
        }

        private static List<EnumValueDto> ExtractEnumValues(IEnumerable<AxEnumValue> enumValues)
        {
            var result = new List<EnumValueDto>();
            if (enumValues == null) return result;
            foreach (var v in enumValues)
                result.Add(new EnumValueDto { Name = v.Name, Value = v.Value, Label = v.Label });
            return result;
        }

        private static Dictionary<string, string> ExtractPropertyModifications(IEnumerable<AxPropertyModification> mods)
        {
            var result = new Dictionary<string, string>();
            if (mods == null) return result;
            foreach (var m in mods)
                if (!string.IsNullOrEmpty(m.Name))
                    result[m.Name] = m.Value;
            return result;
        }

        private static List<FieldDto> ExtractFields(IEnumerable<AxTableField> fields)
        {
            var result = new List<FieldDto>();
            if (fields == null) return result;
            foreach (var f in fields)
            {
                string fieldType = f.GetType().Name;
                if (fieldType.StartsWith("AxTableField"))
                    fieldType = fieldType.Substring("AxTableField".Length);
                var dto = new FieldDto { Name = f.Name, FieldType = fieldType, Label = f.Label };
                var edtProp = f.GetType().GetProperty("ExtendedDataType");
                if (edtProp != null)
                    dto.ExtendedDataType = edtProp.GetValue(f) as string;
                if (f is AxTableFieldEnum enumField)
                    dto.EnumType = enumField.EnumType;
                result.Add(dto);
            }
            return result;
        }

        private static List<IndexDto> ExtractIndexes(IEnumerable<AxTableIndex> indexes)
        {
            var result = new List<IndexDto>();
            if (indexes == null) return result;
            foreach (var idx in indexes)
            {
                var dto = new IndexDto { Name = idx.Name, AllowDuplicates = idx.AllowDuplicates == NoYes.Yes };
                if (idx.Fields != null)
                    foreach (var f in idx.Fields)
                        dto.Fields.Add(f.DataField);
                result.Add(dto);
            }
            return result;
        }

        private static List<FieldGroupDto> ExtractFieldGroups(IEnumerable<AxTableFieldGroup> groups)
        {
            var result = new List<FieldGroupDto>();
            if (groups == null) return result;
            foreach (var fg in groups)
            {
                var dto = new FieldGroupDto { Name = fg.Name, Label = fg.Label };
                if (fg.Fields != null)
                    foreach (var f in fg.Fields)
                        dto.Fields.Add(f.DataField);
                result.Add(dto);
            }
            return result;
        }

        private static List<RelationDto> ExtractRelations(IEnumerable<AxTableRelation> relations)
        {
            var result = new List<RelationDto>();
            if (relations == null) return result;
            foreach (var rel in relations)
            {
                var dto = new RelationDto { Name = rel.Name, RelatedTable = rel.RelatedTable };
                if (rel.Constraints != null)
                    foreach (var c in rel.Constraints)
                        if (c is AxTableRelationConstraintField fc)
                            dto.Constraints.Add(new RelationConstraintDto { Field = fc.Field, RelatedField = fc.RelatedField });
                result.Add(dto);
            }
            return result;
        }

        private static List<QueryDataSourceDto> ExtractQueryDataSources(object query)
        {
            var result = new List<QueryDataSourceDto>();
            if (query == null) return result;

            var dsProp = query.GetType().GetProperty("DataSources");
            var dsCollection = dsProp?.GetValue(query) as System.Collections.IEnumerable;
            if (dsCollection == null) return result;

            foreach (object ds in dsCollection)
            {
                if (ds == null) continue;
                result.Add(ExtractDataSourceNode(ds));
            }

            return result;
        }

        private static QueryDataSourceDto ExtractDataSourceNode(object ds)
        {
            var dto = new QueryDataSourceDto
            {
                Name = ReadStringProperty(ds, "Name", "DataSourceName"),
                Table = ReadStringProperty(ds, "Table", "TableName"),
                ParentDataSource = ReadStringProperty(ds, "ParentDataSource", "JoinDataSource", "Parent"),
                JoinMode = ReadStringProperty(ds, "JoinMode"),
                LinkType = ReadStringProperty(ds, "LinkType")
            };

            dto.DynamicFields = ReadBoolProperty(ds, "DynamicFields");
            dto.Relations = ReadBoolProperty(ds, "Relations");
            dto.FirstOnly = ReadBoolProperty(ds, "FirstOnly");

            var rangesProp = ds.GetType().GetProperty("Ranges");
            var ranges = rangesProp?.GetValue(ds) as System.Collections.IEnumerable;
            if (ranges != null)
            {
                foreach (object r in ranges)
                {
                    if (r == null) continue;
                    dto.Ranges.Add(new QueryRangeDto
                    {
                        Name = ReadStringProperty(r, "Name"),
                        Field = ReadStringProperty(r, "Field", "DataField", "Column"),
                        Value = ReadStringProperty(r, "Value", "Range", "Expression")
                    });
                }
            }

            // Recurse into child DataSources
            var childDsProp = ds.GetType().GetProperty("DataSources");
            var childDs = childDsProp?.GetValue(ds) as System.Collections.IEnumerable;
            if (childDs != null)
            {
                foreach (object child in childDs)
                {
                    if (child == null) continue;
                    dto.ChildDataSources.Add(ExtractDataSourceNode(child));
                }
            }

            return dto;
        }

        private static string ReadStringProperty(object target, params string[] names)
        {
            if (target == null || names == null) return null;
            foreach (string name in names)
            {
                var prop = target.GetType().GetProperty(name);
                if (prop == null || !prop.CanRead) continue;
                try
                {
                    var val = prop.GetValue(target);
                    if (val != null) return val.ToString();
                }
                catch { }
            }
            return null;
        }

        private static bool? ReadBoolProperty(object target, params string[] names)
        {
            if (target == null || names == null) return null;
            foreach (string name in names)
            {
                var prop = target.GetType().GetProperty(name);
                if (prop == null || !prop.CanRead) continue;
                try
                {
                    object val = prop.GetValue(target);
                    if (val == null) continue;

                    Type valType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    if (valType == typeof(bool))
                        return (bool)val;

                    string s = val.ToString();
                    if ("true".Equals(s, StringComparison.OrdinalIgnoreCase)
                        || "yes".Equals(s, StringComparison.OrdinalIgnoreCase)
                        || "1".Equals(s, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if ("false".Equals(s, StringComparison.OrdinalIgnoreCase)
                        || "no".Equals(s, StringComparison.OrdinalIgnoreCase)
                        || "0".Equals(s, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                catch { }
            }
            return null;
        }

        private static string TryGetLabelText(object controller, string labelId)
        {
            try
            {
                var method = controller.GetType().GetMethod("GetLabelText", new[] { typeof(string) });
                if (method == null) return string.Empty;
                return method.Invoke(controller, new object[] { labelId }) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static IEnumerable<LabelEntry> TrySearchLabels(object controller, string searchText, int maxResults)
        {
            var results = new List<LabelEntry>();
            int limit = maxResults > 0 ? maxResults : 50;

            // Prefer text search when available.
            foreach (string methodName in new[] { "SearchByText", "SearchById", "GetAll" })
            {
                try
                {
                    var method = controller.GetType().GetMethod(methodName);
                    if (method == null) continue;

                    object raw;
                    if (method.GetParameters().Length == 1)
                    {
                        string arg = string.IsNullOrEmpty(searchText) ? "" : searchText;
                        raw = method.Invoke(controller, new object[] { arg });
                    }
                    else if (method.GetParameters().Length == 0)
                    {
                        raw = method.Invoke(controller, null);
                    }
                    else
                    {
                        continue;
                    }

                    if (raw is System.Collections.IEnumerable seq)
                    {
                        foreach (var item in seq)
                        {
                            if (item == null) continue;
                            string id = item.GetType().GetProperty("Id")?.GetValue(item)?.ToString();
                            string text = item.GetType().GetProperty("Text")?.GetValue(item)?.ToString();
                            if (string.IsNullOrEmpty(id))
                            {
                                id = item.GetType().GetProperty("LabelId")?.GetValue(item)?.ToString();
                            }
                            if (string.IsNullOrEmpty(text))
                            {
                                text = item.GetType().GetProperty("LabelText")?.GetValue(item)?.ToString();
                            }

                            if (!string.IsNullOrEmpty(searchText) && !string.IsNullOrEmpty(text)
                                && text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0
                                && (id == null || id.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0))
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(id))
                                results.Add(new LabelEntry { Id = id, Text = text ?? string.Empty });

                            if (results.Count >= limit)
                                return results;
                        }
                    }

                    if (results.Count > 0)
                        return results;
                }
                catch
                {
                    // Try next supported API shape.
                }
            }

            return results;
        }

        private bool IsCustomModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string lower = modelName.ToLowerInvariant();
            string[] standard = {
                "applicationsuite", "applicationplatform", "applicationfoundation",
                "applicationcommon", "directory", "currency", "dimensions",
                "generalledger", "personnelcore", "taxengine", "retail",
                "supplychain", "projaccounting", "casemanagement"
            };
            foreach (string s in standard)
                if (lower.Contains(s)) return false;

            // Microsoft/ISV models not covered by the list above are marked Customization=DoNotAllow.
            var info = MetaService?.GetModel(modelName);
            return info == null || (!info.Readonly && info.Customization != ModelCustomizationLevel.DoNotAllow);
        }

        private static bool IsStandardPackagePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string lower = filePath.Replace('/', '\\').ToLowerInvariant();
            string[] standardPackages = {
                "\\applicationsuite\\", "\\applicationplatform\\",
                "\\applicationfoundation\\", "\\applicationcommon\\",
                "\\directory\\", "\\currency\\", "\\dimensions\\",
                "\\generalledger\\", "\\personnelcore\\", "\\taxengine\\",
                "\\retail\\", "\\commerce\\", "\\supplychain\\"
            };
            foreach (string pkg in standardPackages)
                if (lower.Contains(pkg)) return true;
            return false;
        }

        /// <summary>
        /// Strips CDATA wrappers from a string value. The MetaModel API adds its
        /// own CDATA when serializing, so if the input already contains CDATA
        /// wrappers the result would be double-wrapped and corrupted.
        /// </summary>
        private static string StripCData(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string trimmed = value.Trim();
            if (trimmed.StartsWith("<![CDATA[") && trimmed.EndsWith("]]>"))
                return trimmed.Substring(9, trimmed.Length - 12);
            return value;
        }

        private static MetaModelResult Ok(string message) =>
            new MetaModelResult { Success = true, Message = message };

        private static MetaModelResult Fail(string message) =>
            new MetaModelResult { Success = false, Message = message };

        // ── Type name search helpers ──

        private List<TypeSearcher> GetTypeSearchers(string objectType)
        {
            var searchers = new List<TypeSearcher>();

            if (string.IsNullOrEmpty(objectType) || objectType == "AxClass")
                searchers.Add(new TypeSearcher("AxClass", () => MetaService.GetClassNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxTable")
                searchers.Add(new TypeSearcher("AxTable", () => MetaService.GetTableNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxForm")
                searchers.Add(new TypeSearcher("AxForm", () => MetaService.GetFormNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxEnum")
                searchers.Add(new TypeSearcher("AxEnum", () => MetaService.GetEnumNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxView")
                searchers.Add(new TypeSearcher("AxView", () => MetaService.GetViewNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxQuery")
                searchers.Add(new TypeSearcher("AxQuery", () => MetaService.GetQueryNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxEdt")
                searchers.Add(new TypeSearcher("AxEdt", () => MetaService.GetExtendedDataTypeNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxMenuItemDisplay")
                searchers.Add(new TypeSearcher("AxMenuItemDisplay", () => MetaService.GetMenuItemDisplayNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxMenuItemOutput")
                searchers.Add(new TypeSearcher("AxMenuItemOutput", () => MetaService.GetMenuItemOutputNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxMenuItemAction")
                searchers.Add(new TypeSearcher("AxMenuItemAction", () => MetaService.GetMenuItemActionNames()));

            return searchers;
        }

        private sealed class TypeSearcher
        {
            public string TypeName { get; }
            private readonly Func<IEnumerable<string>> _getNamesFunc;

            public TypeSearcher(string typeName, Func<IEnumerable<string>> getNamesFunc)
            {
                TypeName = typeName;
                _getNamesFunc = getNamesFunc;
            }

            public IEnumerable<string> GetNames() => _getNamesFunc();
        }

        // ── Cross-Reference search ──

        // Where-used is answered from the cross-reference database by XRefQuery; this is only reached when that fails.
        public FindReferencesResult FindReferences(string objectType, string objectName, string memberName, string referenceKind, int maxResults)
        {
            return new FindReferencesResult
            {
                Success = false,
                Message = "Cross-reference search needs the cross-reference database named in the active configuration."
            };
        }
    }
}
