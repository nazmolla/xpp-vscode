using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Lists objects in a D365FO model by enumerating metadata file names on disk
    /// (&lt;Root&gt;\&lt;Package&gt;\&lt;Model&gt;\&lt;AxType&gt;\&lt;Name&gt;.xml). Runs entirely in the MCP
    /// server process: no MetaModel API, no VS UI thread, no XML parsing.
    /// </summary>
    internal sealed class ModelObjectLister
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private sealed class ModelLocation
        {
            public string Name;
            public string Package;
            public string Folder;
            public bool IsCustom;
        }

        private readonly object _lock = new object();
        private Dictionary<string, ModelLocation> _models;
        private DateTime _modelsLoadedUtc;

        public string Handle(string idToken, string json)
        {
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            string modelName = JsonHelpers.ExtractArgString(args, "modelName", "model");
            string objectType = JsonHelpers.ExtractArgString(args, "objectType", "type");
            string nameFilter = JsonHelpers.ExtractArgString(args, "nameFilter");
            int offset = ParseInt(JsonHelpers.ExtractArgString(args, "offset"), 0);
            int limit = ParseInt(JsonHelpers.ExtractArgString(args, "limit", "maxResults"), DefaultLimit);
            if (offset < 0) offset = 0;
            if (limit < 1) limit = 1;
            if (limit > MaxLimit) limit = MaxLimit;

            if (string.IsNullOrWhiteSpace(modelName))
                return JsonHelpers.BuildToolResult(idToken, "Parameter 'modelName' is required.", isError: true);

            // objectType becomes a path segment; only allow plain Ax* type names.
            if (!string.IsNullOrWhiteSpace(objectType)
                && !System.Text.RegularExpressions.Regex.IsMatch(objectType.Trim(), "^Ax[A-Za-z]+$"))
                return JsonHelpers.BuildToolResult(idToken,
                    "Invalid objectType '" + objectType + "'. Use a D365FO type name such as AxClass or AxTable.", isError: true);

            try
            {
                var model = ResolveModel(modelName.Trim(), out string resolveError);
                if (model == null)
                    return JsonHelpers.BuildToolResult(idToken, resolveError, isError: true);

                return string.IsNullOrWhiteSpace(objectType)
                    ? JsonHelpers.BuildToolResult(idToken, BuildSummary(model))
                    : BuildListing(idToken, model, objectType.Trim(), nameFilter, offset, limit);
            }
            catch (Exception ex)
            {
                McpLogger.Log("xpp_list_model_objects error: " + ex);
                return JsonHelpers.BuildToolResult(idToken, "Failed to list model objects: " + ex.Message, isError: true);
            }
        }

        private static string BuildSummary(ModelLocation model)
        {
            var counts = new List<KeyValuePair<string, int>>();
            foreach (string dir in Directory.EnumerateDirectories(model.Folder, "Ax*"))
            {
                int n = Directory.EnumerateFiles(dir, "*.xml").Count();
                if (n > 0) counts.Add(new KeyValuePair<string, int>(Path.GetFileName(dir), n));
            }

            var sb = new StringBuilder();
            AppendModelHeader(sb, model);
            sb.AppendLine("Total objects: " + counts.Sum(c => c.Value));
            sb.AppendLine();
            sb.AppendLine("Objects per type:");
            foreach (var c in counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine("  " + c.Key + ": " + c.Value);
            sb.AppendLine();
            sb.Append("Call again with objectType (and optionally nameFilter/offset/limit) to list names for one type.");
            return sb.ToString();
        }

        private static string BuildListing(string idToken, ModelLocation model, string objectType,
            string nameFilter, int offset, int limit)
        {
            string typeFolder = Path.Combine(model.Folder, objectType);
            if (!Directory.Exists(typeFolder))
            {
                var available = Directory.EnumerateDirectories(model.Folder, "Ax*")
                    .Where(d => Directory.EnumerateFiles(d, "*.xml").Any())
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                return JsonHelpers.BuildToolResult(idToken,
                    "Model '" + model.Name + "' has no " + objectType + " objects. Types present: "
                    + string.Join(", ", available));
            }

            var names = Directory.EnumerateFiles(typeFolder, "*.xml")
                .Select(Path.GetFileNameWithoutExtension);
            if (!string.IsNullOrWhiteSpace(nameFilter))
            {
                string f = nameFilter.Trim();
                names = names.Where(n => n.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var page = sorted.Skip(offset).Take(limit).ToList();

            var sb = new StringBuilder();
            AppendModelHeader(sb, model);
            sb.AppendLine("Type: " + objectType
                + (string.IsNullOrWhiteSpace(nameFilter) ? "" : "  Filter: '" + nameFilter.Trim() + "'"));
            sb.AppendLine(page.Count == 0
                ? "No objects in range (total matching: " + sorted.Count + ")."
                : "Showing " + (offset + 1) + "-" + (offset + page.Count) + " of " + sorted.Count + ":");
            foreach (string n in page)
                sb.AppendLine(n);
            if (offset + page.Count < sorted.Count)
                sb.Append("More results: call again with offset=" + (offset + page.Count) + ".");
            return JsonHelpers.BuildToolResult(idToken, sb.ToString().TrimEnd());
        }

        private static void AppendModelHeader(StringBuilder sb, ModelLocation model)
        {
            sb.AppendLine("Model: " + model.Name + "  Package: " + model.Package
                + "  (" + (model.IsCustom ? "custom, editable" : "reference, read-only") + ")");
        }

        private ModelLocation ResolveModel(string modelName, out string error)
        {
            error = null;
            lock (_lock)
            {
                bool fresh = _models != null && DateTime.UtcNow - _modelsLoadedUtc < CacheTtl;
                if (!fresh) LoadModels();

                // A miss on a cached index may be a newly created model; rebuild once.
                if (!_models.TryGetValue(modelName, out var model) && fresh)
                {
                    LoadModels();
                    _models.TryGetValue(modelName, out model);
                }
                if (model != null) return model;

                // Agents often pass the package name (ApplicationSuite) instead of the model (Foundation).
                var inPackage = _models.Values.Where(m => string.Equals(m.Package, modelName, StringComparison.OrdinalIgnoreCase))
                    .Distinct().ToList();
                if (inPackage.Count == 1) return inPackage[0];

                if (_models.Count == 0)
                {
                    error = "No D365FO metadata folders found. Check the active D365FO configuration (XPPConfig) on this machine.";
                    return null;
                }
                if (inPackage.Count > 1)
                {
                    error = "'" + modelName + "' is a package with several models: " + string.Join(", ", inPackage.Select(m => m.Name).Distinct())
                        + ". Pass one of these model names.";
                    return null;
                }

                var similar = _models.Keys
                    .Where(k => k.IndexOf(modelName, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();
                error = "Model '" + modelName + "' not found."
                    + (similar.Count > 0 ? " Similar models: " + string.Join(", ", similar) : " Use xpp_list_models to see model names.");
                return null;
            }
        }

        private void LoadModels()
        {
            var models = new Dictionary<string, ModelLocation>(StringComparer.OrdinalIgnoreCase);
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();

            // Custom root first so custom models win over same-named reference copies.
            var roots = new List<KeyValuePair<string, bool>>();
            if (config.HasCustom) roots.Add(new KeyValuePair<string, bool>(config.CustomMetadataFolder, true));
            foreach (string r in config.ReferenceMetadataFolders) roots.Add(new KeyValuePair<string, bool>(r, false));
            if (roots.Count == 0)
            {
                foreach (string r in new[] { @"K:\AosService\PackagesLocalDirectory", @"C:\AosService\PackagesLocalDirectory" })
                    if (Directory.Exists(r)) roots.Add(new KeyValuePair<string, bool>(r, false));
            }

            foreach (var root in roots)
            {
                if (!Directory.Exists(root.Key)) continue;
                foreach (string packageDir in Directory.EnumerateDirectories(root.Key))
                {
                    string descriptorDir = Path.Combine(packageDir, "Descriptor");
                    if (!Directory.Exists(descriptorDir)) continue;

                    foreach (string descriptor in Directory.EnumerateFiles(descriptorDir, "*.xml"))
                    {
                        string name = Path.GetFileNameWithoutExtension(descriptor);
                        string modelFolder = Path.Combine(packageDir, name);
                        if (models.ContainsKey(name) || !Directory.Exists(modelFolder)) continue;
                        models[name] = new ModelLocation
                        {
                            Name = name,
                            Package = Path.GetFileName(packageDir),
                            Folder = modelFolder,
                            IsCustom = root.Value && config.IsCustomPath(packageDir)
                        };
                    }
                }
            }

            _models = models;
            _modelsLoadedUtc = DateTime.UtcNow;
            McpLogger.Log("ModelObjectLister indexed " + models.Count + " models from " + roots.Count + " root(s).");
        }

        private static int ParseInt(string s, int fallback) =>
            int.TryParse(s, out int v) ? v : fallback;
    }
}
