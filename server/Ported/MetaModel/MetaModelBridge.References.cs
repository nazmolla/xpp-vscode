using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace XppVsCode.Server.MetaModel
{
    // Go To: the elements a designer node refers to (EDT, enum, table, menu item, class, ...), keyed for the designer's
    // right-click commands (xpp.goTo.<key>). Only references whose target exists are returned, so the menu shows nothing unused.
    public sealed partial class MetaModelBridge
    {
        private static readonly string[] EdtTargets = { "AxEdt" };
        private static readonly string[] EnumTargets = { "AxEnum" };
        private static readonly string[] ClassTargets = { "AxClass" };
        private static readonly string[] TableTargets = { "AxTable", "AxView", "AxDataEntityView", "AxMap" };
        private static readonly string[] DisplayFirst = { "AxMenuItemDisplay", "AxMenuItemOutput", "AxMenuItemAction" };
        private static readonly string[] OutputFirst = { "AxMenuItemOutput", "AxMenuItemDisplay", "AxMenuItemAction" };
        private static readonly string[] ActionFirst = { "AxMenuItemAction", "AxMenuItemDisplay", "AxMenuItemOutput" };

        // Order of the keys in a node's references; the first one is what F12 opens.
        private static readonly string[] ReferenceKeyOrder =
        {
            "ExtendedDataType", "EnumType", "BaseEdt", "BaseTable", "BaseObject", "Table", "RelatedTable", "ReferenceTable", "MappingTable",
            "PrimaryTable", "StagingTable", "MenuItem", "Form", "Class", "Report", "Query", "Part", "Document", "DocumentMenuItem",
            "ActionMenuItem", "SubmitToWorkflowMenuItem", "ResubmitMenuItem", "DelegateMenuItem", "CancelMenuItem", "FormRef", "ListPageRef",
            "ReportRef", "PreviewPartRef", "HyperLinkMenuItem", "SourceEDT", "EnumTypeParameter", "Privilege", "Duty", "Role", "DataEntity",
            "Service", "Menu", "Tile", "KPI", "WorkflowElement", "WorkflowType", "WorkflowCategory", "ProviderClass", "PresenceClass",
            "ControlClass", "InteractionClass", "FeatureClass",
        };

        // Items whose own name is the reference (security references, workflow supported elements).
        private static readonly Dictionary<string, KeyValuePair<string, string[]>> NamedReferences = new Dictionary<string, KeyValuePair<string, string[]>>(StringComparer.Ordinal)
        {
            ["AxSecurityPrivilegeReference"] = new KeyValuePair<string, string[]>("Privilege", new[] { "AxSecurityPrivilege" }),
            ["AxSecurityDutyReference"] = new KeyValuePair<string, string[]>("Duty", new[] { "AxSecurityDuty" }),
            ["AxSecurityRoleReference"] = new KeyValuePair<string, string[]>("Role", new[] { "AxSecurityRole" }),
            ["AxSecurityDataEntityPermission"] = new KeyValuePair<string, string[]>("DataEntity", new[] { "AxDataEntityView" }),
            ["AxSecurityDataEntityReference"] = new KeyValuePair<string, string[]>("DataEntity", new[] { "AxDataEntityView", "AxTable", "AxView" }),
            ["AxWorkflowElementReference"] = new KeyValuePair<string, string[]>("WorkflowElement", new[] { "AxWorkflowApproval", "AxWorkflowTask", "AxWorkflowAutomatedTask" }),
        };

        private static readonly Dictionary<Type, PropertyInfo[]> _referenceProperties = new Dictionary<Type, PropertyInfo[]>();
        // Element names per type (case-insensitive key -> the element's own spelling); refreshed every minute for elements created elsewhere.
        private readonly Dictionary<string, Dictionary<string, string>> _typeNames = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private DateTime _typeNamesLoaded = DateTime.MinValue;

        /// <summary>The node's references by key (first property wins per key), in <see cref="ReferenceKeyOrder"/>; null when none.</summary>
        private Dictionary<string, object> NodeReferences(object node)
        {
            var found = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            if (NamedReferences.TryGetValue(node.GetType().Name, out var named) && ItemName(node) is string itemName)
            {
                var target = ResolveReference(named.Value, itemName);
                if (target != null) found[named.Key] = target;
            }
            foreach (var prop in ReferenceProperties(node.GetType()))
            {
                if (!(SafeGet(prop, node) is string value) || value.Length == 0) continue;
                var reference = PropertyReference(node, prop.Name, value, out string key);
                if (reference != null && !found.ContainsKey(key)) found[key] = reference;
            }
            if (found.Count == 0) return null;
            return OrderReferences(found.ToDictionary(k => k.Key, k => (object)k.Value, StringComparer.Ordinal));
        }

        private static Dictionary<string, object> OrderReferences(Dictionary<string, object> refs)
        {
            var ordered = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kvp in refs.OrderBy(k => KeyRank(k.Key))) ordered[kvp.Key] = kvp.Value;
            return ordered;
        }

        /// <summary>What a property value refers to, with the key naming it; labels open the label file at the label.</summary>
        private Dictionary<string, object> PropertyReference(object node, string property, string value, out string key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.StartsWith("@", StringComparison.Ordinal))
            {
                key = "Label";
                return LabelTarget(value);
            }
            string[] candidates = ReferenceCandidates(node, property, out key);
            return candidates == null ? null : ResolveReference(candidates, value.Trim());
        }

        private Dictionary<string, object> ResolveReference(string[] candidates, string name)
        {
            foreach (string type in candidates)
            {
                string actual = ElementName(type, name);
                if (actual != null)
                    return new Dictionary<string, object> { ["objectType"] = type, ["objectName"] = actual };
            }
            return null;
        }

        /// <summary>The element's name as the element spells it, or null when there is no such element.</summary>
        private string ElementName(string type, string name)
        {
            if (DateTime.UtcNow - _typeNamesLoaded > TimeSpan.FromMinutes(1))
            {
                _typeNames.Clear();
                _typeNamesLoaded = DateTime.UtcNow;
            }
            if (!_typeNames.TryGetValue(type, out var names))
            {
                IEnumerable<string> all = null;
                try { all = NamesForType(type); }
                catch (Exception ex) { LogIgnored("Listing " + type + " names", ex); }
                names = all?.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                _typeNames[type] = names;
            }
            if (names != null)
                return names.TryGetValue(name, out string actual) ? actual : null;
            try { return ObjectExists(type, name) ? name : null; }
            catch { return null; }
        }

        // '@File:Id' (label file File) or the legacy '@SYS123' (label file SYS, searched as SYS123).
        private static Dictionary<string, object> LabelTarget(string value)
        {
            var match = LabelReference.Match(value);
            if (!match.Success) return null;
            string file = match.Groups["file"].Value;
            string id = value.Contains(":") ? match.Groups["id"].Value : value.Substring(1);
            return new Dictionary<string, object> { ["objectType"] = "AxLabelFile", ["objectName"] = file, ["labelId"] = id };
        }

        /// <summary>Element types a property can name, most likely first; null for properties that don't name an element.</summary>
        private static string[] ReferenceCandidates(object node, string property, out string key)
        {
            key = property;
            string owner = node.GetType().Name;
            switch (property)
            {
                case "ExtendedDataType":
                case "SourceEDT":
                    return EdtTargets;
                case "EnumType":
                case "EnumTypeParameter":
                    return EnumTargets;
                case "Extends":
                    if (owner.StartsWith("AxEdt", StringComparison.Ordinal)) { key = "BaseEdt"; return EdtTargets; }
                    if (owner.StartsWith("AxTable", StringComparison.Ordinal)) { key = "BaseTable"; return new[] { "AxTable" }; }
                    return null;
                case "Table":
                case "RelatedTable":
                case "ReferenceTable":
                case "MappingTable":
                case "PrimaryTable":
                    return TableTargets;
                case "DataManagementStagingTable":
                    key = "StagingTable";
                    return new[] { "AxTable" };
                case "PreviewPartReference":
                    key = "PreviewPartRef";
                    return DisplayFirst;
                case "FormRef":
                case "ListPageRef":
                case "PreviewPartRef":
                case "HyperLinkMenuItem":
                case "DocumentMenuItem":
                case "SubmitToWorkflowMenuItem":
                    return DisplayFirst;
                case "ReportRef":
                    return OutputFirst;
                case "ResubmitMenuItem":
                case "DelegateMenuItem":
                case "ActionMenuItem":
                case "CancelMenuItem":
                    return ActionFirst;
                case "MenuItemName":
                    key = "MenuItem";
                    switch (Sibling(node, "MenuItemType"))
                    {
                        case "Output": return OutputFirst;
                        case "Action": return ActionFirst;
                        default: return DisplayFirst;
                    }
                case "Object":
                    switch (Sibling(node, "ObjectType"))
                    {
                        case "Form": key = "Form"; return new[] { "AxForm" };
                        case "Class": key = "Class"; return ClassTargets;
                        case "SSRSReport": key = "Report"; return new[] { "AxReport" };
                        case "Query": key = "Query"; return new[] { "AxQuery" };
                        case "InfoPart": key = "Part"; return new[] { "AxInfoPart" };
                        case "FormPart": key = "Part"; return new[] { "AxFormPart" };
                        case "CueGroup": key = "Part"; return new[] { "AxPartCueGroup" };
                        default: return null;
                    }
                case "ObjectName":
                    string entryPoint = Sibling(node, "ObjectType") ?? "";
                    if (entryPoint.StartsWith("MenuItem", StringComparison.Ordinal)) { key = "MenuItem"; return new[] { "Ax" + entryPoint }; }
                    if (entryPoint == "ServiceOperation") { key = "Service"; return new[] { "AxService" }; }
                    return null;
                case "Query":
                case "DataSourceQuery":
                    key = "Query";
                    return new[] { "AxQuery" };
                case "Form":
                    return new[] { "AxForm" };
                case "Class":
                case "ProviderClass":
                case "PresenceClass":
                case "ControlClass":
                case "InteractionClass":
                case "FeatureClass":
                    return ClassTargets;
                case "Document":
                    return owner.StartsWith("AxWorkflow", StringComparison.Ordinal) ? ClassTargets : null;
                case "ConfigurationKey":
                case "CountryConfigurationKey":
                case "ParentKey":
                    return new[] { "AxConfigurationKey" };
                case "LicenseCode":
                    return new[] { "AxLicenseCode" };
                case "MenuName":
                    key = "Menu";
                    return new[] { "AxMenu" };
                case "KPI":
                    return new[] { "AxKPI" };
                case "Tile":
                    return new[] { "AxTile" };
                case "Service":
                    return new[] { "AxService" };
                case "DataEntity":
                    return new[] { "AxDataEntityView" };
                case "Category":
                    if (!owner.StartsWith("AxWorkflow", StringComparison.Ordinal)) return null;
                    key = "WorkflowCategory";
                    return new[] { "AxWorkflowCategory" };
                case "WorkflowType":
                    return new[] { "AxWorkflowTemplate" };
            }
            return property.EndsWith("EventHandler", StringComparison.Ordinal) ? ClassTargets : null;
        }

        private static string Sibling(object node, string property)
            => SafeGet(node.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance), node)?.ToString();

        private static object SafeGet(PropertyInfo prop, object node)
        {
            if (prop == null) return null;
            try { return prop.GetValue(node); }
            catch { return null; }
        }

        private static int KeyRank(string key)
        {
            int index = Array.IndexOf(ReferenceKeyOrder, key);
            return index >= 0 ? index : key == "Label" ? int.MaxValue : ReferenceKeyOrder.Length;
        }

        // Every property name ReferenceCandidates knows, plus Label (the node's label is offered on the node itself).
        private static readonly HashSet<string> ReferencePropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Label", "ExtendedDataType", "SourceEDT", "EnumType", "EnumTypeParameter", "Extends", "Table", "RelatedTable", "ReferenceTable",
            "MappingTable", "PrimaryTable", "DataManagementStagingTable", "PreviewPartReference", "FormRef", "ListPageRef", "PreviewPartRef",
            "HyperLinkMenuItem", "DocumentMenuItem", "SubmitToWorkflowMenuItem", "ReportRef", "ResubmitMenuItem", "DelegateMenuItem",
            "ActionMenuItem", "CancelMenuItem", "MenuItemName", "Object", "ObjectName", "Query", "DataSourceQuery", "Form", "Class",
            "ProviderClass", "PresenceClass", "ControlClass", "InteractionClass", "FeatureClass", "Document", "ConfigurationKey",
            "CountryConfigurationKey", "ParentKey", "LicenseCode", "MenuName", "KPI", "Tile", "Service", "DataEntity", "Category", "WorkflowType",
        };

        private static PropertyInfo[] ReferenceProperties(Type type)
        {
            lock (_referenceProperties)
            {
                if (!_referenceProperties.TryGetValue(type, out var props))
                {
                    props = PublicProperties(type)
                        .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && IsDesignerProperty(p)
                            && (ReferencePropertyNames.Contains(p.Name) || p.Name.EndsWith("EventHandler", StringComparison.Ordinal)))
                        .GroupBy(p => p.Name).Select(g => g.First())
                        .ToArray();
                    _referenceProperties[type] = props;
                }
                return props;
            }
        }
    }
}
