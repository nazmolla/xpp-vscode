using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // D365FO's ExtensionAppliers.ApplyExtension(AxForm, ...) does not add extension controls or data sources,
    // so this merges them the way the form designer does: parent + Begin/End/AfterItem.
    public static class FormExtensionMerger
    {
        private const int MaxPasses = 10;

        public sealed class MergeResult
        {
            public Dictionary<string, string> ControlAddedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> DataSourceAddedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Warnings = new List<string>();
        }

        // Extensions must be private copies: their controls are moved into the form.
        public static MergeResult Merge(AxForm form, IEnumerable<KeyValuePair<string, AxFormExtension>> extensions)
        {
            var result = new MergeResult();
            var pending = new List<KeyValuePair<string, AxFormExtensionControl>>();

            foreach (var ext in extensions)
            {
                foreach (var ds in ext.Value.DataSources.ToList())
                {
                    if (form.DataSources.Contains(ds.Name)) continue;
                    ext.Value.DataSources.Remove(ds);
                    form.DataSources.Add(ds);
                    result.DataSourceAddedBy[ds.Name] = ext.Key;
                }
                foreach (var c in ext.Value.Controls)
                    pending.Add(new KeyValuePair<string, AxFormExtensionControl>(ext.Key, c));
            }

            // Extensions may place controls inside or after controls added by other extensions.
            var anchored = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            for (int pass = 0; pass < MaxPasses && pending.Count > 0; pass++)
            {
                var deferred = new List<KeyValuePair<string, AxFormExtensionControl>>();
                foreach (var item in pending)
                {
                    if (!TryPlace(form, item.Value, anchored))
                    {
                        deferred.Add(item);
                        continue;
                    }
                    Collect(item.Value.FormControl, name => result.ControlAddedBy[name] = item.Key);
                }
                if (deferred.Count == pending.Count) break;
                pending = deferred;
            }

            foreach (var item in pending)
                result.Warnings.Add(item.Key + ": control '" + item.Value.FormControl?.Name + "' could not be placed (parent '"
                    + (item.Value.Parent ?? "Design") + "'" + (string.IsNullOrEmpty(item.Value.PreviousSibling) ? "" : ", after '" + item.Value.PreviousSibling + "'")
                    + " not found).");
            return result;
        }

        private static bool TryPlace(AxForm form, AxFormExtensionControl item, Dictionary<string, HashSet<string>> anchored)
        {
            var control = item.FormControl;
            if (control == null) return true;

            IList<AxFormControl> siblings = string.IsNullOrEmpty(item.Parent)
                ? form.Design.Controls
                : FindChildren(form.Design.Controls, item.Parent);
            if (siblings == null) return false;

            switch (item.PositionType)
            {
                case ExtensionItemPositionType.Begin:
                    siblings.Insert(0, control);
                    return true;
                case ExtensionItemPositionType.AfterItem:
                    int index = IndexOf(siblings, item.PreviousSibling);
                    if (index < 0) return false;

                    // Several controls anchored to the same sibling keep their listed order.
                    if (!anchored.TryGetValue(item.PreviousSibling, out var sameAnchor))
                        anchored[item.PreviousSibling] = sameAnchor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (index + 1 < siblings.Count && sameAnchor.Contains(siblings[index + 1].Name))
                        index++;
                    siblings.Insert(index + 1, control);
                    sameAnchor.Add(control.Name);
                    return true;
                default:
                    siblings.Add(control);
                    return true;
            }
        }

        public static IEnumerable<AxFormControl> ChildrenOf(object container) =>
            container?.GetType().GetProperty("Controls")?.GetValue(container) as IEnumerable<AxFormControl>
            ?? Enumerable.Empty<AxFormControl>();

        private static IList<AxFormControl> FindChildren(IEnumerable<AxFormControl> controls, string name)
        {
            foreach (var c in controls)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c.GetType().GetProperty("Controls")?.GetValue(c) as IList<AxFormControl>;
                var hit = FindChildren(ChildrenOf(c), name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static int IndexOf(IList<AxFormControl> siblings, string name)
        {
            for (int i = 0; i < siblings.Count; i++)
                if (string.Equals(siblings[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static void Collect(AxFormControl control, Action<string> collect)
        {
            if (control == null) return;
            collect(control.Name);
            foreach (var child in ChildrenOf(control))
                Collect(child, collect);
        }
    }
}
