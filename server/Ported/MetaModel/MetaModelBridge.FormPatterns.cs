using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Patterns;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Microsoft.Dynamics.AX.Metadata.Storage;
using Microsoft.Dynamics.AX.Metadata.Validation;
using Microsoft.Dynamics.AX.Metadata.XppCompiler;

namespace XppVsCode.Server.MetaModel
{
    // Form patterns (#43): before a form extension is saved, controls it adds get the property values their pattern
    // requires, and the compiler's own form extension validators run on the unsaved extension.
    public sealed partial class MetaModelBridge
    {
        private static PatternFactory _patternFactory;
        private static PatternFactory Patterns => _patternFactory ?? (_patternFactory = new PatternFactory(true));

        /// <summary>
        /// Sets property values required by the form's patterns (Equals restrictions) on the controls this request adds.
        /// Works on the unsaved extension; returns one line per value set.
        /// </summary>
        private List<string> ApplyPatternRequiredProperties(string baseForm, AxFormExtension ext, ICollection<string> newControls)
        {
            var applied = new List<string>();
            if (newControls == null || newControls.Count == 0) return applied;
            var wanted = new HashSet<string>(newControls, StringComparer.OrdinalIgnoreCase);
            var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int pass = 0; pass < 3; pass++)
            {
                var preview = BuildEffectiveForm(baseForm, ObjectCloner.MakeCopy(ext));
                if (preview == null) break;
                var fixes = new List<Tuple<string, string, string, string>>();   // control, property, value, pattern
                var visited = new HashSet<object>();
                foreach (var patterned in PatternedControls(preview))
                {
                    Pattern pattern;
                    try { pattern = Patterns.GetPatternByName(patterned.Pattern, patterned.PatternVersion, false); }
                    catch { continue; }
                    if (pattern == null) continue;
                    PatternResult result;
                    try { result = new PatternAnalyzer().TestPattern(patterned, pattern, null); }
                    catch { continue; }
                    CollectEqualsViolations(result, pattern, wanted, fixes, visited, 0);
                }

                bool changed = false;
                foreach (var fix in fixes.GroupBy(f => f.Item1 + "|" + f.Item2, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    if (!settled.Add(fix.Item1 + "|" + fix.Item2)) continue;   // set in an earlier pass: never flip it back
                    var own = FindOwnControl(ext, fix.Item1);
                    if (own == null) continue;
                    var prop = FindProperty(own.Control.GetType(), fix.Item2);
                    if (prop == null || !prop.CanWrite) continue;
                    string before = Convert.ToString(prop.GetValue(own.Control));
                    ApplyProperties(own.Control, new Dictionary<string, string> { { fix.Item2, fix.Item3 } });
                    string after = Convert.ToString(prop.GetValue(own.Control));
                    if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) continue;
                    applied.Add(fix.Item1 + "." + fix.Item2 + " = " + (fix.Item3 == "" ? "(empty)" : fix.Item3) + " (pattern " + fix.Item4 + ")");
                    changed = true;
                }
                if (!changed) break;
            }
            return applied;
        }

        /// <summary>Control name ("Design" for the design) to the pattern node it matched, with the pattern's name.</summary>
        private Dictionary<string, KeyValuePair<PatternNode, string>> MapPatternNodes(EffectiveForm form)
        {
            var map = new Dictionary<string, KeyValuePair<PatternNode, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var patterned in PatternedControls(form))
            {
                try
                {
                    var pattern = Patterns.GetPatternByName(patterned.Pattern, patterned.PatternVersion, false);
                    if (pattern == null) continue;
                    var result = new PatternAnalyzer().TestPattern(patterned, pattern, null);
                    MapResult(result, pattern.FriendlyName ?? pattern.Name, map, new HashSet<object>(), 0);
                }
                catch { }
            }
            return map;
        }

        private static void MapResult(PatternResult result, string pattern, Dictionary<string, KeyValuePair<PatternNode, string>> map,
            HashSet<object> visited, int depth)
        {
            if (result == null || depth > 30 || !visited.Add(result)) return;
            string name = result.Control is AxFormControl c ? c.Name : result.Control is AxFormDesign ? DesignRootName : null;
            // A control with its own pattern is described by that pattern (its root), not by the parent's slot.
            if (name != null && result.Node != null && (depth == 0 || !map.ContainsKey(name)))
                map[name] = new KeyValuePair<PatternNode, string>(result.Node, pattern);
            if (result.ChildResults == null) return;
            foreach (var list in result.ChildResults.Values)
                foreach (var child in list)
                    MapResult(child, pattern, map, visited, depth + 1);
        }

        /// <summary>"Pattern X expects children: ..." lines for a container and its direct child containers.</summary>
        private List<string> DescribeExpectedChildren(EffectiveForm form, FormControlNode start)
        {
            var lines = new List<string>();
            var map = MapPatternNodes(form);
            foreach (var node in new[] { start }.Concat(start.Children.Where(c => c.IsContainer)).Take(12))
            {
                if (!map.TryGetValue(node.Name, out var hit) || hit.Key.SubNodes == null || hit.Key.SubNodes.Count == 0) continue;
                lines.Add(node.Name + " (pattern " + hit.Value + "): " + string.Join(", ", hit.Key.SubNodes.Select(n => DescribePatternNode(n, 0))));
            }
            return lines;
        }

        private static string DescribePatternNode(PatternNode n, int depth)
        {
            if (n.IsOneOf)
                return "one of (" + string.Join(" | ", n.SubNodes.Select(s => DescribePatternNode(s, depth))) + ")";
            var sb = new System.Text.StringBuilder(n.Type + (n.AllowMany ? "*" : ""));
            var style = n.RestrictedProperties?.FirstOrDefault(r => r.Op == PropertyViolation.Operator.Equals
                && string.Equals(r.Name, "Style", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.ExpectedValue));
            if (style != null) sb.Append(" Style=" + style.ExpectedValue);
            var subs = n.SubPatterns?.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (subs != null && subs.Count > 0)
                sb.Append(" with Pattern " + string.Join("/", subs.Take(5)) + (subs.Count > 5 ? "/..." : ""));
            if (n.SubNodes != null && n.SubNodes.Count > 0)
                sb.Append(depth < 2 ? " > [" + string.Join(", ", n.SubNodes.Select(s => DescribePatternNode(s, depth + 1))) + "]" : " > [...]");
            return sb.ToString();
        }

        private static IEnumerable<IPatternable> PatternedControls(EffectiveForm form)
        {
            if (form.Form.Design is IPatternable design && !string.IsNullOrEmpty(design.Pattern)) yield return design;
            foreach (var node in form.ByName.Values)
                if (node.Control is IPatternable p && !string.IsNullOrEmpty(p.Pattern) && !string.Equals(p.Pattern, "Custom", StringComparison.OrdinalIgnoreCase))
                    yield return p;
        }

        private static void CollectEqualsViolations(PatternResult result, Pattern pattern, HashSet<string> wanted,
            List<Tuple<string, string, string, string>> fixes, HashSet<object> visited, int depth)
        {
            if (result == null || depth > 30 || !visited.Add(result)) return;
            string name = (result.Control as AxFormControl)?.Name;
            if (name != null && wanted.Contains(name) && result.Violations != null)
                foreach (var v in result.Violations.Where(v => v.Op == PropertyViolation.Operator.Equals))
                    fixes.Add(Tuple.Create(name, v.PropertyName, v.PatternValue ?? "", pattern.FriendlyName ?? pattern.Name));
            if (result.ChildResults == null) return;
            foreach (var list in result.ChildResults.Values)
                foreach (var child in list)
                    CollectEqualsViolations(child, pattern, wanted, fixes, visited, depth + 1);
        }

        /// <summary>The diagnostics the build would report for this form extension (compiler validators on saved metadata).
        /// Pattern errors are attributed to any extension of the form, so the pattern check runs for all of them.</summary>
        private List<DiagnosticItem> RunFormExtensionValidators(AxFormExtension ext)
        {
            var sink = new DiagnosticsHandler();
            string baseForm = ExtensionBaseName(ext.Name);
            var targets = new List<KeyValuePair<Validator<AxFormExtension>, AxFormExtension>>
            {
                new KeyValuePair<Validator<AxFormExtension>, AxFormExtension>(new AxFormExtensionValidator(), ext)
            };
            foreach (string name in GetExtensionNames(MetadataProvider.FormExtensions.GetPrimaryKeys(), baseForm))
            {
                var other = string.Equals(name, ext.Name, StringComparison.OrdinalIgnoreCase) ? ext : MetadataProvider.FormExtensions.Read(name);
                if (other != null)
                    targets.Add(new KeyValuePair<Validator<AxFormExtension>, AxFormExtension>(new AxFormExtensionPatternValidator(), other));
            }
            foreach (var target in targets)
            {
                var validator = target.Key;
                var uri = new Uri("dynamics://Metadata/AxFormExtension/" + Uri.EscapeDataString(target.Value.Name));
                try
                {
                    validator.Initialize(MetadataProvider);
                    int before = sink.Diagnostics().Count();
                    validator.Validate(ObjectCloner.MakeCopy(target.Value), uri, MetadataProvider, sink, null, ModelCustomizationLevel.Allow);
                    BridgeLogStatic("form extension validator " + validator.GetType().Name + " on " + target.Value.Name + ": " + (sink.Diagnostics().Count() - before) + " diagnostic(s)");
                }
                catch (Exception ex)
                {
                    BridgeLogStatic("form extension validator " + validator.GetType().Name + " failed: " + (ex.InnerException ?? ex).Message);
                }
            }
            return sink.Diagnostics().Where(d => !d.Ignored).ToList();
        }

        private static string FormatDiagnostic(DiagnosticItem d)
        {
            string message = d.Message ?? "";
            try
            {
                var args = d.Args?.Cast<object>().ToArray();
                if (args != null && args.Length > 0) message = string.Format(message, args);
            }
            catch { }
            return d.Severity + (string.IsNullOrEmpty(d.Moniker) ? "" : " [" + d.Moniker + "]") + " " + message;
        }

        private static bool IsErrorSeverity(DiagnosticItem d) =>
            d.Severity.ToString().IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
            || d.Severity.ToString().IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void BridgeLogStatic(string message)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XppVsCodeBridge.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); }
            catch { }
        }
    }
}
