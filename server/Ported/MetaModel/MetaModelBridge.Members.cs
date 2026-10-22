using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Members of a type for completion and signature help: methods (with signature, static, access), fields and
    // enum values, walking the base type chain; table and enum extensions are included.
    public sealed partial class MetaModelBridge
    {
        private static readonly Regex ExtendsPattern = new Regex(@"\b(?:class|interface)\s+\w+\s+extends\s+(\w+)", RegexOptions.IgnoreCase);

        public string TypeMembersJson(string objectType, string objectName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var members = new List<Dictionary<string, object>>();
            var chain = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string type = objectType, name = objectName;
            string resolvedType = null;
            for (int depth = 0; depth < 12 && !string.IsNullOrEmpty(name); depth++)
            {
                object obj = ReadViaMetadataProvider(type, name, out _);
                if (obj == null) break;
                resolvedType = resolvedType ?? type;
                chain.Add(name);
                AddMethods(members, obj, name, seen);
                if (obj is AxTable table)
                    foreach (var kv in GetEffectiveTableFields(table.Name) ?? new Dictionary<string, AxTableField>())
                        AddMember(members, seen, "field", kv.Key, FieldTypeName(kv.Value), false, name, null);
                else if (obj is AxEnum enm)
                {
                    foreach (var v in enm.EnumValues) AddMember(members, seen, "enumValue", v.Name, enm.Name, true, name, null);
                    foreach (string key in GetExtensionNames(MetadataProvider.EnumExtensions.GetPrimaryKeys(), enm.Name))
                        foreach (var v in MetadataProvider.EnumExtensions.Read(key)?.EnumValues ?? Enumerable.Empty<AxEnumValue>())
                            AddMember(members, seen, "enumValue", v.Name, enm.Name, true, key, null);
                }
                else if (obj.GetType().GetProperty("Fields")?.GetValue(obj) is IEnumerable fields && !(obj is AxClass))
                    foreach (object f in fields)
                    {
                        string fname = f?.GetType().GetProperty("Name")?.GetValue(f) as string;
                        if (fname != null) AddMember(members, seen, "field", fname, null, false, name, null);
                    }

                // Base type: classes through extends, tables/views through their Extends property (table inheritance).
                string next = null;
                if (obj is AxClass cls)
                    next = ExtendsPattern.Match(cls.Declaration ?? "").Groups[1].Value;
                else next = obj.GetType().GetProperty("Extends")?.GetValue(obj) as string;
                if (string.IsNullOrEmpty(next) || chain.Contains(next, StringComparer.OrdinalIgnoreCase)) break;
                name = next;
            }
            if (resolvedType == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = objectType + " '" + objectName + "' not found." });
            if (resolvedType == "AxTable") AddCommonTableMembers(members, seen);
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["chain"] = chain, ["members"] = members });
        }

        private static void AddMethods(List<Dictionary<string, object>> members, object obj, string owner, HashSet<string> seen)
        {
            if (!(obj.GetType().GetProperty("Methods")?.GetValue(obj) is IEnumerable methods)) return;
            foreach (object m in methods)
            {
                string mname = m?.GetType().GetProperty("Name")?.GetValue(m) as string;
                string source = m?.GetType().GetProperty("Source")?.GetValue(m) as string;
                if (string.IsNullOrEmpty(mname) || mname == "classDeclaration") continue;
                string signature = MemberSignature(source) ?? mname + "()";
                bool isStatic = Regex.IsMatch(signature, @"(^|\s)static\s", RegexOptions.IgnoreCase);
                AddMember(members, seen, "method", mname, null, isStatic, owner, signature, DocCommentOf(source));
            }
        }

        private static void AddMember(List<Dictionary<string, object>> members, HashSet<string> seen, string kind, string name,
            string type, bool isStatic, string owner, string signature, string doc = null)
        {
            // Overrides in a derived type hide the base version.
            if (!seen.Add(kind + ":" + name)) return;
            var m = new Dictionary<string, object> { ["kind"] = kind, ["name"] = name, ["owner"] = owner, ["static"] = isStatic };
            if (type != null) m["type"] = type;
            if (signature != null) m["signature"] = signature;
            if (!string.IsNullOrWhiteSpace(doc)) m["doc"] = doc;
            if (signature != null)
            {
                string access = Regex.Match(signature, @"^\s*(?:\[[^\]]*\]\s*)*(public|private|protected|internal)\b", RegexOptions.IgnoreCase).Groups[1].Value;
                m["access"] = string.IsNullOrEmpty(access) ? "public" : access.ToLowerInvariant();
            }
            members.Add(m);
        }

        private static readonly string[][] CommonTableMembers =
        {
            new[] { "field", "RecId", "RecId" }, new[] { "field", "TableId", "TableId" }, new[] { "field", "DataAreaId", "DataAreaId" },
            new[] { "method", "insert", "public void insert()" }, new[] { "method", "update", "public void update()" },
            new[] { "method", "delete", "public void delete()" }, new[] { "method", "doInsert", "public void doInsert()" },
            new[] { "method", "doUpdate", "public void doUpdate()" }, new[] { "method", "doDelete", "public void doDelete()" },
            new[] { "method", "validateWrite", "public boolean validateWrite()" }, new[] { "method", "validateDelete", "public boolean validateDelete()" },
            new[] { "method", "validateField", "public boolean validateField(FieldId _fieldIdToCheck)" },
            new[] { "method", "modifiedField", "public void modifiedField(FieldId _fieldId)" }, new[] { "method", "initValue", "public void initValue()" },
            new[] { "method", "clear", "public void clear()" }, new[] { "method", "data", "public void data(Common _source)" },
            new[] { "method", "orig", "public Common orig()" }, new[] { "method", "reread", "public void reread()" },
            new[] { "method", "selectForUpdate", "public boolean selectForUpdate(boolean _selectForUpdate = true)" },
            new[] { "method", "skipDataMethods", "public boolean skipDataMethods(boolean _skip = true)" },
            new[] { "method", "skipEvents", "public boolean skipEvents(boolean _skip = true)" },
            new[] { "method", "isTmp", "public boolean isTmp()" }, new[] { "method", "setTmp", "public void setTmp()" },
            new[] { "method", "linkPhysicalTableInstance", "public void linkPhysicalTableInstance(Common _table)" },
        };

        private static void AddCommonTableMembers(List<Dictionary<string, object>> members, HashSet<string> seen)
        {
            foreach (var m in CommonTableMembers)
                AddMember(members, seen, m[0], m[1], m[0] == "field" ? m[2] : null, false, "Common", m[0] == "method" ? m[2] : null);
        }

        private static string FieldTypeName(AxTableField field)
        {
            string edt = field.GetType().GetProperty("ExtendedDataType")?.GetValue(field) as string;
            if (!string.IsNullOrEmpty(edt)) return edt;
            string enumType = field.GetType().GetProperty("EnumType")?.GetValue(field) as string;
            if (!string.IsNullOrEmpty(enumType)) return enumType;
            switch (field.GetType().Name)
            {
                case "AxTableFieldString": return "str";
                case "AxTableFieldInt": return "int";
                case "AxTableFieldInt64": return "int64";
                case "AxTableFieldReal": return "real";
                case "AxTableFieldDate": return "date";
                case "AxTableFieldUtcDateTime": return "utcdatetime";
                case "AxTableFieldTime": return "timeOfDay";
                case "AxTableFieldGuid": return "guid";
                case "AxTableFieldContainer": return "container";
                default: return field.GetType().Name.Replace("AxTableField", "");
            }
        }

        // Declaration line(s) up to the closing parenthesis, without doc comments and attributes.
        private static string MemberSignature(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            var sig = new System.Text.StringBuilder();
            int depth = 0;
            bool opened = false;
            foreach (string raw in source.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (sig.Length == 0 && (line.Length == 0 || line.StartsWith("//") || line.StartsWith("[") || line.StartsWith("/*") || line.StartsWith("*")))
                    continue;
                if (line.StartsWith("{")) break;
                sig.Append(sig.Length > 0 ? " " : "").Append(line);
                foreach (char c in line)
                {
                    if (c == '(') { depth++; opened = true; }
                    else if (c == ')') depth--;
                }
                if (opened && depth <= 0) break;
            }
            return sig.Length > 0 ? Regex.Replace(sig.ToString(), @"\s+", " ") : null;
        }

        // Text of a leading /// <summary> comment.
        private static string DocCommentOf(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            var lines = source.Replace("\r", "").Split('\n').Select(l => l.Trim()).TakeWhile(l => l.Length == 0 || l.StartsWith("///")).Where(l => l.StartsWith("///"));
            string text = string.Join(" ", lines.Select(l => l.Substring(3).Trim()));
            text = Regex.Replace(text, @"<[^>]+>", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }
}
