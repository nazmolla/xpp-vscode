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
        }

        private static RenderContext NewRenderContext(AxForm form)
        {
            var render = new RenderContext();
            foreach (var ds in form.DataSources)
                if (!string.IsNullOrEmpty(ds.Name) && !render.DataSourceTables.ContainsKey(ds.Name))
                    render.DataSourceTables[ds.Name] = ds.Table;
            return render;
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
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(dataField)) return null;
            if (!render.Fields.TryGetValue(table, out var fields))
                render.Fields[table] = fields = GetEffectiveTableFields(table) ?? new Dictionary<string, AxTableField>(StringComparer.OrdinalIgnoreCase);
            return fields.TryGetValue(dataField, out var field) ? field : null;
        }

        /// <summary>The label the web client shows: the field's own label, else its EDT's (following Extends), else its enum's.</summary>
        private string FieldLabel(RenderContext render, AxTableField field)
        {
            if (!string.IsNullOrEmpty(field.Label))
                return ResolveLabel(field.Label);
            string edt = GetStringProperty(field, "ExtendedDataType");
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
            string enumType = GetStringProperty(field, "EnumType");
            if (!string.IsNullOrEmpty(enumType))
            {
                var enm = MetaService.GetEnum(enumType);
                if (!string.IsNullOrEmpty(enm?.Label)) return ResolveLabel(enm.Label);
            }
            return field.Name;
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

        /// <summary>A menu item button's caption: the label of the menu item it opens.</summary>
        private string MenuItemLabel(RenderContext render, object control)
        {
            string name = GetStringProperty(control, "MenuItemName");
            if (string.IsNullOrEmpty(name)) return null;
            string type = "AxMenuItem" + (PropertyText(control, "MenuItemType") ?? "Display");
            string key = type + ":" + name;
            if (!render.EdtLabels.TryGetValue(key, out string label))
            {
                var item = ReadViaMetadataProvider(type, name, out _);
                label = ResolveLabel(GetStringProperty(item, "Label"));
                render.EdtLabels[key] = label;
            }
            return label;
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
