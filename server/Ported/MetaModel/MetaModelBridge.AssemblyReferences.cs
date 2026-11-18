using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // The X++ project's references to C# projects and assemblies, as Visual Studio adds them: an item in the .rnrproj
    // (ProjectReference, or Reference with a HintPath) and an AxReference element in the project's model, through which
    // xppc resolves the assembly's types. The build copies the assemblies into the package (Host/ProjectReferences.cs).
    public sealed partial class MetaModelBridge
    {
        public string ProjectReferencesJson()
        {
            var json = new JavaScriptSerializer();
            var project = GetActiveProjectNode();
            if (project == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["references"] = new object[0] });
            var model = project.GetProjectsModelInfo();
            var inModel = new HashSet<string>(model == null ? Enumerable.Empty<string>() : MetaService.GetReferenceNames()
                .Where(n => InModel(n, model.Name)), StringComparer.OrdinalIgnoreCase);
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["model"] = model?.Name,
                ["references"] = project.GetReferences().Select(r => new Dictionary<string, object>
                {
                    ["kind"] = r.Kind,
                    ["name"] = r.Name,
                    ["path"] = r.Path,
                    ["exists"] = r.Path != null && File.Exists(r.Path),
                }).ToList(),
                ["modelReferences"] = inModel.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            });
        }

        /// <summary>projectPath: the .csproj for a project reference (assemblyPath is its build output); null for an assembly reference.</summary>
        public string AddReferenceJson(string modelName, string assemblyPath, string projectPath)
        {
            var project = GetActiveProjectNode();
            if (project == null)
                return ReferenceFailure("Open a D365FO project first.");
            var model = project.GetProjectsModelInfo();
            if (model == null || !string.Equals(model.Name, modelName, StringComparison.OrdinalIgnoreCase))
                return ReferenceFailure("References are added to the active project's model (" + (model?.Name ?? "none") + ").");
            if (string.IsNullOrEmpty(projectPath)) projectPath = null;
            if (projectPath != null && !File.Exists(projectPath))
                return ReferenceFailure("Project not found: " + projectPath);
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                return ReferenceFailure(projectPath != null ? "Build " + Path.GetFileName(projectPath) + " first: its assembly was not found." : "Assembly not found: " + assemblyPath);

            AssemblyName assembly;
            try { assembly = AssemblyName.GetAssemblyName(assemblyPath); }
            catch (BadImageFormatException) { return ReferenceFailure(Path.GetFileName(assemblyPath) + " is not a .NET assembly."); }
            if (assembly.Name.StartsWith("Dynamics.AX.", StringComparison.OrdinalIgnoreCase))
                return ReferenceFailure(assembly.Name + " is a compiled X++ package: add its model to your model's dependencies instead.");

            bool inModel = InModel(assembly.Name, model.Name);
            if (!inModel)
            {
                byte[] token = assembly.GetPublicKeyToken();
                MetaService.CreateReference(new AxReference
                {
                    Name = assembly.Name,
                    AssemblyName = assembly.Name,
                    AssemblyDisplayName = assembly.FullName,
                    Version = assembly.Version?.ToString(),
                    PublicKeyToken = token == null || token.Length == 0 ? null : string.Concat(token.Select(b => b.ToString("x2"))),
                    ServiceReference = NoYes.No,
                }, GetModelSaveInfo(model.Name));
            }
            bool added = projectPath != null
                ? project.AddProjectReference(projectPath, Path.GetFileNameWithoutExtension(projectPath), ProjectGuidOf(projectPath))
                : project.AddAssemblyReference(assembly.FullName, assembly.Name, assemblyPath);
            InvalidateCaches();
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = assembly.Name,
                ["message"] = (added ? "Added a reference to " : project.Caption + " already references ") + (projectPath != null ? Path.GetFileName(projectPath) : assembly.Name)
                    + (inModel ? "" : "; model " + model.Name + " now has the reference " + assembly.Name) + ".",
            });
        }

        /// <summary>Removes the project item named name, the model's AxReference (assemblyName, else name) and the assembly the build copied.</summary>
        public string RemoveReferenceJson(string modelName, string name, string assemblyName)
        {
            var project = GetActiveProjectNode();
            if (project == null)
                return ReferenceFailure("Open a D365FO project first.");
            var model = project.GetProjectsModelInfo();
            if (model == null || !string.Equals(model.Name, modelName, StringComparison.OrdinalIgnoreCase))
                return ReferenceFailure("References are removed from the active project's model (" + (model?.Name ?? "none") + ").");
            string reference = string.IsNullOrWhiteSpace(assemblyName) ? name : assemblyName;
            bool removed = project.RemoveReference(name);
            bool inModel = InModel(reference, model.Name);
            if (inModel)
                MetaService.DeleteReference(reference, GetModelSaveInfo(model.Name));
            // As Visual Studio: the assembly the build copied into the package goes too.
            if (!reference.StartsWith("Dynamics.AX.", StringComparison.OrdinalIgnoreCase) && reference.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                string bin = Path.Combine(CustomFolder, model.Module ?? model.Name, "bin");
                foreach (string ext in new[] { ".dll", ".pdb", ".xml" })
                {
                    string file = Path.Combine(bin, reference + ext);
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            InvalidateCaches();
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                ["success"] = removed || inModel,
                ["message"] = removed || inModel ? "Removed the reference " + name + "." : project.Caption + " has no reference named " + name + ".",
            });
        }

        private bool InModel(string reference, string modelName) =>
            MetaService.GetReferenceModelInfo(reference)?.Any(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase)) == true;

        private static string ProjectGuidOf(string projectPath)
        {
            var match = Regex.Match(File.ReadAllText(projectPath), @"<ProjectGuid>\s*\{?([0-9A-Fa-f-]{36})\}?\s*</ProjectGuid>");
            return match.Success ? "{" + match.Groups[1].Value.ToLowerInvariant() + "}" : null;
        }

        private static string ReferenceFailure(string message) =>
            new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = message });
    }
}
