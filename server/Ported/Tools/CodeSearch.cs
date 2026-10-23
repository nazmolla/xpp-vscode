using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// X++ code search over every package (standard and custom): the method sources and declarations of classes,
    /// tables, forms, views, data entities, queries and maps. Files are scanned in parallel straight from the metadata
    /// folders (a pre-check on the raw text skips files without a match; the OS file cache makes later searches fast),
    /// and only files with a match are parsed to locate the method and the line.
    /// </summary>
    internal sealed class CodeSearch
    {
        internal static readonly string[] CodeTypes = { "AxClass", "AxTable", "AxForm", "AxView", "AxDataEntityView", "AxQuery", "AxMap" };

        internal sealed class Hit
        {
            public string ObjectType, ObjectName, Model, Member;
            public string[] MemberPath;
            public int Line, Column;
            public string Text;
        }

        internal sealed class Result
        {
            public List<Hit> Hits = new List<Hit>();
            public bool Truncated;
            public int Scanned;
            public long Ms;
            public string Error;
        }

        private static List<string> Roots()
        {
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            return new[] { config.CustomMetadataFolder }.Concat(config.ReferenceMetadataFolders)
                .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Type folders (package\model\AxType) to search, optionally of one model.</summary>
        internal static IEnumerable<KeyValuePair<string, string>> Folders(IEnumerable<string> types, string model)
        {
            var wanted = new HashSet<string>(types ?? CodeTypes, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in Roots())
                foreach (string package in SafeDirectories(root))
                    foreach (string modelDir in SafeDirectories(package))
                    {
                        string modelName = Path.GetFileName(modelDir);
                        if (!string.IsNullOrEmpty(model) && !modelName.Equals(model, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(modelName + "|" + package)) continue;
                        foreach (string type in wanted)
                        {
                            string dir = Path.Combine(modelDir, type);
                            if (Directory.Exists(dir)) yield return new KeyValuePair<string, string>(dir, modelName);
                        }
                    }
        }

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try { return Directory.GetDirectories(dir); } catch { return new string[0]; }
        }

        public Result Search(string pattern, bool regex, bool caseSensitive, IEnumerable<string> objectTypes, string model, int maxResults, int timeoutMs = 90000)
        {
            var result = new Result();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (string.IsNullOrEmpty(pattern) || pattern.Trim().Length < 3)
            {
                result.Error = "pattern must have at least 3 characters.";
                return result;
            }
            maxResults = Math.Max(1, Math.Min(maxResults <= 0 ? 100 : maxResults, 500));
            Regex rx;
            try
            {
                var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                rx = new Regex(regex ? pattern : Regex.Escape(pattern), options, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                result.Error = "Invalid regular expression: " + ex.Message;
                return result;
            }

            var hits = new ConcurrentBag<Hit>();
            int scanned = 0, count = 0;
            bool truncated = false, timedOut = false;
            var files = Folders(objectTypes, model)
                .SelectMany(f => SafeFiles(f.Key).Select(file => new KeyValuePair<string, string>(file, f.Value)));
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, (entry, state) =>
            {
                if (watch.ElapsedMilliseconds > timeoutMs) { timedOut = true; state.Stop(); return; }
                System.Threading.Interlocked.Increment(ref scanned);
                string text;
                try { text = File.ReadAllText(entry.Key); } catch { return; }
                bool any;
                try { any = rx.IsMatch(text); } catch (RegexMatchTimeoutException) { return; }
                if (!any) return;
                foreach (var hit in HitsInFile(entry.Key, entry.Value, text, rx))
                {
                    if (System.Threading.Interlocked.Increment(ref count) > maxResults) { truncated = true; state.Stop(); return; }
                    hits.Add(hit);
                }
            });
            result.Hits = hits.OrderBy(h => h.ObjectType).ThenBy(h => h.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => string.Join("/", h.MemberPath)).ThenBy(h => h.Line).ToList();
            result.Truncated = truncated || timedOut;
            result.Scanned = scanned;
            result.Ms = watch.ElapsedMilliseconds;
            if (timedOut) result.Error = "Stopped after " + timeoutMs / 1000 + " s; narrow the search (objectTypes, model, a more specific pattern).";
            return result;
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*.xml"); } catch { return new string[0]; }
        }

        /// <summary>The matching lines of a metadata file, per method (path in the element designer's terms).</summary>
        internal static IEnumerable<Hit> HitsInFile(string file, string model, string text, Regex rx)
        {
            string objectType = Path.GetFileName(Path.GetDirectoryName(file));
            string objectName = Path.GetFileNameWithoutExtension(file);
            XDocument doc;
            try { doc = XDocument.Parse(text); } catch { yield break; }
            foreach (var source in Sources(doc))
            {
                var lines = source.Value.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    Match m;
                    try { m = rx.Match(lines[i]); } catch (RegexMatchTimeoutException) { continue; }
                    if (!m.Success) continue;
                    yield return new Hit
                    {
                        ObjectType = objectType, ObjectName = objectName, Model = model,
                        MemberPath = source.Key, Member = source.Key[source.Key.Length - 1],
                        Line = i + 1, Column = m.Index, Text = lines[i].Trim().Length > 300 ? lines[i].Trim().Substring(0, 300) : lines[i].Trim(),
                    };
                }
            }
        }

        private static string Local(XElement e) => e.Name.LocalName;

        // Type folders by type, so an object's file is found without walking every package each time.
        private static Dictionary<string, List<KeyValuePair<string, string>>> _folders;
        private static readonly object FoldersLock = new object();

        /// <summary>The declaration and method sources of one object (member path, source), or null if its file is not found.</summary>
        internal static List<KeyValuePair<string[], string>> SourcesOf(string objectType, string objectName)
        {
            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName)) return null;
            List<KeyValuePair<string, string>> folders;
            lock (FoldersLock)
            {
                if (_folders == null) _folders = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
                if (!_folders.TryGetValue(objectType, out folders))
                    _folders[objectType] = folders = Folders(new[] { objectType }, null).ToList();
            }
            foreach (var folder in folders)
            {
                string file = Path.Combine(folder.Key, objectName + ".xml");
                if (!File.Exists(file)) continue;
                try { return Sources(XDocument.Load(file)).ToList(); } catch { return null; }
            }
            return null;
        }

        private static string Child(XElement e, string name) => e.Elements().FirstOrDefault(c => Local(c) == name)?.Value;

        /// <summary>(member path, source) of the declaration and every method; nested form methods get short paths
        /// (DataSources/ds[/Fields/field]/Methods/m, Controls/control/Methods/m) that the designer resolves.</summary>
        internal static IEnumerable<KeyValuePair<string[], string>> Sources(XDocument doc)
        {
            var sourceCode = doc.Root?.Elements().FirstOrDefault(e => Local(e) == "SourceCode");
            if (sourceCode == null) yield break;
            string declaration = Child(sourceCode, "Declaration");
            if (!string.IsNullOrEmpty(declaration)) yield return new KeyValuePair<string[], string>(new[] { "Methods", "classDeclaration" }, declaration);
            foreach (var method in sourceCode.Descendants().Where(e => Local(e) == "Method"))
            {
                string name = Child(method, "Name");
                string src = Child(method, "Source");
                if (string.IsNullOrEmpty(name) || src == null) continue;
                var path = new List<string>();
                foreach (var a in method.Ancestors().TakeWhile(a => a != sourceCode).Reverse())
                {
                    switch (Local(a))
                    {
                        case "DataSource": path.Add("DataSources"); path.Add(Child(a, "Name")); break;
                        case "Field": path.Add("Fields"); path.Add(Child(a, "DataField") ?? Child(a, "Name")); break;
                        case "Control": path.Add("Controls"); path.Add(Child(a, "Name")); break;
                    }
                }
                path.Add("Methods");
                path.Add(name);
                yield return new KeyValuePair<string[], string>(path.ToArray(), src);
            }
        }
    }
}
