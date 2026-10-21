using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Dynamics 365 > Compare metadata: an element as indented text (nodes, non-empty properties, method code) for a diff editor.
    public sealed partial class MetaModelBridge
    {
        public string DumpElementJson(string objectType, string objectName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object element = ReadViaMetadataProvider(objectType, objectName, out string model);
            if (element == null)
                return json.Serialize(Failure(objectType + " '" + objectName + "' not found."));
            var sb = new StringBuilder();
            sb.Append("// ").Append(objectType).Append(' ').Append(objectName).Append("  [").Append(model ?? GetObjectModelName(objectType, objectName)).Append("]\n");
            DumpNode(sb, element, objectName, 0);
            return json.Serialize(new System.Collections.Generic.Dictionary<string, object> { ["success"] = true, ["text"] = sb.ToString() });
        }

        private static void DumpNode(StringBuilder sb, object node, string name, int depth)
        {
            string indent = new string(' ', depth * 4);
            sb.Append(indent).Append(name).Append(" (").Append(node.GetType().Name.Replace("Ax", "")).Append(")\n");
            foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && IsDesignerProperty(p))
                .GroupBy(p => p.Name).Select(g => g.First())
                .OrderBy(p => p.Name, System.StringComparer.OrdinalIgnoreCase))
            {
                object value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                string text = FormatValue(value);
                if (string.IsNullOrEmpty(text) || text.StartsWith("Microsoft.Dynamics.")) continue;
                if (prop.Name == "Declaration")
                {
                    sb.Append(indent).Append("    Declaration\n");
                    foreach (string line in text.Replace("\r\n", "\n").TrimEnd().Split('\n'))
                        sb.Append(indent).Append("    | ").Append(line).Append('\n');
                    continue;
                }
                sb.Append(indent).Append("    ").Append(prop.Name).Append(" = ").Append(text.Replace("\r", "").Replace("\n", "\\n")).Append('\n');
            }
            foreach (var prop in ChildCollections(node.GetType()))
            {
                object value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is IEnumerable items && !(value is string))
                {
                    var list = items.Cast<object>().Where(i => i != null).ToList();
                    if (list.Count == 0) continue;
                    sb.Append(indent).Append("    [").Append(SplitWords(prop.Name)).Append("]\n");
                    foreach (object item in list)
                    {
                        if (item is AxMethod method)
                        {
                            sb.Append(indent).Append("        ").Append(method.Name).Append("()\n");
                            foreach (string line in (method.Source ?? "").Replace("\r\n", "\n").TrimEnd().Split('\n'))
                                sb.Append(indent).Append("        | ").Append(line).Append('\n');
                        }
                        else
                        {
                            DumpNode(sb, item, ItemName(item) ?? "?", depth + 2);
                        }
                    }
                }
                else if (value != null)
                {
                    DumpNode(sb, value, SplitWords(prop.Name), depth + 1);
                }
            }
        }
    }
}
