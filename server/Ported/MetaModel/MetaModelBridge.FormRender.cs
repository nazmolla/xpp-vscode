using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // What the form preview needs to draw controls like the web client: labels of bound fields, field kinds, field group contents.
    public sealed partial class MetaModelBridge
    {
        private sealed class RenderContext
        {
            public Dictionary<string, string> DataSourceTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Dictionary<string, AxTableField>> Fields = new Dictionary<string, Dictionary<string, AxTableField>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, AxTable> Tables = new Dictionary<string, AxTable>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> EdtLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, object> MenuItems = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static RenderContext NewRenderContext(AxForm form)
        {
            var render = new RenderContext();
            foreach (var ds in form.DataSources)
            {
                AddDataSource(render, ds.Name, ds.Table);
                AddNestedDataSources(render, ds.DerivedDataSources, ds.ReferencedDataSources, 0);
            }
            return render;
        }

        // Data sources nested under another: derived ones for table inheritance (DirPartyTable_DirOrganization) and referenced
        // ones for surrogate foreign key lookups (LogisticsElectronicAddress_Phone); controls bind to them by name.
        private static void AddNestedDataSources(RenderContext render, IEnumerable<AxFormDataSourceDerived> derived, IEnumerable<AxFormDataSourceReferenced> referenced, int depth)
        {
            if (depth > 10) return;
            foreach (var ds in derived ?? Enumerable.Empty<AxFormDataSourceDerived>())
            {
                AddDataSource(render, ds.Name, ds.Table);
                AddNestedDataSources(render, ds.DerivedDataSources, ds.ReferencedDataSources, depth + 1);
            }
            foreach (var ds in referenced ?? Enumerable.Empty<AxFormDataSourceReferenced>())
            {
                AddDataSource(render, ds.Name, ds.Table);
                AddNestedDataSources(render, null, ds.ReferencedDataSources, depth + 1);
            }
        }

        private static void AddDataSource(RenderContext render, string name, string table)
        {
            if (!string.IsNullOrEmpty(name) && !render.DataSourceTables.ContainsKey(name))
                render.DataSourceTables[name] = table;
        }

        /// <summary>A control's data source, inherited from the closest parent that sets one.</summary>
        private string EffectiveDataSource(FormControlNode node)
        {
            for (var n = node; n != null; n = n.Parent)
            {
                string ds = GetStringProperty(n.Control, "DataSource");
                if (!string.IsNullOrEmpty(ds)) return ds;
            }
            return null;
        }

        private string RenderTable(RenderContext render, string dataSource)
            => !string.IsNullOrEmpty(dataSource) && render.DataSourceTables.TryGetValue(dataSource, out string table) ? table : null;

        private AxTableField RenderField(RenderContext render, string table, string dataField)
        {
            if (string.IsNullOrEmpty(dataField)) return null;
            // A derived table (DirOrganization) also has the fields of the tables it extends.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (string name = table; !string.IsNullOrEmpty(name) && seen.Add(name);)
            {
                if (!render.Fields.TryGetValue(name, out var fields))
                    render.Fields[name] = fields = GetEffectiveTableFields(name) ?? new Dictionary<string, AxTableField>(StringComparer.OrdinalIgnoreCase);
                if (fields.TryGetValue(dataField, out var field)) return field;
                if (!render.Tables.TryGetValue(name, out var axTable))
                    render.Tables[name] = axTable = MetaService.GetTable(name);
                name = axTable?.Extends;
            }
            return null;
        }

        /// <summary>The label the web client shows: the field's own label, else its EDT's (following Extends), else its enum's.</summary>
        private string FieldLabel(RenderContext render, AxTableField field)
        {
            if (!string.IsNullOrEmpty(field.Label))
                return ResolveLabel(field.Label);
            return TypeLabel(render, GetStringProperty(field, "ExtendedDataType"), GetStringProperty(field, "EnumType")) ?? field.Name;
        }

        /// <summary>An unbound control's label: the label of its EDT or enum.</summary>
        private string ControlTypeLabel(RenderContext render, object control)
            => TypeLabel(render, GetStringProperty(control, "ExtendedDataType"), GetStringProperty(control, "EnumType"));

        private string TypeLabel(RenderContext render, string edt, string enumType)
        {
            if (!string.IsNullOrEmpty(edt))
            {
                if (!render.EdtLabels.TryGetValue(edt, out string label))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (string name = edt; label == null && !string.IsNullOrEmpty(name) && seen.Add(name);)
                    {
                        var type = MetaService.GetExtendedDataType(name);
                        if (type == null) break;
                        if (!string.IsNullOrEmpty(type.Label)) label = ResolveLabel(type.Label);
                        name = type.Extends;
                    }
                    render.EdtLabels[edt] = label;
                }
                if (label != null) return label;
            }
            if (!string.IsNullOrEmpty(enumType))
            {
                string key = "enum:" + enumType;
                if (!render.EdtLabels.TryGetValue(key, out string label))
                {
                    var enm = MetaService.GetEnum(enumType);
                    render.EdtLabels[key] = label = string.IsNullOrEmpty(enm?.Label) ? null : ResolveLabel(enm.Label);
                }
                return label;
            }
            return null;
        }

        /// <summary>How the web client draws a field: toggle (Yes/No), number, date, datetime, enum (dropdown) or text.</summary>
        private static string FieldKind(AxTableField field)
        {
            if (field == null) return null;
            if (field is AxTableFieldEnum e)
                return string.Equals(e.EnumType, "NoYes", StringComparison.OrdinalIgnoreCase) || string.Equals(e.EnumType, "boolean", StringComparison.OrdinalIgnoreCase) ? "toggle" : "enum";
            if (field is AxTableFieldInt || field is AxTableFieldReal) return "number";
            if (field is AxTableFieldInt64) return "reference";
            if (field is AxTableFieldDate) return "date";
            if (field is AxTableFieldUtcDateTime) return "datetime";
            if (field is AxTableFieldTime) return "time";
            return "text";
        }

        private static string PropertyText(object target, string propertyName)
            => target?.GetType().GetProperty(propertyName)?.GetValue(target)?.ToString();

        private static int PropertyInt(object target, string propertyName)
            => target?.GetType().GetProperty(propertyName)?.GetValue(target) is int value ? value : -1;

        /// <summary>
        /// Size and arrangement properties the web client honours, so a change in the designer shows in the preview. Defaults
        /// are left out: Width/Height only count with mode Manual, DisplayLength (characters) only with DisplayLengthMode Fixed.
        /// </summary>
        private static void AddLayoutProperties(Dictionary<string, object> node, object control)
        {
            if (control == null) return;
            string widthMode = PropertyText(control, "WidthMode");
            if (widthMode == "SizeToAvailable" || widthMode == "Manual" || widthMode == "Column") node["widthMode"] = widthMode;
            if (widthMode == "Manual" && PropertyInt(control, "Width") > 0) node["width"] = PropertyInt(control, "Width");
            string heightMode = PropertyText(control, "HeightMode");
            if (heightMode == "SizeToAvailable" || heightMode == "Manual") node["heightMode"] = heightMode;
            if (heightMode == "Manual" && PropertyInt(control, "Height") > 0) node["height"] = PropertyInt(control, "Height");
            if (PropertyText(control, "DisplayLengthMode") == "Fixed" && PropertyInt(control, "DisplayLength") > 0) node["displayLength"] = PropertyInt(control, "DisplayLength");
            if (PropertyInt(control, "DisplayHeight") > 0) node["displayHeight"] = PropertyInt(control, "DisplayHeight");
            if (PropertyText(control, "MultiLine") == "Yes") node["multiLine"] = true;
            string arrange = PropertyText(control, "ArrangeMethod");
            if (!string.IsNullOrEmpty(arrange)) node["arrangeMethod"] = arrange;
            string alignment = PropertyText(control, "Alignment");
            if (alignment == "Right" || alignment == "Center") node["alignment"] = alignment;
            if (PropertyText(control, "VisibleRowsMode") == "Fixed" && PropertyInt(control, "VisibleRows") > 0) node["visibleRows"] = PropertyInt(control, "VisibleRows");
        }

        /// <summary>A menu item button's caption: the label of the menu item it opens.</summary>
        private string MenuItemLabel(RenderContext render, object control)
            => GetStringProperty(MenuItemOf(render, control), "Label") is string label ? ResolveLabel(label) : null;

        /// <summary>
        /// The configuration key that decides whether the web client shows a control: its own, else (for a menu item button)
        /// its menu item's. The preview only marks these; whether a key is enabled is environment data.
        /// </summary>
        private string ControlConfigurationKey(RenderContext render, object control)
        {
            string own = GetStringProperty(control, "ConfigurationKey");
            return !string.IsNullOrEmpty(own) ? own : GetStringProperty(MenuItemOf(render, control), "ConfigurationKey") is string key && key.Length > 0 ? key : null;
        }

        private object MenuItemOf(RenderContext render, object control)
        {
            string name = GetStringProperty(control, "MenuItemName");
            if (string.IsNullOrEmpty(name)) return null;
            string type = "AxMenuItem" + (PropertyText(control, "MenuItemType") ?? "Display");
            string key = type + ":" + name;
            if (!render.MenuItems.TryGetValue(key, out object item))
                render.MenuItems[key] = item = ReadViaMetadataProvider(type, name, out _);
            return item;
        }

        /// <summary>Fields a group bound to a table field group shows, in field-group order.</summary>
        private List<Dictionary<string, object>> GroupFields(RenderContext render, string table, string dataGroup, out string groupLabel)
        {
            groupLabel = null;
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(dataGroup)) return null;
            if (!render.Tables.TryGetValue(table, out var axTable))
                render.Tables[table] = axTable = MetaService.GetTable(table);
            var group = axTable?.FieldGroups.FirstOrDefault(g => string.Equals(g.Name, dataGroup, StringComparison.OrdinalIgnoreCase));
            if (group == null) return null;
            groupLabel = ResolveLabel(group.Label);
            var result = new List<Dictionary<string, object>>();
            foreach (var member in group.Fields)
            {
                var field = RenderField(render, table, member.DataField);
                result.Add(new Dictionary<string, object>
                {
                    ["name"] = member.DataField,
                    ["label"] = field != null ? FieldLabel(render, field) : member.DataField,
                    ["fieldKind"] = FieldKind(field) ?? "text",
                });
            }
            return result;
        }
    }
}
