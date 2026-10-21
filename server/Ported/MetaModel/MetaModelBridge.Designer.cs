using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Structured data for the VS Code form designer and field pickers.
    public sealed partial class MetaModelBridge
    {
        private const int MaxDesignerNodes = 3000;
        private static readonly Regex LabelReference = new Regex(@"^@(?:(?<file>[A-Za-z]\w*):(?<id>\w+)|(?<file>[A-Za-z]+)(?<id>\d+))$");
        private readonly Dictionary<string, Dictionary<string, string>> _labelCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The form with all extensions merged, as JSON for the designer.</summary>
        public string GetFormTreeJson(string formName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var effective = string.IsNullOrWhiteSpace(formName) ? null : BuildEffectiveForm(formName.Trim());
            if (effective == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Form '" + formName + "' not found." });

            string model = GetObjectModelName("AxForm", formName);
            int count = 0;
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["form"] = formName,
                ["model"] = model,
                ["editable"] = IsCustomModel(model),
                ["caption"] = ResolveLabel(effective.Form.Design?.Caption),
                ["pattern"] = effective.Form.Design?.Pattern,
                ["extensions"] = effective.Extensions,
                ["warnings"] = effective.Warnings,
                ["dataSources"] = effective.Form.DataSources.Select(ds => new Dictionary<string, object>
                {
                    ["name"] = ds.Name,
                    ["table"] = ds.Table,
                    ["joinSource"] = ds.JoinSource,
                    ["linkType"] = ds.LinkType.ToString(),
                    ["addedBy"] = effective.DataSourceAddedBy.TryGetValue(ds.Name, out string by) ? by : null,
                }).ToList(),
                ["root"] = DesignerNode(effective.Root, ref count),
                ["truncated"] = count > MaxDesignerNodes,
            });
        }

        private Dictionary<string, object> DesignerNode(FormControlNode node, ref int count)
        {
            count++;
            var ctl = node.Control as AxFormControl;
            string label = GetStringProperty(node.Control, "Label") ?? GetStringProperty(node.Control, "Caption") ?? GetStringProperty(node.Control, "Text");
            var children = new List<object>();
            foreach (var child in node.Children)
            {
                if (count > MaxDesignerNodes) break;
                children.Add(DesignerNode(child, ref count));
            }
            return new Dictionary<string, object>
            {
                ["name"] = node.Name,
                ["type"] = ctl?.Type.ToString() ?? "Design",
                ["label"] = ResolveLabel(label),
                ["labelId"] = label != null && label.StartsWith("@") ? label : null,
                ["dataSource"] = GetStringProperty(node.Control, "DataSource"),
                ["dataField"] = GetStringProperty(node.Control, "DataField"),
                ["dataGroup"] = GetStringProperty(node.Control, "DataGroup"),
                ["visible"] = GetStringProperty(node.Control, "Visible") ?? node.Control?.GetType().GetProperty("Visible")?.GetValue(node.Control)?.ToString(),
                ["pattern"] = GetStringProperty(node.Control, "Pattern"),
                ["addedBy"] = node.AddedBy,
                ["container"] = node.IsContainer,
                ["children"] = children,
            };
        }

        /// <summary>Fields of a table including table extensions, for field pickers.</summary>
        public string GetTableFieldsJson(string tableName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var fields = string.IsNullOrWhiteSpace(tableName) ? null : GetEffectiveTableFields(tableName.Trim());
            if (fields == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Table '" + tableName + "' not found." });

            var groups = MetaService.GetTable(tableName.Trim())?.FieldGroups.Select(g => g.Name).ToList() ?? new List<string>();
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["table"] = tableName,
                ["fieldGroups"] = groups,
                ["fields"] = fields.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new Dictionary<string, object>
                {
                    ["name"] = f.Name,
                    ["type"] = f.GetType().Name.Replace("AxTableField", ""),
                    ["edt"] = GetStringProperty(f, "ExtendedDataType"),
                    ["enumType"] = GetStringProperty(f, "EnumType"),
                    ["label"] = ResolveLabel(f.Label),
                }).ToList(),
            });
        }

        /// <summary>Label text for "@File:Id" or legacy "@SYS123"; the reference itself when it can't be resolved.</summary>
        private string ResolveLabel(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '@') return value;
            var m = LabelReference.Match(value);
            if (!m.Success) return value;

            string file = m.Groups["file"].Value;
            if (!_labelCache.TryGetValue(file, out var labels))
            {
                labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string path = _labels.FindLabelFile(file, "en-US");
                if (path != null)
                    foreach (var entry in _labels.Read(path, null, null, int.MaxValue))
                        labels[entry.Id] = entry.Text;
                _labelCache[file] = labels;
            }
            // Legacy files store ids with the '@' (e.g. "@SYS76213=..."); newer files store just the id.
            string id = value.Contains(":") ? m.Groups["id"].Value : value;
            return labels.TryGetValue(id, out string text) ? text : value;
        }
    }
}
