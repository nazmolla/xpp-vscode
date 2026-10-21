using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using XppVsCode.Server.Host;

namespace XppVsCode.Server.MetaModel
{
    // Dynamics 365 > Export/Import Project: the .axpp format Visual Studio writes (ProjectExporter/ProjectImporter),
    // so packages move freely between Visual Studio and VS Code.
    public sealed partial class MetaModelBridge
    {
        private const string PackageDescriptorEntry = "BA6BECB9-70B9-4E31-BD29-1A3725A0BA4F.xml";

        private static string CustomFolder => DynamicsConfigurationReader.ReadActiveConfiguration().CustomMetadataFolder;

        [DataContract(Name = "PackageDescriptor", Namespace = "http://schemas.datacontract.org/2004/07/Microsoft.Dynamics.Framework.Tools.ProjectSystem.ExportImport")]
        private sealed class AxppDescriptor
        {
            [DataMember] public Dictionary<string, string> ContentResourceDictionary { get; set; } = new Dictionary<string, string>();
            [DataMember] public Version PackageVersion { get; set; } = new Version(1, 1);
        }

        public string ExportProjectJson(string projectFile, string outputFile)
        {
            var json = new JavaScriptSerializer();
            var project = !string.IsNullOrEmpty(projectFile) && File.Exists(projectFile) ? new XppProject(projectFile, MetaService) : GetActiveProject();
            if (project == null)
                return json.Serialize(Failure("Select a project first."));
            if (string.IsNullOrWhiteSpace(outputFile))
                return json.Serialize(Failure("outputFile is required."));
            var model = project.GetProjectsModelInfo();
            if (model == null)
                return json.Serialize(Failure("The project's model '" + project.ModelName + "' was not found."));

            string metadataPath = Path.Combine(CustomFolder, model.Module);
            var log = new List<string>();
            var missing = new List<string>();
            var descriptor = new AxppDescriptor();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile)));
            using (var zip = new ZipArchive(new FileStream(outputFile, FileMode.Create), ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(project.FullName, "Project\\" + Path.GetFileName(project.FullName));
                using (var stream = zip.CreateEntry("Model\\" + model.Name + ".xml").Open())
                    new DataContractSerializer(typeof(ModelInfo)).WriteObject(stream, model);

                foreach (var include in ProjectIncludes(project.FullName))
                {
                    string[] parts = include.Split('\\');
                    if (parts.Length != 2) continue;
                    string file = Path.Combine(metadataPath, model.Name, parts[0], parts[1] + ".xml");
                    if (!File.Exists(file))
                    {
                        missing.Add(include);
                        continue;
                    }
                    string entry = "ProjectItem\\" + RelativePath(file, metadataPath);
                    zip.CreateEntryFromFile(file, entry);
                    log.Add(include);
                    foreach (string content in DependentContent(parts[0], parts[1], Path.Combine(metadataPath, model.Name)))
                    {
                        string contentEntry = "ProjectItem\\" + RelativePath(content, metadataPath);
                        zip.CreateEntryFromFile(content, contentEntry);
                        descriptor.ContentResourceDictionary[entry] = contentEntry;
                    }
                }
                using (var stream = zip.CreateEntry(PackageDescriptorEntry).Open())
                    new DataContractSerializer(typeof(AxppDescriptor)).WriteObject(stream, descriptor);
            }
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = "Exported " + log.Count + " element(s) of model " + model.Name + " to " + outputFile + "."
                    + (missing.Count > 0 ? " Not found (skipped): " + string.Join(", ", missing) + "." : ""),
                ["items"] = log,
            });
        }

        /// <summary>Lists what an .axpp contains, so the user can confirm before importing.</summary>
        public string InspectProjectPackageJson(string file)
        {
            var json = new JavaScriptSerializer();
            try
            {
                using (var zip = ZipFile.OpenRead(file))
                {
                    var project = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("Project\\", StringComparison.Ordinal));
                    var modelEntry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("Model\\", StringComparison.Ordinal));
                    if (project == null || modelEntry == null)
                        return json.Serialize(Failure("Not a Dynamics 365 project export (.axpp)."));
                    ModelInfo model;
                    using (var stream = modelEntry.Open())
                        model = (ModelInfo)new DataContractSerializer(typeof(ModelInfo)).ReadObject(stream);
                    var items = zip.Entries.Where(e => IsElementEntry(e.FullName)).Select(e => ElementOfEntry(e.FullName)).ToList();
                    return json.Serialize(new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["project"] = project.Name,
                        ["model"] = model.Name,
                        ["modelExists"] = MetaService.GetModel(model.Name) != null,
                        ["items"] = items.Select(i => new Dictionary<string, object> { ["objectType"] = i.Item1, ["objectName"] = i.Item2, ["exists"] = ElementFileExists(model, i.Item1, i.Item2) }).ToList(),
                    });
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is SerializationException)
            {
                return json.Serialize(Failure("Can't read " + file + ": " + ex.Message));
            }
        }

        public string ImportProjectJson(string file, string targetFolder, bool overwrite)
        {
            var json = new JavaScriptSerializer();
            if (string.IsNullOrWhiteSpace(targetFolder))
                return json.Serialize(Failure("targetFolder is required."));
            var imported = new List<string>();
            var skipped = new List<string>();
            string projectPath;
            string modelName;
            using (var zip = ZipFile.OpenRead(file))
            {
                var projectEntry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("Project\\", StringComparison.Ordinal));
                var modelEntry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("Model\\", StringComparison.Ordinal));
                if (projectEntry == null || modelEntry == null)
                    return json.Serialize(Failure("Not a Dynamics 365 project export (.axpp)."));

                ModelInfo archived;
                using (var stream = modelEntry.Open())
                    archived = (ModelInfo)new DataContractSerializer(typeof(ModelInfo)).ReadObject(stream);
                modelName = archived.Name;
                var model = MetaService.GetModel(archived.Name);
                if (model == null)
                {
                    MetaService.CreateModel(archived);
                    _host.Reset();
                    model = MetaService.GetModel(archived.Name);
                    if (model == null)
                        return json.Serialize(Failure("Could not create model " + archived.Name + "."));
                }
                if (!IsCustomModel(model.Name))
                    return json.Serialize(Failure("Model " + model.Name + " is not one of your models; importing into it is not allowed."));

                AxppDescriptor descriptor = null;
                var descriptorEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals(PackageDescriptorEntry, StringComparison.Ordinal));
                if (descriptorEntry != null)
                    using (var stream = descriptorEntry.Open())
                        descriptor = new DataContractSerializer(typeof(AxppDescriptor)).ReadObject(stream) as AxppDescriptor;

                string metadataPath = Path.GetFullPath(Path.Combine(CustomFolder, model.Module));
                foreach (var entry in zip.Entries.Where(e => IsElementEntry(e.FullName)))
                {
                    var element = ElementOfEntry(entry.FullName);
                    string target = Path.Combine(metadataPath, model.Name, element.Item1, element.Item2 + ".xml");
                    if (File.Exists(target) && !overwrite)
                    {
                        skipped.Add(element.Item1 + "\\" + element.Item2);
                        continue;
                    }
                    ExtractSafely(entry, target, metadataPath);
                    imported.Add(element.Item1 + "\\" + element.Item2);
                    if (descriptor != null && descriptor.ContentResourceDictionary.TryGetValue(entry.FullName, out string contentName))
                    {
                        var content = zip.GetEntry(contentName) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(contentName, StringComparison.OrdinalIgnoreCase));
                        if (content != null)
                            ExtractSafely(content, Path.Combine(metadataPath, RetargetModel(contentName.Substring("ProjectItem\\".Length), model.Name)), metadataPath);
                    }
                }

                string projectFolder = Path.Combine(targetFolder, Path.GetFileNameWithoutExtension(projectEntry.Name));
                Directory.CreateDirectory(projectFolder);
                projectPath = Path.Combine(projectFolder, projectEntry.Name);
                projectEntry.ExtractToFile(projectPath, overwrite: true);
            }
            _host.Reset();
            InvalidateCaches();
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = "Imported " + imported.Count + " element(s) into model " + modelName + "."
                    + (skipped.Count > 0 ? " Kept existing: " + string.Join(", ", skipped) + "." : ""),
                ["projectFile"] = projectPath,
                ["imported"] = imported,
                ["skipped"] = skipped,
            });
        }

        private static IEnumerable<string> ProjectIncludes(string projectFile)
        {
            XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
            return XDocument.Load(projectFile).Descendants(ns + "Content")
                .Where(c => c.Element(ns + "DependentUpon") == null)
                .Select(c => (string)c.Attribute("Include"))
                .Where(i => !string.IsNullOrEmpty(i));
        }

        // Label files and resources keep their content next to the element; the descriptor maps one to the other.
        private static IEnumerable<string> DependentContent(string objectType, string objectName, string modelFolder)
        {
            if (objectType == "AxLabelFile")
            {
                // e.g. AxLabelFile\MyLabels_en-US.xml -> AxLabelFile\LabelResources\en-US\MyLabels.en-US.label.txt
                var match = Regex.Match(objectName, "^(.+)_([A-Za-z]{2}(?:-[A-Za-z]+)?)$");
                if (match.Success)
                {
                    string label = Path.Combine(modelFolder, "AxLabelFile", "LabelResources", match.Groups[2].Value, match.Groups[1].Value + "." + match.Groups[2].Value + ".label.txt");
                    if (File.Exists(label)) yield return label;
                }
            }
            else if (objectType == "AxResource")
            {
                string folder = Path.Combine(modelFolder, "AxResource", "ResourceContent");
                if (Directory.Exists(folder))
                    foreach (string f in Directory.EnumerateFiles(folder, objectName + ".*"))
                        yield return f;
            }
        }

        private static string RelativePath(string file, string root)
            => Path.GetFullPath(file).Substring(Path.GetFullPath(root).TrimEnd('\\').Length + 1);

        // ProjectItem\<model>\<AxType>\<Name>.xml
        private static bool IsElementEntry(string name)
        {
            var parts = name.Split('\\');
            return parts.Length == 4 && parts[0] == "ProjectItem" && parts[2].StartsWith("Ax", StringComparison.Ordinal)
                && parts[3].EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        }

        private static Tuple<string, string> ElementOfEntry(string name)
        {
            var parts = name.Split('\\');
            return Tuple.Create(parts[2], Path.GetFileNameWithoutExtension(parts[3]));
        }

        private bool ElementFileExists(ModelInfo archived, string objectType, string objectName)
        {
            var model = MetaService.GetModel(archived.Name);
            return model != null && File.Exists(Path.Combine(CustomFolder, model.Module, model.Name, objectType, objectName + ".xml"));
        }

        // An exported item's path starts with its source model's folder; content goes to the target model's folder.
        private static string RetargetModel(string relative, string modelName)
        {
            int slash = relative.IndexOf('\\');
            return slash < 0 ? relative : modelName + relative.Substring(slash);
        }

        private static void ExtractSafely(ZipArchiveEntry entry, string target, string root)
        {
            string full = Path.GetFullPath(target);
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Entry is outside the target folder: " + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            if (File.Exists(full))
                File.SetAttributes(full, File.GetAttributes(full) & ~FileAttributes.ReadOnly);
            entry.ExtractToFile(full, overwrite: true);
        }
    }
}
