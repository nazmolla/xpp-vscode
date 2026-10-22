using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.Core.Collections;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Changing a table's or table extension's own fields, indexes, field groups and relations (updateFields, remove*).
    // A field's base type never changes, like in Visual Studio: the database sync cannot convert a column (int -> str),
    // so a field of another base type needs another name. Removed fields are remembered, so a field is not re-added
    // under its old name with another base type either (also across a delete and re-create of the whole object).
    public sealed partial class MetaModelBridge
    {
        private static string RemovedFieldsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode", "removed-fields.json");

        private static readonly object RemovedFieldsLock = new object();

        private static Dictionary<string, string> LoadRemovedFields()
        {
            lock (RemovedFieldsLock)
            {
                try
                {
                    if (File.Exists(RemovedFieldsFile))
                        return new Dictionary<string, string>(new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(RemovedFieldsFile))
                            ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                }
                catch { }
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void RememberRemovedFields(string baseTable, IEnumerable<AxTableField> fields)
        {
            var list = fields?.Where(f => f != null && !string.IsNullOrEmpty(f.Name)).ToList();
            if (list == null || list.Count == 0) return;
            lock (RemovedFieldsLock)
            {
                var map = LoadRemovedFields();
                foreach (var f in list) map[baseTable + "/" + f.Name] = BaseTypeOfField(f);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(RemovedFieldsFile));
                    File.WriteAllText(RemovedFieldsFile, new JavaScriptSerializer().Serialize(map));
                }
                catch { }
            }
        }

        /// <summary>Fields of a deleted table or table extension, so they are not re-created with another base type.</summary>
        private void RememberFieldsOfDeleted(string objectType, string objectName)
        {
            try
            {
                if (objectType == "AxTable")
                    RememberRemovedFields(objectName, MetaService.GetTable(objectName)?.Fields);
                else if (objectType == "AxTableExtension")
                    RememberRemovedFields(objectName.Split('.')[0], MetadataProvider.TableExtensions.Read(objectName)?.Fields);
            }
            catch { }
        }

        private static string BaseTypeOfField(AxTableField field) => field.GetType().Name.Replace("AxTableField", "");

        private static string BaseTypeOfFieldDto(FieldDto f)
        {
            switch ((f.FieldType ?? "String").ToLowerInvariant())
            {
                case "int": return "Int";
                case "real": return "Real";
                case "date": return "Date";
                case "datetime": case "utcdatetime": return "UtcDateTime";
                case "enum": return "Enum";
                case "int64": return "Int64";
                case "container": return "Container";
                case "guid": return "Guid";
                case "time": return "Time";
                default: return "String";
            }
        }

        /// <summary>Base type of an EDT, as a field type name (String, Int, Int64, Real, Enum, ...), or null if not found.</summary>
        private string BaseTypeOfEdt(string edtName)
        {
            if (string.IsNullOrEmpty(edtName)) return null;
            var edt = ReadViaMetadataProvider("AxEdt", edtName, out _);
            return edt?.GetType().Name.Replace("AxEdt", "");
        }

        /// <summary>Checks the table changes of a create or update request before anything is written; null when fine.</summary>
        private string ValidateTableChanges(string objectType, string objectName, List<FieldDto> addFields, UpdateObjectRequest update)
        {
            string baseTable = objectType == "AxTable" ? objectName : objectName.Split('.')[0];
            var errors = new List<string>();

            KeyedObjectCollection<AxTableField> own = null;
            if (update != null)
                own = objectType == "AxTable" ? MetaService.GetTable(objectName)?.Fields : MetadataProvider.TableExtensions.Read(objectName)?.Fields;
            var ownNames = new HashSet<string>((own ?? Enumerable.Empty<AxTableField>()).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var removing = new HashSet<string>(update?.RemoveFields ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var effective = GetEffectiveTableFields(baseTable) ?? new Dictionary<string, AxTableField>();

            foreach (string name in removing)
                if (!ownNames.Contains(name))
                    errors.Add("removeFields '" + name + "': " + (effective.ContainsKey(name)
                        ? "the field is not defined by " + objectName + " (it belongs to the base table or another extension) and cannot be removed here."
                        : "no such field."));

            var removedBefore = LoadRemovedFields();
            foreach (var f in addFields ?? new List<FieldDto>())
            {
                string type = BaseTypeOfFieldDto(f);
                if (effective.TryGetValue(f.Name, out var existing) && !removing.Contains(f.Name))
                {
                    errors.Add("fields '" + f.Name + "': the field already exists on " + baseTable
                        + (ownNames.Contains(f.Name) ? "; change it with updateFields." : " (defined by the base table or another extension)."));
                    continue;
                }
                string previousType = removing.Contains(f.Name) && Find(own, f.Name) != null ? BaseTypeOfField(Find(own, f.Name))
                    : removedBefore.TryGetValue(baseTable + "/" + f.Name, out string t) ? t : null;
                if (previousType != null && !string.Equals(previousType, type, StringComparison.OrdinalIgnoreCase))
                    errors.Add("fields '" + f.Name + "': a field " + baseTable + "." + f.Name + " of type " + previousType
                        + " existed before; re-adding it as " + type + " breaks the database synchronization (the column cannot change type). Use another field name.");
                string edtType = BaseTypeOfEdt(f.ExtendedDataType);
                if (!string.IsNullOrEmpty(f.ExtendedDataType) && edtType == null)
                    errors.Add("fields '" + f.Name + "': EDT '" + f.ExtendedDataType + "' not found.");
                else if (edtType != null && !string.Equals(edtType, type, StringComparison.OrdinalIgnoreCase))
                    errors.Add("fields '" + f.Name + "': EDT '" + f.ExtendedDataType + "' is " + edtType + " but fieldType is " + type + ".");
            }

            foreach (var u in update?.UpdateFields ?? new List<FieldDto>())
            {
                string label = "updateFields '" + u.Name + "'";
                if (string.IsNullOrEmpty(u.Name)) { errors.Add("updateFields: name is required."); continue; }
                if (!ownNames.Contains(u.Name))
                {
                    errors.Add(label + ": " + (effective.ContainsKey(u.Name)
                        ? "the field is not defined by " + objectName + " (base table or another extension); only fields this object defines can be changed."
                        : "no such field on " + objectName + "."));
                    continue;
                }
                var field = Find(own, u.Name);
                string current = BaseTypeOfField(field);
                if (!string.IsNullOrEmpty(u.FieldType) && !string.Equals(BaseTypeOfFieldDto(u), current, StringComparison.OrdinalIgnoreCase))
                    errors.Add(label + ": the base type cannot change (" + current + " -> " + BaseTypeOfFieldDto(u)
                        + "): the database synchronization cannot convert the column. Add a new field with another name (fields) and remove this one (removeFields).");
                if (!string.IsNullOrEmpty(u.ExtendedDataType))
                {
                    string edtType = BaseTypeOfEdt(u.ExtendedDataType);
                    if (edtType == null)
                        errors.Add(label + ": EDT '" + u.ExtendedDataType + "' not found.");
                    else if (!string.Equals(edtType, current, StringComparison.OrdinalIgnoreCase))
                        errors.Add(label + ": EDT '" + u.ExtendedDataType + "' is " + edtType + " but the field is " + current
                            + "; the base type cannot change (database synchronization). Use an EDT of type " + current
                            + ", or add a new field with another name and remove this one.");
                }
                if (!string.IsNullOrEmpty(u.EnumType) && !(field is AxTableFieldEnum))
                    errors.Add(label + ": enumType only applies to Enum fields (this one is " + current + ").");
                foreach (var key in (u.Properties ?? new Dictionary<string, string>()).Keys)
                {
                    var prop = FindProperty(field.GetType(), key);
                    if (prop == null || !prop.CanWrite || key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        errors.Add(label + ": " + field.GetType().Name + " has no settable property '" + key + "'.");
                }
            }

            if (update != null)
            {
                object target = objectType == "AxTable" ? (object)MetaService.GetTable(objectName) : MetadataProvider.TableExtensions.Read(objectName);
                // Indexes and relations of this object that still use a removed field (unless removed or replaced too).
                var dropped = new HashSet<string>((update.RemoveIndexes ?? new List<string>())
                    .Concat((update.Indexes ?? new List<IndexDto>()).Select(i => i.Name))
                    .Concat(update.RemoveRelations ?? new List<string>())
                    .Concat((update.Relations ?? new List<RelationDto>()).Select(r => r.Name)), StringComparer.OrdinalIgnoreCase);
                if (removing.Count > 0)
                {
                    foreach (var idx in (target?.GetType().GetProperty("Indexes")?.GetValue(target) as IEnumerable<AxTableIndex>) ?? Enumerable.Empty<AxTableIndex>())
                        if (!dropped.Contains(idx.Name))
                            foreach (var f in idx.Fields.Where(f => removing.Contains(f.DataField ?? f.Name)))
                                errors.Add("removeFields '" + (f.DataField ?? f.Name) + "': index " + idx.Name + " uses it; remove the index (removeIndexes) or give it again without the field (indexes).");
                    foreach (var rel in (target?.GetType().GetProperty("Relations")?.GetValue(target) as IEnumerable<AxTableRelation>) ?? Enumerable.Empty<AxTableRelation>())
                        if (!dropped.Contains(rel.Name))
                            foreach (var c in rel.Constraints.OfType<AxTableRelationConstraintField>().Where(c => removing.Contains(c.Field ?? "")))
                                errors.Add("removeFields '" + c.Field + "': relation " + rel.Name + " uses it; remove the relation (removeRelations) or replace it (relations).");
                }
                CheckRemovals(target, "Indexes", update.RemoveIndexes, "removeIndexes", errors);
                CheckRemovals(target, "FieldGroups", update.RemoveFieldGroups, "removeFieldGroups", errors);
                CheckRemovals(target, "Relations", update.RemoveRelations, "removeRelations", errors);
            }
            return errors.Count == 0 ? null : "Nothing was changed:\n  - " + string.Join("\n  - ", errors);
        }

        // KeyedObjectCollection's indexer throws for unknown names.
        private static T Find<T>(IEnumerable<T> items, string name) where T : class =>
            string.IsNullOrEmpty(name) || items == null ? null
                : items.FirstOrDefault(i => string.Equals(i?.GetType().GetProperty("Name")?.GetValue(i) as string, name, StringComparison.OrdinalIgnoreCase));

        private static void CheckRemovals(object target, string collection, List<string> names, string parameter, List<string> errors)
        {
            if (names == null || names.Count == 0) return;
            var items = (target?.GetType().GetProperty(collection)?.GetValue(target) as System.Collections.IEnumerable)?.Cast<object>()
                .Select(o => o.GetType().GetProperty("Name")?.GetValue(o) as string).ToList() ?? new List<string>();
            foreach (string name in names.Where(n => !items.Contains(n, StringComparer.OrdinalIgnoreCase)))
                errors.Add(parameter + " '" + name + "': not defined by this object.");
        }

        /// <summary>Applies removals and field updates; indexes, field groups and relations given again by name are replaced.</summary>
        private List<string> ApplyTableChanges(object target, string baseTable, UpdateObjectRequest req)
        {
            var report = new List<string>();
            var fields = target.GetType().GetProperty("Fields")?.GetValue(target) as KeyedObjectCollection<AxTableField>;
            var indexes = target.GetType().GetProperty("Indexes")?.GetValue(target) as KeyedObjectCollection<AxTableIndex>;
            var groups = target.GetType().GetProperty("FieldGroups")?.GetValue(target) as KeyedObjectCollection<AxTableFieldGroup>;
            var relations = target.GetType().GetProperty("Relations")?.GetValue(target) as KeyedObjectCollection<AxTableRelation>;

            foreach (string name in req.RemoveRelations ?? new List<string>()) if (Find(relations, name) != null && relations.Remove(Find(relations, name).Name)) report.Add("Relation removed: " + name);
            foreach (string name in req.RemoveFieldGroups ?? new List<string>()) if (Find(groups, name) != null && groups.Remove(Find(groups, name).Name)) report.Add("Field group removed: " + name);
            foreach (string name in req.RemoveIndexes ?? new List<string>()) if (Find(indexes, name) != null && indexes.Remove(Find(indexes, name).Name)) report.Add("Index removed: " + name);
            if (fields != null && req.RemoveFields != null && req.RemoveFields.Count > 0)
            {
                var removed = req.RemoveFields.Select(n => Find(fields, n)).Where(f => f != null).ToList();
                RememberRemovedFields(baseTable, removed);
                foreach (var f in removed)
                {
                    fields.Remove(f.Name);
                    report.Add("Field removed: " + f.Name + " (" + BaseTypeOfField(f) + ")");
                    // References inside this object would break the build.
                    foreach (var g in groups ?? Enumerable.Empty<AxTableFieldGroup>())
                    {
                        var member = g.Fields.FirstOrDefault(m => string.Equals(m.DataField ?? m.Name, f.Name, StringComparison.OrdinalIgnoreCase));
                        if (member != null && g.Fields.Remove(member.Name)) report.Add("  removed from field group " + g.Name);
                    }
                }
            }

            foreach (var name in (req.Indexes ?? new List<IndexDto>()).Select(i => i.Name).Where(n => Find(indexes, n) != null).ToList())
            { indexes.Remove(Find(indexes, name).Name); report.Add("Index replaced: " + name); }
            foreach (var name in (req.FieldGroups ?? new List<FieldGroupDto>()).Select(g => g.Name).Where(n => Find(groups, n) != null).ToList())
            { groups.Remove(Find(groups, name).Name); report.Add("Field group replaced: " + name); }
            foreach (var name in (req.Relations ?? new List<RelationDto>()).Select(r => r.Name).Where(n => Find(relations, n) != null).ToList())
            { relations.Remove(Find(relations, name).Name); report.Add("Relation replaced: " + name); }

            foreach (var u in req.UpdateFields ?? new List<FieldDto>())
            {
                var field = Find(fields, u.Name);
                if (field == null) continue;
                var changes = new List<string>();
                if (!string.IsNullOrEmpty(u.ExtendedDataType))
                {
                    field.GetType().GetProperty("ExtendedDataType")?.SetValue(field, u.ExtendedDataType);
                    changes.Add("EDT=" + u.ExtendedDataType);
                }
                if (field is AxTableFieldEnum enumField && !string.IsNullOrEmpty(u.EnumType)) { enumField.EnumType = u.EnumType; changes.Add("EnumType=" + u.EnumType); }
                if (u.Label != null) { field.Label = u.Label; changes.Add("Label=" + u.Label); }
                if (u.HelpText != null) { field.HelpText = u.HelpText; changes.Add("HelpText=" + u.HelpText); }
                if (u.Properties != null && u.Properties.Count > 0)
                {
                    ApplyProperties(field, u.Properties);
                    changes.AddRange(u.Properties.Select(p => p.Key + "=" + p.Value));
                }
                report.Add("Field updated: " + u.Name + (changes.Count > 0 ? " (" + string.Join(", ", changes) + ")" : ""));
            }
            return report;
        }
    }
}
