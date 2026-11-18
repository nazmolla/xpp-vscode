using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Service;
using XppVsCode.Server.MetaModel;

namespace XppVsCode.Server.Host
{
    /// <summary>A D365FO project (.rnrproj), edited the same way Visual Studio saves it.</summary>
    internal sealed class XppProject
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        private readonly IMetaModelService _metaService;

        public XppProject(string projectFile, IMetaModelService metaService)
        {
            FullName = projectFile;
            _metaService = metaService;
        }

        /// <summary>The project file alone: its items and references, without model lookups.</summary>
        internal static XppProject ForFile(string projectFile) => new XppProject(projectFile, null);

        public string FullName { get; }
        public string Caption => Path.GetFileNameWithoutExtension(FullName);

        public string ModelName =>
            Load().Descendants(Ns + "Model").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);

        public ModelInfo GetProjectsModelInfo()
        {
            string model = ModelName;
            return string.IsNullOrEmpty(model) ? null : _metaService.GetModel(model);
        }

        public List<ProjectItemInfo> GetItems()
        {
            string dir = Path.GetDirectoryName(FullName);
            return Load().Descendants(Ns + "Content")
                .Select(c =>
                {
                    string include = (string)c.Attribute("Include") ?? "";
                    int slash = include.IndexOf('\\');
                    return new ProjectItemInfo
                    {
                        Name = (string)c.Element(Ns + "Name") ?? Path.GetFileName(include),
                        ObjectType = slash > 0 ? include.Substring(0, slash) : null,
                        FilePath = Path.Combine(dir, include),
                    };
                })
                .ToList();
        }

        public bool Contains(string objectType, string objectName)
        {
            string include = objectType + "\\" + objectName;
            return Load().Descendants(Ns + "Content")
                .Any(c => string.Equals((string)c.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase));
        }

        /// <returns>false when the item was not in the project.</returns>
        public bool RemoveItem(string objectType, string objectName)
        {
            var doc = Load();
            string include = objectType + "\\" + objectName;
            var items = doc.Descendants(Ns + "Content")
                .Where(c => string.Equals((string)c.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (items.Count == 0) return false;
            foreach (var item in items) item.Remove();

            // Drop the folder entry when no item of that type is left, as Visual Studio does.
            string folder = FolderFor(objectType) + "\\";
            bool typeLeft = doc.Descendants(Ns + "Content")
                .Any(c => ((string)c.Attribute("Include") ?? "").StartsWith(objectType + "\\", StringComparison.OrdinalIgnoreCase));
            if (!typeLeft)
                doc.Descendants(Ns + "Folder")
                    .Where(f => string.Equals((string)f.Attribute("Include"), folder, StringComparison.OrdinalIgnoreCase))
                    .ToList().ForEach(f => f.Remove());
            foreach (var group in doc.Root.Elements(Ns + "ItemGroup").Where(g => !g.HasElements).ToList())
                group.Remove();

            doc.Save(FullName);
            return true;
        }

        /// <returns>false when the item is already in the project.</returns>
        public bool AddItem(string objectType, string objectName)
        {
            var doc = Load();
            string include = objectType + "\\" + objectName;
            if (doc.Descendants(Ns + "Content").Any(c => string.Equals((string)c.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase)))
                return false;

            string folder = FolderFor(objectType);
            var root = doc.Root;
            var contentGroup = root.Elements(Ns + "ItemGroup").FirstOrDefault(g => g.Elements(Ns + "Content").Any());
            if (contentGroup == null)
            {
                contentGroup = new XElement(Ns + "ItemGroup");
                root.Add(contentGroup);
            }
            contentGroup.Add(new XElement(Ns + "Content",
                new XAttribute("Include", include),
                new XElement(Ns + "SubType", "Content"),
                new XElement(Ns + "Name", objectName),
                new XElement(Ns + "Link", folder + "\\" + objectName)));

            var folderGroup = root.Elements(Ns + "ItemGroup").FirstOrDefault(g => g.Elements(Ns + "Folder").Any());
            if (folderGroup == null)
            {
                folderGroup = new XElement(Ns + "ItemGroup");
                contentGroup.AddBeforeSelf(folderGroup);
            }
            if (!folderGroup.Elements(Ns + "Folder").Any(f => string.Equals((string)f.Attribute("Include"), folder + "\\", StringComparison.OrdinalIgnoreCase)))
                folderGroup.Add(new XElement(Ns + "Folder", new XAttribute("Include", folder + "\\")));

            doc.Save(FullName);
            return true;
        }

        // Visual Studio writes project files indented with two spaces; XDocument.Save matches that.
        private XDocument Load() => XDocument.Load(FullName);

        /// <summary>A C# project (ProjectReference) or an assembly (Reference with a HintPath) the X++ project references.</summary>
        public sealed class ReferenceItem
        {
            public string Kind;
            public string Name;
            public string Include;
            /// <summary>Full path of the .csproj, or of the assembly (HintPath).</summary>
            public string Path;
        }

        public List<ReferenceItem> GetReferences()
        {
            string dir = System.IO.Path.GetDirectoryName(FullName);
            var doc = Load();
            var projects = doc.Descendants(Ns + "ProjectReference").Select(r => new ReferenceItem
            {
                Kind = "project",
                Include = (string)r.Attribute("Include") ?? "",
                Name = (string)r.Element(Ns + "Name") ?? System.IO.Path.GetFileNameWithoutExtension((string)r.Attribute("Include") ?? ""),
            });
            var assemblies = doc.Descendants(Ns + "Reference").Select(r => new ReferenceItem
            {
                Kind = "assembly",
                Include = (string)r.Attribute("Include") ?? "",
                Name = (string)r.Element(Ns + "Name") ?? ((string)r.Attribute("Include") ?? "").Split(',')[0].Trim(),
                Path = (string)r.Element(Ns + "HintPath"),
            });
            var all = projects.Concat(assemblies).ToList();
            foreach (var r in all)
            {
                string relative = r.Kind == "project" ? r.Include : r.Path;
                r.Path = string.IsNullOrEmpty(relative) ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, relative));
            }
            return all;
        }

        /// <returns>false when the project already references it.</returns>
        public bool AddProjectReference(string projectPath, string name, string projectGuid)
        {
            var doc = Load();
            string include = RelativePath(projectPath);
            if (doc.Descendants(Ns + "ProjectReference").Any(r => string.Equals((string)r.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)r.Element(Ns + "Name"), name, StringComparison.OrdinalIgnoreCase)))
                return false;
            var item = new XElement(Ns + "ProjectReference", new XAttribute("Include", include), new XElement(Ns + "Name", name));
            if (!string.IsNullOrEmpty(projectGuid)) item.Add(new XElement(Ns + "Project", projectGuid));
            AddReferenceItem(doc, "ProjectReference", item);
            return true;
        }

        /// <returns>false when the project already references it.</returns>
        public bool AddAssemblyReference(string assemblyDisplayName, string name, string assemblyPath)
        {
            var doc = Load();
            if (doc.Descendants(Ns + "Reference").Any(r => string.Equals((string)r.Element(Ns + "Name"), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(((string)r.Attribute("Include") ?? "").Split(',')[0].Trim(), name, StringComparison.OrdinalIgnoreCase)))
                return false;
            AddReferenceItem(doc, "Reference", new XElement(Ns + "Reference", new XAttribute("Include", assemblyDisplayName),
                new XElement(Ns + "Name", name), new XElement(Ns + "HintPath", RelativePath(assemblyPath))));
            return true;
        }

        /// <returns>false when no reference has that name.</returns>
        public bool RemoveReference(string name)
        {
            var doc = Load();
            bool Named(XElement r, string fromInclude) =>
                string.Equals((string)r.Element(Ns + "Name"), name, StringComparison.OrdinalIgnoreCase) || string.Equals(fromInclude, name, StringComparison.OrdinalIgnoreCase);
            // A project reference is named after its project file; an assembly reference after its assembly (which may contain dots).
            var items = doc.Descendants(Ns + "ProjectReference").Where(r => Named(r, System.IO.Path.GetFileNameWithoutExtension((string)r.Attribute("Include") ?? "")))
                .Concat(doc.Descendants(Ns + "Reference").Where(r => Named(r, ((string)r.Attribute("Include") ?? "").Split(',')[0].Trim())))
                .ToList();
            if (items.Count == 0) return false;
            foreach (var item in items) item.Remove();
            foreach (var group in doc.Root.Elements(Ns + "ItemGroup").Where(g => !g.HasElements).ToList())
                group.Remove();
            doc.Save(FullName);
            return true;
        }

        private void AddReferenceItem(XDocument doc, string itemName, XElement item)
        {
            var root = doc.Root;
            var group = root.Elements(Ns + "ItemGroup").FirstOrDefault(g => g.Elements(Ns + itemName).Any());
            if (group == null)
            {
                group = new XElement(Ns + "ItemGroup");
                // Item groups go before the imports, as Visual Studio keeps them.
                var firstImport = root.Elements(Ns + "Import").FirstOrDefault();
                if (firstImport != null) firstImport.AddBeforeSelf(group); else root.Add(group);
            }
            group.Add(item);
            doc.Save(FullName);
        }

        /// <summary>The path relative to the project's folder (absolute when on another drive), as MSBuild items keep them.</summary>
        private string RelativePath(string path)
        {
            var from = new Uri(System.IO.Path.GetDirectoryName(FullName).TrimEnd('\\') + "\\");
            var to = new Uri(System.IO.Path.GetFullPath(path));
            if (!string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(System.IO.Path.GetPathRoot(from.LocalPath), System.IO.Path.GetPathRoot(to.LocalPath), StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFullPath(path);
            return Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString()).Replace('/', '\\');
        }

        private static string FolderFor(string objectType)
        {
            switch (objectType)
            {
                case "AxClass": return "Classes";
                case "AxTable": return "Tables";
                case "AxForm": return "Forms";
                case "AxEdt": return "Extended Data Types";
                case "AxEnum": return "Base Enums";
                case "AxView": return "Views";
                case "AxQuery": return "Queries";
                case "AxMap": return "Maps";
                case "AxDataEntityView": return "Data Entities";
                case "AxMenu": return "Menus";
                case "AxTile": return "Tiles";
                case "AxMenuItemDisplay": return "Display Menu Items";
                case "AxMenuItemOutput": return "Output Menu Items";
                case "AxMenuItemAction": return "Action Menu Items";
                case "AxSecurityPrivilege": return "Security Privileges";
                case "AxSecurityDuty": return "Security Duties";
                case "AxSecurityRole": return "Security Roles";
                case "AxService": return "Services";
                case "AxServiceGroup": return "Service Groups";
                case "AxConfigurationKey": return "Configuration Keys";
                case "AxLabelFile": return "Label Files";
                case "AxReport": return "Reports";
                case "AxTableExtension": return "Table Extensions";
                case "AxFormExtension": return "Form Extensions";
                case "AxEnumExtension": return "Base Enum Extensions";
                case "AxEdtExtension": return "Extended Data Type Extensions";
                case "AxDataEntityViewExtension": return "Data Entity Extensions";
                case "AxSecurityDutyExtension": return "Security Duty Extensions";
                case "AxSecurityRoleExtension": return "Security Role Extensions";
                default:
                    // e.g. AxMenuItemDisplayExtension -> "Menu Item Display Extensions"
                    string bare = objectType.StartsWith("Ax", StringComparison.Ordinal) ? objectType.Substring(2) : objectType;
                    return System.Text.RegularExpressions.Regex.Replace(bare, "(?<=[a-z])(?=[A-Z])", " ") + "s";
            }
        }
    }
}
