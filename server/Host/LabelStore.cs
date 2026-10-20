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

        public bool IsWritable(string path) =>
            _config.HasCustom && path.StartsWith(Path.GetFullPath(_config.CustomMetadataFolder), StringComparison.OrdinalIgnoreCase);

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

        /// <summary>Inserts the label in ID order, as the Visual Studio label editor does.</summary>
        public void Insert(string path, string labelId, string text, string comment)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
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
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }

        private IEnumerable<string> Roots()
        {
            if (_config.HasCustom) yield return _config.CustomMetadataFolder;
            foreach (string r in _config.ReferenceMetadataFolders) yield return r;
        }

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
