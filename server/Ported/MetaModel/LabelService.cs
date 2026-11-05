using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Microsoft.Dynamics.AX.Metadata.Service;
using XppVsCode.Server.Host;
using static XppVsCode.Server.MetaModel.MetaModelBridge;

namespace XppVsCode.Server.MetaModel
{
    // Labels: read and create (agent tools), and the label editor (U8): all languages of a label file side by side,
    // with create/update/delete.
    internal sealed class LabelService : ILabelService
    {
        private readonly IMetadataCore _core;
        private readonly LabelStore _labels;

        public LabelService(IMetadataCore core, LabelStore labels)
        {
            _core = core;
            _labels = labels;
        }

        private IMetaModelService MetaService => _core.MetaService;
        private IMetadataProvider MetadataProvider => _core.MetadataProvider;

        public LabelResult ReadLabel(string labelFileId, string language, string labelId, string searchText, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new LabelResult { Message = "MetaModel service not available." };

            try
            {
                string labelPath = _labels.FindLabelFile(labelFileId, language);
                if (labelPath == null)
                    return new LabelResult { Message = "Label file '" + labelFileId + "' (" + (language ?? "en-US") + ") not found." };

                var result = new LabelResult { Success = true };

                // If specific label requested
                if (!string.IsNullOrEmpty(labelId))
                {
                    result.Labels.AddRange(_labels.Read(labelPath, labelId, null, 1));
                    if (result.Labels.Count == 0)
                        result.Message = "Label '" + labelId + "' not found in " + labelFileId;
                    return result;
                }

                result.Labels.AddRange(_labels.Read(labelPath, null, searchText, maxResults > 0 ? maxResults : 50));

                result.Message = result.Labels.Count > 0
                    ? "Retrieved " + result.Labels.Count + " labels from " + labelFileId + "."
                    : "No labels found in " + labelFileId + ".";
                return result;
            }
            catch (Exception ex)
            {
                return new LabelResult { Message = "Label read failed: " + Cause(ex) };
            }
        }

        public MetaModelResult CreateLabel(string labelFileId, string language, string labelId, string text, string comment)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return Fail("MetaModel service not available.");

            try
            {
                string labelPath = _labels.FindLabelFile(labelFileId, language);
                if (labelPath == null)
                    return Fail("Label file '" + labelFileId + "' (" + (language ?? "en-US") + ") not found.");
                if (!_labels.IsWritable(labelPath))
                    return Fail("Label file '" + labelFileId + "' is not in your custom metadata folder. Add labels only to label files of your own model.");

                if (_labels.Exists(labelPath, labelId))
                    return Fail("Label '" + labelId + "' already exists in " + labelFileId);

                _labels.Insert(labelPath, labelId, text, comment);

                return new MetaModelResult
                {
                    Success = true,
                    Message = "Created label @" + labelFileId + ":" + labelId + " = " + text
                };
            }
            catch (Exception ex)
            {
                return Fail("Label create failed: " + Cause(ex));
            }
        }

        private static readonly System.Text.RegularExpressions.Regex LabelIdPattern =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

        /// <summary>
        /// Creates several labels (each with optional translations). Every language's label file is read once and
        /// written once. Same ID with the same text is reported as unchanged; same ID with another text fails.
        /// </summary>
        public MetaModelResult CreateLabels(string labelFileId, string language, List<LabelRequestDto> labels)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null) return Fail("MetaModel service not available.");
            if (labels == null || labels.Count == 0) return Fail("No labels given.");
            string baseLanguage = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();

            var lines = new List<string>();
            bool failed = false;
            var perLanguage = new Dictionary<string, List<KeyValuePair<LabelRequestDto, string>>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in labels)
            {
                string id = label.LabelId?.Trim();
                if (string.IsNullOrEmpty(id) || !LabelIdPattern.IsMatch(id))
                {
                    lines.Add("FAILED '" + label.LabelId + "': the label ID must be letters, digits or _ (e.g. HSRemovalStatus).");
                    failed = true;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(label.Text))
                {
                    lines.Add("FAILED " + id + ": text is required.");
                    failed = true;
                    continue;
                }
                if (!seen.Add(id))
                {
                    lines.Add("FAILED " + id + ": listed more than once in this call.");
                    failed = true;
                    continue;
                }
                label.LabelId = id;
                AddFor(perLanguage, baseLanguage, label, label.Text);
                foreach (var t in label.Translations ?? new Dictionary<string, string>())
                {
                    if (string.IsNullOrWhiteSpace(t.Value)) { lines.Add("FAILED " + id + " " + t.Key + ": empty translation."); failed = true; continue; }
                    AddFor(perLanguage, t.Key, label, t.Value);
                }
            }

            var outcome = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in perLanguage)
            {
                try
                {
                    string path = _labels.FindLabelFile(labelFileId, group.Key);
                    string fileError = path == null
                        ? "label file '" + labelFileId + "' has no " + group.Key + " language (create it with xpp_create_object AxLabelFile, properties.Languages)"
                        : !_labels.IsWritable(path) ? "label file '" + labelFileId + "' is not in your custom metadata folder" : null;
                    var existing = fileError == null ? _labels.ReadAll(path) : null;
                    var toInsert = new List<LabelEntry>();
                    foreach (var item in group.Value)
                    {
                        string state;
                        if (fileError != null) { state = "FAILED (" + fileError + ")"; failed = true; }
                        else if (existing.TryGetValue(item.Key.LabelId, out var current))
                        {
                            if (string.Equals(current.Text, item.Value, StringComparison.Ordinal)) state = "unchanged (already there)";
                            else { state = "FAILED (already exists with text '" + current.Text + "')"; failed = true; }
                        }
                        else
                        {
                            toInsert.Add(new LabelEntry { Id = item.Key.LabelId, Text = item.Value, Comment = item.Key.Comment });
                            state = "created";
                        }
                        Outcome(outcome, item.Key.LabelId).Add(group.Key + " " + state + (state == "created" ? ": " + item.Value : ""));
                    }
                    if (toInsert.Count > 0) _labels.InsertMany(path, toInsert);
                }
                catch (Exception ex)
                {
                    failed = true;
                    foreach (var item in group.Value)
                        Outcome(outcome, item.Key.LabelId).Add(group.Key + " FAILED (" + ex.Message + ")");
                }
            }

            foreach (var label in labels.Where(l => outcome.ContainsKey(l.LabelId ?? "")))
                lines.Add("@" + labelFileId + ":" + label.LabelId + " - " + string.Join("; ", outcome[label.LabelId]));
            int created = outcome.Values.SelectMany(v => v).Count(s => s.Contains(" created"));
            lines.Insert(0, (failed ? "Some labels failed. " : "") + "Labels in " + labelFileId + ": " + created + " text(s) written, "
                + perLanguage.Count + " label file(s) touched.");
            return new MetaModelResult { Success = !failed, Message = string.Join("\n", lines) };
        }

        private static void AddFor(Dictionary<string, List<KeyValuePair<LabelRequestDto, string>>> map, string language, LabelRequestDto label, string text)
        {
            if (!map.TryGetValue(language, out var list)) map[language] = list = new List<KeyValuePair<LabelRequestDto, string>>();
            list.Add(new KeyValuePair<LabelRequestDto, string>(label, text));
        }

        private static List<string> Outcome(Dictionary<string, List<string>> map, string id)
        {
            if (!map.TryGetValue(id, out var list)) map[id] = list = new List<string>();
            return list;
        }

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
