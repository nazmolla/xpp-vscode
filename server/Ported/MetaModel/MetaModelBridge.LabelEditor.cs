using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace XppVsCode.Server.MetaModel
{
    // Label editor (U8): all languages of a label file side by side, with create/update/delete.
    public sealed partial class MetaModelBridge
    {
        /// <summary>Label file ID and language of an AxLabelFile name ("MyLabels_en-US"), or the ID itself.</summary>
        private void ResolveLabelFile(string name, out string labelFileId, out string language)
        {
            labelFileId = name;
            language = null;
            try
            {
                var file = MetadataProvider?.LabelFiles.Read(name);
                if (file != null)
                {
                    labelFileId = string.IsNullOrEmpty(file.LabelFileId) ? name : file.LabelFileId;
                    language = file.Language;
                    return;
                }
            }
            catch { }
            int underscore = name.LastIndexOf('_');
            if (underscore > 0 && name.IndexOf('-', underscore) > 0)
            {
                labelFileId = name.Substring(0, underscore);
                language = name.Substring(underscore + 1);
            }
        }

        /// <summary>
        /// Labels of a label file for the editor: the opened language plus the requested other languages (all of them
        /// when the file has at most 6), filtered by search (ID, text or comment), at most limit entries.
        /// </summary>
        public string LabelFileEntriesJson(string labelFile, string search, List<string> otherLanguages, int limit)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (string.IsNullOrWhiteSpace(labelFile)) return json.Serialize(Failure("labelFile is required."));
            ResolveLabelFile(labelFile.Trim(), out string id, out string language);
            var files = _labels.FindLanguages(id);
            if (files.Count == 0) return json.Serialize(Failure("No label texts found for label file '" + id + "'."));
            if (language == null || !files.ContainsKey(language)) language = files.ContainsKey("en-US") ? "en-US" : files.Keys.First();
            if (limit <= 0) limit = 500;

            var all = files.Keys.ToList();
            var shown = new List<string> { language };
            shown.AddRange(otherLanguages != null
                ? otherLanguages.Where(l => files.ContainsKey(l) && !string.Equals(l, language, StringComparison.OrdinalIgnoreCase))
                : all.Count <= 6 ? all.Where(l => !string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) : Enumerable.Empty<string>());

            var primary = _labels.ReadAll(files[language]).Values.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
            string q = (search ?? "").Trim();
            var matches = q.Length == 0 ? primary : primary.Where(e =>
                e.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || (e.Text ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || (e.Comment ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var page = matches.Take(limit).ToList();

            var others = shown.Skip(1).ToDictionary(l => l, l => _labels.ReadAll(files[l]), StringComparer.OrdinalIgnoreCase);
            var entries = page.Select(e =>
            {
                var texts = new Dictionary<string, string> { [language] = e.Text };
                foreach (var o in others)
                    if (o.Value.TryGetValue(e.Id, out var t)) texts[o.Key] = t.Text;
                return new Dictionary<string, object> { ["id"] = e.Id, ["texts"] = texts, ["comment"] = e.Comment ?? "" };
            }).ToList();

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["labelFileId"] = id,
                ["language"] = language,
                ["languages"] = shown,
                ["allLanguages"] = all,
                ["editable"] = files.Values.All(_labels.IsWritable),
                ["total"] = primary.Count,
                ["matches"] = matches.Count,
                ["entries"] = entries,
            });
        }

        /// <summary>Creates or updates one label: texts per language (empty = leave that language alone) and the comment.</summary>
        public string SaveLabelJson(string labelFile, string labelId, Dictionary<string, string> texts, string comment)
        {
            var json = new JavaScriptSerializer();
            ResolveLabelFile(labelFile ?? "", out string id, out string language);
            if (string.IsNullOrEmpty(labelId) || !LabelIdPattern.IsMatch(labelId))
                return json.Serialize(Failure("The label ID must be letters, digits or _ (e.g. MyFieldLabel)."));
            var files = _labels.FindLanguages(id);
            if (files.Count == 0) return json.Serialize(Failure("Label file '" + id + "' not found."));
            if (!files.Values.All(_labels.IsWritable)) return json.Serialize(Failure("Label file '" + id + "' is not in one of your models; it cannot be changed."));
            var given = (texts ?? new Dictionary<string, string>()).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
            if (given.Count == 0) return json.Serialize(Failure("Give the label text in at least one language."));
            var unknown = given.Select(kv => kv.Key).Where(l => !files.ContainsKey(l)).ToList();
            if (unknown.Count > 0) return json.Serialize(Failure("Label file '" + id + "' has no language " + string.Join(", ", unknown) + "."));

            var done = new List<string>();
            foreach (var kv in given)
            {
                // The comment belongs to the opened language; other languages keep theirs.
                bool primary = language == null ? kv.Key == given[0].Key : string.Equals(kv.Key, language, StringComparison.OrdinalIgnoreCase);
                bool created = _labels.Upsert(files[kv.Key], labelId, kv.Value, primary ? comment ?? "" : null);
                done.Add(kv.Key + (created ? " created" : " updated"));
            }
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = "@" + id + ":" + labelId + ": " + string.Join(", ", done) + "." });
        }

        public string DeleteLabelJson(string labelFile, string labelId)
        {
            var json = new JavaScriptSerializer();
            ResolveLabelFile(labelFile ?? "", out string id, out _);
            var files = _labels.FindLanguages(id);
            if (files.Count == 0) return json.Serialize(Failure("Label file '" + id + "' not found."));
            if (!files.Values.All(_labels.IsWritable)) return json.Serialize(Failure("Label file '" + id + "' is not in one of your models; it cannot be changed."));
            var removed = files.Where(f => _labels.Remove(f.Value, labelId)).Select(f => f.Key).ToList();
            return json.Serialize(removed.Count > 0
                ? new Dictionary<string, object> { ["success"] = true, ["message"] = "Deleted @" + id + ":" + labelId + " (" + string.Join(", ", removed) + "). References to it in code or properties now show the raw ID; check with xpp_find_references." }
                : Failure("Label '" + labelId + "' not found in " + id + "."));
        }
    }
}
