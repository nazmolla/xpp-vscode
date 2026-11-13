using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>
    /// The element types the bridge creates, updates and reads with type-specific code. Types without an entry (or without one
    /// of the operations) use the generic metadata-provider paths.
    /// </summary>
    public sealed partial class MetaModelBridge
    {
        internal sealed class ElementTypeHandler
        {
            public Func<MetaModelBridge, CreateObjectRequest, ModelSaveInfo, MetaModelResult> Create;
            public Func<MetaModelBridge, UpdateObjectRequest, MetaModelResult> Update;
            /// <summary>Fills the result; returns a failed result to report instead (not found), or null.</summary>
            public Func<MetaModelBridge, ReadObjectResult, string, string, ReadObjectResult> Read;
            /// <summary>Create saves a blank element, then applies the request through Update (one code path for both).</summary>
            public bool UpdateAfterCreate;
        }

        internal static readonly IReadOnlyDictionary<string, ElementTypeHandler> ElementTypes = BuildElementTypes();

        private static ElementTypeHandler ElementType(string objectType) =>
            objectType != null && ElementTypes.TryGetValue(objectType, out var handler) ? handler : null;

        /// <summary>The types with type-specific code for an operation: create, update, read or updateAfterCreate.</summary>
        internal static IReadOnlyList<string> ElementTypesWith(string operation) => ElementTypes
            .Where(t => operation == "create" ? t.Value.Create != null
                : operation == "update" ? t.Value.Update != null
                : operation == "read" ? t.Value.Read != null
                : operation == "updateAfterCreate" && t.Value.UpdateAfterCreate)
            .Select(t => t.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

        private static Dictionary<string, ElementTypeHandler> BuildElementTypes()
        {
            var types = new Dictionary<string, ElementTypeHandler>(StringComparer.Ordinal);
            void Add(string name, ElementTypeHandler handler) => types.Add(name, handler);

            Add("AxClass", new ElementTypeHandler { Create = (b, r, s) => b.CreateClass(r, s), Update = (b, r) => b.UpdateClass(r), Read = (b, r, t, n) => b.ReadClass(r, t, n), UpdateAfterCreate = true });
            Add("AxTable", new ElementTypeHandler { Create = (b, r, s) => b.CreateTable(r, s), Update = (b, r) => b.UpdateTable(r), Read = (b, r, t, n) => b.ReadTable(r, t, n), UpdateAfterCreate = true });
            Add("AxForm", new ElementTypeHandler { Create = (b, r, s) => b.CreateForm(r, s), Update = (b, r) => b.UpdateForm(r), Read = (b, r, t, n) => b.ReadForm(r, t, n), UpdateAfterCreate = true });
            Add("AxEdt", new ElementTypeHandler { Create = (b, r, s) => b.CreateEdt(r, s), Update = (b, r) => b.UpdateEdt(r), Read = (b, r, t, n) => b.ReadEdt(r, t, n), UpdateAfterCreate = true });
            Add("AxEnum", new ElementTypeHandler { Create = (b, r, s) => b.CreateEnum(r, s), Update = (b, r) => b.UpdateEnum(r), Read = (b, r, t, n) => b.ReadEnum(r, t, n), UpdateAfterCreate = true });
            Add("AxView", new ElementTypeHandler { Create = (b, r, s) => b.CreateView(r, s), Update = (b, r) => b.UpdateView(r), Read = (b, r, t, n) => b.ReadView(r, t, n), UpdateAfterCreate = true });
            Add("AxQuery", new ElementTypeHandler { Create = (b, r, s) => b.CreateQuery(r, s), Update = (b, r) => b.UpdateQuery(r), Read = (b, r, t, n) => b.ReadQuery(r, t, n), UpdateAfterCreate = true });
            Add("AxDataEntityView", new ElementTypeHandler { Create = (b, r, s) => b.CreateDataEntityView(r, s), Update = (b, r) => b.UpdateDataEntityView(r), Read = (b, r, t, n) => b.ReadDataEntityView(r, t, n), UpdateAfterCreate = true });
            Add("AxCompositeDataEntityView", new ElementTypeHandler { Create = (b, r, s) => b.CreateCompositeDataEntityView(r, s), Update = (b, r) => b.UpdateCompositeDataEntityView(r), UpdateAfterCreate = true });
            Add("AxMenu", new ElementTypeHandler { Create = (b, r, s) => b.CreateMenu(r, s), Update = (b, r) => b.UpdateMenu(r), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetMenu(n), "Menu"), UpdateAfterCreate = true });
            Add("AxMenuItemDisplay", new ElementTypeHandler { Create = (b, r, s) => b.CreateMenuItemDisplay(r, s), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetMenuItemDisplay(n), "MenuItemDisplay") });
            Add("AxMenuItemOutput", new ElementTypeHandler { Create = (b, r, s) => b.CreateMenuItemOutput(r, s), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetMenuItemOutput(n), "MenuItemOutput") });
            Add("AxMenuItemAction", new ElementTypeHandler { Create = (b, r, s) => b.CreateMenuItemAction(r, s), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetMenuItemAction(n), "MenuItemAction") });
            Add("AxSecurityPrivilege", new ElementTypeHandler { Create = (b, r, s) => b.CreateSecurityPrivilege(r, s), Update = (b, r) => b.UpdateSecurityPrivilege(r), Read = (b, r, t, n) => b.ReadSecurityPrivilege(r, t, n), UpdateAfterCreate = true });
            Add("AxSecurityDuty", new ElementTypeHandler { Create = (b, r, s) => b.CreateSecurityDuty(r, s), Update = (b, r) => b.UpdateSecurityDuty(r), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetSecurityDuty(n), "SecurityDuty"), UpdateAfterCreate = true });
            Add("AxSecurityRole", new ElementTypeHandler { Create = (b, r, s) => b.CreateSecurityRole(r, s), Update = (b, r) => b.UpdateSecurityRole(r), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetSecurityRole(n), "SecurityRole"), UpdateAfterCreate = true });
            Add("AxService", new ElementTypeHandler { Create = (b, r, s) => b.CreateService(r, s), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetService(n), "Service") });
            Add("AxServiceGroup", new ElementTypeHandler { Create = (b, r, s) => b.CreateServiceGroup(r, s), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetServiceGroup(n), "ServiceGroup") });
            Add("AxTile", new ElementTypeHandler { Create = (b, r, s) => b.CreateTile(r, s), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetTile(n), "Tile") });
            Add("AxConfigurationKey", new ElementTypeHandler { Create = (b, r, s) => b.CreateConfigurationKey(r, s), Read = (b, r, t, n) => b.ReadGenericTyped(r, t, n, b.MetaService.GetConfigurationKey(n), "ConfigurationKey") });
            Add("AxTableExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateTableExtension(r, s), Update = (b, r) => b.UpdateTableExtension(r), Read = (b, r, t, n) => b.ReadTableExtension(r, t, n), UpdateAfterCreate = true });
            Add("AxEnumExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateEnumExtension(r, s), Update = (b, r) => b.UpdateEnumExtension(r), Read = (b, r, t, n) => b.ReadEnumExtension(r, t, n), UpdateAfterCreate = true });
            Add("AxEdtExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateEdtExtension(r, s), Update = (b, r) => b.UpdateEdtExtension(r), Read = (b, r, t, n) => b.ReadEdtExtension(r, t, n), UpdateAfterCreate = true });
            Add("AxFormExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateFormExtension(r, s), Update = (b, r) => b.UpdateFormExtension(r), Read = (b, r, t, n) => b.ReadFormExtension(r, t, n), UpdateAfterCreate = true });
            Add("AxSecurityDutyExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateSecurityDutyExtension(r, s), Update = (b, r) => b.UpdateSecurityDutyExtension(r), UpdateAfterCreate = true });
            Add("AxSecurityRoleExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateSecurityRoleExtension(r, s), Update = (b, r) => b.UpdateSecurityRoleExtension(r), UpdateAfterCreate = true });
            Add("AxDataEntityViewExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateDataEntityViewExtension(r, s), Update = (b, r) => b.UpdateDataEntityViewExtension(r), UpdateAfterCreate = true });
            // Menu extensions apply their elements while creating; a second pass through Update would add them twice.
            Add("AxMenuExtension", new ElementTypeHandler { Create = (b, r, s) => b.CreateMenuExtension(r, s), Update = (b, r) => b.UpdateMenuExtension(r) });
            Add("AxLabelFile", new ElementTypeHandler { Create = (b, r, s) => b.CreateLabelFile(r, s) });
            foreach (string viaProvider in new[] { "AxWorkflowCategory", "AxWorkflowApproval", "AxWorkflowTemplate", "AxReport" })
                Add(viaProvider, new ElementTypeHandler { Create = (b, r, s) => b.CreateViaProvider(r, s) });
            return types;
        }

        // ── Reads per type (null = the result is filled) ──

        private static ReadObjectResult NotFound(string label, string objectName) =>
            new ReadObjectResult { Message = label + " '" + objectName + "' not found." };

        /// <summary>Types whose read is the generic property dump of the typed element.</summary>
        private ReadObjectResult ReadGenericTyped(ReadObjectResult result, string objectType, string objectName, object element, string label)
        {
            if (element == null) return NotFound(label, objectName);
            PopulateReadResultFromGenericObject(result, objectType, element);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadClass(ReadObjectResult result, string objectType, string objectName)
        {
            var cls = MetaService.GetClass(objectName);
            if (cls == null) return NotFound("Class", objectName);
            result.Declaration = cls.Declaration;
            if (cls.Methods != null)
                foreach (var m in cls.Methods)
                    result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
            result.Properties = ExtractProperties(cls);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, cls);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadTable(ReadObjectResult result, string objectType, string objectName)
        {
            var tbl = MetaService.GetTable(objectName);
            if (tbl == null) return NotFound("Table", objectName);
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
            return null;
        }

        private ReadObjectResult ReadForm(ReadObjectResult result, string objectType, string objectName)
        {
            var frm = MetaService.GetForm(objectName);
            if (frm == null) return NotFound("Form", objectName);
            result.Declaration = frm.SourceCode?.Declaration;
            if (frm.Methods != null)
                foreach (var m in frm.Methods)
                    result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
            result.Properties = ExtractProperties(frm);
            result.DataSources = frm.DataSources.Select(ds => new QueryDataSourceDto
            {
                Name = ds.Name,
                Table = ds.Table,
                ParentDataSource = ds.JoinSource,
                LinkType = string.IsNullOrEmpty(ds.JoinSource) ? null : ds.LinkType.ToString()
            }).ToList();
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, frm);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadEnum(ReadObjectResult result, string objectType, string objectName)
        {
            var enm = MetaService.GetEnum(objectName);
            if (enm == null) return NotFound("Enum", objectName);
            result.Properties = ExtractProperties(enm);
            result.EnumValues = ExtractEnumValues(enm.EnumValues);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, enm);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadEdt(ReadObjectResult result, string objectType, string objectName)
        {
            var edt = MetaService.GetExtendedDataType(objectName);
            if (edt == null) return NotFound("EDT", objectName);
            result.Properties = ExtractProperties(edt);
            result.Properties["EdtType"] = edt.GetType().Name.Substring("AxEdt".Length);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, edt);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadView(ReadObjectResult result, string objectType, string objectName)
        {
            var view = MetaService.GetView(objectName);
            if (view == null) return NotFound("View", objectName);
            result.Declaration = view.Declaration;
            if (view.Methods != null)
                foreach (var m in view.Methods)
                    result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
            result.Properties = ExtractProperties(view);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, view);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadQuery(ReadObjectResult result, string objectType, string objectName)
        {
            var qry = MetaService.GetQuery(objectName);
            if (qry == null) return NotFound("Query", objectName);
            result.Properties = ExtractProperties(qry);
            result.DataSources = ExtractQueryDataSources(qry);
            foreach (var m in qry.Methods)
                result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
            if (qry is AxQuerySimple)
                result.TypedMetadataJson = BuildTypedMetadataJson(objectType, qry);
            else
            {
                // Composite query: its own ranges come from the generic graph; data sources come from the base query.
                result.TypedMetadataJson = BuildFullGraphJson(qry);
                string baseName = ReadStringProperty(qry, "Query");
                var baseQuery = string.IsNullOrEmpty(baseName) ? null : MetaService.GetQuery(baseName);
                if (baseQuery != null && result.DataSources.Count == 0)
                {
                    result.DataSources = ExtractQueryDataSources(baseQuery);
                    result.Message = "Composite query over " + baseName + ": the data sources below are the base query's; "
                        + "this query's own ranges are in the Metadata (typedMetadata) section.";
                }
            }
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadDataEntityView(ReadObjectResult result, string objectType, string objectName)
        {
            var dev = MetaService.GetDataEntityView(objectName);
            if (dev == null) return NotFound("DataEntityView", objectName);
            result.Declaration = dev.Declaration;
            if (dev.Methods != null)
                foreach (var m in dev.Methods)
                    result.Methods.Add(new MethodInfo { Name = m.Name, Source = m.Source });
            result.Properties = ExtractProperties(dev);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, dev);
            result.ModelName = GetObjectModelName(objectType, objectName);
            // Agents need the data source names and mappings to extend an entity.
            result.DataSources = ExtractQueryDataSources(dev.ViewMetadata);
            result.EntityFields = dev.Fields
                .Select(f => new EntityFieldDto
                {
                    Name = f.Name,
                    DataSource = f.GetType().GetProperty("DataSource")?.GetValue(f) as string,
                    DataField = f.GetType().GetProperty("DataField")?.GetValue(f) as string,
                })
                .ToList();
            return null;
        }

        private ReadObjectResult ReadSecurityPrivilege(ReadObjectResult result, string objectType, string objectName)
        {
            var priv = MetaService.GetSecurityPrivilege(objectName);
            if (priv == null) return NotFound("SecurityPrivilege", objectName);
            PopulateReadResultFromGenericObject(result, objectType, priv);
            result.EntryPoints = ExtractEntryPoints(priv);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadTableExtension(ReadObjectResult result, string objectType, string objectName)
        {
            var tblExt = MetaService.GetTableExtension(objectName);
            if (tblExt == null) return NotFound("TableExtension", objectName);
            result.Properties = ExtractPropertyModifications(tblExt.PropertyModifications);
            result.Fields = ExtractFields(tblExt.Fields);
            result.Indexes = ExtractIndexes(tblExt.Indexes);
            result.FieldGroups = ExtractFieldGroups(tblExt.FieldGroups);
            result.Relations = ExtractRelations(tblExt.Relations);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, tblExt);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadEnumExtension(ReadObjectResult result, string objectType, string objectName)
        {
            var enmExt = MetaService.GetEnumExtension(objectName);
            if (enmExt == null) return NotFound("EnumExtension", objectName);
            result.Properties = ExtractPropertyModifications(enmExt.PropertyModifications);
            result.EnumValues = ExtractEnumValues(enmExt.EnumValues);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, enmExt);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadEdtExtension(ReadObjectResult result, string objectType, string objectName)
        {
            var edtExt = MetaService.GetEdtExtension(objectName);
            if (edtExt == null) return NotFound("EdtExtension", objectName);
            result.Properties = ExtractPropertyModifications(edtExt.PropertyModifications);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, edtExt);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        private ReadObjectResult ReadFormExtension(ReadObjectResult result, string objectType, string objectName)
        {
            var frmExt = MetaService.GetFormExtension(objectName);
            if (frmExt == null) return NotFound("FormExtension", objectName);
            result.Properties = ExtractPropertyModifications(frmExt.PropertyModifications);
            result.DataSources = ExtractFormExtensionDataSources(frmExt);
            result.Controls = ExtractFormExtensionControls(frmExt);
            result.ControlModifications = ExtractControlModifications(frmExt);
            result.TypedMetadataJson = BuildTypedMetadataJson(objectType, frmExt);
            result.ModelName = GetObjectModelName(objectType, objectName);
            return null;
        }

        /// <summary>Any other type, through the metadata provider.</summary>
        private ReadObjectResult ReadGeneric(ReadObjectResult result, string objectType, string objectName)
        {
            var generic = ReadViaMetadataProvider(objectType, objectName, out string genericModel);
            if (generic == null)
                return new ReadObjectResult
                {
                    Message = objectType + " '" + objectName + "' not found, or '" + objectType
                        + "' is not a D365FO metadata type. Check the type and name with xpp_find_object."
                };
            PopulateReadResultFromGenericObject(result, objectType, generic);
            result.ModelName = genericModel ?? GetObjectModelName(objectType, objectName);
            return null;
        }
    }
}
