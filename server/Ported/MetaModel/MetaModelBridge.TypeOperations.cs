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
    /// <summary>Create and update of each element type (dispatched through the element type table).</summary>
    public sealed partial class MetaModelBridge
    {
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
            var form = new AxForm { Name = req.ObjectName };
            EnsureDeclaration(form, "AxForm");
            MetaService.CreateForm(form, saveInfo);
            return Ok("Created AxForm '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateEdt(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string kind = null;
            req.Properties?.TryGetValue("EdtType", out kind);
            string extends = null;
            req.Properties?.TryGetValue("Extends", out extends);
            if (string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(extends))
            {
                var parent = MetaService.GetExtendedDataType(extends);
                if (parent == null) return Fail("Extends: EDT '" + extends + "' does not exist.");
                kind = parent.GetType().Name.Substring("AxEdt".Length);
            }
            if (string.IsNullOrEmpty(kind))
                kind = req.Properties != null && req.Properties.ContainsKey("EnumType") ? "Enum" : "String";
            if (kind.Equals("DateTime", StringComparison.OrdinalIgnoreCase)) kind = "UtcDateTime";

            Type edtType = typeof(AxEdt).Assembly.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(AxEdt).IsAssignableFrom(t) && t.Name.Equals("AxEdt" + kind, StringComparison.OrdinalIgnoreCase));
            if (edtType == null)
                return Fail("Unknown EdtType '" + kind + "'. Use String, Int, Int64, Real, Enum, Date, UtcDateTime, Time, Guid or Container.");

            var edt = (AxEdt)Activator.CreateInstance(edtType);
            edt.Name = req.ObjectName;
            MetaService.CreateExtendedDataType(edt, saveInfo);
            return Ok("Created AxEdt '" + req.ObjectName + "' (" + edtType.Name.Substring("AxEdt".Length) + ").");
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
            var query = new AxQuerySimple { Name = req.ObjectName };
            EnsureDeclaration(query, "AxQuery");
            MetaService.CreateQuery(query, saveInfo);
            return Ok("Created AxQuery '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateView(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            var view = new AxView { Name = req.ObjectName };
            EnsureDeclaration(view, "AxView");
            MetaService.CreateView(view, saveInfo);
            return Ok("Created AxView '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateDataEntityView(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            // DataEntityView has no Create API; UpdateDataEntityView creates-or-updates.
            var entity = new AxDataEntityView { Name = req.ObjectName };
            EnsureDeclaration(entity, "AxDataEntityView");
            MetaService.UpdateDataEntityView(entity, saveInfo);
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

        /// <summary>Workflow categories, types and approvals and reports: built from typedMetadata/properties, saved by their provider's Create.</summary>
        private MetaModelResult CreateViaProvider(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            Type type = ResolveMetadataType(req.ObjectType);
            object sub = SubProvider(req.ObjectType, out Type providerType);
            var create = sub == null || type == null ? null : FindProviderMethod(providerType, "Create", type, typeof(ModelSaveInfo));
            if (create == null) return Fail("The metadata API can't create " + req.ObjectType + ".");
            object element = Activator.CreateInstance(type);
            type.GetProperty("Name")?.SetValue(element, req.ObjectName);
            ApplyTypedMetadata(element, req);
            type.GetProperty("Name")?.SetValue(element, req.ObjectName);
            create.Invoke(sub, new[] { element, saveInfo });
            return Ok("Created " + req.ObjectType + " '" + req.ObjectName + "'.");
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
                return Fail("ApplyTypedMetadata failed for AxTable '" + req.ObjectName + "': " + Cause(ex));
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
            // The compiler rejects extensible enums with explicit values.
            if (enm.IsExtensible)
                TrySetPropertyValue(enm, "No", "UseEnumValue");
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
            EnsureDeclaration(qry, "AxQuery");
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

        // Simple metadata types (menu items, services, tiles, configuration keys, ...): MetaService.Get<X>/Update<X> by reflection.
        private MetaModelResult UpdateGenericByTypedApi(UpdateObjectRequest req)
        {
            string core = req.ObjectType.StartsWith("Ax", StringComparison.Ordinal) ? req.ObjectType.Substring(2) : req.ObjectType;
            Type metaType = ResolveMetadataType(req.ObjectType);
            var svcType = MetaService.GetType();
            var getter = svcType.GetMethod("Get" + core, new[] { typeof(string) });
            var updater = metaType == null ? null : svcType.GetMethods().FirstOrDefault(m => m.Name == "Update" + core
                && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.IsAssignableFrom(metaType));
            if (getter == null || updater == null)
                return Fail("Update for type '" + req.ObjectType + "' is not supported.");

            object obj = getter.Invoke(MetaService, new object[] { req.ObjectName });
            if (obj == null)
                return Fail(req.ObjectType + " '" + req.ObjectName + "' not found.");
            var saveInfo = GetModelSaveInfoForObject(req.ObjectType, req.ObjectName);
            if (saveInfo == null) return Fail("Cannot resolve model for '" + req.ObjectName + "'.");

            ApplyTypedMetadata(obj, req);
            updater.Invoke(MetaService, new object[] { obj, saveInfo });
            return Ok("Updated " + req.ObjectType + " '" + req.ObjectName + "'.");
        }
    }
}
