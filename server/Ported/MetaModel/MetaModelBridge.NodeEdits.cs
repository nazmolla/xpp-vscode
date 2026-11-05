using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Designer Rename, Duplicate and Move of items inside a folder. Names that other parts of the same element use are
    // updated with a rename (field groups, indexes, relations, data source bindings); X++ code is not.
    public sealed partial class MetaModelBridge
    {
        private const string CodeNotUpdated = " X++ code that uses the old name is not changed.";

        // Properties of form, query and entity nodes that name a data source of the same element.
        private static readonly HashSet<string> DataSourceReferenceProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "DataSource", "JoinSource", "JoinDataSource", "TitleDataSource", "FilterDataSource", "HyperLinkDataSource", "WorkflowDataSource",
            "PresenceDataSource", "LinkDataSource",
        };

        private static readonly Dictionary<Type, DataContractSerializer> _cloneSerializers = new Dictionary<Type, DataContractSerializer>();
        private static Type[] _dataContractTypes;

        public string RenameElementNodeJson(string objectType, string objectName, string[] path, string newName)
        {
            var json = new JavaScriptSerializer();
            if (!IsValidName(newName))
                return json.Serialize(Failure("'" + newName + "' is not a valid name."));
            if (!TryOpenFolderItem(objectType, objectName, path, out var target, out string error))
                return json.Serialize(Failure(error));
            if (target.Item is AxMethod)
                return json.Serialize(Failure("Rename a method by changing its name in the code."));
            if (string.Equals(target.Name, newName, StringComparison.Ordinal))
                return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = "Nothing to rename." });
            if (!string.Equals(target.Name, newName, StringComparison.OrdinalIgnoreCase) && NameTaken(target, newName, out error))
                return json.Serialize(Failure(error));

            // The collection is keyed by name: take the item out, rename it and put it back at the same place.
            int index = target.Items.IndexOf(target.Item);
            target.Items.RemoveAt(index);
            SetItemName(target.Item, newName);
            target.Items.Insert(index, target.Item);
            int updated = RenameReferences(target.Element, target.Folder, target.Name, newName);
            string message = "Renamed " + target.Name + " to " + newName + (updated > 0 ? " and updated " + updated + " reference(s) in " + objectName : "") + "." + CodeNotUpdated;
            return SaveElement(json, objectType, target.Element, target.Model, message);
        }

        public string DuplicateElementNodeJson(string objectType, string objectName, string[] path, string newName)
        {
            var json = new JavaScriptSerializer();
            if (!IsValidName(newName))
                return json.Serialize(Failure("'" + newName + "' is not a valid name."));
            if (!TryOpenFolderItem(objectType, objectName, path, out var target, out string error))
                return json.Serialize(Failure(error));
            if (target.Item is AxMethod)
                return json.Serialize(Failure("Copy a method in the code editor."));
            if (NameTaken(target, newName, out error))
                return json.Serialize(Failure(error));

            object copy = CloneMetadata(target.Item, out error);
            if (copy == null)
                return json.Serialize(Failure(error));
            SetItemName(copy, newName);
            string renamed = "";
            if (target.ControlNames != null)
            {
                // Control names are unique in the whole form: the copied container's controls get new names too.
                target.ControlNames.Add(newName);
                int count = RenameCopiedControls(copy.GetType().GetProperty("FormControl")?.GetValue(copy) ?? copy, target.ControlNames);
                if (count > 0) renamed = " Its " + count + " control(s) got new names (a number is added).";
            }
            target.Items.Insert(target.Items.IndexOf(target.Item) + 1, copy);
            return SaveElement(json, objectType, target.Element, target.Model, "Copied " + target.Name + " to " + newName + "." + renamed);
        }

        /// <summary>Moves an item up (negative offset) or down within its folder; the order is what forms, field groups and enums show.</summary>
        public string MoveElementNodeJson(string objectType, string objectName, string[] path, int offset)
        {
            var json = new JavaScriptSerializer();
            if (!TryOpenFolderItem(objectType, objectName, path, out var target, out string error))
                return json.Serialize(Failure(error));
            int index = target.Items.IndexOf(target.Item);
            int to = Math.Max(0, Math.Min(target.Items.Count - 1, index + offset));
            if (to == index)
            {
                string scope = BaseTypeOfExtension(objectType) != null ? " of this extension's items (the base element's items keep their place)" : "";
                return json.Serialize(Failure(target.Name + " is already " + (offset < 0 ? "first" : "last") + scope + "."));
            }
            target.Items.RemoveAt(index);
            target.Items.Insert(to, target.Item);
            return SaveElement(json, objectType, target.Element, target.Model, "Moved " + target.Name + ".");
        }

        private sealed class FolderItem
        {
            public object Element;
            public string Model;
            public IList Items;
            public object Item;
            public string Name;
            public string Folder;
            /// <summary>All control names of the form (forms and form extensions, when the folder holds controls).</summary>
            public HashSet<string> ControlNames;
        }

        private static bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");

        /// <summary>An item of a folder of an element in your own model, read fresh for changing it.</summary>
        private bool TryOpenFolderItem(string objectType, string objectName, string[] path, out FolderItem target, out string error)
        {
            target = null;
            if (path == null || path.Length < 2)
            {
                error = "Select an item inside a folder.";
                return false;
            }
            object element = ReadEditableElement(objectType, objectName, out string model, out error);
            if (element == null)
                return false;
            if (!TryResolveCollection(element, path.Take(path.Length - 1).ToArray(), out object collection, out _, out error))
                return false;
            if (!(collection is IList items))
            {
                error = "Items in this folder can't be changed here.";
                return false;
            }
            string name = path[path.Length - 1];
            object item = items.Cast<object>().FirstOrDefault(o => string.Equals(ItemName(o), name, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                error = "'" + name + "' not found.";
                return false;
            }
            string folder = path[path.Length - 2];
            target = new FolderItem
            {
                Element = element, Model = model, Items = items, Item = item, Name = ItemName(item), Folder = folder,
                ControlNames = folder == "Controls" && (element is AxForm || element is AxFormExtension) ? FormControlNames(element) : null,
            };
            return true;
        }

        private static bool NameTaken(FolderItem target, string name, out string error)
        {
            bool taken = target.ControlNames != null
                ? target.ControlNames.Contains(name)
                : target.Items.Cast<object>().Any(o => string.Equals(ItemName(o), name, StringComparison.OrdinalIgnoreCase));
            error = taken ? "'" + name + "' already exists" + (target.ControlNames != null ? " in the form." : ".") : null;
            return taken;
        }

        /// <summary>Form extension controls wrap the control: both carry the name.</summary>
        private static void SetItemName(object item, string name)
        {
            item.GetType().GetProperty("Name")?.SetValue(item, name);
            var inner = item.GetType().GetProperty("FormControl")?.GetValue(item);
            inner?.GetType().GetProperty("Name")?.SetValue(inner, name);
        }

        /// <summary>Every control name of a form; for a form extension, the base form's and the extension's.</summary>
        private HashSet<string> FormControlNames(object element)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (element is AxForm form)
            {
                CollectControlNames(form.Design, names);
            }
            else if (element is AxFormExtension extension)
            {
                CollectControlNames(extension, names);
                if (ReadElementCachedBase("AxForm", extension.Name.Split('.')[0]) is AxForm baseForm)
                    CollectControlNames(baseForm.Design, names);
            }
            return names;
        }

        private static void CollectControlNames(object container, HashSet<string> names)
        {
            if (!(container?.GetType().GetProperty("Controls")?.GetValue(container) is IEnumerable controls)) return;
            foreach (object control in controls)
            {
                if (control == null) continue;
                if (ItemName(control) is string name) names.Add(name);
                CollectControlNames(control, names);
                CollectControlNames(control.GetType().GetProperty("FormControl")?.GetValue(control), names);
            }
        }

        private static int RenameCopiedControls(object container, HashSet<string> names)
        {
            if (!(container?.GetType().GetProperty("Controls")?.GetValue(container) is IList controls)) return 0;
            int count = 0;
            for (int i = 0; i < controls.Count; i++)
            {
                object control = controls[i];
                string name = ItemName(control);
                if (name != null)
                {
                    int n = 1;
                    while (names.Contains(name + n)) n++;
                    controls.RemoveAt(i);
                    SetItemName(control, name + n);
                    controls.Insert(i, control);
                    names.Add(name + n);
                    count++;
                }
                count += RenameCopiedControls(control, names);
            }
            return count;
        }

        /// <summary>
        /// Updates what names the renamed item inside the same element; returns how many properties changed. Items whose
        /// name follows a changed property (field group fields are named by their field) move back to their place in their folder.
        /// </summary>
        private static int RenameReferences(object element, string folder, string oldName, string newName)
        {
            var applies = RenameRule(element, folder);
            if (applies == null) return 0;
            int count = 0;
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            var metadataAssembly = typeof(AxTable).Assembly;

            void Walk(object node)
            {
                if (node == null || node is AxMethod || node.GetType().Assembly != metadataAssembly || !seen.Add(node)) return;
                foreach (var prop in PublicProperties(node.GetType()))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (prop.PropertyType == typeof(string))
                    {
                        if (prop.CanWrite && applies(node, prop.Name) && string.Equals(SafeGet(prop, node) as string, oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            prop.SetValue(node, newName);
                            count++;
                        }
                    }
                    else if (!prop.PropertyType.IsValueType)
                    {
                        object value = SafeGet(prop, node);
                        if (value is IList list)
                        {
                            for (int i = 0; i < list.Count; i++)
                            {
                                object item = list[i];
                                string before = item == null ? null : ItemName(item);
                                Walk(item);
                                if (before != null && ItemName(item) != before)
                                {
                                    list.RemoveAt(i);
                                    list.Insert(i, item);
                                }
                            }
                        }
                        else if (!(value is IEnumerable))
                        {
                            Walk(value);
                        }
                    }
                }
            }

            Walk(element);
            return count;
        }

        /// <summary>Which properties name an item of the folder, by the node that holds them.</summary>
        private static Func<object, string, bool> RenameRule(object element, string folder)
        {
            switch (folder)
            {
                case "Fields":
                    bool isMap = element.GetType().Name.StartsWith("AxMap", StringComparison.Ordinal);
                    return (node, prop) =>
                    {
                        string owner = node.GetType().Name;
                        switch (prop)
                        {
                            case "DataField": return owner.EndsWith("FieldGroupField", StringComparison.Ordinal) || owner.EndsWith("IndexField", StringComparison.Ordinal) || owner.Contains("KeyField");
                            case "Field": return owner.Contains("RelationConstraint");
                            case "MapFieldTo": return !isMap;
                            case "MapField": return isMap;
                            case "TitleField1":
                            case "TitleField2":
                            case "CountryRegionContextField":
                                return ReferenceEquals(node, element);
                            default: return false;
                        }
                    };
                case "Indexes":
                    return (node, prop) => ReferenceEquals(node, element) && (prop == "PrimaryIndex" || prop == "ClusteredIndex" || prop == "ReplacementKey");
                case "Keys":
                    return (node, prop) => ReferenceEquals(node, element) && (prop == "PrimaryKey" || prop == "ReplacementKey");
                case "Relations":
                    return (node, prop) => prop == "Relation" && node.GetType().Name.Contains("DeleteAction");
                case "DataSources":
                    return (node, prop) => DataSourceReferenceProperties.Contains(prop);
                case "Controls":
                    return (node, prop) => (prop == "Parent" || prop == "PreviousSibling") && node is AxFormExtensionControl;
                default:
                    return null;
            }
        }

        /// <summary>A deep copy through the metadata classes' data contracts.</summary>
        private static object CloneMetadata(object source, out string error)
        {
            error = null;
            try
            {
                DataContractSerializer serializer;
                lock (_cloneSerializers)
                {
                    if (!_cloneSerializers.TryGetValue(source.GetType(), out serializer))
                    {
                        _dataContractTypes = _dataContractTypes ?? MetadataTypes()
                            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && t.GetCustomAttributes(typeof(DataContractAttribute), false).Length > 0)
                            .ToArray();
                        serializer = new DataContractSerializer(source.GetType(), _dataContractTypes);
                        _cloneSerializers[source.GetType()] = serializer;
                    }
                }
                using (var stream = new System.IO.MemoryStream())
                {
                    serializer.WriteObject(stream, source);
                    stream.Position = 0;
                    return serializer.ReadObject(stream);
                }
            }
            catch (Exception ex)
            {
                error = "Can't copy " + source.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
