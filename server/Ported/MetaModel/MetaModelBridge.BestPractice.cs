using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Best-practice warnings after a create or update: what the agent should fix or justify (they never block a save).
    // Only cheap checks on the saved object itself: labels, EDTs, keys and indexes, caching, privileges for menu items,
    // extension class naming.
    public sealed partial class MetaModelBridge
    {
        private static readonly string[] TextProperties = { "Label", "HelpText", "Caption", "Text", "DeveloperDocumentation" };

        public string BestPracticeReport(string objectType, string objectName)
        {
            var warnings = new List<string>();
            try
            {
                object obj = ReadViaMetadataProvider(objectType, objectName, out _);
                if (obj == null) return "";
                switch (obj)
                {
                    case AxTable table:
                        CheckTable(table, warnings);
                        break;
                    case AxTableExtension ext:
                        CheckFields(ext.Fields, objectName, warnings);
                        CheckLiteralTexts(ext.FieldGroups, "field group", warnings);
                        break;
                    case AxEdt edt:
                        if (string.IsNullOrEmpty(edt.Label) && string.IsNullOrEmpty(edt.Extends)) warnings.Add("EDT " + edt.Name + " has no label.");
                        CheckLiteral(edt, "EDT " + edt.Name, warnings);
                        break;
                    case AxEnum enm:
                        if (string.IsNullOrEmpty(enm.Label)) warnings.Add("Enum " + enm.Name + " has no label.");
                        CheckLiteral(enm, "Enum " + enm.Name, warnings);
                        foreach (var v in enm.EnumValues)
                        {
                            if (string.IsNullOrEmpty(v.Label)) warnings.Add("Enum value " + enm.Name + "::" + v.Name + " has no label.");
                            CheckLiteral(v, "Enum value " + enm.Name + "::" + v.Name, warnings);
                        }
                        break;
                    case AxEnumExtension enumExt:
                        foreach (var v in enumExt.EnumValues)
                        {
                            if (string.IsNullOrEmpty(v.Label)) warnings.Add("Enum value " + v.Name + " has no label.");
                            CheckLiteral(v, "Enum value " + v.Name, warnings);
                        }
                        break;
                    case AxMenuItem item:
                        if (string.IsNullOrEmpty(item.Label)) warnings.Add("Menu item " + item.Name + " has no label.");
                        CheckLiteral(item, "Menu item " + item.Name, warnings);
                        if (!PrivilegeGrants(objectType, objectName))
                            warnings.Add("No privilege in model " + ModelOf(objectType, objectName) + " grants menu item " + item.Name
                                + "; users without a privilege cannot open it (add it as an entry point of a privilege, and the privilege to a duty or role; ignore this if that is the next step of your plan).");
                        break;
                    case AxSecurityPrivilege privilege:
                        if (string.IsNullOrEmpty(privilege.Label)) warnings.Add("Privilege " + privilege.Name + " has no label.");
                        CheckLiteral(privilege, "Privilege " + privilege.Name, warnings);
                        break;
                    case AxSecurityDuty duty:
                        if (string.IsNullOrEmpty(duty.Label)) warnings.Add("Duty " + duty.Name + " has no label.");
                        CheckLiteral(duty, "Duty " + duty.Name, warnings);
                        break;
                    case AxSecurityRole role:
                        if (string.IsNullOrEmpty(role.Label)) warnings.Add("Role " + role.Name + " has no label.");
                        CheckLiteral(role, "Role " + role.Name, warnings);
                        break;
                    case AxFormExtension formExt:
                        foreach (var c in formExt.Controls.Where(c => c.FormControl != null))
                            CheckControlTexts(c.FormControl, warnings);
                        break;
                    case AxClass cls:
                        CheckExtensionClass(cls, warnings);
                        break;
                }
            }
            catch (Exception ex)
            {
                BridgeLogStatic("best-practice check of " + objectType + " " + objectName + " failed: " + ex.Message);
            }
            return warnings.Count == 0 ? ""
                : "\nBest-practice warnings (fix them, or say in your summary why they stay):\n  - " + string.Join("\n  - ", warnings.Distinct());
        }

        private void CheckTable(AxTable table, List<string> warnings)
        {
            CheckFields(table.Fields, table.Name, warnings);
            CheckLiteral(table, "Table " + table.Name, warnings);
            if (string.IsNullOrEmpty(table.Label)) warnings.Add("Table " + table.Name + " has no label.");
            bool temporary = table.TableType.ToString() != "Regular";
            if (!temporary)
            {
                if (string.IsNullOrEmpty(table.PrimaryIndex))
                    warnings.Add("Table " + table.Name + " has no PrimaryIndex: add a unique index on its natural key (AlternateKey = Yes) and set it as PrimaryIndex (or SurrogateKey).");
                if (!table.Indexes.Any(i => i.AllowDuplicates.ToString() == "No" && i.AlternateKey.ToString() == "Yes"))
                    warnings.Add("Table " + table.Name + " has no alternate key: add a unique index on its natural key (AllowDuplicates = No, AlternateKey = Yes), "
                        + "and use it as ReplacementKey (and PrimaryIndex / ClusteredIndex where it fits).");
                if (string.IsNullOrEmpty(table.ClusteredIndex))
                    warnings.Add("Table " + table.Name + " has no ClusteredIndex: set it (usually the primary index).");
                if (table.CacheLookup.ToString() == "None")
                    warnings.Add("Table " + table.Name + " has CacheLookup None: use Found (or EntireTable for parameter/small setup tables, FoundAndEmpty for lookups that often miss).");
                if (table.TableGroup.ToString() == "Miscellaneous")
                    warnings.Add("Table " + table.Name + " has TableGroup Miscellaneous: set the group that matches its role (Parameter, Group, Main, Transaction, WorksheetHeader, ...).");
            }
            foreach (string group in new[] { "AutoIdentification", "AutoLookup" })
            {
                var g = table.FieldGroups.FirstOrDefault(x => string.Equals(x.Name, group, StringComparison.OrdinalIgnoreCase));
                if (!temporary && (g == null || g.Fields.Count == 0))
                    warnings.Add("Table " + table.Name + ": field group " + group + " is empty (it drives lookups and identification).");
            }
            CheckLiteralTexts(table.FieldGroups, "field group", warnings);
        }

        private void CheckFields(IEnumerable<AxTableField> fields, string owner, List<string> warnings)
        {
            foreach (var f in fields ?? Enumerable.Empty<AxTableField>())
            {
                string edt = f.GetType().GetProperty("ExtendedDataType")?.GetValue(f) as string;
                string enumType = (f as AxTableFieldEnum)?.EnumType;
                if (string.IsNullOrEmpty(edt) && string.IsNullOrEmpty(enumType))
                    warnings.Add("Field " + owner + "." + f.Name + " has no EDT" + (f is AxTableFieldEnum ? " or enum type" : "")
                        + ": use an EDT (or enum) so label, size and relations come from the type.");
                else if (string.IsNullOrEmpty(f.Label) && !string.IsNullOrEmpty(edt) && !EdtHasLabel(edt))
                    warnings.Add("Field " + owner + "." + f.Name + " has no label and its EDT " + edt + " has none either.");
                else if (string.IsNullOrEmpty(f.Label) && string.IsNullOrEmpty(edt) && !string.IsNullOrEmpty(enumType))
                    warnings.Add("Field " + owner + "." + f.Name + " has no label.");
                CheckLiteral(f, "Field " + owner + "." + f.Name, warnings);
            }
        }

        private bool EdtHasLabel(string edtName)
        {
            for (int depth = 0; depth < 10 && !string.IsNullOrEmpty(edtName); depth++)
            {
                var edt = ReadViaMetadataProvider("AxEdt", edtName, out _) as AxEdt;
                if (edt == null) return true;   // unknown EDT: reported elsewhere
                if (!string.IsNullOrEmpty(edt.Label)) return true;
                edtName = edt.Extends;
            }
            return false;
        }

        private static void CheckLiteral(object node, string what, List<string> warnings)
        {
            foreach (string prop in TextProperties)
            {
                string value = node?.GetType().GetProperty(prop)?.GetValue(node) as string;
                if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("@", StringComparison.Ordinal) && prop != "DeveloperDocumentation")
                    warnings.Add(what + ": " + prop + " is literal text '" + value + "'; use a label (@File:Id) so it can be translated.");
            }
        }

        private static void CheckLiteralTexts(IEnumerable items, string kind, List<string> warnings)
        {
            foreach (object item in items ?? new object[0])
                CheckLiteral(item, kind + " " + (item?.GetType().GetProperty("Name")?.GetValue(item) as string), warnings);
        }

        private static void CheckControlTexts(AxFormControl control, List<string> warnings)
        {
            CheckLiteral(control, "Control " + control.Name, warnings);
            foreach (var child in FormExtensionMerger.ChildrenOf(control))
                CheckControlTexts(child, warnings);
        }

        private static readonly Regex ExtensionOfPattern = new Regex(@"\[\s*ExtensionOf\s*\(", RegexOptions.IgnoreCase);

        private static void CheckExtensionClass(AxClass cls, List<string> warnings)
        {
            string declaration = cls.Declaration ?? "";
            if (!ExtensionOfPattern.IsMatch(declaration)) return;
            if (!cls.Name.EndsWith("_Extension", StringComparison.OrdinalIgnoreCase))
                warnings.Add("Class " + cls.Name + " is a Chain of Command extension: its name must end with _Extension (e.g. " + cls.Name + "_Extension).");
            if (!Regex.IsMatch(declaration, @"\bfinal\b"))
                warnings.Add("Class " + cls.Name + " is a Chain of Command extension: declare it final.");
        }

        /// <summary>Whether a privilege of the menu item's model has it as an entry point (scans that model's privilege files).</summary>
        private bool PrivilegeGrants(string objectType, string itemName)
        {
            try
            {
                string model = ModelOf(objectType, itemName);
                var config = DynamicsConfigurationReader.ReadActiveConfiguration();
                foreach (string root in new[] { config.CustomMetadataFolder }.Concat(config.ReferenceMetadataFolders)
                             .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (string package in Directory.GetDirectories(root))
                    {
                        string dir = Path.Combine(package, model ?? "", "AxSecurityPrivilege");
                        if (!Directory.Exists(dir)) continue;
                        string needle = "<ObjectName>" + itemName + "</ObjectName>";
                        if (Directory.EnumerateFiles(dir, "*.xml").Any(f => File.ReadAllText(f).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                            return true;
                    }
            }
            catch { return true; }
            return false;
        }
    }
}
