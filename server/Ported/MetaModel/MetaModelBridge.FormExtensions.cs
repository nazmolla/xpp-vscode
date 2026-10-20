using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Dynamics.AX.Metadata.Core.Collections;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Storage;

namespace XppVsCode.Server.MetaModel
{
    // Form extensions: effective-form survey, placement validation, and AxFormExtension writes.
    public sealed partial class MetaModelBridge
    {
        private const string DesignRootName = "Design";
        private const int DefaultLayoutDepth = 4;
        private const int MaxLayoutDepth = 12;
        private const int MaxLayoutNodes = 400;

        private static readonly Dictionary<string, Type> FormControlClasses =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                { "String", typeof(AxFormStringControl) },
                { "Integer", typeof(AxFormIntegerControl) },
                { "Int64", typeof(AxFormInt64Control) },
                { "Real", typeof(AxFormRealControl) },
                { "Date", typeof(AxFormDateControl) },
                { "DateTime", typeof(AxFormDateTimeControl) },
                { "Time", typeof(AxFormTimeControl) },
                { "Guid", typeof(AxFormGuidControl) },
                { "CheckBox", typeof(AxFormCheckBoxControl) },
                { "ComboBox", typeof(AxFormComboBoxControl) },
                { "RadioButton", typeof(AxFormRadioButtonControl) },
                { "StaticText", typeof(AxFormStaticTextControl) },
                { "ReferenceGroup", typeof(AxFormReferenceGroupControl) },
                { "Group", typeof(AxFormGroupControl) },
                { "Tab", typeof(AxFormTabControl) },
                { "TabPage", typeof(AxFormTabPageControl) },
                { "Grid", typeof(AxFormGridControl) },
                { "ButtonGroup", typeof(AxFormButtonGroupControl) },
                { "Button", typeof(AxFormButtonControl) },
                { "CommandButton", typeof(AxFormCommandButtonControl) },
                { "MenuFunctionButton", typeof(AxFormMenuFunctionButtonControl) },
                { "MenuButton", typeof(AxFormMenuButtonControl) },
                { "ActionPaneTab", typeof(AxFormActionPaneTabControl) }
            };

        private sealed class FormControlNode
        {
            public string Name;
            public object Control;
            public FormControlNode Parent;
            public List<FormControlNode> Children = new List<FormControlNode>();
            public string AddedBy;
            public bool IsContainer;
        }

        private sealed class EffectiveForm
        {
            public AxForm Form;
            public FormControlNode Root;
            public Dictionary<string, FormControlNode> ByName =
                new Dictionary<string, FormControlNode>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> DataSourceAddedBy =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Extensions = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        // ── Survey ──

        public FormLayoutResult GetFormLayout(string formName, string rootControl, int maxDepth)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null || MetadataProvider == null)
                return new FormLayoutResult { Message = "MetaModel service not available." };
            if (string.IsNullOrWhiteSpace(formName))
                return new FormLayoutResult { Message = "formName is required." };

            if (maxDepth <= 0) maxDepth = DefaultLayoutDepth;
            if (maxDepth > MaxLayoutDepth) maxDepth = MaxLayoutDepth;

            try
            {
                var effective = BuildEffectiveForm(formName.Trim());
                if (effective == null)
                    return new FormLayoutResult { Message = "Form '" + formName + "' not found." };

                FormControlNode start = effective.Root;
                if (!string.IsNullOrWhiteSpace(rootControl)
                    && !effective.ByName.TryGetValue(rootControl.Trim(), out start))
                    return new FormLayoutResult { Message = "Control '" + rootControl + "' not found on form '" + formName + "'." };

                var sb = new StringBuilder();
                sb.AppendLine("Form: " + formName + "  Model: " + (GetObjectModelName("AxForm", formName) ?? "?")
                    + "  Pattern: " + (effective.Form.Design?.Pattern ?? "-"));
                sb.AppendLine("Extensions applied (" + effective.Extensions.Count + "): "
                    + (effective.Extensions.Count == 0 ? "none" : string.Join(", ", effective.Extensions)));
                foreach (string w in effective.Warnings)
                    sb.AppendLine("WARNING: " + w);

                sb.AppendLine();
                sb.AppendLine("Data sources:");
                foreach (var ds in effective.Form.DataSources)
                {
                    sb.Append("  " + ds.Name + "  table=" + ds.Table);
                    if (!string.IsNullOrEmpty(ds.JoinSource))
                        sb.Append("  joinSource=" + ds.JoinSource + "  linkType=" + ds.LinkType);
                    if (effective.DataSourceAddedBy.TryGetValue(ds.Name, out string dsOrigin))
                        sb.Append("  {added by " + dsOrigin + "}");
                    sb.AppendLine();
                }

                sb.AppendLine();
                sb.AppendLine("Controls" + (start == effective.Root ? "" : " under '" + start.Name + "'")
                    + " (depth " + maxDepth + "; [Type] ds.field; {added by extension}):");
                int count = 0;
                bool truncated = false;
                RenderLayoutNode(start, 0, maxDepth, sb, ref count, ref truncated);
                if (truncated)
                    sb.AppendLine("... stopped after " + MaxLayoutNodes + " controls. Call again with rootControl set to the container you need.");

                sb.AppendLine();
                sb.Append("Placement: a new top-level control needs parent=<container above>, position=end|begin|after; "
                    + "with after, previousSibling must be a DIRECT child of that parent. "
                    + "Containers whose children are hidden show (+N children) \u2014 call again with rootControl to expand.");

                return new FormLayoutResult { Success = true, Layout = sb.ToString() };
            }
            catch (Exception ex)
            {
                return new FormLayoutResult { Message = "Form layout failed: " + ex.Message };
            }
        }

        private static void RenderLayoutNode(FormControlNode node, int depth, int maxDepth,
            StringBuilder sb, ref int count, ref bool truncated)
        {
            if (truncated) return;
            if (++count > MaxLayoutNodes) { truncated = true; return; }

            sb.Append(new string(' ', depth * 2) + node.Name);
            if (node.Control is AxFormControl ctl)
            {
                sb.Append(" [" + ctl.Type + "]");
                string ds = GetStringProperty(ctl, "DataSource");
                string field = GetStringProperty(ctl, "DataField");
                string group = GetStringProperty(ctl, "DataGroup");
                if (!string.IsNullOrEmpty(field)) sb.Append(" " + (ds ?? "?") + "." + field);
                else if (!string.IsNullOrEmpty(group)) sb.Append(" " + (ds ?? "?") + " group=" + group);
                else if (!string.IsNullOrEmpty(ds)) sb.Append(" ds=" + ds);
            }
            if (node.AddedBy != null) sb.Append("  {added by " + node.AddedBy + "}");

            if (node.Children.Count > 0 && depth >= maxDepth)
            {
                sb.AppendLine("  (+" + node.Children.Count + " children)");
                return;
            }
            sb.AppendLine();

            foreach (var child in node.Children)
                RenderLayoutNode(child, depth + 1, maxDepth, sb, ref count, ref truncated);
        }

        private EffectiveForm BuildEffectiveForm(string formName)
        {
            var baseForm = MetaService.GetForm(formName);
            if (baseForm == null) return null;

            // Work on copies so merging extensions never mutates the instances VS has cached.
            var effective = new EffectiveForm { Form = ObjectCloner.MakeCopy(baseForm) };
            var extensions = new List<KeyValuePair<string, AxFormExtension>>();

            foreach (string extName in GetExtensionNames(MetadataProvider.FormExtensions.GetPrimaryKeys(), formName))
            {
                try
                {
                    var ext = MetadataProvider.FormExtensions.Read(extName);
                    if (ext == null) continue;
                    extensions.Add(new KeyValuePair<string, AxFormExtension>(extName, ObjectCloner.MakeCopy(ext)));
                    effective.Extensions.Add(extName);
                }
                catch (Exception ex)
                {
                    effective.Warnings.Add("Could not read " + extName + ": " + ex.Message);
                }
            }

            var merged = FormExtensionMerger.Merge(effective.Form, extensions);
            effective.DataSourceAddedBy = merged.DataSourceAddedBy;
            effective.Warnings.AddRange(merged.Warnings);
            var controlOrigin = merged.ControlAddedBy;

            effective.Root = new FormControlNode { Name = DesignRootName, Control = effective.Form.Design, IsContainer = true };
            foreach (var child in GetChildControls(effective.Form.Design))
                AddControlNode(child, effective.Root, controlOrigin, effective.ByName);
            return effective;
        }

        private static void AddControlNode(AxFormControl control, FormControlNode parent,
            Dictionary<string, string> controlOrigin, Dictionary<string, FormControlNode> byName)
        {
            if (control == null || string.IsNullOrEmpty(control.Name)) return;

            var node = new FormControlNode
            {
                Name = control.Name,
                Control = control,
                Parent = parent,
                IsContainer = IsContainerControl(control)
            };
            controlOrigin.TryGetValue(control.Name, out node.AddedBy);
            parent.Children.Add(node);
            byName[control.Name] = node;

            foreach (var child in GetChildControls(control))
                AddControlNode(child, node, controlOrigin, byName);
        }

        private static IEnumerable<string> GetExtensionNames(System.Collections.IEnumerable primaryKeys, string baseName)
        {
            string prefix = baseName + ".";
            foreach (object key in primaryKeys)
            {
                string name = key?.ToString();
                if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    yield return name;
            }
        }

        private static IEnumerable<AxFormControl> GetChildControls(object control) =>
            FormExtensionMerger.ChildrenOf(control);

        private static bool IsContainerControl(object control) =>
            control?.GetType().GetProperty("Controls")?.PropertyType == typeof(KeyedObjectCollection<AxFormControl>);

        private static string GetStringProperty(object target, string propertyName) =>
            target?.GetType().GetProperty(propertyName)?.GetValue(target) as string;

        // Base table fields plus fields added by every table extension.
        private Dictionary<string, AxTableField> GetEffectiveTableFields(string tableName)
        {
            var fields = new Dictionary<string, AxTableField>(StringComparer.OrdinalIgnoreCase);
            var table = MetaService.GetTable(tableName);
            if (table == null) return null;

            foreach (var f in table.Fields)
                fields[f.Name] = f;
            foreach (string extName in GetExtensionNames(MetadataProvider.TableExtensions.GetPrimaryKeys(), tableName))
            {
                var ext = MetadataProvider.TableExtensions.Read(extName);
                if (ext == null) continue;
                foreach (var f in ext.Fields)
                    fields[f.Name] = f;
            }
            return fields;
        }

        // ── Create / update ──

        private MetaModelResult CreateFormExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxForm",
                baseName => MetaService.GetForm(baseName) != null,
                () => MetadataProvider.FormExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            // Reject bad placement before anything is written.
            string baseForm = req.ObjectName.Substring(0, req.ObjectName.IndexOf('.'));
            var errors = ValidateFormExtensionRequest(BuildEffectiveForm(baseForm), req.ToUpdateRequest());
            if (errors.Count > 0) return Fail(FormatPlacementErrors(errors));

            MetadataProvider.FormExtensions.Create(new AxFormExtension { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxFormExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateFormExtension(UpdateObjectRequest req)
        {
            var ext = MetaService.GetFormExtension(req.ObjectName);
            if (ext == null)
                return Fail("FormExtension '" + req.ObjectName + "' not found.");

            var saveInfo = GetExtensionSaveInfo("AxFormExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);

            string baseForm = req.ObjectName.Substring(0, req.ObjectName.IndexOf('.'));
            var before = BuildEffectiveForm(baseForm);
            if (before == null)
                return Fail("Base form '" + baseForm + "' not found.");

            var errors = ValidateFormExtensionRequest(before, req);
            if (errors.Count > 0) return Fail(FormatPlacementErrors(errors));

            ApplyTypedMetadata(ext, req);
            ApplyFormExtensionDataSources(ext, req.DataSources);
            if (req.Controls != null)
                foreach (var dto in req.Controls)
                    ext.Controls.Add(BuildExtensionControl(dto));
            ApplyControlModifications(ext, req.ControlModifications);

            MetadataProvider.FormExtensions.Update(ext, saveInfo);

            var placement = VerifyFormPlacement(baseForm, req.Controls, before);
            if (placement.Errors.Count > 0)
                return Fail("Saved AxFormExtension '" + req.ObjectName + "', but the placement check failed:\n  - "
                    + string.Join("\n  - ", placement.Errors));

            return Ok("Updated AxFormExtension '" + req.ObjectName + "'."
                + (placement.Placed.Count > 0 ? "\nPlacement verified:\n  - " + string.Join("\n  - ", placement.Placed) : ""));
        }

        private static string FormatPlacementErrors(List<string> errors) =>
            "Form extension not saved. Fix these and retry (call xpp_get_form_layout to see valid parents and siblings):\n  - "
            + string.Join("\n  - ", errors);

        // ── Validation (before save) ──

        private List<string> ValidateFormExtensionRequest(EffectiveForm effective, UpdateObjectRequest req)
        {
            var errors = new List<string>();
            if (effective == null)
            {
                errors.Add("Base form not found.");
                return errors;
            }

            // Data source name -> table, including data sources added by this request.
            var dataSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ds in effective.Form.DataSources)
                dataSources[ds.Name] = ds.Table;

            var linkTypeProp = typeof(AxFormDataSourceRoot).GetProperty("LinkType");
            if (req.DataSources != null)
            {
                foreach (var ds in req.DataSources)
                {
                    if (dataSources.ContainsKey(ds.Name))
                        errors.Add("Data source '" + ds.Name + "' already exists on the form.");
                    else if (string.IsNullOrEmpty(ds.Table) || MetaService.GetTable(ds.Table) == null)
                        errors.Add("Data source '" + ds.Name + "': table '" + ds.Table + "' not found.");
                    else
                        dataSources[ds.Name] = ds.Table;
                }
                foreach (var ds in req.DataSources)
                {
                    if (!string.IsNullOrEmpty(ds.ParentDataSource) && !dataSources.ContainsKey(ds.ParentDataSource))
                        errors.Add("Data source '" + ds.Name + "': parentDataSource '" + ds.ParentDataSource + "' is not a data source on the form.");
                    if (!string.IsNullOrEmpty(ds.LinkType)
                        && (!linkTypeProp.PropertyType.IsEnum
                            || !Enum.GetNames(linkTypeProp.PropertyType).Contains(ds.LinkType, StringComparer.OrdinalIgnoreCase)))
                        errors.Add("Data source '" + ds.Name + "': linkType '" + ds.LinkType + "' is invalid. Valid: "
                            + (linkTypeProp.PropertyType.IsEnum ? string.Join(", ", Enum.GetNames(linkTypeProp.PropertyType)) : "?"));
                }
            }

            var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tableFieldCache = new Dictionary<string, Dictionary<string, AxTableField>>(StringComparer.OrdinalIgnoreCase);

            if (req.Controls != null)
            {
                foreach (var dto in req.Controls)
                {
                    FormControlNode parentNode = ResolvePlacement(effective, dto, errors);
                    string inheritedDs = parentNode?.Control != null ? GetStringProperty(parentNode.Control, "DataSource") : null;
                    ValidateControlDefinition(dto, inheritedDs, effective, dataSources, tableFieldCache, newNames, errors);
                }
            }

            if (req.ControlModifications != null)
            {
                foreach (var mod in req.ControlModifications)
                {
                    if (!effective.ByName.ContainsKey(mod.Name ?? ""))
                        errors.Add("controlModifications: control '" + mod.Name + "' not found on the form.");
                    else if (mod.Properties == null || mod.Properties.Count == 0)
                        errors.Add("controlModifications: '" + mod.Name + "' has no properties to change.");
                }
            }

            return errors;
        }

        // Normalizes dto.Parent/Position/PreviousSibling and returns the parent node, or null on error.
        private static FormControlNode ResolvePlacement(EffectiveForm effective, FormControlDto dto, List<string> errors)
        {
            string label = "Control '" + dto.Name + "'";
            string position = string.IsNullOrWhiteSpace(dto.Position) ? "end" : dto.Position.Trim().ToLowerInvariant();
            if (position != "end" && position != "begin" && position != "after")
            {
                errors.Add(label + ": position must be 'end', 'begin' or 'after' (got '" + dto.Position + "').");
                return null;
            }
            dto.Position = position;

            FormControlNode parentNode = null;
            bool parentGiven = !string.IsNullOrWhiteSpace(dto.Parent);
            if (parentGiven && !string.Equals(dto.Parent.Trim(), DesignRootName, StringComparison.OrdinalIgnoreCase))
            {
                if (!effective.ByName.TryGetValue(dto.Parent.Trim(), out parentNode))
                {
                    errors.Add(label + ": parent '" + dto.Parent + "' not found on the form.");
                    return null;
                }
            }

            if (position == "after")
            {
                if (string.IsNullOrWhiteSpace(dto.PreviousSibling))
                {
                    errors.Add(label + ": position 'after' requires previousSibling.");
                    return null;
                }
                if (!effective.ByName.TryGetValue(dto.PreviousSibling.Trim(), out var sibling))
                {
                    errors.Add(label + ": previousSibling '" + dto.PreviousSibling + "' not found on the form.");
                    return null;
                }
                if (!parentGiven)
                    parentNode = sibling.Parent == effective.Root ? null : sibling.Parent;
                else if (sibling.Parent != (parentNode ?? effective.Root))
                {
                    errors.Add(label + ": previousSibling '" + sibling.Name + "' is not a direct child of '"
                        + (parentNode?.Name ?? DesignRootName) + "' (its parent is '" + sibling.Parent.Name + "').");
                    return null;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(dto.PreviousSibling))
                {
                    errors.Add(label + ": previousSibling is only valid with position 'after'.");
                    return null;
                }
                if (!parentGiven)
                {
                    errors.Add(label + ": parent is required for position '" + position
                        + "' (use 'Design' for the top level of the form).");
                    return null;
                }
            }

            var resolvedParent = parentNode ?? effective.Root;
            if (!resolvedParent.IsContainer)
            {
                errors.Add(label + ": parent '" + resolvedParent.Name + "' is a "
                    + ((resolvedParent.Control as AxFormControl)?.Type.ToString() ?? "control") + " and cannot contain controls.");
                return null;
            }

            dto.Parent = parentNode?.Name ?? DesignRootName;
            dto.PreviousSibling = dto.PreviousSibling?.Trim();
            return resolvedParent;
        }

        private static bool IsDesignRoot(string name) =>
            string.IsNullOrEmpty(name) || string.Equals(name, DesignRootName, StringComparison.OrdinalIgnoreCase);

        private void ValidateControlDefinition(FormControlDto dto, string inheritedDataSource, EffectiveForm effective,
            Dictionary<string, string> dataSources, Dictionary<string, Dictionary<string, AxTableField>> tableFieldCache,
            HashSet<string> newNames, List<string> errors)
        {
            string label = "Control '" + dto.Name + "'";
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                errors.Add("Every control needs a name.");
                return;
            }
            if (effective.ByName.ContainsKey(dto.Name))
                errors.Add(label + ": a control with this name already exists on the form.");
            if (!newNames.Add(dto.Name))
                errors.Add(label + ": name is used more than once in this request.");

            if (string.IsNullOrEmpty(dto.DataSource) && !string.IsNullOrEmpty(dto.DataField))
                dto.DataSource = inheritedDataSource;

            AxTableField boundField = null;
            if (!string.IsNullOrEmpty(dto.DataSource))
            {
                if (!dataSources.TryGetValue(dto.DataSource, out string table))
                    errors.Add(label + ": dataSource '" + dto.DataSource + "' is not a data source on the form.");
                else if (!string.IsNullOrEmpty(dto.DataField))
                {
                    if (!tableFieldCache.TryGetValue(table, out var fields))
                        tableFieldCache[table] = fields = GetEffectiveTableFields(table);
                    if (fields == null || !fields.TryGetValue(dto.DataField, out boundField))
                        errors.Add(label + ": field '" + dto.DataField + "' not found on table '" + table
                            + "' (data source '" + dto.DataSource + "'), including its table extensions.");
                }
            }
            else if (!string.IsNullOrEmpty(dto.DataField))
                errors.Add(label + ": dataField '" + dto.DataField + "' needs a dataSource (none given or inherited from the parent).");

            if (string.IsNullOrWhiteSpace(dto.ControlType))
                dto.ControlType = InferControlType(boundField);
            if (string.IsNullOrWhiteSpace(dto.ControlType))
                errors.Add(label + ": controlType is required (it can only be inferred for controls bound to a dataField).");
            else if (!FormControlClasses.ContainsKey(dto.ControlType))
                errors.Add(label + ": controlType '" + dto.ControlType + "' is not supported. Supported: "
                    + string.Join(", ", FormControlClasses.Keys));
            else if (dto.Controls.Count > 0 && !IsContainerControl(Activator.CreateInstance(FormControlClasses[dto.ControlType])))
                errors.Add(label + ": a " + dto.ControlType + " control cannot contain child controls.");

            foreach (var child in dto.Controls)
            {
                if (!string.IsNullOrWhiteSpace(child.Parent) || !string.IsNullOrWhiteSpace(child.Position)
                    || !string.IsNullOrWhiteSpace(child.PreviousSibling))
                    errors.Add("Control '" + child.Name + "': nested controls are placed inside '" + dto.Name
                        + "' in list order; do not set parent/position/previousSibling on them.");
                ValidateControlDefinition(child, dto.DataSource ?? inheritedDataSource, effective,
                    dataSources, tableFieldCache, newNames, errors);
            }
        }

        private static string InferControlType(AxTableField field)
        {
            switch (field)
            {
                case null: return null;
                case AxTableFieldEnum e:
                    return string.Equals(e.EnumType, "NoYes", StringComparison.OrdinalIgnoreCase) ? "CheckBox" : "ComboBox";
                case AxTableFieldString _: return "String";
                case AxTableFieldInt _: return "Integer";
                case AxTableFieldInt64 _: return "Int64";
                case AxTableFieldReal _: return "Real";
                case AxTableFieldDate _: return "Date";
                case AxTableFieldUtcDateTime _: return "DateTime";
                case AxTableFieldTime _: return "Time";
                case AxTableFieldGuid _: return "Guid";
                default: return null;
            }
        }

        // ── Apply ──

        private AxFormExtensionControl BuildExtensionControl(FormControlDto dto)
        {
            var position = dto.Position == "after" ? ExtensionItemPositionType.AfterItem
                : dto.Position == "begin" ? ExtensionItemPositionType.Begin
                : ExtensionItemPositionType.End;

            return new AxFormExtensionControl
            {
                Name = dto.Name,
                FormControl = BuildFormControl(dto),
                Parent = IsDesignRoot(dto.Parent) ? null : dto.Parent,
                PositionType = position,
                PreviousSibling = position == ExtensionItemPositionType.AfterItem ? dto.PreviousSibling : null
            };
        }

        private static AxFormControl BuildFormControl(FormControlDto dto)
        {
            var control = (AxFormControl)Activator.CreateInstance(FormControlClasses[dto.ControlType]);
            control.Name = dto.Name;

            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(dto.DataSource)) props["DataSource"] = dto.DataSource;
            if (!string.IsNullOrEmpty(dto.DataField)) props["DataField"] = dto.DataField;
            if (!string.IsNullOrEmpty(dto.DataGroup)) props["DataGroup"] = dto.DataGroup;
            if (!string.IsNullOrEmpty(dto.Label))
                props[control.GetType().GetProperty("Label") != null ? "Label" : "Caption"] = dto.Label;
            if (dto.Properties != null)
                foreach (var kv in dto.Properties)
                    props[kv.Key] = kv.Value;
            ApplyProperties(control, props);

            if (control.GetType().GetProperty("Controls")?.GetValue(control) is KeyedObjectCollection<AxFormControl> children)
                foreach (var child in dto.Controls)
                    children.Add(BuildFormControl(child));
            return control;
        }

        private void ApplyFormExtensionDataSources(AxFormExtension ext, List<QueryDataSourceDto> dataSources)
        {
            if (dataSources == null) return;
            foreach (var dto in dataSources)
            {
                var ds = new AxFormDataSourceRoot { Name = dto.Name, Table = dto.Table };
                if (!string.IsNullOrEmpty(dto.ParentDataSource))
                    ds.JoinSource = dto.ParentDataSource;
                if (!string.IsNullOrEmpty(dto.LinkType))
                    ApplyProperties(ds, new Dictionary<string, string> { { "LinkType", dto.LinkType } });

                // The designer lists every table field on a new data source; mirror that.
                var fields = GetEffectiveTableFields(dto.Table);
                if (fields != null)
                    foreach (string fieldName in fields.Keys)
                        ds.Fields.Add(new AxFormDataSourceField { DataField = fieldName });
                ext.DataSources.Add(ds);
            }
        }

        private static void ApplyControlModifications(AxFormExtension ext, List<ControlModificationDto> mods)
        {
            if (mods == null) return;
            foreach (var mod in mods)
            {
                var target = ext.ControlModifications.FirstOrDefault(m =>
                    string.Equals(m.Name, mod.Name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    target = new AxExtensionModification { Name = mod.Name };
                    ext.ControlModifications.Add(target);
                }
                foreach (var kv in mod.Properties)
                {
                    var existing = target.PropertyModifications.FirstOrDefault(p =>
                        string.Equals(p.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                        existing.Value = kv.Value;
                    else
                        target.PropertyModifications.Add(new AxPropertyModification { Name = kv.Key, Value = kv.Value });
                }
            }
        }

        // ── Verification (after save) ──

        private sealed class PlacementReport
        {
            public List<string> Placed = new List<string>();
            public List<string> Errors = new List<string>();
        }

        // Re-applies all extensions with D365FO's own applier and checks each control landed where requested.
        private PlacementReport VerifyFormPlacement(string formName, List<FormControlDto> controls, EffectiveForm before)
        {
            var report = new PlacementReport();
            if (controls == null || controls.Count == 0) return report;

            var after = BuildEffectiveForm(formName);
            foreach (string w in after.Warnings)
                report.Errors.Add(w);

            foreach (var dto in controls)
            {
                if (!after.ByName.TryGetValue(dto.Name, out var node))
                {
                    report.Errors.Add("'" + dto.Name + "' is not on the form after applying extensions.");
                    continue;
                }

                string expectedParent = IsDesignRoot(dto.Parent) ? DesignRootName : dto.Parent;
                if (!string.Equals(node.Parent.Name, expectedParent, StringComparison.OrdinalIgnoreCase))
                {
                    report.Errors.Add("'" + dto.Name + "' landed under '" + node.Parent.Name + "', expected '" + expectedParent + "'.");
                    continue;
                }

                var siblings = node.Parent.Children;
                int index = siblings.IndexOf(node);
                // Begin/end are relative to the base form's controls; order among several extensions' begin/end controls depends on load order.
                var originalNames = new HashSet<string>(
                    (IsDesignRoot(dto.Parent) ? before.Root : before.ByName[dto.Parent]).Children
                        .Where(c => c.AddedBy == null).Select(c => c.Name),
                    StringComparer.OrdinalIgnoreCase);
                var originalIndexes = siblings.Where(s => originalNames.Contains(s.Name)).Select(s => siblings.IndexOf(s)).ToList();

                bool ok;
                string where;
                switch (dto.Position)
                {
                    case "after":
                        int siblingIndex = siblings.FindIndex(s => string.Equals(s.Name, dto.PreviousSibling, StringComparison.OrdinalIgnoreCase));
                        ok = siblingIndex >= 0 && siblingIndex < index;
                        where = "after '" + dto.PreviousSibling + "'";
                        break;
                    case "begin":
                        ok = originalIndexes.All(i => i > index);
                        where = "at the beginning (before the form's own controls)";
                        break;
                    default:
                        ok = originalIndexes.All(i => i < index);
                        where = "at the end (after the form's own controls)";
                        break;
                }

                string summary = "'" + dto.Name + "' in '" + expectedParent + "' " + where
                    + " (position " + (index + 1) + " of " + siblings.Count + ")";
                if (ok) report.Placed.Add(summary);
                else report.Errors.Add("expected " + summary + ", but the order does not match.");

                VerifyNestedControls(dto, node, report);
            }
            return report;
        }

        private static void VerifyNestedControls(FormControlDto dto, FormControlNode node, PlacementReport report)
        {
            foreach (var child in dto.Controls)
            {
                var childNode = node.Children.FirstOrDefault(c => string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase));
                if (childNode == null)
                {
                    report.Errors.Add("'" + child.Name + "' is not inside '" + dto.Name + "'.");
                    continue;
                }
                VerifyNestedControls(child, childNode, report);
            }
        }

        // ── Read ──

        private static void FlattenControls(IEnumerable<FormControlDto> controls, Dictionary<string, FormControlDto> target)
        {
            if (controls == null) return;
            foreach (var c in controls)
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                target[c.Name] = c;
                FlattenControls(c.Controls, target);
            }
        }

        private static List<FormControlDto> ExtractFormExtensionControls(AxFormExtension ext)
        {
            var result = new List<FormControlDto>();
            foreach (var c in ext.Controls)
            {
                var dto = ExtractFormControl(c.FormControl);
                if (dto == null) continue;
                dto.Parent = c.Parent;
                dto.Position = c.PositionType == ExtensionItemPositionType.AfterItem ? "after"
                    : c.PositionType == ExtensionItemPositionType.Begin ? "begin" : "end";
                dto.PreviousSibling = c.PreviousSibling;
                result.Add(dto);
            }
            return result;
        }

        private static FormControlDto ExtractFormControl(AxFormControl control)
        {
            if (control == null) return null;
            var dto = new FormControlDto
            {
                Name = control.Name,
                ControlType = control.Type.ToString(),
                DataSource = GetStringProperty(control, "DataSource"),
                DataField = GetStringProperty(control, "DataField"),
                DataGroup = GetStringProperty(control, "DataGroup"),
                Label = GetStringProperty(control, "Label") ?? GetStringProperty(control, "Caption")
            };
            foreach (var child in GetChildControls(control))
            {
                var childDto = ExtractFormControl(child);
                if (childDto != null) dto.Controls.Add(childDto);
            }
            return dto;
        }

        private static List<ControlModificationDto> ExtractControlModifications(AxFormExtension ext)
        {
            return ext.ControlModifications.Select(m => new ControlModificationDto
            {
                Name = m.Name,
                Properties = ExtractPropertyModifications(m.PropertyModifications)
            }).ToList();
        }

        private static List<QueryDataSourceDto> ExtractFormExtensionDataSources(AxFormExtension ext)
        {
            return ext.DataSources.Select(ds => new QueryDataSourceDto
            {
                Name = ds.Name,
                Table = ds.Table,
                ParentDataSource = ds.JoinSource,
                LinkType = string.IsNullOrEmpty(ds.JoinSource) ? null : ds.LinkType.ToString()
            }).ToList();
        }
    }
}
