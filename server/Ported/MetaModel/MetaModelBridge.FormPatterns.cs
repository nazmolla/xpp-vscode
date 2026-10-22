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
        private List<string> ApplyPatternRequiredProperties(string baseForm, AxFormExtension ext, ICollection<string> newControls,
            ICollection<string> updatedControls = null)
        {
            var applied = new List<string>();
            var added = new HashSet<string>(newControls ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var wanted = new HashSet<string>(added.Concat(updatedControls ?? new List<string>()), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return applied;

            // A pattern name with a missing or unknown version gets the pattern's latest version.
            foreach (string name in wanted)
            {
                var own = FindOwnControl(ext, name);
                if (!(own?.Control is IPatternable p) || string.IsNullOrEmpty(p.Pattern)
                    || string.Equals(p.Pattern, "Custom", StringComparison.OrdinalIgnoreCase)) continue;
                Pattern exact = null;
                try { exact = string.IsNullOrEmpty(p.PatternVersion) ? null : Patterns.GetPatternByName(p.Pattern, p.PatternVersion, false); }
                catch { }
                if (exact != null) continue;
                var latest = LatestPattern(p.Pattern);
                if (latest == null) continue;
                string before = p.PatternVersion;
                p.PatternVersion = latest.Version;
                applied.Add(name + ".PatternVersion = " + latest.Version + " (latest version of " + p.Pattern
                    + (string.IsNullOrEmpty(before) ? "" : "; " + before + " does not exist") + ")");
            }
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
                    CollectEqualsViolations(result, pattern, wanted, added, fixes, visited, 0);
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

        private static void CollectEqualsViolations(PatternResult result, Pattern pattern, HashSet<string> wanted, HashSet<string> added,
            List<Tuple<string, string, string, string>> fixes, HashSet<object> visited, int depth)
        {
            if (result == null || depth > 30 || !visited.Add(result)) return;
            string name = (result.Control as AxFormControl)?.Name;
            if (name != null && wanted.Contains(name) && result.Violations != null)
                foreach (var v in result.Violations.Where(v => v.Op == PropertyViolation.Operator.Equals))
                    fixes.Add(Tuple.Create(name, v.PropertyName, v.PatternValue ?? "", pattern.FriendlyName ?? pattern.Name));
            // A container placed where the pattern expects a sub-pattern (e.g. a TabPage in a Details or Table of contents
            // tab) gets the first one allowed there, as Visual Studio proposes; its required values follow in the next pass.
            if (name != null && added.Contains(name) && result.Control is IPatternable patternable && string.IsNullOrEmpty(patternable.Pattern)
                && depth > 0 && result.Node?.SubPatterns != null)
            {
                string sub = result.Node.SubPatterns.FirstOrDefault(p => !string.IsNullOrEmpty(p) && !string.Equals(p, "Custom", StringComparison.OrdinalIgnoreCase));
                var latest = sub == null ? null : LatestPattern(sub);
                if (latest != null)
                {
                    fixes.Add(Tuple.Create(name, "Pattern", latest.Name, pattern.FriendlyName ?? pattern.Name));
                    fixes.Add(Tuple.Create(name, "PatternVersion", latest.Version, pattern.FriendlyName ?? pattern.Name));
                }
            }
            if (result.ChildResults == null) return;
            foreach (var list in result.ChildResults.Values)
                foreach (var child in list)
                    CollectEqualsViolations(child, pattern, wanted, added, fixes, visited, depth + 1);
        }

        private static Pattern LatestPattern(string name)
        {
            try
            {
                return Patterns.GetPatternsByName(name, false)?.Where(p => p != null && p.Active)
                    .OrderByDescending(p => Version.TryParse(p.Version, out var v) ? v : new Version(0, 0)).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>The diagnostics the build would report for this form extension (compiler validators on saved metadata).
        /// The pattern validator reports a broken pattern under the extension that changed it (and again under every
        /// extension of the form, each run rebuilding the merged form: about 1.5 s per extension, a minute on SalesTable),
        /// so it runs on this extension and on the extensions whose controls this request modifies.</summary>
        private List<DiagnosticItem> RunFormExtensionValidators(AxFormExtension ext, IEnumerable<string> alsoCheck = null)
        {
            var sink = new DiagnosticsHandler();
            string baseForm = ExtensionBaseName(ext.Name);
            var targets = new List<KeyValuePair<Validator<AxFormExtension>, AxFormExtension>>
            {
                new KeyValuePair<Validator<AxFormExtension>, AxFormExtension>(ScopedFormExtensionValidator(ext, baseForm), ext),
                new KeyValuePair<Validator<AxFormExtension>, AxFormExtension>(new AxFormExtensionPatternValidator(), ext),
            };
            foreach (string name in (alsoCheck ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !string.Equals(n, ext.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var other = MetadataProvider.FormExtensions.Read(name);
                if (other != null)
                    targets.Add(new KeyValuePair<Validator<AxFormExtension>, AxFormExtension>(new AxFormExtensionPatternValidator(), other));
            }
            foreach (var target in targets)
            {
                var validator = target.Key;
                var uri = new Uri("dynamics://Metadata/AxFormExtension/" + Uri.EscapeDataString(target.Value.Name));
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    if (!(validator is AxFormExtensionValidator)) validator.Initialize(MetadataProvider);   // scoped validators come initialized
                    long init = watch.ElapsedMilliseconds;
                    int before = sink.Diagnostics().Count();
                    validator.Validate(ObjectCloner.MakeCopy(target.Value), uri, MetadataProvider, sink, null, ModelCustomizationLevel.Allow);
                    BridgeLogStatic("form extension validator " + validator.GetType().Name + " on " + target.Value.Name + ": " + (sink.Diagnostics().Count() - before)
                        + " diagnostic(s), init " + init + " ms, validate " + (watch.ElapsedMilliseconds - init) + " ms");
                }
                catch (Exception ex)
                {
                    BridgeLogStatic("form extension validator " + validator.GetType().Name + " failed: " + (ex.InnerException ?? ex).Message);
                }
            }
            return sink.Diagnostics().Where(d => !d.Ignored).ToList();
        }

        // AxFormExtensionValidator builds the list of tables extended by other tables (all tables: about a minute) once per
        // instance, so one instance is kept for the server's lifetime and that list is built in the background at start.
        private static readonly object FormExtensionValidatorLock = new object();
        private static AxFormExtensionValidator _formExtensionValidator;

        private AxFormExtensionValidator SharedFormExtensionValidator()
        {
            lock (FormExtensionValidatorLock)
            {
                if (_formExtensionValidator == null)
                {
                    var validator = new AxFormExtensionValidator();
                    validator.Initialize(MetadataProvider);
                    _formExtensionValidator = validator;
                }
                return _formExtensionValidator;
            }
        }

        /// <summary>
        /// AxFormExtensionValidator validates and merges every other extension of the form before checking this one (about
        /// 1.5 s each: a minute on SalesTable). Its extension loader is replaced by one that lists only this extension and the
        /// extensions it builds on (their controls are its parents, previous siblings or modified controls, their data sources
        /// its references), in the same precedence order. Errors in unrelated extensions are not this change's anyway.
        /// </summary>
        private AxFormExtensionValidator ScopedFormExtensionValidator(AxFormExtension ext, string baseForm)
        {
            var validator = new AxFormExtensionValidator();
            validator.Initialize(MetadataProvider);
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var shared = SharedFormExtensionValidator();
                var listField = typeof(AxFormExtensionValidator).GetField("tableExtendedByList", flags);
                if (listField?.GetValue(shared) != null) listField.SetValue(validator, listField.GetValue(shared));

                var keep = FormExtensionDependencies(ext, baseForm);
                var loader = new FormExtensionLoader(MetadataProvider);
                var lazyField = typeof(ExtensionLoader<AxFormExtension>).GetField("runTimeExtensionList", flags);
                var all = (IDictionary<string, ExtensionList>)((Lazy<IDictionary<string, ExtensionList>>)lazyField.GetValue(loader)).Value;
                if (all.TryGetValue(baseForm, out var full))
                {
                    var precedence = (System.Collections.IDictionary)typeof(ExtensionList).GetField("Precedence", flags).GetValue(full);
                    var scoped = new ExtensionList();
                    foreach (string name in full.GetSortedNames().Where(n => keep.Contains(n)))
                        scoped.AddNameToList(name, (PrecedenceValue)precedence[name]);
                    // A copy: the original dictionary can be the provider's shared extension list cache.
                    var copy = new System.Collections.Concurrent.ConcurrentDictionary<string, ExtensionList>(all, StringComparer.OrdinalIgnoreCase);
                    copy[baseForm] = scoped;
                    lazyField.SetValue(loader, new Lazy<IDictionary<string, ExtensionList>>(() => copy));
                    typeof(AxFormExtensionValidator).GetField("formExtensionLoader", flags).SetValue(validator, loader);
                    BridgeLogStatic("form extension validator scoped to " + scoped.GetSortedNames().Count + " of " + full.GetSortedNames().Count + " extensions of " + baseForm);
                }
            }
            catch (Exception ex)
            {
                BridgeLogStatic("form extension validator scoping failed, validating against all extensions: " + (ex.InnerException ?? ex).Message);
            }
            return validator;
        }

        /// <summary>This extension and, transitively, the extensions whose controls or data sources it builds on.</summary>
        private HashSet<string> FormExtensionDependencies(AxFormExtension ext, string baseForm)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ext.Name };
            var effective = BuildEffectiveForm(baseForm, ObjectCloner.MakeCopy(ext));
            if (effective == null) return keep;
            var queue = new Queue<AxFormExtension>(new[] { ext });
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var names = new List<string>();
                foreach (var c in current.Controls ?? Enumerable.Empty<AxFormExtensionControl>())
                {
                    names.Add(c.Parent);
                    names.Add(c.PreviousSibling);
                }
                foreach (var m in current.ControlModifications ?? Enumerable.Empty<AxExtensionModification>()) names.Add(m.Name);
                var owners = names.Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => effective.ByName.TryGetValue(n, out var node) ? node.AddedBy : null).ToList();
                foreach (var r in current.DataSourceReferences ?? Enumerable.Empty<AxFormExtensionDataSourceReference>())
                    if (!string.IsNullOrEmpty(r.Parent) && effective.DataSourceAddedBy != null && effective.DataSourceAddedBy.TryGetValue(r.Parent, out string dsOwner))
                        owners.Add(dsOwner);
                foreach (var ds in current.DataSources ?? Enumerable.Empty<AxFormDataSourceRoot>())
                {
                    string joinSource = GetStringProperty(ds, "JoinSource");
                    if (!string.IsNullOrEmpty(joinSource) && effective.DataSourceAddedBy != null && effective.DataSourceAddedBy.TryGetValue(joinSource, out string joinOwner))
                        owners.Add(joinOwner);
                }
                foreach (string owner in owners.Where(o => !string.IsNullOrEmpty(o)))
                    if (keep.Add(owner))
                    {
                        var other = MetadataProvider.FormExtensions.Read(owner);
                        if (other != null) queue.Enqueue(other);
                    }
            }
            return keep;
        }

        /// <summary>Builds the validator's table inheritance list in the background, so the first form extension check is fast.</summary>
        private static int _formExtensionValidatorWarming;

        internal void WarmFormExtensionValidator()
        {
            if (System.Threading.Interlocked.Exchange(ref _formExtensionValidatorWarming, 1) == 1) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var provider = MetadataProvider;
                    if (provider == null) return;
                    var validator = SharedFormExtensionValidator();
                    typeof(AxFormExtensionValidator).GetMethod("GetTableExtendedByList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                        ?.Invoke(validator, new object[] { provider });
                    BridgeLogStatic("form extension validator warmed in " + watch.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex)
                {
                    BridgeLogStatic("form extension validator warm-up failed: " + (ex.InnerException ?? ex).Message);
                }
            });
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
