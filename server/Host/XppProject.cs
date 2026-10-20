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
                .Select(c => new ProjectItemInfo
                {
                    Name = (string)c.Element(Ns + "Name") ?? Path.GetFileName((string)c.Attribute("Include")),
                    FilePath = Path.Combine(dir, (string)c.Attribute("Include") ?? ""),
                })
                .ToList();
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
                    string bare = objectType.StartsWith("Ax") ? objectType.Substring(2) : objectType;
                    return System.Text.RegularExpressions.Regex.Replace(bare, "(?<=[a-z])(?=[A-Z])", " ") + "s";
            }
        }
    }
}
