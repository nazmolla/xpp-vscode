using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Storage;

namespace XppVsCode.Server.MetaModel
{
    // Composite data entities (AxCompositeDataEntityView): a root data entity reference with embedded entity
    // references, each linked to its parent through a relation on the child entity. Data management only (XML).
    public sealed partial class MetaModelBridge
    {
        private MetaModelResult CreateCompositeDataEntityView(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            if (MetadataProvider == null) return Fail("D365FO metadata provider not available.");
            string declaration = "[CompositeDataEntityView]\r\npublic class " + req.ObjectName + "\r\n{\r\n}\r\n";
            if (!string.IsNullOrEmpty(req.Declaration)) declaration = PrepareSource(req.Declaration, req.FormatCode);
            MetadataProvider.CompositeDataEntityViews.Create(
                new AxCompositeDataEntityView { Name = req.ObjectName, Declaration = declaration }, saveInfo);
            return Ok("Created AxCompositeDataEntityView '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateCompositeDataEntityView(UpdateObjectRequest req)
        {
            if (MetadataProvider == null) return Fail("D365FO metadata provider not available.");
            var composite = MetadataProvider.CompositeDataEntityViews.Read(req.ObjectName);
            if (composite == null) return Fail("AxCompositeDataEntityView '" + req.ObjectName + "' not found.");

            string model = ModelOf("AxCompositeDataEntityView", req.ObjectName);
            if (!IsCustomModel(model))
                return Fail("AxCompositeDataEntityView '" + req.ObjectName + "' belongs to model '" + model
                    + "', which is not one of your custom models. Composite entities cannot be extended; create your own instead.");
            var saveInfo = GetModelSaveInfo(model);
            if (saveInfo == null) return Fail("Cannot resolve model '" + model + "'.");

            if (!string.IsNullOrEmpty(req.Declaration))
                composite.Declaration = PrepareSource(req.Declaration, req.FormatCode);
            ApplyTypedMetadata(composite, req);

            var errors = new List<string>();
            var warnings = new List<string>();
            var roots = composite.RootDataEntities.Cast<AxDataEntityViewReference>().ToList();
            if (roots.Count > 1)
                errors.Add("A composite entity has one root data entity reference; found " + roots.Count + ".");
            foreach (var root in roots)
                CheckCompositeReference(root, null, errors, warnings);
            if (errors.Count > 0)
                return Fail("Composite entity '" + req.ObjectName + "' not saved:\n- " + string.Join("\n- ", errors));

            MetadataProvider.CompositeDataEntityViews.Update(composite, saveInfo);

            var report = new StringBuilder("Updated AxCompositeDataEntityView '" + req.ObjectName + "'.");
            if (roots.Count > 0)
                report.Append("\nStructure: " + string.Join("; ", roots.Select(DescribeReference)));
            if (warnings.Count > 0)
                report.Append("\nStaging/data management checks (fix before importing):\n- " + string.Join("\n- ", warnings));
            else if (roots.Count > 0)
                report.Append("\nStaging/data management checks: OK. After build and sync, refresh the entity list (Data management parameters > Entity settings) before testing.");
            return Ok(report.ToString());
        }

        private static string DescribeReference(AxDataEntityViewReference r)
        {
            var children = r.EmbeddedDataEntities.Cast<AxDataEntityViewReferenceEmbedded>().ToList();
            return r.Name + "=" + r.DataEntity + (children.Count == 0 ? "" : " [" + string.Join(", ", children.Select(DescribeReference)) + "]");
        }

        /// <summary>
        /// Checks one reference and its embedded references: entities exist, every embedded reference names a relation on
        /// its own entity that points to the parent entity, and the staging tables have what composite import needs.
        /// </summary>
        private void CheckCompositeReference(AxDataEntityViewReference reference, AxDataEntityViewReference parent,
            List<string> errors, List<string> warnings)
        {
            string label = "'" + reference.Name + "'";
            if (string.IsNullOrEmpty(reference.Name)) errors.Add("Every data entity reference needs a Name.");
            var entity = string.IsNullOrEmpty(reference.DataEntity) ? null : MetaService.GetDataEntityView(reference.DataEntity);
            if (entity == null)
            {
                errors.Add("Reference " + label + ": data entity '" + reference.DataEntity + "' not found.");
                return;
            }

            var embedded = reference as AxDataEntityViewReferenceEmbedded;
            if (parent != null && embedded != null)
            {
                if (string.IsNullOrEmpty(embedded.Relation))
                {
                    errors.Add("Embedded reference " + label + ": Relation is required (a relation on " + entity.Name + " to " + parent.DataEntity + ").");
                }
                else
                {
                    var relations = EntityRelations(entity.Name).ToList();
                    var relation = relations.FirstOrDefault(r => string.Equals(r.Name, embedded.Relation, StringComparison.OrdinalIgnoreCase));
                    var toParent = relations.Where(r => string.Equals(r.RelatedDataEntity, parent.DataEntity, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).ToList();
                    if (relation == null)
                        errors.Add("Embedded reference " + label + ": relation '" + embedded.Relation + "' not found on " + entity.Name
                            + (toParent.Count > 0 ? ". Relations to " + parent.DataEntity + ": " + string.Join(", ", toParent) + "." : ". It has no relation to " + parent.DataEntity + "; add one first."));
                    else if (!string.Equals(relation.RelatedDataEntity, parent.DataEntity, StringComparison.OrdinalIgnoreCase))
                        errors.Add("Embedded reference " + label + ": relation '" + relation.Name + "' points to " + relation.RelatedDataEntity
                            + ", not to the parent " + parent.DataEntity + (toParent.Count > 0 ? ". Use one of: " + string.Join(", ", toParent) + "." : "."));
                }
            }

            CheckCompositeStaging(entity, parent == null ? null : MetaService.GetDataEntityView(parent.DataEntity), warnings);

            foreach (var child in reference.EmbeddedDataEntities.Cast<AxDataEntityViewReference>())
                CheckCompositeReference(child, reference, errors, warnings);
        }

        private IEnumerable<AxDataEntityViewRelation> EntityRelations(string entityName)
        {
            var entity = MetaService.GetDataEntityView(entityName);
            if (entity?.Relations != null)
                foreach (var r in entity.Relations) yield return r;
            foreach (string key in MetadataProvider.DataEntityViewExtensions.GetPrimaryKeys()
                         .Where(k => k.StartsWith(entityName + ".", StringComparison.OrdinalIgnoreCase)))
            {
                var ext = MetadataProvider.DataEntityViewExtensions.Read(key);
                if (ext?.Relations != null)
                    foreach (var r in ext.Relations) yield return r;
            }
        }

        private void CheckCompositeStaging(AxDataEntityView entity, AxDataEntityView parentEntity, List<string> warnings)
        {
            string staging = entity.DataManagementStagingTable;
            if (entity.DataManagementEnabled != NoYes.Yes || string.IsNullOrEmpty(staging))
            {
                warnings.Add(entity.Name + ": data management is not enabled or it has no staging table; composite import/export needs both.");
                return;
            }
            if (MetaService.GetTable(staging) == null)
            {
                warnings.Add(entity.Name + ": staging table '" + staging + "' not found.");
                return;
            }

            var missing = new[] { "RowId", "ParentRowId" }.Where(f => FindTableField(staging, f) == null).ToList();
            if (missing.Count > 0)
                warnings.Add("Staging table " + staging + " lacks " + string.Join(" and ", missing)
                    + " (Int; copy them from SysCompositeHeaderStaging). Composite import uses them to link parent and child rows.");

            var table = MetaService.GetTable(staging);
            bool hasIndex = table.Indexes.Any(ix =>
                new[] { "RowId", "ParentRowId", "DefinitionGroup", "ExecutionId" }.All(f =>
                    ix.Fields.Any(x => string.Equals(x.DataField, f, StringComparison.OrdinalIgnoreCase))));
            if (missing.Count == 0 && !hasIndex)
                warnings.Add("Staging table " + staging + ": no index on RowId, ParentRowId, DefinitionGroup and ExecutionId; the documentation recommends one as the cluster index for performance.");

            string parentStaging = parentEntity?.DataManagementStagingTable;
            if (!string.IsNullOrEmpty(parentStaging) && !StagingRelationExists(staging, parentStaging))
                warnings.Add("Staging table " + staging + " has no relation to the parent staging table " + parentStaging
                    + " (on the natural key plus DefinitionGroup and ExecutionId).");
        }

        private bool StagingRelationExists(string staging, string parentStaging)
        {
            bool Points(IEnumerable<AxTableRelation> rels) =>
                rels != null && rels.Any(r => string.Equals(r.RelatedTable, parentStaging, StringComparison.OrdinalIgnoreCase));
            if (Points(MetaService.GetTable(staging)?.Relations)) return true;
            return MetadataProvider.TableExtensions.GetPrimaryKeys()
                .Where(k => k.StartsWith(staging + ".", StringComparison.OrdinalIgnoreCase))
                .Any(k => Points(MetadataProvider.TableExtensions.Read(k)?.Relations));
        }
    }
}
