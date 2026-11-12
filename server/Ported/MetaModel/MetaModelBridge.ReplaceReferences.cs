using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Rename everywhere: after an element or a table field is renamed, the metadata of the elements that name it (menu item
    // objects, data source tables, EDT references, control and entity fields, relation fields) is updated here. X++ code is
    // changed by the extension through the code views.
    public sealed partial class MetaModelBridge
    {
        /// <summary>
        /// Replaces references in one element of your models: to the element oldName of targetType, or, with table, to the
        /// field oldName of that table. Returns how many properties changed.
        /// </summary>
        public string ReplaceReferencesJson(string objectType, string objectName, string targetType, string table, string oldName, string newName)
        {
            var json = new JavaScriptSerializer();
            if (string.IsNullOrWhiteSpace(oldName) || !IsValidName(newName))
                return json.Serialize(Failure("Give oldName and a valid newName."));
            if (string.IsNullOrWhiteSpace(table) && string.IsNullOrWhiteSpace(targetType))
                return json.Serialize(Failure("Give targetType (element rename) or table (field rename)."));
            object element = ReadEditableElement(objectType, objectName, out string model, out string error);
            if (element == null)
                return json.Serialize(Failure(error));
            int count = string.IsNullOrWhiteSpace(table)
                ? ReplaceElementReferences(element, targetType, oldName, newName)
                : ReplaceFieldReferences(element, table, oldName, newName);
            if (count == 0)
                return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["changed"] = 0, ["message"] = objectName + " names no " + oldName + " in its metadata." });
            string result = SaveElement(json, objectType, element, model, "Updated " + count + " reference(s) to " + oldName + " in " + objectName + ".");
            return result.Contains("\"success\":true") ? result.Replace("\"success\":true", "\"success\":true,\"changed\":" + count) : result;
        }

        private static int ReplaceElementReferences(object element, string targetType, string oldName, string newName)
        {
            int count = 0;
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            var metadataAssembly = typeof(AxTable).Assembly;
            bool Matches(object node, string property)
            {
                var candidates = ReferenceCandidates(node, property, out _);
                return candidates != null && candidates.Contains(targetType, StringComparer.Ordinal);
            }

            void Walk(object node)
            {
                if (node == null || node is AxMethod || node.GetType().Assembly != metadataAssembly || !seen.Add(node)) return;
                foreach (var prop in ReferenceProperties(node.GetType()))
                {
                    if (prop.CanWrite && string.Equals(SafeGet(prop, node) as string, oldName, StringComparison.OrdinalIgnoreCase) && Matches(node, prop.Name))
                    {
                        prop.SetValue(node, newName);
                        count++;
                    }
                }
                foreach (var prop in PublicProperties(node.GetType()))
                {
                    if (prop.GetIndexParameters().Length > 0 || prop.PropertyType.IsValueType || prop.PropertyType == typeof(string)) continue;
                    object value = SafeGet(prop, node);
                    if (value is IList list)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            object item = list[i];
                            if (item == null) continue;
                            // Security and workflow references are items named after what they reference.
                            if (NamedReferences.TryGetValue(item.GetType().Name, out var named) && named.Value.Contains(targetType, StringComparer.Ordinal)
                                && string.Equals(ItemName(item), oldName, StringComparison.OrdinalIgnoreCase))
                            {
                                list.RemoveAt(i);
                                SetItemName(item, newName);
                                list.Insert(i, item);
                                count++;
                            }
                            Walk(item);
                        }
                    }
                    else if (!(value is IEnumerable))
                    {
                        Walk(value);
                    }
                }
            }

            Walk(element);
            return count;
        }

        /// <summary>
        /// Fields of the table named through a data source of it (form controls and data source fields, entity fields, query
        /// ranges) or through a relation to it (RelatedField of relation constraints).
        /// </summary>
        private static int ReplaceFieldReferences(object element, string table, string oldField, string newField)
        {
            int count = 0;
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            var metadataAssembly = typeof(AxTable).Assembly;
            var dataSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string Text(object node, string property) =>
                SafeGet(node.GetType().GetProperty(property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance), node) as string;

            void Collect(object node)
            {
                if (node == null || node is AxMethod || node.GetType().Assembly != metadataAssembly || !seen.Add(node)) return;
                if (string.Equals(Text(node, "Table"), table, StringComparison.OrdinalIgnoreCase) && Text(node, "Name") is string name && name.Length > 0)
                    dataSources.Add(name);
                foreach (var child in Children(node)) Collect(child);
            }

            void Walk(object node, string dataSource, bool related)
            {
                if (node == null || node is AxMethod || node.GetType().Assembly != metadataAssembly || !seen.Add(node)) return;
                string own = Text(node, "DataSource");
                if (!string.IsNullOrEmpty(own)) dataSource = own;
                if (string.Equals(Text(node, "Table"), table, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Text(node, "Name")))
                    dataSource = Text(node, "Name");
                if (string.Equals(Text(node, "RelatedTable"), table, StringComparison.OrdinalIgnoreCase))
                    related = true;
                bool ours = dataSource != null && dataSources.Contains(dataSource);
                string typeName = node.GetType().Name;
                foreach (var prop in PublicProperties(node.GetType()))
                {
                    if (prop.PropertyType != typeof(string) || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                    if (!string.Equals(SafeGet(prop, node) as string, oldField, StringComparison.OrdinalIgnoreCase)) continue;
                    bool replace = prop.Name == "DataField" ? ours
                        : prop.Name == "RelatedField" ? related
                        : prop.Name == "Field" && ours && !typeName.Contains("RelationConstraint");
                    if (replace)
                    {
                        prop.SetValue(node, newField);
                        count++;
                    }
                }
                foreach (var child in Children(node)) Walk(child, dataSource, related);
            }

            Collect(element);
            if (dataSources.Count == 0 && !ElementRelatesTo(element, table)) return 0;
            seen.Clear();
            Walk(element, null, false);
            return count;
        }

        private static bool ElementRelatesTo(object element, string table) =>
            element is AxTable t && (t.Relations?.Any(r => string.Equals(r.RelatedTable, table, StringComparison.OrdinalIgnoreCase)) ?? false)
            || element is AxTableExtension e && (e.Relations?.Any(r => string.Equals(r.RelatedTable, table, StringComparison.OrdinalIgnoreCase)) ?? false);

        private static IEnumerable<object> Children(object node)
        {
            foreach (var prop in PublicProperties(node.GetType()))
            {
                if (prop.GetIndexParameters().Length > 0 || prop.PropertyType.IsValueType || prop.PropertyType == typeof(string)) continue;
                object value = SafeGet(prop, node);
                if (value is IList list)
                {
                    foreach (object item in list.Cast<object>().ToList())
                        if (item != null) yield return item;
                }
                else if (value != null && !(value is IEnumerable))
                {
                    yield return value;
                }
            }
        }
    }
}
