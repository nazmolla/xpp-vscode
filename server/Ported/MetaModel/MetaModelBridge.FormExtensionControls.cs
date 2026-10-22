using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Form extensions: rules for control modifications (#41) and changes to the extension's own controls (#42).
    public sealed partial class MetaModelBridge
    {
        private static HashSet<string> _modifiableControlProperties;

        // Properties a form extension may modify on base-form controls: D365FO's own list (AxFormExtensionValidator),
        // so it follows the installed platform; the fallback is the list of platform update 10.0.4x.
        private static HashSet<string> ModifiableControlProperties()
        {
            if (_modifiableControlProperties != null) return _modifiableControlProperties;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var validator = Type.GetType("Microsoft.Dynamics.AX.Metadata.Validation.AxFormExtensionValidator, Microsoft.Dynamics.AX.Metadata.Validation");
                var field = validator?.GetField("supportedModificationTypes", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (field?.GetValue(null) is IEnumerable<string> values)
                    foreach (string v in values) set.Add(v);
            }
            catch { }
            if (set.Count == 0)
                foreach (string v in new[] { "Text", "Label", "HelpText", "Caption", "Visible", "Enabled", "EnableFormRef", "CountryRegionCodes",
                                             "Pattern", "PatternVersion", "NeedsRecord", "SaveRecord", "AllowEdit", "NeededPermission", "Style", "DataGroup" })
                    set.Add(v);
            return _modifiableControlProperties = set;
        }

        private static void ValidateControlModifications(EffectiveForm effective, UpdateObjectRequest req, List<string> errors)
        {
            if (req.ControlModifications == null) return;
            var inRequest = new Dictionary<string, FormControlDto>(StringComparer.OrdinalIgnoreCase);
            FlattenControls(req.Controls ?? new List<FormControlDto>(), inRequest);
            var modifiable = ModifiableControlProperties();

            foreach (var mod in req.ControlModifications)
            {
                string label = "controlModifications '" + mod.Name + "'";
                if (inRequest.ContainsKey(mod.Name ?? ""))
                {
                    errors.Add(label + ": the control is added by this request; put the properties on the control itself (controls[].properties).");
                    continue;
                }
                if (!effective.ByName.TryGetValue(mod.Name ?? "", out var node))
                {
                    errors.Add(label + ": control not found on the form.");
                    continue;
                }
                if (string.Equals(node.AddedBy, req.ObjectName, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(label + ": the control is added by this extension, and an extension cannot modify its own controls "
                        + "(the build fails with UnsupportedPropertyForModification). Change it with updateControls: [{ \"name\": \"" + mod.Name + "\", \"properties\": { ... } }].");
                    continue;
                }
                if (mod.Properties == null || mod.Properties.Count == 0)
                {
                    errors.Add(label + ": no properties to change.");
                    continue;
                }
                var blocked = mod.Properties.Keys.Where(k => !modifiable.Contains(k)).ToList();
                if (blocked.Count > 0)
                    errors.Add(label + ": " + string.Join(", ", blocked)
                        + " cannot be changed by a form extension (UnsupportedPropertyForModification). Properties an extension can change: "
                        + string.Join(", ", modifiable.OrderBy(p => p)) + ".");
            }
        }

        // ── Own controls: locate, validate, apply ──

        private sealed class OwnControl
        {
            public AxFormExtensionControl Top;               // the extension entry holding the control tree
            public AxFormControl Control;
            public IList<AxFormControl> ParentCollection;     // null when Control is the top entry's control
        }

        private static OwnControl FindOwnControl(AxFormExtension ext, string name)
        {
            foreach (var top in ext.Controls)
            {
                if (top.FormControl == null) continue;
                if (string.Equals(top.FormControl.Name, name, StringComparison.OrdinalIgnoreCase))
                    return new OwnControl { Top = top, Control = top.FormControl };
                var hit = FindNested(top.FormControl, name);
                if (hit != null) { hit.Top = top; return hit; }
            }
            return null;
        }

        private static OwnControl FindNested(AxFormControl container, string name)
        {
            if (!(container.GetType().GetProperty("Controls")?.GetValue(container) is IList<AxFormControl> children)) return null;
            foreach (var child in children)
            {
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                    return new OwnControl { Control = child, ParentCollection = children };
                var hit = FindNested(child, name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static IEnumerable<string> SubtreeNames(AxFormControl control, ICollection<string> skip = null)
        {
            if (skip != null && skip.Contains(control.Name)) yield break;
            yield return control.Name;
            foreach (var child in GetChildControls(control))
                foreach (string n in SubtreeNames(child, skip))
                    yield return n;
        }

        private static bool WantsMove(FormControlDto dto) =>
            !string.IsNullOrWhiteSpace(dto.Parent) || !string.IsNullOrWhiteSpace(dto.Position) || !string.IsNullOrWhiteSpace(dto.PreviousSibling);

        private void ValidateOwnControlChanges(AxFormExtension ext, EffectiveForm effective, UpdateObjectRequest req, List<string> errors)
        {
            string extName = req.ObjectName;
            bool IsOwn(string name) => effective.ByName.TryGetValue(name ?? "", out var n)
                && string.Equals(n.AddedBy, extName, StringComparison.OrdinalIgnoreCase);
            string Owner(string name) => effective.ByName.TryGetValue(name ?? "", out var n)
                ? (n.AddedBy == null ? "the base form" : "extension " + n.AddedBy) : null;

            var dataSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ds in effective.Form.DataSources) dataSources[ds.Name] = ds.Table;
            if (req.DataSources != null) foreach (var ds in req.DataSources) dataSources[ds.Name] = ds.Table;

            // Controls moved out of a removed container in the same request survive (moves are applied first).
            var movedOut = new HashSet<string>((req.UpdateControls ?? new List<FormControlDto>()).Where(WantsMove).Select(d => d.Name),
                StringComparer.OrdinalIgnoreCase);
            var removing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in req.RemoveControls ?? new List<string>())
            {
                if (!IsOwn(name))
                {
                    string owner = Owner(name);
                    errors.Add("removeControls '" + name + "': " + (owner == null ? "control not found on the form."
                        : "the control belongs to " + owner + "; an extension can only remove controls it added (hide others with controlModifications Visible=No)."));
                    continue;
                }
                var own = FindOwnControl(ext, name);
                if (own != null) foreach (string n in SubtreeNames(own.Control, movedOut)) removing.Add(n);
            }
            if (removing.Count > 0)
            {
                // Controls that stay but are anchored to a removed one would be left without a place.
                foreach (var top in ext.Controls.Where(t => t.FormControl != null && !removing.Contains(t.FormControl.Name)))
                    if (removing.Contains(top.Parent ?? "") || removing.Contains(top.PreviousSibling ?? ""))
                        errors.Add("removeControls: '" + top.FormControl.Name + "' is placed in or after a removed control; remove or move it too.");
                foreach (string other in GetExtensionNames(MetadataProvider.FormExtensions.GetPrimaryKeys(), ExtensionBaseName(extName))
                             .Where(n => !string.Equals(n, extName, StringComparison.OrdinalIgnoreCase)))
                {
                    var otherExt = MetadataProvider.FormExtensions.Read(other);
                    foreach (var top in otherExt?.Controls ?? Enumerable.Empty<AxFormExtensionControl>())
                        if (removing.Contains(top.Parent ?? "") || removing.Contains(top.PreviousSibling ?? ""))
                            errors.Add("removeControls: extension " + other + " places '" + top.FormControl?.Name + "' in or after a removed control.");
                }
            }

            foreach (var dto in req.UpdateControls ?? new List<FormControlDto>())
            {
                string label = "updateControls '" + dto.Name + "'";
                if (!IsOwn(dto.Name))
                {
                    string owner = Owner(dto.Name);
                    errors.Add(label + ": " + (owner == null ? "control not found on the form."
                        : "the control belongs to " + owner + "; change it with controlModifications (properties: "
                          + string.Join(", ", ModifiableControlProperties().OrderBy(p => p)) + ")."));
                    continue;
                }
                if ((req.RemoveControls ?? new List<string>()).Contains(dto.Name, StringComparer.OrdinalIgnoreCase)) { errors.Add(label + ": the control is also being removed."); continue; }
                if (removing.Contains(dto.Name)) { errors.Add(label + ": it is inside a control being removed; move it out (parent/position) or drop the update."); continue; }
                var control = (AxFormControl)effective.ByName[dto.Name].Control;
                if (!string.IsNullOrWhiteSpace(dto.ControlType)
                    && FormControlClasses.TryGetValue(dto.ControlType, out Type wanted) && wanted != control.GetType())
                    errors.Add(label + ": the control type cannot be changed (" + control.Type + " -> " + dto.ControlType + "); remove it and add a new one.");
                if (dto.Controls != null && dto.Controls.Count > 0)
                    errors.Add(label + ": to add child controls use controls with parent '" + dto.Name + "'.");

                var unknown = (dto.Properties ?? new Dictionary<string, string>()).Keys
                    .Where(k => FindProperty(control.GetType(), k)?.CanWrite != true).ToList();
                if (unknown.Count > 0)
                    errors.Add(label + ": " + control.GetType().Name + " has no property " + string.Join(", ", unknown) + ".");

                string ds = dto.DataSource ?? GetStringProperty(control, "DataSource");
                if (!string.IsNullOrEmpty(dto.DataSource) && !dataSources.ContainsKey(dto.DataSource))
                    errors.Add(label + ": data source '" + dto.DataSource + "' is not on the form.");
                else if (!string.IsNullOrEmpty(dto.DataField) && ds != null && dataSources.TryGetValue(ds, out string table))
                {
                    var fields = GetEffectiveTableFields(table);
                    if (fields != null && !fields.ContainsKey(dto.DataField))
                        errors.Add(label + ": field '" + dto.DataField + "' not found on " + table + " (data source " + ds + ").");
                }

                if (WantsMove(dto))
                {
                    var target = ResolvePlacement(effective, dto, errors);
                    if (target != null)
                    {
                        var own = FindOwnControl(ext, dto.Name);
                        var subtree = own == null ? new HashSet<string>() : new HashSet<string>(SubtreeNames(own.Control), StringComparer.OrdinalIgnoreCase);
                        if (subtree.Contains(target.Name))
                            errors.Add(label + ": cannot move a control into itself or one of its children.");
                        if (string.Equals(dto.PreviousSibling, dto.Name, StringComparison.OrdinalIgnoreCase))
                            errors.Add(label + ": previousSibling cannot be the control itself.");
                    }
                }
            }

            foreach (string entry in req.RemoveControlModifications ?? new List<string>())
            {
                SplitModificationKey(entry, out string control, out string property);
                var mod = ext.ControlModifications.FirstOrDefault(m => string.Equals(m.Name, control, StringComparison.OrdinalIgnoreCase));
                if (mod == null)
                    errors.Add("removeControlModifications '" + entry + "': this extension has no modification of control '" + control + "'."
                        + (ext.ControlModifications.Count > 0 ? " It modifies: " + string.Join(", ", ext.ControlModifications.Select(m => m.Name)) + "." : ""));
                else if (property != null && !mod.PropertyModifications.Any(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase)))
                    errors.Add("removeControlModifications '" + entry + "': the modification of '" + control + "' does not set " + property
                        + " (it sets " + string.Join(", ", mod.PropertyModifications.Select(p => p.Name)) + ").");
            }
        }

        // "Control" or "Control.Property".
        private static void SplitModificationKey(string entry, out string control, out string property)
        {
            int dot = (entry ?? "").LastIndexOf('.');
            control = dot > 0 ? entry.Substring(0, dot) : entry;
            property = dot > 0 ? entry.Substring(dot + 1) : null;
        }

        /// <summary>Applies removeControls, updateControls and removeControlModifications. Returns report lines.</summary>
        private List<string> ApplyOwnControlChanges(AxFormExtension ext, EffectiveForm effective, UpdateObjectRequest req, out List<FormControlDto> moved)
        {
            var report = new List<string>();
            moved = new List<FormControlDto>();

            var updated = new List<string>();
            foreach (var dto in req.UpdateControls ?? new List<FormControlDto>())
            {
                var own = FindOwnControl(ext, dto.Name);
                if (own == null) continue;
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(dto.DataSource)) props["DataSource"] = dto.DataSource;
                if (!string.IsNullOrEmpty(dto.DataField)) props["DataField"] = dto.DataField;
                if (!string.IsNullOrEmpty(dto.DataGroup)) props["DataGroup"] = dto.DataGroup;
                if (!string.IsNullOrEmpty(dto.Label))
                    props[own.Control.GetType().GetProperty("Label") != null ? "Label" : "Caption"] = dto.Label;
                if (dto.Properties != null) foreach (var kv in dto.Properties) props[kv.Key] = kv.Value;
                if (props.Count > 0)
                {
                    ApplyProperties(own.Control, props);
                    updated.Add(dto.Name + " (" + string.Join(", ", props.Select(kv => kv.Key + "=" + kv.Value)) + ")");
                }

                if (WantsMove(dto))
                {
                    var position = dto.Position == "after" ? ExtensionItemPositionType.AfterItem
                        : dto.Position == "begin" ? ExtensionItemPositionType.Begin
                        : ExtensionItemPositionType.End;
                    Detach(ext, own);
                    ext.Controls.Add(new AxFormExtensionControl
                    {
                        Name = dto.Name,
                        FormControl = own.Control,
                        Parent = IsDesignRoot(dto.Parent) ? null : dto.Parent,
                        PositionType = position,
                        PreviousSibling = position == ExtensionItemPositionType.AfterItem ? dto.PreviousSibling : null
                    });
                    moved.Add(new FormControlDto { Name = dto.Name, Parent = dto.Parent, Position = dto.Position, PreviousSibling = dto.PreviousSibling });
                }
            }
            if (updated.Count > 0) report.Add("Controls updated: " + string.Join("; ", updated));

            var removed = new List<string>();
            foreach (string name in req.RemoveControls ?? new List<string>())
            {
                var own = FindOwnControl(ext, name);
                if (own == null) continue;
                var names = new HashSet<string>(SubtreeNames(own.Control), StringComparer.OrdinalIgnoreCase);
                Detach(ext, own);
                foreach (var mod in ext.ControlModifications.Where(m => names.Contains(m.Name)).ToList())
                    ext.ControlModifications.Remove(mod);
                removed.Add(name + (names.Count > 1 ? " (with " + (names.Count - 1) + " child control(s))" : ""));
            }
            if (removed.Count > 0) report.Add("Controls removed: " + string.Join(", ", removed));


            var dropped = new List<string>();
            foreach (string entry in req.RemoveControlModifications ?? new List<string>())
            {
                SplitModificationKey(entry, out string control, out string property);
                var mod = ext.ControlModifications.FirstOrDefault(m => string.Equals(m.Name, control, StringComparison.OrdinalIgnoreCase));
                if (mod == null) continue;
                if (property == null)
                    ext.ControlModifications.Remove(mod);
                else
                {
                    foreach (var p in mod.PropertyModifications.Where(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase)).ToList())
                        mod.PropertyModifications.Remove(p);
                    if (mod.PropertyModifications.Count == 0) ext.ControlModifications.Remove(mod);
                }
                dropped.Add(entry);
            }
            if (dropped.Count > 0) report.Add("Control modifications removed: " + string.Join(", ", dropped));
            return report;
        }

        private static void Detach(AxFormExtension ext, OwnControl own)
        {
            if (own.ParentCollection == null) ext.Controls.Remove(own.Top);
            else own.ParentCollection.Remove(own.Control);
        }
    }
}
