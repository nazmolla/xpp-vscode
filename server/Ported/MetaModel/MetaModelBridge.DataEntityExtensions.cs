using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Storage;

namespace XppVsCode.Server.MetaModel
{
    // Data entity extensions: mapped fields on the entity plus the same fields on its staging table.
    public sealed partial class MetaModelBridge
    {
        private sealed class EntityFieldPlan
        {
            public EntityFieldDto Field;
            public string Table;
            public AxTableField SourceField;
            public bool AlreadyInExtension;
        }

        private MetaModelResult CreateDataEntityViewExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxDataEntityView",
                baseName => MetaService.GetDataEntityView(baseName) != null,
                () => MetadataProvider.DataEntityViewExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            MetadataProvider.DataEntityViewExtensions.Create(new AxDataEntityViewExtension { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxDataEntityViewExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateDataEntityViewExtension(UpdateObjectRequest req)
        {
            if (MetadataProvider == null) return Fail("D365FO metadata provider not available; cannot update extensions.");

            var ext = MetadataProvider.DataEntityViewExtensions.Read(req.ObjectName);
            if (ext == null) return Fail("DataEntityViewExtension '" + req.ObjectName + "' not found.");

            var saveInfo = GetExtensionSaveInfo("AxDataEntityViewExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);

            var entity = MetaService.GetDataEntityView(ExtensionBaseName(req.ObjectName));
            if (entity == null) return Fail("Data entity '" + ExtensionBaseName(req.ObjectName) + "' not found.");

            var plan = PlanEntityFields(entity, req.ObjectName, ext, req.EntityFields, out error);
            if (plan == null) return Fail(error);

            foreach (var p in plan.Where(p => !p.AlreadyInExtension))
            {
                var field = new AxDataEntityViewMappedField
                {
                    Name = p.Field.Name,
                    DataSource = p.Field.DataSource,
                    DataField = p.Field.DataField
                };
                if (!string.IsNullOrEmpty(p.Field.Label)) field.Label = p.Field.Label;
                if (p.Field.Mandatory.HasValue) field.Mandatory = p.Field.Mandatory.Value ? AutoNoYes.Yes : AutoNoYes.No;
                ext.Fields.Add(field);
            }

            ApplyPropertyModifications(ext, req.Properties);
            MetadataProvider.DataEntityViewExtensions.Update(ext, saveInfo);

            var report = new StringBuilder("Updated AxDataEntityViewExtension '" + req.ObjectName + "'.");
            var added = plan.Where(p => !p.AlreadyInExtension).ToList();
            if (added.Count > 0)
                report.Append("\nEntity fields added: " + string.Join(", ", added.Select(p => p.Field.Name + " (" + p.Field.DataSource + "." + p.Field.DataField + ")")));
            var present = plan.Where(p => p.AlreadyInExtension).ToList();
            if (present.Count > 0)
                report.Append("\nAlready in this extension: " + string.Join(", ", present.Select(p => p.Field.Name)));

            if (plan.Count > 0)
            {
                report.Append("\n");
                report.Append(req.SkipStagingTable
                    ? "Staging table: skipped (skipStagingTable=true). Data management import/export will not carry these fields until the staging table has them."
                    : ExtendStagingTable(entity, req.ObjectName, plan, saveInfo));
            }
            return Ok(report.ToString());
        }

        /// <summary>Resolves every requested field to a data source table field, before anything is saved.</summary>
        private List<EntityFieldPlan> PlanEntityFields(AxDataEntityView entity, string extensionName,
            AxDataEntityViewExtension currentExtension, List<EntityFieldDto> fields, out string error)
        {
            error = null;
            var plan = new List<EntityFieldPlan>();
            if (fields == null) return plan;

            string baseName = entity.Name;
            var dataSources = CollectEntityDataSources(entity);

            // Field names already used by the entity or by other extensions of it.
            var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in entity.Fields) taken[f.Name] = "the entity itself";
            foreach (string key in MetadataProvider.DataEntityViewExtensions.GetPrimaryKeys()
                         .Where(k => k.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(k, extensionName, StringComparison.OrdinalIgnoreCase)))
            {
                var other = MetadataProvider.DataEntityViewExtensions.Read(key);
                if (other == null) continue;
                foreach (var f in other.Fields) taken[f.Name] = "extension " + key;
                foreach (var kv in CollectDataSources(other.DataSources)) dataSources[kv.Key] = kv.Value;
            }
            if (currentExtension != null)
                foreach (var kv in CollectDataSources(currentExtension.DataSources)) dataSources[kv.Key] = kv.Value;

            var errors = new List<string>();
            foreach (var dto in fields)
            {
                if (taken.TryGetValue(dto.Name, out string owner))
                {
                    errors.Add("Field '" + dto.Name + "' already exists on " + owner + "; choose another name.");
                    continue;
                }
                if (string.IsNullOrEmpty(dto.DataSource) || string.IsNullOrEmpty(dto.DataField))
                {
                    errors.Add("Field '" + dto.Name + "': dataSource and dataField are required.");
                    continue;
                }
                if (!dataSources.TryGetValue(dto.DataSource, out string table))
                {
                    errors.Add("Field '" + dto.Name + "': data source '" + dto.DataSource + "' is not on entity " + baseName
                        + ". Available: " + string.Join(", ", dataSources.Select(kv => kv.Key + " (" + kv.Value + ")")) + ".");
                    continue;
                }
                var source = FindTableField(table, dto.DataField);
                if (source == null)
                {
                    errors.Add("Field '" + dto.Name + "': field '" + dto.DataField + "' not found on table " + table
                        + " (data source " + dto.DataSource + ") or its extensions. Add it with an AxTableExtension first.");
                    continue;
                }
                bool inExtension = currentExtension != null
                    && currentExtension.Fields.Any(f => string.Equals(f.Name, dto.Name, StringComparison.OrdinalIgnoreCase));
                plan.Add(new EntityFieldPlan { Field = dto, Table = table, SourceField = source, AlreadyInExtension = inExtension });
            }

            if (errors.Count > 0)
            {
                error = string.Join("\n", errors);
                return null;
            }
            return plan;
        }

        /// <summary>Adds the entity's new fields to an extension of its data management staging table.</summary>
        private string ExtendStagingTable(AxDataEntityView entity, string extensionName, List<EntityFieldPlan> plan, ModelSaveInfo saveInfo)
        {
            string staging = entity.DataManagementStagingTable;
            if (entity.DataManagementEnabled != NoYes.Yes || string.IsNullOrEmpty(staging))
                return "Staging table: entity " + entity.Name + " has no data management staging table; nothing to extend.";
            if (MetaService.GetTable(staging) == null)
                return "Staging table: '" + staging + "' not found; extend it manually with the same fields.";

            string stagingExtName = staging + "." + ExtensionSuffix(extensionName);
            var tableExt = MetaService.GetTableExtension(stagingExtName);
            bool created = tableExt == null;
            ModelSaveInfo stagingSaveInfo = saveInfo;
            if (created)
            {
                MetadataProvider.TableExtensions.Create(new AxTableExtension { Name = stagingExtName }, saveInfo);
                tableExt = MetadataProvider.TableExtensions.Read(stagingExtName);
            }
            else
            {
                stagingSaveInfo = GetExtensionSaveInfo("AxTableExtension", stagingExtName, out string error);
                if (stagingSaveInfo == null) return "Staging table: " + error;
            }

            var added = new List<string>();
            var skipped = new List<string>();
            foreach (var p in plan)
            {
                if (FindTableField(staging, p.Field.Name) != null)
                {
                    skipped.Add(p.Field.Name);
                    continue;
                }
                // Same field class, EDT/enum and size as the source field; staging rows are never mandatory.
                var copy = ObjectCloner.MakeCopy(p.SourceField);
                copy.Name = p.Field.Name;
                copy.Mandatory = NoYes.No;
                tableExt.Fields.Add(copy);
                added.Add(p.Field.Name);
            }
            if (added.Count > 0)
                MetadataProvider.TableExtensions.Update(tableExt, stagingSaveInfo);

            var sb = new StringBuilder("Staging table " + staging + ": " + (created ? "created" : "updated")
                + " AxTableExtension '" + stagingExtName + "'");
            sb.Append(added.Count > 0 ? "; fields added: " + string.Join(", ", added) : "; no fields added");
            if (skipped.Count > 0) sb.Append("; already present: " + string.Join(", ", skipped));
            sb.Append(".");
            if (created)
            {
                var addResult = AddToProject("AxTableExtension", stagingExtName);
                sb.Append(addResult.Success
                    ? " Added to the active project."
                    : " Add '" + stagingExtName + "' to the project yourself: " + addResult.Message);
            }
            return sb.ToString();
        }

        /// <summary>Data source name to table, for the entity's own query.</summary>
        private static Dictionary<string, string> CollectEntityDataSources(AxDataEntityView entity) =>
            CollectDataSources(entity.ViewMetadata?.DataSources);

        // Walks nested query data sources by reflection: root, embedded and extension data source types differ.
        private static Dictionary<string, string> CollectDataSources(System.Collections.IEnumerable dataSources)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (dataSources == null) return result;
            foreach (object item in dataSources)
            {
                object ds = item?.GetType().GetProperty("DataSource")?.GetValue(item) ?? item;
                if (ds == null) continue;
                string name = ds.GetType().GetProperty("Name")?.GetValue(ds) as string;
                string table = ds.GetType().GetProperty("Table")?.GetValue(ds) as string;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(table))
                    result[name] = table;
                var children = ds.GetType().GetProperty("DataSources")?.GetValue(ds) as System.Collections.IEnumerable;
                foreach (var kv in CollectDataSources(children))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>A field on the table or on any of its extensions.</summary>
        private AxTableField FindTableField(string tableName, string fieldName)
        {
            var table = MetaService.GetTable(tableName);
            var field = table?.Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            if (field != null) return field;

            foreach (string key in MetadataProvider.TableExtensions.GetPrimaryKeys()
                         .Where(k => k.StartsWith(tableName + ".", StringComparison.OrdinalIgnoreCase)))
            {
                field = MetadataProvider.TableExtensions.Read(key)?.Fields
                    .FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
                if (field != null) return field;
            }
            return null;
        }

        private static string ExtensionBaseName(string extensionName)
        {
            int dot = extensionName.IndexOf('.');
            return dot > 0 ? extensionName.Substring(0, dot) : extensionName;
        }

        private static string ExtensionSuffix(string extensionName)
        {
            int dot = extensionName.IndexOf('.');
            return dot > 0 ? extensionName.Substring(dot + 1) : extensionName;
        }

        /// <summary>Checks a create request before the blank object is saved, so failures leave nothing behind.</summary>
        private string PreValidateCreate(CreateObjectRequest request)
        {
            if (IsSecurityType(request.ObjectType))
                return ValidateSecurityRequest(request.ToUpdateRequest());

            if (request.ObjectType == "AxDataEntityViewExtension" && request.EntityFields != null && MetadataProvider != null)
            {
                var entity = MetaService.GetDataEntityView(ExtensionBaseName(request.ObjectName));
                if (entity == null) return null; // ValidateNewExtension reports the missing base entity.
                return PlanEntityFields(entity, request.ObjectName, null, request.EntityFields, out string error) == null ? error : null;
            }
            return null;
        }
    }
}
