using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Designer drag and drop, as in Visual Studio (its MergeReference rules): an Application Explorer element dropped on a
    // node creates the child that refers to it (EDT on a table: a field of that type; menu item on a privilege: an entry
    // point; ...), a node dragged inside the designer adds a reference (field to a field group, data source field to a form
    // design) or moves it, and an element dropped on a property row sets the property.
    public sealed partial class MetaModelBridge
    {
        private static readonly string[] MenuItemTypes = { "AxMenuItemDisplay", "AxMenuItemAction", "AxMenuItemOutput" };
        private static readonly string[] TableLikeTypes = { "AxTable", "AxView", "AxDataEntityView", "AxMap", "AxAggregateDataEntity" };
        private static readonly string[] QuerySourceTypes = { "AxTable", "AxView", "AxDataEntityView", "AxAggregateDataEntity" };
        private static readonly string[] FormControlTargets = { "AxFormDesign", "AxFormGroupControl", "AxFormTabPageControl", "AxFormButtonGroupControl" };
        private static readonly string[] BoundControlTargets = { "AxFormDesign", "AxFormGroupControl", "AxFormGridControl", "AxFormTabPageControl", "AxFormTableControl" };

        private sealed class DropContext
        {
            public MetaModelBridge Bridge;
            public object Element;
            public object Node;
            public string SourceType;
            public string SourceName;
            public IList Items;
            /// <summary>Form control names of the whole form, when the drop creates a control.</summary>
            public HashSet<string> ControlNames;
            public string Error;
        }

        private sealed class DropRule
        {
            public string[] Targets;
            public string[] Sources;
            /// <summary>Collection of the target the new item goes to; null for rules that set a property of the target.</summary>
            public string Folder;
            public Func<DropContext, object> Create;
        }

        private static readonly DropRule[] DropRules =
        {
            // Tables and table extensions
            Rule(new[] { "AxTable", "AxTableExtension" }, new[] { "AxEdt" }, "Fields", c => c.Bridge.FieldFromEdt(c)),
            Rule(new[] { "AxTable", "AxTableExtension" }, new[] { "AxEnum" }, "Fields", c => Named(new AxTableFieldEnum { EnumType = c.SourceName }, c)),
            Rule(new[] { "AxTable", "AxTableExtension" }, new[] { "AxTable" }, "Relations", c => Named(new AxTableRelation { RelatedTable = c.SourceName }, c)),
            Rule(new[] { "AxTable" }, new[] { "AxTable" }, "DeleteActions", c => Named(new AxTableDeleteAction { Table = c.SourceName }, c)),
            Rule(new[] { "AxMap" }, new[] { "AxTable" }, "Mappings", c => Named(new AxTableMapping { MappingTable = c.SourceName }, c)),
            // Forms and form extensions
            Rule(new[] { "AxForm", "AxFormExtension" }, TableLikeTypes, "DataSources", c => c.Bridge.FormDataSource(c, c.SourceType, c.SourceName, c.SourceName)),
            Rule(new[] { "AxForm" }, new[] { "AxQuery" }, "DataSources", c => c.Bridge.FormFromQuery(c)),
            Rule(FormControlTargets, MenuItemTypes, "Controls", c => NamedControl(new AxFormMenuFunctionButtonControl
            {
                MenuItemName = c.SourceName, MenuItemType = MenuItemTypeOf(c.SourceType),
            }, c)),
            // Queries
            Rule(new[] { "AxQuerySimple" }, QuerySourceTypes, "DataSources", c => Named(new AxQuerySimpleRootDataSource { Table = c.SourceName }, c)),
            Rule(new[] { "AxQuerySimpleRootDataSource", "AxQuerySimpleEmbeddedDataSource" }, QuerySourceTypes, "DataSources",
                c => Named(new AxQuerySimpleEmbeddedDataSource { Table = c.SourceName }, c)),
            // Menus
            Rule(new[] { "AxMenu", "AxMenuElementSubMenu" }, MenuItemTypes, "Elements", c => Named(new AxMenuElementMenuItem
            {
                MenuItemName = c.SourceName, MenuItemType = MenuItemTypeOf(c.SourceType),
            }, c)),
            Rule(new[] { "AxMenu", "AxMenuElementSubMenu" }, new[] { "AxMenu" }, "Elements", c => Named(new AxMenuElementMenuReference { MenuName = c.SourceName }, c)),
            Rule(new[] { "AxMenu", "AxMenuElementSubMenu" }, new[] { "AxTile" }, "Elements", c => Named(new AxMenuElementTile { Tile = c.SourceName }, c)),
            // Security
            Rule(new[] { "AxSecurityPrivilege" }, MenuItemTypes, "EntryPoints", c => Named(new AxSecurityEntryPointReference
            {
                ObjectName = c.SourceName, ObjectType = EntryPointTypeOf(c.SourceType), Grant = DefaultGrant(EntryPointTypeOf(c.SourceType)),
            }, c)),
            Rule(new[] { "AxSecurityPrivilege" }, new[] { "AxService" }, "EntryPoints", c => c.Bridge.ServiceEntryPoints(c)),
            Rule(new[] { "AxSecurityPrivilege" }, new[] { "AxDataEntityView" }, "DataEntityPermissions", c => Granted(new AxSecurityDataEntityPermission(), c)),
            Rule(new[] { "AxSecurityPrivilege", "AxSecurityRole", "AxSecurityRoleExtension" }, new[] { "AxTable" }, "DirectAccessPermissions", c => Granted(new AxSecurityDataEntityReference(), c)),
            Rule(new[] { "AxSecurityPrivilege" }, new[] { "AxForm" }, "FormControlOverrides", c => Exact(new AxSecurityFormControlReferenceCollection(), c)),
            Rule(new[] { "AxSecurityDuty", "AxSecurityDutyExtension", "AxSecurityRole", "AxSecurityRoleExtension" }, new[] { "AxSecurityPrivilege" }, "Privileges", c => Exact(new AxSecurityPrivilegeReference(), c)),
            Rule(new[] { "AxSecurityRole", "AxSecurityRoleExtension" }, new[] { "AxSecurityDuty" }, "Duties", c => Exact(new AxSecurityDutyReference(), c)),
            Rule(new[] { "AxSecurityRole" }, new[] { "AxSecurityRole" }, "SubRoles", c => Exact(new AxSecurityRoleReference(), c)),
            // Services, configuration keys, entities, reports, workflow, analytics
            Rule(new[] { "AxService" }, new[] { "AxClass" }, null, c => SetProperty(c, "Class")),
            Rule(new[] { "AxServiceGroup" }, new[] { "AxService" }, "Services", c => Named(new AxServiceGroupService { Service = c.SourceName }, c)),
            Rule(new[] { "AxConfigurationKeyGroup" }, new[] { "AxConfigurationKey" }, "ConfigurationKeys", c => Exact(new AxConfigurationKeyReference(), c)),
            Rule(new[] { "AxCompositeDataEntityView" }, new[] { "AxDataEntityView" }, "RootDataEntities", c => Named(new AxDataEntityViewReferenceRoot { DataEntity = c.SourceName }, c)),
            Rule(new[] { "AxDataEntityViewReferenceRoot", "AxDataEntityViewReferenceEmbedded" }, new[] { "AxDataEntityView" }, "EmbeddedDataEntities",
                c => Named(new AxDataEntityViewReferenceEmbedded { DataEntity = c.SourceName }, c)),
            Rule(new[] { "AxReport" }, new[] { "AxQuery", "AxClass" }, "DataSets", c => c.Bridge.ReportDataSet(c)),
            Rule(MenuItemTypes, new[] { "AxForm", "AxClass", "AxReport", "AxQuery" }, null, c => MenuItemTarget(c)),
            Rule(new[] { "AxTile" }, new[] { "AxKPI" }, null, c => SetProperty(c, "KPI")),
            Rule(new[] { "AxTile" }, MenuItemTypes, null, c => { ((AxTile)c.Node).MenuItemType = MenuItemTypeOf(c.SourceType); return SetProperty(c, "MenuItemName"); }),
            Rule(new[] { "AxWorkflowTemplate" }, new[] { "AxWorkflowApproval", "AxWorkflowTask", "AxWorkflowAutomatedTask" }, "SupportedElements", c => WorkflowElement(c)),
            Rule(new[] { "AxAggregateMeasurement" }, QuerySourceTypes, "MeasureGroups", c => Named(new AxMeasureGroup { Table = c.SourceName }, c)),
        };

        private static DropRule Rule(string[] targets, string[] sources, string folder, Func<DropContext, object> create)
            => new DropRule { Targets = targets, Sources = sources, Folder = folder, Create = create };

        /// <summary>Drops an Application Explorer element (sourceType/sourceName) or a node of the same element (sourcePath) on a node.</summary>
        public string DropOnNodeJson(string objectType, string objectName, string[] path, bool onBaseNode, string sourceType, string sourceName, string[] sourcePath)
        {
            var json = new JavaScriptSerializer();
            object element = ReadEditableElement(objectType, objectName, out string model, out string error);
            if (element == null)
                return json.Serialize(Failure(error));
            string message = sourcePath != null && sourcePath.Length > 0
                ? DropNode(element, path ?? new string[0], sourcePath, out error)
                : onBaseNode
                    ? DropOnBaseNode(objectType, objectName, element, path ?? new string[0], sourceType, sourceName, out error)
                    : DropElement(element, path ?? new string[0], sourceType, sourceName, out error);
            return message == null ? json.Serialize(Failure(error)) : SaveElement(json, objectType, element, model, message);
        }

        /// <summary>Sets a property to a dropped element when the property refers to that kind of element.</summary>
        public string DropOnPropertyJson(string objectType, string objectName, string[] path, string property, string sourceType, string sourceName)
        {
            var json = new JavaScriptSerializer();
            object element = ReadEditableElement(objectType, objectName, out string model, out string error);
            if (element == null)
                return json.Serialize(Failure(error));
            object node = path == null || path.Length == 0 ? element : WalkElementPath(element, path, out error);
            if (node == null)
                return json.Serialize(Failure(error));
            var prop = node.GetType().GetProperty(property ?? "", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || prop.PropertyType != typeof(string) || !prop.CanWrite || !IsDesignerProperty(prop))
                return json.Serialize(Failure("'" + property + "' can't be set by dropping an element."));

            // Properties whose target type follows another property: set that one from the dropped element first.
            if (property == "MenuItemName" && MenuItemTypes.Contains(sourceType))
                node.GetType().GetProperty("MenuItemType")?.SetValue(node, MenuItemTypeOf(sourceType));
            else if (property == "Object" && node is AxMenuItem menuItem && MenuItemObjectTypeOf(sourceType) is MenuItemObjectType objectKind)
                menuItem.ObjectType = objectKind;
            else if (property == "ObjectName" && node is AxSecurityEntryPointReference entryPoint && MenuItemTypes.Contains(sourceType))
                entryPoint.ObjectType = EntryPointTypeOf(sourceType);

            string[] candidates = ReferenceCandidates(node, property, out _);
            if (candidates == null || !candidates.Contains(sourceType))
                return json.Serialize(Failure(property + " doesn't refer to a " + sourceType.Substring(2) + "."));
            prop.SetValue(node, sourceName);
            return SaveElement(json, objectType, element, model, property + " set to " + sourceName + ".");
        }

        /// <summary>Element types each node of an editable element accepts as drops (Add from Application Explorer, drop highlighting).</summary>
        private List<string> AcceptedDrops(object node, string folder, bool onBaseNode)
        {
            string type = node.GetType().Name;
            var accepted = new List<string>();
            foreach (var rule in DropRules)
            {
                if (!rule.Targets.Contains(type) || (folder != null && rule.Folder != folder)) continue;
                if (onBaseNode && !(rule.Folder == "Controls" || rule.Folder == "Elements")) continue;
                accepted.AddRange(rule.Sources.Where(s => !accepted.Contains(s)));
            }
            return accepted;
        }

        private string DropElement(object element, string[] path, string sourceType, string sourceName, out string error)
        {
            if (!ResolveDropTarget(element, path, out object node, out string folder, out error))
                return null;
            var rule = DropRules.FirstOrDefault(r => r.Targets.Contains(node.GetType().Name) && r.Sources.Contains(sourceType) && (folder == null || r.Folder == folder));
            if (rule == null)
            {
                error = sourceType.Substring(2) + " " + sourceName + " can't be dropped on " + (folder ?? node.GetType().Name.Substring(2)) + ".";
                return null;
            }
            IList items = null;
            if (rule.Folder != null && !(node.GetType().GetProperty(rule.Folder)?.GetValue(node) is IList list && (items = list) != null))
            {
                error = node.GetType().Name.Substring(2) + " has no " + rule.Folder + ".";
                return null;
            }
            var context = new DropContext
            {
                Bridge = this, Element = element, Node = node, SourceType = sourceType, SourceName = sourceName, Items = items,
                ControlNames = rule.Folder == "Controls" ? FormControlNames(element) : null,
            };
            object created = rule.Create(context);
            if (created == null)
            {
                error = context.Error ?? "Nothing was added.";
                return null;
            }
            // Rules that change the target itself (properties, a form's query) return the target.
            if (rule.Folder == null || ReferenceEquals(created, node))
                return context.Error ?? "Set from " + sourceName + ".";
            foreach (object item in created as IEnumerable<object> ?? new[] { created })
                items.Add(item);
            var names = (created as IEnumerable<object> ?? new[] { created }).Select(ItemName).ToList();
            return "Added " + string.Join(", ", names) + " to " + rule.Folder + "." + (context.Error != null ? " " + context.Error : "");
        }

        /// <summary>
        /// An extension's designer shows the base element's nodes: a drop there becomes an extension item placed under that
        /// node (form extension controls, menu extension elements).
        /// </summary>
        private string DropOnBaseNode(string objectType, string objectName, object extension, string[] path, string sourceType, string sourceName, out string error)
        {
            error = null;
            string baseType = BaseTypeOfExtension(objectType);
            object baseElement = baseType == null ? null : ReadElementCachedBase(baseType, objectName.Split('.')[0]);
            if (baseElement == null || !ResolveDropTarget(baseElement, path, out object node, out string folder, out error))
            {
                error = error ?? "The base element isn't available.";
                return null;
            }
            var rule = DropRules.FirstOrDefault(r => r.Targets.Contains(node.GetType().Name) && r.Sources.Contains(sourceType)
                && (r.Folder == "Controls" || r.Folder == "Elements"));
            string parent = node is AxFormDesign ? null : ItemName(node);
            if (rule == null)
            {
                error = sourceType.Substring(2) + " " + sourceName + " can't be dropped here. Drop it on the extension's own nodes.";
                return null;
            }
            var context = new DropContext
            {
                Bridge = this, Element = extension, Node = node, SourceType = sourceType, SourceName = sourceName, Items = new ArrayList(),
                ControlNames = rule.Folder == "Controls" ? FormControlNames(extension) : null,
            };
            object created = rule.Create(context);
            if (created == null)
            {
                error = context.Error ?? "Nothing was added.";
                return null;
            }
            string name = ItemName(created);
            if (extension is AxFormExtension formExtension && created is AxFormControl control)
            {
                formExtension.Controls.Add(new AxFormExtensionControl { Name = name, FormControl = control, Parent = parent, PositionType = ExtensionItemPositionType.End });
                return "Added " + name + " to " + (parent ?? "the design") + ".";
            }
            if (extension is AxMenuExtension menuExtension && created is AxMenuElement menuElement && node is AxMenuElementSubMenu)
            {
                // Name forwards to MenuElement, so the element must be set first.
                var wrapper = new AxMenuExtensionElement { MenuElement = menuElement };
                wrapper.Name = name;
                wrapper.Parent = parent ?? "";
                wrapper.PreviousSibling = "";
                wrapper.PositionType = ExtensionItemPositionType.End;
                menuExtension.Elements.Add(wrapper);
                return "Added " + name + " to " + parent + ".";
            }
            error = "Drop it on a submenu of the menu.";
            return null;
        }

        /// <summary>A node of the same element dropped on another: field references, bound form controls, or moving it.</summary>
        private string DropNode(object element, string[] targetPath, string[] sourcePath, out string error)
        {
            error = null;
            if (sourcePath.Length < 2)
            {
                error = "Drag a node inside a folder.";
                return null;
            }
            object source = WalkElementPath(element, sourcePath, out error);
            if (source == null)
                return null;
            if (!ResolveDropTarget(element, targetPath, out object target, out string folder, out error))
                return null;
            string sourceFolder = sourcePath[sourcePath.Length - 2];
            string sourceName = sourcePath[sourcePath.Length - 1];

            // Same folder: move before the target.
            if (folder == null && targetPath.Length == sourcePath.Length && targetPath.Take(targetPath.Length - 1).SequenceEqual(sourcePath.Take(sourcePath.Length - 1)))
                return MoveBefore(element, sourcePath, targetPath[targetPath.Length - 1], out error);

            // A field of the element on a field group, index, entity key or range.
            if (sourceFolder == "Fields" && sourcePath.Length == 2 && (folder == null || folder == "Fields" || folder == "Ranges"))
            {
                string field = sourceName;
                switch (target)
                {
                    case AxTableFieldGroup group:
                        return AddFieldReference(group.Fields, new AxTableFieldGroupField { DataField = field }, field, group.Name, out error);
                    case AxTableIndex index:
                        return AddFieldReference(index.Fields, new AxTableIndexField { DataField = field }, field, index.Name, out error);
                    case AxTableFullTextIndex fullText:
                        return AddFieldReference(fullText.Fields, new AxTableIndexField { DataField = field }, field, fullText.Name, out error);
                    case AxDataEntityViewKey key:
                        return AddFieldReference(key.Fields, new AxDataEntityViewKeyField { DataField = field }, field, key.Name, out error);
                    case AxDataEntityView entity when folder == "Ranges":
                        return AddFieldReference(entity.Ranges, new AxDataEntityViewRange { Name = field, Field = field }, field, "Ranges", out error);
                }
            }

            // A field of a query data source on that data source's ranges, order by, group by or having.
            if (sourceFolder == "Fields" && source is AxQuerySimpleDataSourceField && target is AxQuerySimpleDataSource dataSource)
            {
                string owner = sourcePath[sourcePath.Length - 3];
                string field = (source as AxQuerySimpleDataSourceField)?.Field ?? sourceName;
                if (!string.Equals(owner, dataSource.Name, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Drop the field on its own data source (" + owner + ").";
                    return null;
                }
                var root = dataSource as AxQuerySimpleRootDataSource;
                switch (folder ?? "Ranges")
                {
                    case "Ranges": return AddFieldReference(dataSource.Ranges, new AxQuerySimpleDataSourceRange { Name = field, Field = field }, field, "Ranges", out error);
                    case "OrderBy" when root != null: return AddFieldReference(root.OrderBy, new AxQuerySimpleOrderByField { Name = field, Field = field, DataSource = owner }, field, "Order By", out error);
                    case "GroupBy" when root != null: return AddFieldReference(root.GroupBy, new AxQuerySimpleGroupByField { Name = field, Field = field, DataSource = owner }, field, "Group By", out error);
                    case "Having" when root != null: return AddFieldReference(root.Having, new AxQuerySimpleHavingPredicate { Name = field, Field = field, DataSource = owner }, field, "Having", out error);
                }
            }

            // A data source field on an entity's or view's Fields: a field mapped to it.
            if (sourceFolder == "Fields" && source is AxQuerySimpleDataSourceField queryField && folder == "Fields" && (target is AxDataEntityView || target is AxView))
            {
                string owner = sourcePath[sourcePath.Length - 3];
                string field = queryField.Field ?? sourceName;
                var fields = (IList)target.GetType().GetProperty("Fields").GetValue(target);
                string name = UniqueName(fields, field);
                object mapped = target is AxDataEntityView
                    ? new AxDataEntityViewMappedField { Name = name, DataSource = owner, DataField = field }
                    : (object)new AxViewFieldBound { Name = name, DataSource = owner, DataField = field };
                fields.Add(mapped);
                return "Added field " + name + " mapped to " + owner + "." + field + ".";
            }

            // A form data source field on the design or a container: a control bound to it.
            if (element is AxForm form && source is AxFormDataSourceField dsField && sourcePath[0] == "DataSources" && BoundControlTargets.Contains(target.GetType().Name))
            {
                string ds = sourcePath[1];
                var controls = (IList)target.GetType().GetProperty("Controls").GetValue(target);
                var control = BoundControl(form, ds, dsField.DataField ?? sourceName);
                SetItemName(control, UniqueControlName(FormControlNames(element), ds + "_" + (dsField.DataField ?? sourceName)));
                controls.Add(control);
                return "Added " + ItemName(control) + " bound to " + ds + "." + (dsField.DataField ?? sourceName) + ".";
            }

            // A form control on another container: move it there.
            if (source is AxFormControl movedControl && sourceFolder == "Controls" && target.GetType().GetProperty("Controls")?.GetValue(target) is IList targetControls
                && !ReferenceEquals(source, target))
            {
                if (IsDescendant(source, target))
                {
                    error = "A control can't be moved into itself.";
                    return null;
                }
                if (!TryResolveCollection(element, sourcePath.Take(sourcePath.Length - 1).ToArray(), out object from, out _, out error))
                    return null;
                ((IList)from).Remove(movedControl);
                targetControls.Add(movedControl);
                return "Moved " + movedControl.Name + " to " + (ItemName(target) ?? "the design") + ".";
            }

            error = "'" + sourceName + "' can't be dropped there.";
            return null;
        }

        /// <summary>The node a drop lands on; when it is a folder, its owner and the folder's name.</summary>
        private static bool ResolveDropTarget(object element, string[] path, out object node, out string folder, out string error)
        {
            node = null;
            folder = null;
            error = null;
            if (path.Length == 0)
            {
                node = element;
                return true;
            }
            if (TryResolveCollection(element, path, out _, out _, out _))
            {
                folder = path[path.Length - 1];
                node = path.Length == 1 ? element : WalkElementPath(element, path.Take(path.Length - 1).ToArray(), out error);
                return node != null;
            }
            node = WalkElementPath(element, path, out error);
            return node != null;
        }

        private string MoveBefore(object element, string[] sourcePath, string beforeName, out string error)
        {
            if (!TryResolveCollection(element, sourcePath.Take(sourcePath.Length - 1).ToArray(), out object collection, out _, out error))
                return null;
            var items = (IList)collection;
            object item = items.Cast<object>().FirstOrDefault(o => string.Equals(ItemName(o), sourcePath[sourcePath.Length - 1], StringComparison.OrdinalIgnoreCase));
            object before = items.Cast<object>().FirstOrDefault(o => string.Equals(ItemName(o), beforeName, StringComparison.OrdinalIgnoreCase));
            if (item == null || before == null || ReferenceEquals(item, before))
            {
                error = "Nothing to move.";
                return null;
            }
            items.Remove(item);
            items.Insert(items.IndexOf(before), item);
            return "Moved " + ItemName(item) + " before " + ItemName(before) + ".";
        }

        private static string AddFieldReference(IList items, object reference, string field, string target, out string error)
        {
            error = null;
            if (items.Cast<object>().Any(o => string.Equals(o.GetType().GetProperty("DataField")?.GetValue(o) as string ?? o.GetType().GetProperty("Field")?.GetValue(o) as string,
                field, StringComparison.OrdinalIgnoreCase)))
            {
                error = field + " is already in " + target + ".";
                return null;
            }
            if (reference.GetType().GetProperty("Name") is PropertyInfo name && name.CanWrite && name.GetValue(reference) == null)
                name.SetValue(reference, field);
            items.Add(reference);
            return "Added " + field + " to " + target + ".";
        }

        private static bool IsDescendant(object container, object candidate)
        {
            if (!(container.GetType().GetProperty("Controls")?.GetValue(container) is IEnumerable controls)) return false;
            foreach (object child in controls)
                if (ReferenceEquals(child, candidate) || IsDescendant(child, candidate)) return true;
            return false;
        }

        // ── item factories ──

        private static object Named(object item, DropContext c)
        {
            SetItemName(item, UniqueName(c.Items, c.SourceName));
            return item;
        }

        /// <summary>References named exactly like the element (security and configuration key references); one per element.</summary>
        private static object Exact(object item, DropContext c)
        {
            if (c.Items.Cast<object>().Any(o => string.Equals(ItemName(o), c.SourceName, StringComparison.OrdinalIgnoreCase)))
            {
                c.Error = c.SourceName + " is already there.";
                return null;
            }
            SetItemName(item, c.SourceName);
            return item;
        }

        private static object Granted(object item, DropContext c)
        {
            object created = Exact(item, c);
            if (created != null && TryParseGrant("Read", out AccessGrant grant, out _))
                created.GetType().GetProperty("Grant")?.SetValue(created, grant);
            return created;
        }

        private static object NamedControl(AxFormControl control, DropContext c)
        {
            SetItemName(control, UniqueControlName(c.ControlNames, c.SourceName));
            return control;
        }

        private static object SetProperty(DropContext c, string property)
        {
            c.Node.GetType().GetProperty(property).SetValue(c.Node, c.SourceName);
            c.Error = property + " set to " + c.SourceName + ".";
            return c.Node;
        }

        private static object MenuItemTarget(DropContext c)
        {
            var item = (AxMenuItem)c.Node;
            item.ObjectType = MenuItemObjectTypeOf(c.SourceType).Value;
            item.Object = c.SourceName;
            c.Error = "Object set to " + c.SourceType.Substring(2) + " " + c.SourceName + ".";
            return item;
        }

        private static object WorkflowElement(DropContext c)
        {
            var type = c.SourceType == "AxWorkflowApproval" ? WorkflowElementType.Approval
                : c.SourceType == "AxWorkflowTask" ? WorkflowElementType.Task : WorkflowElementType.AutomatedTask;
            return Named(new AxWorkflowElementReference { ElementName = c.SourceName, Type = type }, c);
        }

        /// <summary>A field typed like the EDT: string EDTs give string fields, enum EDTs enum fields, and so on.</summary>
        private object FieldFromEdt(DropContext c)
        {
            if (!(ReadViaMetadataProvider("AxEdt", c.SourceName, out _) is AxEdt edt))
            {
                c.Error = "EDT " + c.SourceName + " not found.";
                return null;
            }
            string kind = edt.GetType().Name.Substring("AxEdt".Length);
            var fieldType = MetadataTypes().FirstOrDefault(t => t.Name == "AxTableField" + kind && !t.IsAbstract);
            if (fieldType == null)
            {
                c.Error = "No table field type for " + edt.GetType().Name + ".";
                return null;
            }
            var field = (AxTableField)Activator.CreateInstance(fieldType);
            field.ExtendedDataType = c.SourceName;
            if (field is AxTableFieldEnum enumField && edt is AxEdtEnum enumEdt)
                enumField.EnumType = enumEdt.EnumType;
            return Named(field, c);
        }

        private object FormDataSource(DropContext c, string tableType, string table, string name)
        {
            var dataSource = new AxFormDataSourceRoot { Table = table };
            SetItemName(dataSource, UniqueName(c.Items, name));
            // Visual Studio lists the table's fields under the data source, ready to be dragged onto the design.
            if (ReadViaMetadataProvider(tableType, table, out _) is object source && source.GetType().GetProperty("Fields")?.GetValue(source) is IEnumerable fields)
            {
                foreach (string field in fields.Cast<object>().Select(ItemName).Where(n => n != null))
                    dataSource.Fields.Add(new AxFormDataSourceField { Name = field, DataField = field });
            }
            return dataSource;
        }

        /// <summary>A query on a form: the form's DataSourceQuery and a data source per root data source of the query.</summary>
        private object FormFromQuery(DropContext c)
        {
            var form = (AxForm)c.Node;
            if (!(ReadViaMetadataProvider("AxQuery", c.SourceName, out _) is AxQuerySimple query))
            {
                c.Error = "Query " + c.SourceName + " not found.";
                return null;
            }
            form.DataSourceQuery = c.SourceName;
            var added = new List<string>();
            c.Items = form.DataSources;
            foreach (var root in query.DataSources)
            {
                if (form.DataSources.Cast<object>().Any(d => string.Equals(ItemName(d), root.Name, StringComparison.OrdinalIgnoreCase))) continue;
                string type = TableLikeTypes.FirstOrDefault(t => ObjectExists(t, root.Table)) ?? "AxTable";
                var dataSource = (AxFormDataSourceRoot)FormDataSource(c, type, root.Table, root.Name);
                form.DataSources.Add(dataSource);
                added.Add(dataSource.Name);
            }
            c.Error = "DataSourceQuery set to " + c.SourceName + (added.Count > 0 ? "; added data source(s) " + string.Join(", ", added) : "") + ".";
            return form;
        }

        /// <summary>One entry point per operation of the service.</summary>
        private object ServiceEntryPoints(DropContext c)
        {
            if (!(ReadViaMetadataProvider("AxService", c.SourceName, out _) is AxService service) || service.ServiceOperations.Count == 0)
            {
                c.Error = "Service " + c.SourceName + " has no operations.";
                return null;
            }
            var entryPoints = new List<object>();
            foreach (var operation in service.ServiceOperations)
            {
                var entryPoint = new AxSecurityEntryPointReference
                {
                    ObjectType = EntryPointType.ServiceOperation, ObjectName = c.SourceName, ObjectChildName = operation.Name,
                    Grant = DefaultGrant(EntryPointType.ServiceOperation),
                };
                entryPoint.Name = UniqueName(c.Items, c.SourceName + "." + operation.Name, entryPoints);
                entryPoints.Add(entryPoint);
            }
            return entryPoints;
        }

        /// <summary>A query dataset, or a report data provider dataset on the provider's temporary table ([SRSReportDataSetAttribute]).</summary>
        private object ReportDataSet(DropContext c)
        {
            var dataSet = new AxReportDataSet();
            if (c.SourceType == "AxQuery")
            {
                dataSet.DataSourceType = DataSourceType.Query;
                dataSet.Query = "SELECT * FROM " + c.SourceName;
            }
            else
            {
                dataSet.DataSourceType = DataSourceType.ReportDataProvider;
                var provider = ReadViaMetadataProvider("AxClass", c.SourceName, out _) as AxClass;
                string table = provider?.Methods.Select(m => Regex.Match(m.Source ?? "", @"SRSReportDataSetAttribute\s*\(\s*(?:tableStr\s*\()?\s*['""]?(\w+)", RegexOptions.IgnoreCase))
                    .FirstOrDefault(m => m.Success)?.Groups[1].Value;
                dataSet.Query = "SELECT * FROM " + c.SourceName + (table != null ? "." + table : "");
                if (table == null) c.Error = "Set the dataset's Query to SELECT * FROM " + c.SourceName + ".<temporary table>.";
            }
            return Named(dataSet, c);
        }

        /// <summary>The control Visual Studio creates for a field: by the field's type (NoYes enums as check boxes).</summary>
        private AxFormControl BoundControl(AxForm form, string dataSourceName, string fieldName)
        {
            string table = form.DataSources.Cast<object>().Select(d => d as AxFormDataSourceRoot)
                .FirstOrDefault(d => d != null && string.Equals(d.Name, dataSourceName, StringComparison.OrdinalIgnoreCase))?.Table;
            AxTableField field = table == null ? null
                : (ReadViaMetadataProvider("AxTable", table, out _) as AxTable)?.Fields.Cast<AxTableField>()
                    .FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            AxFormControl control;
            switch (field)
            {
                case AxTableFieldEnum e when string.Equals(e.EnumType, "NoYes", StringComparison.OrdinalIgnoreCase) || string.Equals(e.ExtendedDataType, "NoYesId", StringComparison.OrdinalIgnoreCase):
                    control = new AxFormCheckBoxControl(); break;
                case AxTableFieldEnum _: control = new AxFormComboBoxControl(); break;
                case AxTableFieldInt _: control = new AxFormIntegerControl(); break;
                case AxTableFieldInt64 _: control = new AxFormInt64Control(); break;
                case AxTableFieldReal _: control = new AxFormRealControl(); break;
                case AxTableFieldDate _: control = new AxFormDateControl(); break;
                case AxTableFieldUtcDateTime _: control = new AxFormDateTimeControl(); break;
                case AxTableFieldTime _: control = new AxFormTimeControl(); break;
                case AxTableFieldGuid _: control = new AxFormGuidControl(); break;
                default: control = new AxFormStringControl(); break;
            }
            control.GetType().GetProperty("DataSource")?.SetValue(control, dataSourceName);
            control.GetType().GetProperty("DataField")?.SetValue(control, fieldName);
            return control;
        }

        // ── names and enums ──

        private static string UniqueName(IList items, string name, IEnumerable<object> pending = null)
        {
            var taken = new HashSet<string>((items?.Cast<object>() ?? Enumerable.Empty<object>()).Concat(pending ?? Enumerable.Empty<object>())
                .Select(ItemName).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            string unique = name;
            for (int n = 1; taken.Contains(unique); n++) unique = name + n;
            return unique;
        }

        private static string UniqueControlName(HashSet<string> names, string name)
        {
            string clean = Regex.Replace(name, @"\W", "_");
            string unique = clean;
            for (int n = 1; names.Contains(unique); n++) unique = clean + n;
            names.Add(unique);
            return unique;
        }

        private static MenuItemType MenuItemTypeOf(string elementType)
            => elementType == "AxMenuItemAction" ? MenuItemType.Action : elementType == "AxMenuItemOutput" ? MenuItemType.Output : MenuItemType.Display;

        private static EntryPointType EntryPointTypeOf(string elementType)
            => elementType == "AxMenuItemAction" ? EntryPointType.MenuItemAction : elementType == "AxMenuItemOutput" ? EntryPointType.MenuItemOutput : EntryPointType.MenuItemDisplay;

        private static MenuItemObjectType? MenuItemObjectTypeOf(string elementType)
        {
            switch (elementType)
            {
                case "AxForm": return MenuItemObjectType.Form;
                case "AxClass": return MenuItemObjectType.Class;
                case "AxReport": return MenuItemObjectType.SSRSReport;
                case "AxQuery": return MenuItemObjectType.Query;
                default: return null;
            }
        }
    }
}
