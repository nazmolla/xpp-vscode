using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using static XppVsCode.Server.MetaModel.MetaModelBridge;

namespace XppVsCode.Server.MetaModel
{
    // What the data browser needs to show a table's rows the way F&O does: field labels, enum value names, whether
    // rows are per company and whether the table is date-effective. Table extensions and enum extensions included.
    internal sealed class TableDataShapeService : ITableDataShape
    {
        private readonly IMetadataCore _core;

        public TableDataShapeService(IMetadataCore core)
        {
            _core = core;
        }

        public string TableDataShapeJson(string tableName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var table = string.IsNullOrWhiteSpace(tableName) ? null : _core.MetaService.GetTable(tableName.Trim());
            if (table == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Table '" + tableName + "' not found." });

            var enums = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var fields = new List<Dictionary<string, object>>();
            foreach (var field in (_core.EffectiveTableFields(table.Name) ?? new Dictionary<string, AxTableField>()).Values
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                string enumType = EnumTypeOf(field);
                if (enumType != null && !enums.ContainsKey(enumType))
                    enums[enumType] = EnumValues(enumType);
                fields.Add(new Dictionary<string, object>
                {
                    ["name"] = field.Name,
                    ["type"] = field.GetType().Name.Replace("AxTableField", ""),
                    ["label"] = _core.ResolveLabel(field.Label),
                    ["enumType"] = enumType,
                });
            }

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["table"] = table.Name,
                ["label"] = _core.ResolveLabel(table.Label),
                ["tableType"] = table.TableType.ToString(),
                ["perCompany"] = !"No".Equals(table.SaveDataPerCompany.ToString(), StringComparison.OrdinalIgnoreCase),
                ["validTimeState"] = table.ValidTimeStateFieldType.ToString(),
                ["fields"] = fields,
                ["enums"] = enums,
            });
        }

        /// <summary>The enum of an enum field, directly or through its EDT chain; null for other fields.</summary>
        private string EnumTypeOf(AxTableField field)
        {
            if (!(field is AxTableFieldEnum)) return null;
            string enumType = GetStringProperty(field, "EnumType");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (string edt = GetStringProperty(field, "ExtendedDataType"); string.IsNullOrEmpty(enumType) && !string.IsNullOrEmpty(edt) && seen.Add(edt);)
            {
                var type = _core.MetaService.GetExtendedDataType(edt);
                if (type == null) break;
                enumType = GetStringProperty(type, "EnumType");
                edt = type.Extends;
            }
            return string.IsNullOrEmpty(enumType) ? null : enumType;
        }

        // Base enum values are fixed; values added by enum extensions are assigned per environment at sync, so they stay numbers.
        private Dictionary<string, object> EnumValues(string enumType)
        {
            var enm = _core.MetaService.GetEnum(enumType);
            bool extensible = enm != null && (enm.IsExtensible.ToString() is string flag) && (flag == "Yes" || flag == "True");
            var values = new Dictionary<string, object>();
            if (enm != null)
                foreach (var v in enm.EnumValues)
                    values[v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] = v.Name;
            return new Dictionary<string, object> { ["extensible"] = extensible, ["values"] = values };
        }
    }
}
