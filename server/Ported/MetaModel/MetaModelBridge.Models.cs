using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Model management (Dynamics 365 > Model Management): create a model, read and update its parameters.
    public sealed partial class MetaModelBridge
    {
        private static readonly string[] LayerNames = { "SYS", "SYP", "GLS", "GLP", "FPK", "FPP", "SLN", "SLP", "ISV", "ISP", "VAR", "VAP", "CUS", "CUP", "USR", "USP" };

        public string GetModelJson(string name)
        {
            var json = new JavaScriptSerializer();
            var model = string.IsNullOrWhiteSpace(name) ? null : MetaService.GetModel(name.Trim());
            if (model == null)
                return json.Serialize(Failure("Model '" + name + "' not found."));
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = model.Name,
                ["displayName"] = model.DisplayName,
                ["description"] = model.Description,
                ["publisher"] = model.Publisher,
                ["package"] = model.Module,
                ["layer"] = model.Layer >= 0 && model.Layer < LayerNames.Length ? LayerNames[model.Layer] : model.Layer.ToString(),
                ["version"] = model.VersionMajor + "." + model.VersionMinor + "." + model.VersionBuild + "." + model.VersionRevision,
                ["references"] = model.ModuleReferences?.ToList() ?? new List<string>(),
                ["isCustom"] = IsCustomModel(model.Name),
            });
        }

        /// <summary>Creates a model in its own new package (the recommended setup) in the custom metadata folder.</summary>
        public string CreateModelJson(string name, string displayName, string publisher, string layer, string description, string[] references)
        {
            var json = new JavaScriptSerializer();
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*$"))
                return json.Serialize(Failure("'" + name + "' is not a valid model name."));
            if (MetaService.GetModel(name) != null)
                return json.Serialize(Failure("A model named '" + name + "' already exists."));
            int layerIndex = Array.FindIndex(LayerNames, l => l.Equals(layer ?? "USR", StringComparison.OrdinalIgnoreCase));
            if (layerIndex < 0)
                return json.Serialize(Failure("Unknown layer '" + layer + "'. Use one of " + string.Join(", ", LayerNames) + "."));

            var known = new HashSet<string>(MetaService.GetModels().Select(m => m.Module), StringComparer.OrdinalIgnoreCase);
            var missing = (references ?? new string[0]).Where(r => !known.Contains(r)).ToList();
            if (missing.Count > 0)
                return json.Serialize(Failure("Unknown package(s): " + string.Join(", ", missing) + "."));

            var info = new ModelInfo
            {
                Name = name,
                Module = name,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                Publisher = publisher ?? "",
                Description = description ?? "",
                Layer = layerIndex,
                VersionMajor = 1,
                Customization = Microsoft.Dynamics.AX.Metadata.Core.MetaModel.ModelCustomizationLevel.Allow,
                ModuleReferences = new List<string>(references ?? new string[0]),
                ModelReferences = new List<string>(),
                InternalsVisibleTo = new List<string>(),
            };
            foreach (var list in new[] { "AppliedUpdates" })
            {
                var prop = typeof(ModelInfo).GetProperty(list);
                if (prop != null && prop.GetValue(info) == null && prop.CanWrite)
                    prop.SetValue(info, Activator.CreateInstance(typeof(List<>).MakeGenericType(prop.PropertyType.GetGenericArguments())));
            }
            MetaService.CreateModel(info);
            _host.Reset();
            InvalidateCaches();
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = "Created model " + name + " in package " + name + "." });
        }

        /// <summary>Updates a custom model's display name, description, publisher and package references.</summary>
        public string UpdateModelJson(string name, string displayName, string publisher, string description, string[] references)
        {
            var json = new JavaScriptSerializer();
            var model = string.IsNullOrWhiteSpace(name) ? null : MetaService.GetModel(name.Trim());
            if (model == null)
                return json.Serialize(Failure("Model '" + name + "' not found."));
            if (!IsCustomModel(model.Name))
                return json.Serialize(Failure("Only your own models can be changed."));
            if (displayName != null) model.DisplayName = displayName;
            if (publisher != null) model.Publisher = publisher;
            if (description != null) model.Description = description;
            if (references != null)
            {
                var known = new HashSet<string>(MetaService.GetModels().Select(m => m.Module), StringComparer.OrdinalIgnoreCase);
                var missing = references.Where(r => !known.Contains(r)).ToList();
                if (missing.Count > 0)
                    return json.Serialize(Failure("Unknown package(s): " + string.Join(", ", missing) + "."));
                model.ModuleReferences = new List<string>(references);
            }
            MetaService.UpdateModel(model);
            _host.Reset();
            InvalidateCaches();
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = "Updated model " + model.Name + "." });
        }

        /// <summary>All packages, for the reference picker.</summary>
        public string ListPackagesJson()
        {
            var json = new JavaScriptSerializer();
            var packages = MetaService.GetModels()
                .GroupBy(m => m.Module, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Dictionary<string, object>
                {
                    ["name"] = g.Key,
                    ["models"] = g.Select(m => m.DisplayName ?? m.Name).ToList(),
                    ["isCustom"] = g.Any(m => IsCustomModel(m.Name)),
                })
                .OrderBy(p => (string)p["name"], StringComparer.OrdinalIgnoreCase)
                .ToList();
            return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["packages"] = packages });
        }
    }
}
