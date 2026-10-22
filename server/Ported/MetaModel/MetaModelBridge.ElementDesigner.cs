using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace XppVsCode.Server.MetaModel
{
    // Element designer: the node tree and Properties window of any element, generated from the metadata classes.
    // Properties marked [AxProperty] are what Visual Studio's Properties window shows; [AxCollectionProperty] members are child nodes.
    public sealed partial class MetaModelBridge
    {
        private const int MaxElementNodes = 20000;

        // Not designer nodes in Visual Studio.
        private static readonly HashSet<string> HiddenCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Attributes", "Members", "Conflicts", "TypeParameters", "CompilerMetadata", "SourceCode",
        };

        // Visual Studio's node order; anything else follows in declaration order.
        private static readonly string[] NodeOrder =
        {
            "Fields", "FieldGroups", "Indexes", "FullTextIndexes", "Relations", "Mappings", "DeleteActions", "EnumValues",
            "DataSources", "Design", "Parts", "Ranges", "Controls", "Methods", "Events", "StateMachines",
        };

        // Per element type, from Visual Studio's designer (Events nodes are added by the extension after Methods).
        private static readonly Dictionary<string, string[]> TypeNodeOrder = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["AxTable"] = new[] { "Fields", "FieldGroups", "Indexes", "FullTextIndexes", "Relations", "DeleteActions", "StateMachines", "Mappings", "Methods" },
            ["AxTableExtension"] = new[] { "Fields", "FieldGroups", "Indexes", "FullTextIndexes", "Relations", "DeleteActions", "StateMachines", "Mappings", "Methods" },
            ["AxView"] = new[] { "ViewMetadata", "Fields", "FieldGroups", "Indexes", "Relations", "StateMachines", "Mappings", "Methods" },
            ["AxDataEntityView"] = new[] { "ViewMetadata", "DataSources", "Fields", "FieldGroups", "Keys", "Relations", "Ranges", "DeleteActions", "StateMachines", "Mappings", "Methods" },
            ["AxForm"] = new[] { "Methods", "DataSources", "Parts", "Design" },
            ["AxEdtString"] = new[] { "ArrayElements", "Relations", "TableReferences" },
        };

        private static string[] NodeOrderFor(Type type)
        {
            if (TypeNodeOrder.TryGetValue(type.Name, out var order)) return order;
            if (type.Name.StartsWith("AxQuery", StringComparison.Ordinal)) return new[] { "Methods", "DataSources" };
            if (type.Name.StartsWith("AxEdt", StringComparison.Ordinal)) return TypeNodeOrder["AxEdtString"];
            // Form controls and designs: Methods (and Events) first, then the child controls.
            if (type.Name.StartsWith("AxForm", StringComparison.Ordinal) && (type.Name.EndsWith("Control", StringComparison.Ordinal) || type.Name.Contains("Design")))
                return new[] { "Methods", "Controls" };
            return NodeOrder;
        }

        /// <summary>Folders Visual Studio doesn't show: their children appear directly under the parent.</summary>
        private static bool InlineFolder(Type owner, string collection) =>
            (collection == "Methods" && owner.Name.StartsWith("AxClass", StringComparison.Ordinal))
            || (collection == "EnumValues" && owner.Name.StartsWith("AxEnum", StringComparison.Ordinal))
            || (collection == "ViewMetadata" && owner.Name.StartsWith("AxDataEntityView", StringComparison.Ordinal))
            || (collection == "Controls" && owner.Name.StartsWith("AxForm", StringComparison.Ordinal));

        // Collections the designer tree leaves out, like Visual Studio (they stay in metadata dumps and compares).
        private static bool HiddenTreeNode(Type owner, string collection, List<string> path) =>
            (collection == "Implements" && owner.Name.StartsWith("AxClass", StringComparison.Ordinal))
            || (collection == "Methods" && path.Count == 1 && path[0] == "ViewMetadata");

        private static string TreeLabel(Type owner, string collection) =>
            collection == "Relations" && owner.Name.StartsWith("AxEdt", StringComparison.Ordinal) ? "Table Relations" : SplitWords(collection);

        private string _elementCacheKey;
        private object _elementCacheObject;
        private string _elementCacheModel;
        private DateTime _elementCacheTime;

        public string GetElementTreeJson(string objectType, string objectName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object element = ReadElementCached(objectType, objectName, out string model);
            if (element == null)
                return json.Serialize(Failure(objectType + " '" + objectName + "' not found."));

            int count = 0;
            var root = ElementNode(element, objectName, element.GetType().Name, new List<string>(), "element", ref count);
            string baseType = BaseTypeOfExtension(objectType);
            if (baseType != null)
                MergeBaseTree(root, baseType, objectName.Split('.')[0], ref count);
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["objectType"] = objectType,
                ["objectName"] = objectName,
                ["model"] = model,
                ["editable"] = IsCustomModel(model),
                ["root"] = root,
                ["baseType"] = baseType,
                ["truncated"] = count > MaxElementNodes,
            });
        }

        public string GetElementPropertiesJson(string objectType, string objectName, string[] path)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object element = ReadElementCached(objectType, objectName, out string model);
            if (element == null)
                return json.Serialize(Failure(objectType + " '" + objectName + "' not found."));

            object node = WalkElementPath(element, path, out string error);
            if (node == null)
                return json.Serialize(Failure(error));

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["type"] = node.GetType().Name,
                ["editable"] = IsCustomModel(model),
                ["properties"] = DescribeProperties(node),
            });
        }

        public string SetElementPropertyJson(string objectType, string objectName, string[] path, string property, string value)
        {
            var json = new JavaScriptSerializer();
            if (string.IsNullOrWhiteSpace(property))
                return json.Serialize(Failure("property is required."));

            _elementCacheKey = null;
            object element = ReadViaMetadataProvider(objectType, objectName, out string model);
            if (element == null)
                return json.Serialize(Failure(objectType + " '" + objectName + "' not found."));
            if (!IsCustomModel(model))
                return json.Serialize(Failure(objectType + " '" + objectName + "' belongs to " + (model ?? "a standard model")
                    + ", which you can't change. Create an extension in your model instead."));

            object node = WalkElementPath(element, path, out string error);
            if (node == null)
                return json.Serialize(Failure(error));

            var prop = node.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite || !IsDesignerProperty(prop) || prop.Name == "Name")
                return json.Serialize(Failure("'" + property + "' is not an editable property of " + node.GetType().Name + "."));
            if (!TryConvertPropertyValue(prop.PropertyType, value, out object converted, out error))
                return json.Serialize(Failure(error));
            prop.SetValue(node, converted);

            var saveInfo = GetModelSaveInfo(model);
            if (saveInfo == null)
                return json.Serialize(Failure("Cannot resolve model '" + model + "'."));
            if (!UpdateViaMetadataProvider(objectType, element, saveInfo, out error))
                return json.Serialize(Failure(error));

            _host.Reset();
            InvalidateCaches();
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = property + " saved." });
        }

        private object ReadElementCached(string objectType, string objectName, out string model)
        {
            string key = (objectType + ":" + objectName).ToLowerInvariant();
            if (key == _elementCacheKey && DateTime.UtcNow - _elementCacheTime < TimeSpan.FromSeconds(30))
            {
                model = _elementCacheModel;
                return _elementCacheObject;
            }
            object element = ReadViaMetadataProvider(objectType, objectName, out model);
            model = model ?? (element != null ? GetObjectModelName(objectType, objectName) : null);
            _elementCacheKey = element != null ? key : null;
            _elementCacheObject = element;
            _elementCacheModel = model;
            _elementCacheTime = DateTime.UtcNow;
            return element;
        }

        private static readonly Dictionary<string, string> ExtensionBaseTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AxTableExtension"] = "AxTable", ["AxFormExtension"] = "AxForm", ["AxEnumExtension"] = "AxEnum", ["AxEdtExtension"] = "AxEdt",
            ["AxDataEntityViewExtension"] = "AxDataEntityView", ["AxViewExtension"] = "AxView", ["AxMapExtension"] = "AxMap",
            ["AxMenuExtension"] = "AxMenu", ["AxQuerySimpleExtension"] = "AxQuery", ["AxSecurityDutyExtension"] = "AxSecurityDuty",
            ["AxSecurityRoleExtension"] = "AxSecurityRole", ["AxMenuItemDisplayExtension"] = "AxMenuItemDisplay",
            ["AxMenuItemActionExtension"] = "AxMenuItemAction", ["AxMenuItemOutputExtension"] = "AxMenuItemOutput",
            ["AxTileExtension"] = "AxTile", ["AxWorkflowApprovalExtension"] = "AxWorkflowApproval", ["AxWorkflowTaskExtension"] = "AxWorkflowTask",
        };

        private static string BaseTypeOfExtension(string objectType) =>
            objectType != null && ExtensionBaseTypes.TryGetValue(objectType, out string b) ? b : null;

        /// <summary>
        /// An extension's designer shows the base element's nodes (read-only, "inherited", with the base element as their
        /// source) and the extension's own nodes merged into the same folders ("own"), like Visual Studio's extension designer.
        /// </summary>
        private void MergeBaseTree(Dictionary<string, object> extRoot, string baseType, string baseName, ref int count)
        {
            object baseElement = ReadElementCachedBase(baseType, baseName);
            if (baseElement == null) return;
            var baseRoot = ElementNode(baseElement, baseName, baseElement.GetType().Name, new List<string>(), "element", ref count);
            var source = new Dictionary<string, object> { ["objectType"] = baseType, ["objectName"] = baseName };
            MarkInherited(baseRoot, source);

            var extChildren = (List<object>)extRoot["children"];
            foreach (var child in extChildren.Cast<Dictionary<string, object>>()) MarkOwn(child);
            var merged = new List<object>();
            var used = new HashSet<Dictionary<string, object>>();
            foreach (var baseChild in ((List<object>)baseRoot["children"]).Cast<Dictionary<string, object>>())
            {
                var same = extChildren.Cast<Dictionary<string, object>>().FirstOrDefault(e =>
                    (string)e["kind"] == "folder" && (string)baseChild["kind"] == "folder" && Equals(e["type"], baseChild["type"]));
                if (same != null)
                {
                    used.Add(same);
                    var items = (List<object>)baseChild["children"];
                    items.AddRange((List<object>)same["children"]);
                    baseChild["own"] = false;
                }
                merged.Add(baseChild);
            }
            // Folders only extensions have (field modifications, property modifications, ...) keep their place at the end.
            merged.AddRange(extChildren.Cast<Dictionary<string, object>>().Where(e => !used.Contains(e)));
            extRoot["children"] = merged;
        }

        private object ReadElementCachedBase(string baseType, string baseName)
        {
            try { return ReadViaMetadataProvider(baseType, baseName, out _); }
            catch { return null; }
        }

        private static void MarkInherited(Dictionary<string, object> node, Dictionary<string, object> source)
        {
            node["inherited"] = true;
            node["source"] = source;
            foreach (var child in ((List<object>)node["children"]).Cast<Dictionary<string, object>>()) MarkInherited(child, source);
        }

        private static void MarkOwn(Dictionary<string, object> node)
        {
            node["own"] = true;
            foreach (var child in ((List<object>)node["children"]).Cast<Dictionary<string, object>>()) MarkOwn(child);
        }

        private Dictionary<string, object> ElementNode(object obj, string name, string type, List<string> path, string kind, ref int count)
        {
            count++;
            var children = new List<object>();
            foreach (var prop in ChildCollections(obj.GetType()))
            {
                if (count > MaxElementNodes) break;
                object value;
                try { value = prop.GetValue(obj); }
                catch { continue; }

                var childPath = new List<string>(path) { prop.Name };
                if (HiddenTreeNode(obj.GetType(), prop.Name, path)) continue;
                if (value is IEnumerable items && !(value is string))
                {
                    var folder = new List<object>();
                    bool methods = prop.Name == "Methods";
                    foreach (object item in items)
                    {
                        if (item == null || count > MaxElementNodes) break;
                        string itemName = ItemName(item);
                        if (itemName == null) continue;
                        if (methods)
                        {
                            count++;
                            folder.Add(new Dictionary<string, object>
                            {
                                ["name"] = itemName, ["type"] = "Method", ["kind"] = "method",
                                ["path"] = new List<string>(childPath) { itemName }, ["children"] = new List<object>(),
                            });
                        }
                        else
                        {
                            folder.Add(ElementNode(item, itemName, item.GetType().Name, new List<string>(childPath) { itemName }, "item", ref count));
                        }
                    }
                    count++;
                    var folderNode = new Dictionary<string, object>
                    {
                        ["name"] = TreeLabel(obj.GetType(), prop.Name), ["type"] = prop.Name, ["kind"] = "folder", ["path"] = childPath, ["children"] = folder,
                    };
                    if (InlineFolder(obj.GetType(), prop.Name)) folderNode["inline"] = true;
                    children.Add(folderNode);
                }
                else if (value != null)
                {
                    var itemNode = ElementNode(value, SplitWords(prop.Name), value.GetType().Name, childPath, "item", ref count);
                    if (InlineFolder(obj.GetType(), prop.Name)) itemNode["inline"] = true;
                    children.Add(itemNode);
                }
            }
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["type"] = type.StartsWith("Ax") ? type.Substring(2) : type,
                ["kind"] = kind,
                ["path"] = path,
                ["children"] = children,
            };
        }

        private static IEnumerable<PropertyInfo> ChildCollections(Type type)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && !HiddenCollections.Contains(p.Name)
                    && (HasAttribute(p, "AxCollectionPropertyAttribute") || p.Name == "Methods"))
                .GroupBy(p => p.Name).Select(g => g.First())
                .ToList();
            var order = NodeOrderFor(type);
            return props.OrderBy(p =>
            {
                int index = Array.IndexOf(order, p.Name);
                return index < 0 ? order.Length : index;
            });
        }

        private List<Dictionary<string, object>> DescribeProperties(object node)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && (IsDesignerProperty(p) || p.Name == "Name"))
                .GroupBy(p => p.Name).Select(g => g.First())
                .OrderBy(p => p.Name == "Name" ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                object value;
                try { value = prop.GetValue(node); }
                catch { continue; }

                Type type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                string kind = type.IsEnum ? "enum"
                    : type == typeof(bool) ? "bool"
                    : type == typeof(string) ? "string"
                    : IsNumeric(type) ? "number"
                    : "other";
                var entry = new Dictionary<string, object>
                {
                    ["name"] = prop.Name,
                    ["value"] = FormatValue(value),
                    ["kind"] = kind,
                    ["readOnly"] = !prop.CanWrite || kind == "other" || prop.Name == "Name",
                };
                if (type.IsEnum)
                    entry["options"] = Enum.GetNames(type);
                else if (type == typeof(bool))
                    entry["options"] = new[] { "False", "True" };
                if (value is string text && text.StartsWith("@"))
                    entry["resolved"] = ResolveLabel(text);
                result.Add(entry);
            }
            return result;
        }

        private static object WalkElementPath(object element, string[] path, out string error)
        {
            error = null;
            object current = element;
            int i = 0;
            while (path != null && i < path.Length)
            {
                var prop = current.GetType().GetProperty(path[i], BindingFlags.Public | BindingFlags.Instance);
                if (prop == null)
                {
                    error = "Node '" + path[i] + "' not found.";
                    return null;
                }
                object value = prop.GetValue(current);
                if (value is IEnumerable items && !(value is string))
                {
                    if (i + 1 >= path.Length)
                    {
                        error = "'" + path[i] + "' is a folder and has no properties.";
                        return null;
                    }
                    string itemName = path[i + 1];
                    current = items.Cast<object>().FirstOrDefault(o => o != null && string.Equals(ItemName(o), itemName, StringComparison.OrdinalIgnoreCase));
                    if (current == null)
                    {
                        error = "'" + itemName + "' not found under " + path[i] + ".";
                        return null;
                    }
                    i += 2;
                }
                else
                {
                    current = value;
                    if (current == null)
                    {
                        error = "Node '" + path[i] + "' is empty.";
                        return null;
                    }
                    i++;
                }
            }
            return current;
        }

        private bool UpdateViaMetadataProvider(string objectType, object element, ModelSaveInfo saveInfo, out string error)
        {
            error = null;
            foreach (var prop in typeof(IMetadataProvider).GetProperties())
            {
                var update = prop.PropertyType.GetMethod("Update", new[] { element.GetType(), typeof(ModelSaveInfo) })
                    ?? prop.PropertyType.GetInterfaces().Select(i => i.GetMethod("Update", new[] { element.GetType(), typeof(ModelSaveInfo) })).FirstOrDefault(m => m != null);
                if (update == null) continue;
                try
                {
                    update.Invoke(prop.GetValue(MetadataProvider), new object[] { element, saveInfo });
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    error = "Save failed: " + (ex.InnerException?.Message ?? ex.Message);
                    return false;
                }
            }
            error = "Saving " + objectType + " is not supported.";
            return false;
        }

        private static bool TryConvertPropertyValue(Type propertyType, string value, out object converted, out string error)
        {
            error = null;
            converted = null;
            Type type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            try
            {
                if (string.IsNullOrEmpty(value) && (Nullable.GetUnderlyingType(propertyType) != null || !type.IsValueType))
                    converted = type == typeof(string) ? (object)(value ?? "") : null;
                else if (type == typeof(string))
                    converted = value;
                else if (type.IsEnum)
                    converted = Enum.Parse(type, value, true);
                else if (type == typeof(bool))
                    converted = bool.Parse(value);
                else if (IsNumeric(type))
                    converted = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
                else
                {
                    error = "This property can't be edited here.";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
            {
                error = "'" + value + "' is not a valid " + type.Name + " value.";
                return false;
            }
        }

        private static bool IsDesignerProperty(PropertyInfo prop) => HasAttribute(prop, "AxPropertyAttribute");

        private static bool HasAttribute(PropertyInfo prop, string attributeName)
            => prop.GetCustomAttributesData().Any(a => a.AttributeType.Name == attributeName);

        private static bool IsNumeric(Type type)
            => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(decimal) || type == typeof(double) || type == typeof(float);

        private static string ItemName(object item)
            => item.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) as string;

        private static string FormatValue(object value)
            => value == null ? "" : value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString();

        private static string SplitWords(string name)
            => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");

        private static Dictionary<string, object> Failure(string message)
            => new Dictionary<string, object> { ["success"] = false, ["message"] = message };
    }
}
