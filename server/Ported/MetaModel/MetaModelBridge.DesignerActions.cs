using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Designer context-menu actions: New <node>, New/Override method, Delete, and editing methods of nested nodes.
    public sealed partial class MetaModelBridge
    {
        private static Type[] _metadataTypes;

        /// <summary>What can be added under a node: for a folder, its own item types; for an item, the item types of its folders.</summary>
        public string GetNodeTypesJson(string objectType, string objectName, string[] path)
        {
            var json = new JavaScriptSerializer();
            object element = ReadElementCached(objectType, objectName, out _);
            if (element == null)
                return json.Serialize(Failure(objectType + " '" + objectName + "' not found."));

            var collections = new List<object>();
            if (TryResolveCollection(element, path, out object collection, out Type itemType, out _))
            {
                collections.Add(DescribeCollection(path, itemType));
            }
            else
            {
                object node = WalkElementPath(element, path, out string error);
                if (node == null)
                    return json.Serialize(Failure(error));
                foreach (var prop in ChildCollections(node.GetType()).Where(p => p.Name != "Methods"))
                {
                    var childPath = (path ?? new string[0]).Concat(new[] { prop.Name }).ToArray();
                    if (TryResolveCollection(element, childPath, out _, out Type childType, out _))
                        collections.Add(DescribeCollection(childPath, childType));
                }
            }
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["collections"] = collections });
        }

        public string AddElementNodeJson(string objectType, string objectName, string[] collectionPath, string typeName, string name, Dictionary<string, string> properties)
        {
            var json = new JavaScriptSerializer();
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                return json.Serialize(Failure("'" + name + "' is not a valid name."));

            object element = ReadEditableElement(objectType, objectName, out string model, out string error);
            if (element == null)
                return json.Serialize(Failure(error));
            if (!TryResolveCollection(element, collectionPath, out object collection, out Type itemType, out error))
                return json.Serialize(Failure(error));

            Type type = MetadataTypes().FirstOrDefault(t => t.Name == typeName);
            if (type == null || !itemType.IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                return json.Serialize(Failure("'" + typeName + "' can't be added here."));
            if (((IEnumerable)collection).Cast<object>().Any(o => string.Equals(ItemName(o), name, StringComparison.OrdinalIgnoreCase)))
                return json.Serialize(Failure("'" + name + "' already exists."));

            object item = Activator.CreateInstance(type);
            type.GetProperty("Name")?.SetValue(item, name);
            if (properties != null)
            {
                foreach (var kvp in properties)
                {
                    var prop = type.GetProperty(kvp.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanWrite && TryConvertPropertyValue(prop.PropertyType, kvp.Value, out object value, out _))
                        prop.SetValue(item, value);
                }
            }
            if (!AddToCollection(collection, item, out error))
                return json.Serialize(Failure(error));
            return SaveElement(json, objectType, element, model, "Added " + name + ".");
        }

        public string DeleteElementNodeJson(string objectType, string objectName, string[] path)
        {
            var json = new JavaScriptSerializer();
            if (path == null || path.Length < 2)
                return json.Serialize(Failure("Only items inside a folder can be deleted."));

            object element = ReadEditableElement(objectType, objectName, out string model, out string error);
            if (element == null)
                return json.Serialize(Failure(error));
            var collectionPath = path.Take(path.Length - 1).ToArray();
            if (!TryResolveCollection(element, collectionPath, out object collection, out _, out error))
                return json.Serialize(Failure(error));

            string name = path[path.Length - 1];
            object item = ((IEnumerable)collection).Cast<object>().FirstOrDefault(o => string.Equals(ItemName(o), name, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return json.Serialize(Failure("'" + name + "' not found."));
            var remove = collection.GetType().GetMethod("Remove", new[] { item.GetType() })
                ?? collection.GetType().GetMethods().FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(item));
            if (remove == null)
                return json.Serialize(Failure("Items can't be removed from this folder."));
            remove.Invoke(collection, new[] { item });
            return SaveElement(json, objectType, element, model, "Deleted " + name + ".");
        }

        /// <summary>Source of a method anywhere in the element (path ends with Methods, name).</summary>
        public string GetElementMethodJson(string objectType, string objectName, string[] path)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object element = ReadElementCached(objectType, objectName, out string model);
            if (element == null)
                return json.Serialize(Failure(objectType + " '" + objectName + "' not found."));
            if (!(WalkElementPath(element, path, out string error) is AxMethod method))
                return json.Serialize(Failure(error ?? "Not a method."));
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = method.Name,
                ["source"] = method.Source ?? "",
                ["isCustom"] = IsCustomModel(model),
            });
        }

        /// <summary>Adds or replaces a method on a node (path to the node that has Methods); the name comes from the source.</summary>
        public string SetElementMethodJson(string objectType, string objectName, string[] nodePath, string originalName, string source)
        {
            var json = new JavaScriptSerializer();
            string name = MethodNameOf(source);
            if (string.IsNullOrEmpty(name))
                return json.Serialize(Failure("Can't find the method name in the source."));

            object element = ReadEditableElement(objectType, objectName, out string model, out string error);
            if (element == null)
                return json.Serialize(Failure(error));
            var methodsPath = (nodePath ?? new string[0]).Concat(new[] { "Methods" }).ToArray();
            if (!TryResolveCollection(element, methodsPath, out object collection, out _, out error))
                return json.Serialize(Failure(error));

            var methods = ((IEnumerable)collection).Cast<AxMethod>().ToList();
            var existing = methods.FirstOrDefault(m => string.Equals(m.Name, originalName ?? name, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(originalName ?? name, name, StringComparison.OrdinalIgnoreCase)
                && methods.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
                return json.Serialize(Failure("A method named '" + name + "' already exists."));

            if (existing != null)
            {
                existing.Name = name;
                existing.Source = source;
            }
            else if (!AddToCollection(collection, new AxMethod { Name = name, Source = source }, out error))
            {
                return json.Serialize(Failure(error));
            }
            return SaveElement(json, objectType, element, model, "Saved method " + name + ".");
        }

        /// <summary>Methods that can be overridden on a node, with ready-to-insert source.</summary>
        public string GetOverridesJson(string objectType, string objectName, string[] nodePath)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object element = ReadElementCached(objectType, objectName, out _);
            if (element == null)
                return json.Serialize(Failure(objectType + " '" + objectName + "' not found."));
            object node = WalkElementPath(element, nodePath, out string error);
            if (node == null)
                return json.Serialize(Failure(error));

            var existing = new HashSet<string>(
                (node.GetType().GetProperty("Methods")?.GetValue(node) as IEnumerable)?.Cast<object>().Select(ItemName) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            string indent = new string(' ', MethodIndent(nodePath));
            var signatures = node is AxClass cls ? BaseClassSignatures(cls) : KernelSignatures(node);
            var result = signatures
                .Where(s => !existing.Contains(s.Key))
                .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .Select(s => new Dictionary<string, object>
                {
                    ["name"] = s.Key,
                    ["signature"] = s.Value,
                    ["source"] = OverrideSource(s.Value, s.Key, indent),
                })
                .ToList();
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["overrides"] = result, ["indent"] = indent.Length });
        }

        // ── helpers ──

        private object ReadEditableElement(string objectType, string objectName, out string model, out string error)
        {
            error = null;
            _elementCacheKey = null;
            object element = ReadViaMetadataProvider(objectType, objectName, out model);
            model = model ?? (element != null ? GetObjectModelName(objectType, objectName) : null);
            if (element == null)
            {
                error = objectType + " '" + objectName + "' not found.";
                return null;
            }
            if (!IsCustomModel(model))
            {
                error = objectType + " '" + objectName + "' belongs to " + (model ?? "a standard model")
                    + ", which you can't change. Create an extension in your model instead.";
                return null;
            }
            return element;
        }

        private string SaveElement(JavaScriptSerializer json, string objectType, object element, string model, string message)
        {
            var saveInfo = GetModelSaveInfo(model);
            if (saveInfo == null)
                return json.Serialize(Failure("Cannot resolve model '" + model + "'."));
            if (!UpdateViaMetadataProvider(objectType, element, saveInfo, out string error))
                return json.Serialize(Failure(error));
            _host.Reset();
            InvalidateCaches();
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = message });
        }

        private static bool TryResolveCollection(object element, string[] path, out object collection, out Type itemType, out string error)
        {
            collection = null;
            itemType = null;
            error = null;
            if (path == null || path.Length == 0)
            {
                error = "Select a folder.";
                return false;
            }
            object owner = path.Length == 1 ? element : WalkElementPath(element, path.Take(path.Length - 1).ToArray(), out error);
            if (owner == null)
                return false;
            var prop = owner.GetType().GetProperty(path[path.Length - 1], BindingFlags.Public | BindingFlags.Instance);
            object value = prop?.GetValue(owner);
            if (!(value is IEnumerable) || value is string)
            {
                error = "'" + path[path.Length - 1] + "' is not a folder.";
                return false;
            }
            collection = value;
            itemType = CollectionItemType(value.GetType());
            if (itemType == null)
            {
                error = "Unknown item type for '" + path[path.Length - 1] + "'.";
                return false;
            }
            return true;
        }

        private static Type CollectionItemType(Type collectionType)
        {
            for (var t = collectionType; t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericArguments().Length == 1)
                    return t.GetGenericArguments()[0];
            }
            return collectionType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        }

        private static bool AddToCollection(object collection, object item, out string error)
        {
            error = null;
            var add = collection.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.IsInstanceOfType(item));
            if (add == null)
            {
                error = "Items can't be added to this folder.";
                return false;
            }
            try
            {
                add.Invoke(collection, new[] { item });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        private static Dictionary<string, object> DescribeCollection(string[] path, Type itemType)
        {
            var types = MetadataTypes()
                .Where(t => itemType.IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => new Dictionary<string, object> { ["typeName"] = t.Name, ["label"] = TypeLabel(t, itemType), ["hasDataField"] = t.GetProperty("DataField") != null })
                .OrderBy(t => (string)t["label"], StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Dictionary<string, object>
            {
                ["path"] = path,
                ["label"] = SplitWords(path[path.Length - 1]),
                ["types"] = types,
            };
        }

        private static string TypeLabel(Type type, Type baseType)
        {
            if (type == baseType)
                return SplitWords(type.Name.StartsWith("Ax") ? type.Name.Substring(2) : type.Name);
            string name = type.Name;
            // AxTableFieldString under AxTableField -> "String"; AxFormStringControl under AxFormControl -> "String".
            string prefix = baseType.Name.EndsWith("Control") ? baseType.Name.Substring(0, baseType.Name.Length - "Control".Length) : baseType.Name;
            if (name.StartsWith(prefix) && name.Length > prefix.Length)
                name = name.Substring(prefix.Length);
            else if (name.StartsWith("Ax"))
                name = name.Substring(2);
            if (name.EndsWith("Control") && name.Length > "Control".Length)
                name = name.Substring(0, name.Length - "Control".Length);
            return SplitWords(name);
        }

        private static Type[] MetadataTypes()
        {
            if (_metadataTypes == null)
            {
                try { _metadataTypes = typeof(AxTable).Assembly.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { _metadataTypes = ex.Types.Where(t => t != null).ToArray(); }
            }
            return _metadataTypes;
        }

        // Code nesting of a node's methods: element 4, data sources and controls 8, data source fields 12.
        private static int MethodIndent(string[] nodePath)
        {
            if (nodePath == null || nodePath.Length == 0) return 4;
            return nodePath.Length >= 4 && nodePath[0] == "DataSources" && nodePath[2] == "Fields" ? 12 : 8;
        }

        private static string MethodNameOf(string source)
        {
            string text = Regex.Replace(source ?? "", @"/\*[\s\S]*?\*/|//[^\n]*", "");
            int brace = text.IndexOf('{');
            string header = brace >= 0 ? text.Substring(0, brace) : text;
            header = Regex.Replace(header, @"\[[^\]]*\]", " ");
            var match = Regex.Match(header, @"([A-Za-z_][A-Za-z0-9_]*)\s*\(");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>Overridable methods of the base classes, signature without modifiers that can't be overridden.</summary>
        private Dictionary<string, string> BaseClassSignatures(AxClass cls)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string baseName = cls.Extends;
            while (!string.IsNullOrEmpty(baseName) && seen.Add(baseName))
            {
                var baseClass = ReadViaMetadataProvider("AxClass", baseName, out _) as AxClass;
                if (baseClass == null) break;
                foreach (AxMethod m in baseClass.Methods)
                {
                    string signature = SignatureOf(m.Source);
                    if (signature == null || result.ContainsKey(m.Name) || m.Name == "new" || m.Name == "classDeclaration") continue;
                    if (Regex.IsMatch(signature, @"\b(private|final|static)\b")) continue;
                    result[m.Name] = Regex.Replace(signature, @"\babstract\s+", "");
                }
                baseName = baseClass.Extends;
            }
            return result;
        }

        private static string SignatureOf(string source)
        {
            var lines = new List<string>();
            foreach (string raw in (source ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("[")) continue;
                if (line.StartsWith("{")) break;
                lines.Add(line);
                if (line.Contains("{")) break;
            }
            string signature = string.Join(" ", lines).Split('{')[0].Trim();
            return signature.Contains("(") ? signature : null;
        }

        private static string OverrideSource(string signature, string name, string indent)
        {
            int open = signature.IndexOf('(');
            int close = signature.LastIndexOf(')');
            string parameters = open >= 0 && close > open ? signature.Substring(open + 1, close - open - 1) : "";
            var args = SplitParameters(parameters)
                .Select(p => Regex.Replace(p.Split('=')[0].Trim(), @"\s+", " ").Split(' ').Last())
                .Where(a => a.Length > 0)
                .ToList();
            string beforeName = signature.Substring(0, Math.Max(0, signature.LastIndexOf(name, open < 0 ? signature.Length : open, StringComparison.Ordinal))).Trim();
            string returnType = Regex.Replace(beforeName, @"\b(public|protected|internal|private|final|static|abstract|client|server|display|edit)\b", "").Trim();
            string superCall = "super(" + string.Join(", ", args) + ")";
            string inner = indent + "    ";
            var sb = new StringBuilder();
            sb.Append(indent).Append(signature).Append('\n');
            sb.Append(indent).Append("{\n");
            if (string.IsNullOrEmpty(returnType) || returnType == "void")
            {
                sb.Append(inner).Append(superCall).Append(";\n");
            }
            else
            {
                sb.Append(inner).Append(returnType).Append(" ret;\n\n");
                sb.Append(inner).Append("ret = ").Append(superCall).Append(";\n\n");
                sb.Append(inner).Append("return ret;\n");
            }
            sb.Append(indent).Append("}\n");
            return sb.ToString();
        }

        private static IEnumerable<string> SplitParameters(string parameters)
        {
            int depth = 0;
            var current = new StringBuilder();
            foreach (char c in parameters)
            {
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                if (c == ',' && depth == 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                else current.Append(c);
            }
            if (current.ToString().Trim().Length > 0) yield return current.ToString();
        }

        /// <summary>Kernel methods (xRecord, FormRun, FormDataSource, FormDataObject, form controls) offered by Visual Studio's Override menu.</summary>
        private static Dictionary<string, string> KernelSignatures(object node)
        {
            string[] list;
            string typeName = node.GetType().Name;
            if (node is AxTable || node is AxView || node is AxDataEntityView || node is AxMap)
                list = TableOverrides;
            else if (node is AxForm)
                list = FormOverrides;
            else if (typeName.StartsWith("AxFormDataSource") && typeName.EndsWith("Field"))
                list = DataFieldOverrides;
            else if (typeName.StartsWith("AxFormDataSource"))
                list = DataSourceOverrides;
            else if (node is AxFormControl)
                list = typeName.Contains("Button") ? ButtonOverrides : ControlOverrides;
            else
                list = new string[0];
            return list.ToDictionary(s => Regex.Match(s, @"([A-Za-z_]\w*)\s*\(").Groups[1].Value, s => s, StringComparer.OrdinalIgnoreCase);
        }

        private static readonly string[] TableOverrides =
        {
            "public boolean aosValidateDelete()", "public boolean aosValidateInsert()", "public boolean aosValidateRead()", "public boolean aosValidateUpdate()",
            "public str caption()", "public void clear()", "public void defaultField(FieldId _fieldId)", "public void defaultRow()", "public void delete()",
            "public void initValue()", "public void insert()", "public void modifiedField(FieldId _fieldId)",
            "public void modifiedFieldValue(FieldName _fieldName, int _arrayIndex = 1)", "public void postLoad()", "public void renamePrimaryKey()",
            "public void reread()", "public str toolTipField(FieldId _fieldId)", "public str toolTipRecord()", "public void update()",
            "public boolean validateDelete()", "public boolean validateField(FieldId _fieldIdToCheck)",
            "public boolean validateFieldValue(FieldName _fieldName, int _arrayIndex = 1)", "public boolean validateWrite()",
        };

        private static readonly string[] FormOverrides =
        {
            "public void activate(boolean _active)", "public boolean canClose()", "public void close()", "public void closeCancel()", "public void closeOk()",
            "public void firstField(int _flags = 1)", "public void init()", "public void lastField(int _flags = 1)", "public void nextField(int _flags = 1)",
            "public void prevField(int _flags = 1)", "public void resize(int _width, int _height)", "public void run()", "public int task(int _taskId)",
        };

        private static readonly string[] DataSourceOverrides =
        {
            "public int active()", "public void create(boolean _append = false)", "public void cursorNotify(int _event)", "public void delete()",
            "public void executeQuery()", "public void init()", "public void initValue()", "public boolean leaveRecord(boolean _forceUpdate = false)",
            "public void linkActive()", "public void refresh()", "public void removeFilter()", "public void reread()",
            "public void research(boolean _retainPosition = false)", "public boolean validateDelete()", "public boolean validateWrite()", "public void write()",
        };

        private static readonly string[] DataFieldOverrides =
        {
            "public void jumpRef()", "public void lookup(FormControl _formControl, str _filterStr)", "public void modified()",
            "public boolean validate()",
        };

        private static readonly string[] ButtonOverrides =
        {
            "public void clicked()", "public void gotFocus()", "public void lostFocus()",
        };

        private static readonly string[] ControlOverrides =
        {
            "public void gotFocus()", "public void jumpRef()", "public void lookup()", "public void lostFocus()", "public boolean modified()",
            "public void textChange()", "public boolean validate()",
        };
    }
}
