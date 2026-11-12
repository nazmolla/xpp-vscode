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
    /// <summary>Applying request metadata (properties, fields, typed metadata graphs, query data sources) to elements.</summary>
    public sealed partial class MetaModelBridge
    {
        // ── Strongly-typed metadata apply methods ──

        // Properties of the current create/update that could not be applied; reported back instead of being dropped silently.
        private static readonly List<string> IgnoredProperties = new List<string>();
        private static readonly HashSet<string> RequestOnlyProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EdtType", "Languages", "Language" };

        public static void ClearIgnoredProperties() { lock (IgnoredProperties) IgnoredProperties.Clear(); }

        public static string IgnoredPropertiesReport()
        {
            lock (IgnoredProperties)
                return IgnoredProperties.Count == 0 ? ""
                    : "\nWARNING - properties NOT applied: " + string.Join("; ", IgnoredProperties.Distinct()) + ". Check names with xpp_get_object_type_schema.";
        }

        private static void ApplyProperties(object target, Dictionary<string, string> props)
        {
            if (props == null || target == null) return;
            var type = target.GetType();
            foreach (var kvp in props)
            {
                var prop = type.GetProperty(kvp.Key);
                if (prop == null || !prop.CanWrite)
                {
                    if (!RequestOnlyProperties.Contains(kvp.Key))
                        lock (IgnoredProperties) IgnoredProperties.Add(kvp.Key + " (no such property on " + type.Name + ")");
                    continue;
                }
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
                            value = Enum.Parse(underlying, BoolToNoYes(underlying, kvp.Value), true);
                        else
                            continue;
                    }
                    else if (propType == typeof(string))
                        value = kvp.Value;
                    else if (propType == typeof(bool))
                        value = "true".Equals(kvp.Value, StringComparison.OrdinalIgnoreCase)
                             || "1".Equals(kvp.Value, StringComparison.Ordinal) || "yes".Equals(kvp.Value, StringComparison.OrdinalIgnoreCase);
                    else if (propType == typeof(int))
                        value = int.Parse(kvp.Value);
                    else if (propType == typeof(long))
                        value = long.Parse(kvp.Value);
                    else if (propType.IsEnum)
                        value = Enum.Parse(propType, BoolToNoYes(propType, kvp.Value), true);
                    else
                        continue;

                    prop.SetValue(target, value);
                }
                catch
                {
                    string allowed = prop.PropertyType.IsEnum ? "; allowed: " + string.Join(", ", Enum.GetNames(prop.PropertyType)) : "";
                    lock (IgnoredProperties) IgnoredProperties.Add(kvp.Key + "='" + kvp.Value + "' (invalid value" + allowed + ")");
                }
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
                if (idx.AlternateKey && !idx.AllowDuplicates) axIdx.AlternateKey = NoYes.Yes;
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
                // properties given next to typedMetadata (or to rootDataEntities) still apply.
                if (req.Properties != null && req.Properties.Count > 0 && !ApplyPropertyModifications(target, req.Properties))
                    ApplyProperties(target, req.Properties);
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
                // properties given next to typedMetadata (or to rootDataEntities) still apply.
                if (req.Properties != null && req.Properties.Count > 0 && !ApplyPropertyModifications(target, req.Properties))
                    ApplyProperties(target, req.Properties);
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
                _tableChangeReport = ApplyTableChanges(axTable, axTable.Name, req);
                ApplyFields(axTable.Fields, req.Fields);
                ApplyIndexes(axTable.Indexes, req.Indexes);
                ApplyFieldGroups(axTable.FieldGroups, req.FieldGroups);
                ApplyRelations(axTable.Relations, req.Relations);
            }
            if (target is AxTableExtension tableExt)
            {
                _tableChangeReport = ApplyTableChanges(tableExt, tableExt.Name.Split('.')[0], req);
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
            CheckTypedMetadataKeys(target.GetType(), typedMetadataJson);

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

            // Proxies model only the base types; "$type" (as written by xpp_read_object) names a subtype whose
            // extra properties (e.g. DataField of AxDataEntityViewMappedField) the proxy would drop.
            Type proxyType = typedMetadataJson.Contains("\"$type\"") ? null : ResolveProxyType(objectType);
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
                string wantedType = Convert.ToString(GetNodeValue(childNode, "$type"));
                if (child != null && !string.IsNullOrEmpty(wantedType)
                    && !string.Equals(child.GetType().Name, wantedType, StringComparison.OrdinalIgnoreCase))
                    child = prop.CanWrite ? CreateNodeInstance(propType, childNode) : child;
                if (child == null)
                {
                    if (!prop.CanWrite) continue;
                    try { child = CreateNodeInstance(propType, childNode); }
                    catch when (string.IsNullOrEmpty(wantedType)) { continue; }
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
                        object item = CreateNodeInstance(itemType, childDict);
                        ApplyObjectNode(item, childDict);
                        addValue = item;
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
                return Convert.ToString(input, System.Globalization.CultureInfo.InvariantCulture);

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

        private static readonly Dictionary<string, Type> _subtypeCache = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        // Rejects typedMetadata keys that are not properties of the target type (or of the "$type" subtype), so a
        // misspelled or unsupported key fails the call instead of being dropped without a trace.
        private static void CheckTypedMetadataKeys(Type targetType, string typedMetadataJson)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
            var unknown = new List<string>();
            CollectUnknownKeys(targetType, serializer.DeserializeObject(typedMetadataJson) as Dictionary<string, object>, "typedMetadata", unknown, 0);
            if (unknown.Count > 0)
                throw new InvalidOperationException("unknown properties: " + string.Join(", ", unknown.Take(20))
                    + ". Check the names with xpp_get_object_type_schema or xpp_read_object. Nothing was applied.");
        }

        private static void CollectUnknownKeys(Type type, Dictionary<string, object> node, string path, List<string> unknown, int depth)
        {
            if (type == null || node == null || depth > 12) return;
            string typeName = Convert.ToString(GetNodeValue(node, "$type", "__type"));
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                Type sub = ResolveSubtype(type, typeName.Trim());
                if (sub == null) { unknown.Add(path + ".$type (" + typeName + " is not a " + type.Name + ")"); return; }
                type = sub;
            }
            foreach (var kv in node)
            {
                if (kv.Key == "$type" || kv.Key == "__type") continue;
                var prop = FindProperty(type, kv.Key);
                if (prop == null) { unknown.Add(path + "." + kv.Key); continue; }
                Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (kv.Value is Dictionary<string, object> child)
                {
                    CollectUnknownKeys(propType, child, path + "." + kv.Key, unknown, depth + 1);
                }
                else if (kv.Value is object[] || kv.Value is System.Collections.ArrayList)
                {
                    Type itemType = TypedCollectionItemType(propType);
                    var items = ToObjectArray(kv.Value) ?? new object[0];
                    for (int i = 0; i < items.Length; i++)
                        if (items[i] is Dictionary<string, object> itemNode && itemType != null)
                            CollectUnknownKeys(itemType, itemNode, path + "." + kv.Key + "[" + i + "]", unknown, depth + 1);
                }
            }
        }

        private static Type TypedCollectionItemType(Type collectionType)
        {
            Type fallback = null;
            foreach (var m in collectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Add" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (ps[0].ParameterType != typeof(object)) return ps[0].ParameterType;
                fallback = ps[0].ParameterType;
            }
            return fallback == typeof(object) ? null : fallback;
        }

        // Instance for a typed-metadata node: the concrete type named by "$type" (must derive from baseType), else baseType.
        // Throws instead of skipping, so a payload is never saved with parts silently dropped.
        private static object CreateNodeInstance(Type baseType, Dictionary<string, object> node)
        {
            string typeName = Convert.ToString(GetNodeValue(node, "$type", "__type"));
            Type concrete = baseType;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                concrete = ResolveSubtype(baseType, typeName.Trim());
                if (concrete == null)
                    throw new InvalidOperationException("\"$type\": \"" + typeName + "\" is not a " + baseType.Name
                        + " type. Use one of: " + string.Join(", ", ListConcreteSubtypes(baseType)) + ".");
            }
            object instance = CreateDefaultInstance(concrete);
            if (instance == null)
            {
                string name = Convert.ToString(GetNodeValue(node, "Name", "name"));
                throw new InvalidOperationException(baseType.Name + " is abstract, so the item"
                    + (string.IsNullOrEmpty(name) ? "" : " '" + name + "'")
                    + " needs \"$type\" with its concrete type: one of " + string.Join(", ", ListConcreteSubtypes(baseType)) + ".");
            }
            return instance;
        }

        private static Type ResolveSubtype(Type baseType, string typeName)
        {
            string key = baseType.FullName + "|" + typeName;
            lock (_subtypeCache)
            {
                if (_subtypeCache.TryGetValue(key, out Type cached)) return cached;
            }
            Type found = string.Equals(baseType.Name, typeName, StringComparison.OrdinalIgnoreCase) ? baseType : null;
            if (found == null)
            {
                foreach (Type t in CandidateSubtypes(baseType))
                    if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)) { found = t; break; }
            }
            lock (_subtypeCache) { _subtypeCache[key] = found; }
            return found;
        }

        private static IEnumerable<string> ListConcreteSubtypes(Type baseType)
        {
            return CandidateSubtypes(baseType).Where(t => !t.IsAbstract).Select(t => t.Name).OrderBy(n => n).Take(40);
        }

        private static IEnumerable<Type> CandidateSubtypes(Type baseType)
        {
            Type[] types;
            try { types = baseType.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            return types.Where(t => t.IsClass && t.IsPublic && baseType.IsAssignableFrom(t));
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

                if (nt == typeof(string)) return Convert.ToString(input, System.Globalization.CultureInfo.InvariantCulture);
                if (nt.IsEnum)
                {
                    if (input is string es)
                        return Enum.Parse(nt, es, true);
                    return Enum.ToObject(nt, Convert.ToInt32(input, System.Globalization.CultureInfo.InvariantCulture));
                }
                if (nt == typeof(bool))
                {
                    if (input is string bs)
                    {
                        if ("1".Equals(bs, StringComparison.Ordinal) || "yes".Equals(bs, StringComparison.OrdinalIgnoreCase)) return true;
                        if ("0".Equals(bs, StringComparison.Ordinal) || "no".Equals(bs, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return Convert.ToBoolean(input, System.Globalization.CultureInfo.InvariantCulture);
                }
                if (nt == typeof(Guid))
                    return Guid.Parse(Convert.ToString(input, System.Globalization.CultureInfo.InvariantCulture));
                if (nt == typeof(DateTime))
                    return DateTime.Parse(Convert.ToString(input, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

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

            var addMethod = SingleItemAddMethod(collection.GetType());
            if (addMethod == null || addMethod.GetParameters().Length != 1) return null;
            Type dsType = addMethod.GetParameters()[0].ParameterType;

            object ds = CreateDefaultInstance(dsType);
            if (ds == null) return null;

            TrySetPropertyValue(ds, dto.Name, "Name", "DataSourceName");
            TrySetPropertyValue(ds, dto.Table, "Table", "TableName");
            TrySetPropertyValue(ds, dto.ParentDataSource, "ParentDataSource", "JoinDataSource", "Parent");
            TrySetPropertyValue(ds, dto.JoinMode, "JoinMode");
            TrySetPropertyValue(ds, dto.LinkType, "LinkType");
            // An empty field list with Dynamic Fields = No fails the build (FieldListEmpty).
            bool hasFields = dto.Fields != null && dto.Fields.Count > 0;
            if (dto.DynamicFields == false && !hasFields)
                throw new InvalidOperationException("data source '" + (dto.Name ?? dto.Table) + "': dynamicFields is false but no fields are given "
                    + "(the build would fail with FieldListEmpty). Pass fields: [\"Field1\", ...] or set dynamicFields to true.");
            bool dynamic = dto.DynamicFields ?? !hasFields;
            TrySetPropertyValue(ds, dynamic ? "true" : "false", "DynamicFields");
            if (hasFields && FindProperty(ds.GetType(), "Fields")?.GetValue(ds) is object fieldCollection)
            {
                var addField = SingleItemAddMethod(fieldCollection.GetType());
                Type fieldType = addField?.GetParameters()[0].ParameterType;
                foreach (string fieldName in dto.Fields.Where(f => !string.IsNullOrWhiteSpace(f)))
                {
                    object field = fieldType == null ? null : CreateDefaultInstance(fieldType);
                    if (field == null) throw new InvalidOperationException("query data source fields cannot be created through the metadata API.");
                    TrySetPropertyValue(field, fieldName, "Name");
                    TrySetPropertyValue(field, fieldName, "Field", "DataField");
                    addField.Invoke(fieldCollection, new[] { field });
                }
            }
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

            var addMethod = SingleItemAddMethod(rangesCollection.GetType());
            if (addMethod == null || addMethod.GetParameters().Length != 1) return;
            Type rangeType = addMethod.GetParameters()[0].ParameterType;

            // Range names are the collection's key: unnamed ranges are named after their field, like Visual Studio
            // (SalesStatus, SalesStatus1, ... when a field has several ranges).
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object existing in (System.Collections.IEnumerable)rangesCollection)
            {
                string existingName = existing?.GetType().GetProperty("Name")?.GetValue(existing) as string;
                if (!string.IsNullOrEmpty(existingName)) usedNames.Add(existingName);
            }

            foreach (var dto in ranges)
            {
                if (string.IsNullOrWhiteSpace(dto?.Field) && string.IsNullOrWhiteSpace(dto?.Name))
                    continue;

                string name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Field.Trim() : dto.Name.Trim();
                if (string.IsNullOrWhiteSpace(dto.Name))
                    for (int n = 1; usedNames.Contains(name); n++) name = dto.Field.Trim() + n;
                usedNames.Add(name);

                object range = Activator.CreateInstance(rangeType);
                TrySetPropertyValue(range, name, "Name");
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
                        converted = Enum.Parse(propType, BoolToNoYes(propType, value), true);
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

        // NoYes-style enums accept JSON booleans.
        private static string BoolToNoYes(Type enumType, string value)
        {
            if (!Enum.GetNames(enumType).Contains("Yes", StringComparer.OrdinalIgnoreCase)) return value;
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1") return "Yes";
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0") return "No";
            return value;
        }
    }
}
