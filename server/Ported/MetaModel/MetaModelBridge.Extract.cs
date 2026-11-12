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
    /// <summary>Reading element metadata into the result DTOs.</summary>
    public sealed partial class MetaModelBridge
    {
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

            // A fresh instance carries the real defaults (e.g. UseEnumValue defaults to Yes, so No must be shown).
            object defaults = null;
            try { defaults = Activator.CreateInstance(axObj.GetType()); } catch { }

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
                    if (underlying == typeof(string) && string.IsNullOrEmpty((string)val)) continue;
                    // Skip defaults to keep output clean
                    if (defaults != null)
                    {
                        if (Equals(val, prop.GetValue(defaults))) continue;
                    }
                    else
                    {
                        if (underlying == typeof(bool) && !(bool)val) continue;
                        if (underlying == typeof(int) && (int)val == 0) continue;
                        if (underlying == typeof(long) && (long)val == 0) continue;
                        if (underlying.IsEnum && Convert.ToInt32(val) == 0) continue;
                    }
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
                if (fieldType.StartsWith("AxTableField", StringComparison.Ordinal))
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
            if (ds.GetType().GetProperty("Fields")?.GetValue(ds) is System.Collections.IEnumerable dsFields)
            {
                var names = dsFields.Cast<object>().Select(f => ReadStringProperty(f, "Field", "DataField", "Name"))
                    .Where(n => !string.IsNullOrEmpty(n)).ToList();
                if (names.Count > 0) dto.Fields = names;
            }

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
            var info = MetaService?.GetModel(modelName);
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            // Ownership is where the model lives: Customization=DoNotAllow only seals a model against others' overlayering,
            // and partners seal their own models too.
            if (info != null && config.HasCustom)
            {
                string package = Path.Combine(config.CustomMetadataFolder, info.Module ?? info.Name);
                return File.Exists(Path.Combine(package, "Descriptor", info.Name + ".xml")) && config.IsCustomPath(package);
            }

            string lower = modelName.ToLowerInvariant();
            string[] standard = {
                "applicationsuite", "applicationplatform", "applicationfoundation",
                "applicationcommon", "directory", "currency", "dimensions",
                "generalledger", "personnelcore", "taxengine", "retail",
                "supplychain", "projaccounting", "casemanagement"
            };
            foreach (string s in standard)
                if (lower.Contains(s)) return false;

            // Without a custom metadata folder: Microsoft/ISV models not covered by the list above are marked Customization=DoNotAllow.
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
            if (trimmed.StartsWith("<![CDATA[", StringComparison.Ordinal) && trimmed.EndsWith("]]>", StringComparison.Ordinal))
                return trimmed.Substring(9, trimmed.Length - 12);
            return value;
        }

        private static MetaModelResult Ok(string message) =>
            new MetaModelResult { Success = true, Message = message };

        internal static MetaModelResult Fail(string message) =>
            new MetaModelResult { Success = false, Message = message };
    }
}
