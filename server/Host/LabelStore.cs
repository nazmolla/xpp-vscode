using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XppVsCode.Server.MetaModel;

namespace XppVsCode.Server.Host
{
    /// <summary>Reads and writes label text files (LabelResources\&lt;lang&gt;\&lt;Id&gt;.&lt;lang&gt;.label.txt).</summary>
    internal sealed class LabelStore
    {
        private const string DefaultLanguage = "en-US";
        private readonly DynamicsMetadataConfig _config;

        public LabelStore(DynamicsMetadataConfig config)
        {
            _config = config;
        }

        public string FindLabelFile(string labelFileId, string language)
        {
            string fileName = labelFileId + "." + (language ?? DefaultLanguage) + ".label.txt";
            string relative = Path.Combine("AxLabelFile", "LabelResources", language ?? DefaultLanguage, fileName);

            foreach (string root in Roots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (string package in Directory.EnumerateDirectories(root))
                {
                    foreach (string model in Directory.EnumerateDirectories(package))
                    {
                        string path = Path.Combine(model, relative);
                        if (File.Exists(path)) return path;
                    }
                }
            }
            return null;
        }

        public bool IsWritable(string path) => _config.IsCustomPath(path);

        /// <summary>Every language of a label file ID: language to the label text file (the first root that has it).</summary>
        public SortedDictionary<string, string> FindLanguages(string labelFileId)
        {
            var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string suffix = ".label.txt";
            foreach (string root in Roots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (string package in Directory.EnumerateDirectories(root))
                    foreach (string model in Directory.EnumerateDirectories(package))
                    {
                        string resources = Path.Combine(model, "AxLabelFile", "LabelResources");
                        if (!Directory.Exists(resources)) continue;
                        foreach (string langDir in Directory.EnumerateDirectories(resources))
                        {
                            string lang = Path.GetFileName(langDir);
                            string file = Path.Combine(langDir, labelFileId + "." + lang + suffix);
                            if (!result.ContainsKey(lang) && File.Exists(file)) result[lang] = file;
                        }
                    }
                if (result.Count > 0) break;   // a label file ID lives in one model
            }
            return result;
        }

        /// <summary>Sets text and comment of a label, inserting it in ID order when missing (one read, one write).</summary>
        public bool Upsert(string path, string labelId, string text, string comment)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            int at = lines.FindIndex(l => string.Equals(IdOf(l), labelId, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                InsertInto(lines, labelId, text, comment);
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
                return true;
            }
            lines[at] = IdOf(lines[at]) + "=" + text.Replace("\r", " ").Replace("\n", " ");
            bool hasComment = at + 1 < lines.Count && lines[at + 1].StartsWith(" ;");
            if (comment != null)
            {
                if (string.IsNullOrWhiteSpace(comment)) { if (hasComment) lines.RemoveAt(at + 1); }
                else if (hasComment) lines[at + 1] = " ;" + comment.Replace("\r", " ").Replace("\n", " ");
                else lines.Insert(at + 1, " ;" + comment.Replace("\r", " ").Replace("\n", " "));
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            return false;
        }

        /// <summary>Removes a label (and its comment line). Returns false when it was not there.</summary>
        public bool Remove(string path, string labelId)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            int at = lines.FindIndex(l => string.Equals(IdOf(l), labelId, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return false;
            int count = at + 1 < lines.Count && lines[at + 1].StartsWith(" ;") ? 2 : 1;
            lines.RemoveRange(at, count);
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            return true;
        }

        public List<LabelEntry> Read(string path, string labelId, string searchText, int maxResults)
        {
            var results = new List<LabelEntry>();
            foreach (var entry in Parse(File.ReadAllLines(path, Encoding.UTF8)))
            {
                if (!string.IsNullOrEmpty(labelId))
                {
                    if (string.Equals(entry.Id, labelId, StringComparison.OrdinalIgnoreCase))
                        return new List<LabelEntry> { entry };
                    continue;
                }
                if (!string.IsNullOrEmpty(searchText)
                    && entry.Id.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                results.Add(entry);
                if (results.Count >= maxResults) break;
            }
            return results;
        }

        public bool Exists(string path, string labelId) =>
            Parse(File.ReadAllLines(path, Encoding.UTF8)).Any(e => string.Equals(e.Id, labelId, StringComparison.OrdinalIgnoreCase));

        /// <summary>Existing labels of a file by ID (one read).</summary>
        public Dictionary<string, LabelEntry> ReadAll(string path)
        {
            var result = new Dictionary<string, LabelEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Parse(File.ReadAllLines(path, Encoding.UTF8))) result[e.Id] = e;
            return result;
        }

        /// <summary>Inserts several labels in ID order with a single read and a single write of the file.</summary>
        public void InsertMany(string path, IEnumerable<LabelEntry> labels)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            foreach (var label in labels)
                InsertInto(lines, label.Id, label.Text, label.Comment);
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }

        /// <summary>Inserts the label in ID order, as the Visual Studio label editor does.</summary>
        public void Insert(string path, string labelId, string text, string comment) =>
            InsertMany(path, new[] { new LabelEntry { Id = labelId, Text = text, Comment = comment } });

        private static void InsertInto(List<string> lines, string labelId, string text, string comment)
        {
            var block = new List<string> { labelId + "=" + text.Replace("\r", " ").Replace("\n", " ") };
            if (!string.IsNullOrWhiteSpace(comment))
                block.Add(" ;" + comment.Replace("\r", " ").Replace("\n", " "));

            int insertAt = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string id = IdOf(lines[i]);
                if (id != null && string.Compare(id, labelId, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    insertAt = i;
                    break;
                }
            }
            lines.InsertRange(insertAt, block);
        }

        private IEnumerable<string> Roots() => _config.MetadataRoots();

        private static IEnumerable<LabelEntry> Parse(IEnumerable<string> lines)
        {
            LabelEntry current = null;
            foreach (string line in lines)
            {
                if (line.StartsWith(" ;"))
                {
                    if (current != null) current.Comment = line.Substring(2);
                    continue;
                }
                string id = IdOf(line);
                if (id == null) continue;
                if (current != null) yield return current;
                current = new LabelEntry { Id = id, Text = line.Substring(id.Length + 1) };
            }
            if (current != null) yield return current;
        }

        private static string IdOf(string line)
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) return null;
            int eq = line.IndexOf('=');
            return eq > 0 ? line.Substring(0, eq) : null;
        }
    }
}
