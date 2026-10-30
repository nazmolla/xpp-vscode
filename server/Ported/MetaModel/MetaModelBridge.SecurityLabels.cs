using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace XppVsCode.Server.MetaModel
{
    // #46: security roles, duties and privileges are named in requirements by their label ("Sales administrator"),
    // not their AOT name, so name searches for these types also match the label text.
    public sealed partial class MetaModelBridge
    {
        private static readonly HashSet<string> LabelSearchTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AxSecurityRole", "AxSecurityDuty", "AxSecurityPrivilege" };

        private readonly Dictionary<string, Dictionary<string, string>> _labelIndex =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, LabelEntry>> _labelTexts =
            new Dictionary<string, Dictionary<string, LabelEntry>>(StringComparer.OrdinalIgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex LabelElement =
            new System.Text.RegularExpressions.Regex(@"<Label>([^<]*)</Label>", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Object name to label text (en-US) for a security type, built once per server. Reads only the Label element of
        /// each object's file in the type folders (about 4 s for 20,000 privileges; the metadata API needs minutes).
        /// </summary>
        private Dictionary<string, string> SecurityLabelIndex(string objectType)
        {
            if (_labelIndex.TryGetValue(objectType, out var cached)) return cached;
            var sw = Stopwatch.StartNew();
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            foreach (string root in config.MetadataRoots().Where(System.IO.Directory.Exists))
                foreach (string package in System.IO.Directory.GetDirectories(root))
                    foreach (string model in System.IO.Directory.GetDirectories(package))
                    {
                        string folder = System.IO.Path.Combine(model, objectType);
                        if (!System.IO.Directory.Exists(folder)) continue;
                        foreach (string file in System.IO.Directory.GetFiles(folder, "*.xml"))
                        {
                            string name = System.IO.Path.GetFileNameWithoutExtension(file);
                            if (index.ContainsKey(name)) continue;   // custom roots come first
                            try
                            {
                                var m = LabelElement.Match(System.IO.File.ReadAllText(file));
                                string text = m.Success ? ResolveLabelText(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)) : null;
                                if (!string.IsNullOrEmpty(text)) index[name] = text;
                            }
                            catch { }
                        }
                    }
            BridgeLogStatic("security label index " + objectType + ": " + index.Count + " labels in " + sw.ElapsedMilliseconds + " ms");
            _labelIndex[objectType] = index;
            return index;
        }

        /// <summary>Text of "@File:Id" or legacy "@SYS1234" in en-US; plain text is returned as is.</summary>
        private string ResolveLabelText(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            if (!label.StartsWith("@")) return label;
            int colon = label.IndexOf(':');
            string file, id;
            if (colon > 1) { file = label.Substring(1, colon - 1); id = label.Substring(colon + 1); }
            else
            {
                int digits = 1;
                while (digits < label.Length && !char.IsDigit(label[digits])) digits++;
                file = label.Substring(1, digits - 1);
                id = label;
            }
            if (!_labelTexts.TryGetValue(file, out var texts))
            {
                string path = _labels.FindLabelFile(file, "en-US");
                texts = path == null ? new Dictionary<string, LabelEntry>() : _labels.ReadAll(path);
                _labelTexts[file] = texts;
            }
            return texts.TryGetValue(id, out var entry) ? entry.Text : null;
        }

        private static readonly HashSet<string> StopWords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "an", "the", "of", "and", "or", "for", "to", "in", "on", "with" };

        /// <summary>
        /// Security objects by label text: labels containing every word first (exact, then prefix, then shortest);
        /// if none, the closest labels by number of matching words. allWords tells which case applied.
        /// </summary>
        private List<KeyValuePair<string, string>> FindSecurityByLabel(string objectType, string text, int max, out bool allWords)
        {
            // Light stemming: "accountant" also matches "accounting", "administrator" "administration".
            var stems = text.Split(new[] { ' ', '\t', '-', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !StopWords.Contains(w))
                .Select(w => w.Length > 6 ? w.Substring(0, Math.Max(5, w.Length - 3)) : w).ToList();
            allWords = true;
            if (stems.Count == 0) return new List<KeyValuePair<string, string>>();
            var scored = SecurityLabelIndex(objectType)
                .Select(kv => new { kv, hits = stems.Count(w => kv.Value.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) })
                .Where(x => x.hits > 0).ToList();
            var all = scored.Where(x => x.hits == stems.Count).ToList();
            allWords = all.Count > 0;
            return (allWords ? all : scored)
                .OrderByDescending(x => x.hits)
                .ThenBy(x => string.Equals(x.kv.Value, text, StringComparison.OrdinalIgnoreCase) ? 0 : x.kv.Value.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(x => x.kv.Value.Length)
                .Take(max)
                .Select(x => x.kv)
                .ToList();
        }
    }
}
