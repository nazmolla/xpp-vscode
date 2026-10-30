using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Win32;

namespace XppVsCode.Server
{
    /// <summary>
    /// Reads the active D365FO local XPP configuration.
    /// 
    /// Step 1: Registry at HKCU\Software\Microsoft\Dynamics\AX7\Development\Configurations
    ///         → "CurrentMetadataConfig" gives the full path to the active JSON config file.
    ///         → "FrameworkDirectory" gives the reference packages path as a fallback.
    /// Step 2: Parse the JSON config file (under %LOCALAPPDATA%\Microsoft\Dynamics365\XPPConfig)
    ///         → "ModelStoreFolder" = custom metadata
    ///         → "ReferencePackagesPaths" = reference metadata folders
    /// </summary>
    internal static class DynamicsConfigurationReader
    {
        private const string RegistryKey = @"Software\Microsoft\Dynamics\AX7\Development\Configurations";

        /// <summary>When set (from --config), used instead of the registry's active configuration.</summary>
        public static string ConfigFileOverride { get; set; }

        public static DynamicsMetadataConfig ReadActiveConfiguration()
        {
            var result = new DynamicsMetadataConfig();

            try
            {
                // Step 1: Read the registry to find the active config JSON path.
                string configJsonPath = ConfigFileOverride;
                string registryFrameworkDir = null;

                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
                {
                    if (key == null && configJsonPath == null)
                        return result;

                    configJsonPath = configJsonPath ?? key?.GetValue("CurrentMetadataConfig") as string;
                    registryFrameworkDir = key?.GetValue("FrameworkDirectory") as string;
                }

                // Step 2: Parse the JSON config file.
                if (!string.IsNullOrWhiteSpace(configJsonPath) && File.Exists(configJsonPath))
                {
                    result.ConfigurationName = Path.GetFileNameWithoutExtension(configJsonPath);
                    result.ConfigurationFilePath = configJsonPath;

                    var json = File.ReadAllText(configJsonPath, Encoding.UTF8);
                    var parsed = DeserializeConfig(json);

                    if (parsed != null)
                    {
                        result.FrameworkDirectory = parsed.FrameworkDirectory?.Trim();
                        result.DefaultModelForNewProjects = parsed.DefaultModelForNewProjects?.Trim();
                        result.CrossReferencesDbServerName = parsed.CrossReferencesDbServerName?.Trim();
                        result.CrossReferencesDatabaseName = parsed.CrossReferencesDatabaseName?.Trim();
                        result.RuntimePackagesDirectory = parsed.RuntimePackagesDirectory?.Trim();
                        result.DatabaseServer = parsed.DatabaseServer?.Trim();
                        result.BusinessDatabaseName = parsed.BusinessDatabaseName?.Trim();
                        result.RuntimeHostType = parsed.RuntimeHostType;
                        result.CloudInstanceUrl = parsed.CloudInstanceURL?.Trim();
                        // Custom metadata folder
                        if (!string.IsNullOrWhiteSpace(parsed.ModelStoreFolder))
                            result.CustomMetadataFolder = parsed.ModelStoreFolder.Trim();

                        // Reference metadata folders
                        if (parsed.ReferencePackagesPaths != null)
                        {
                            result.ReferenceMetadataFolders.AddRange(
                                parsed.ReferencePackagesPaths
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .Select(s => s.Trim()));
                        }

                        // Fallback: FrameworkDirectory from JSON
                        if (result.ReferenceMetadataFolders.Count == 0
                            && !string.IsNullOrWhiteSpace(parsed.FrameworkDirectory))
                        {
                            result.ReferenceMetadataFolders.Add(parsed.FrameworkDirectory.Trim());
                        }
                    }
                }

                // Final fallback: FrameworkDirectory from registry
                if (result.ReferenceMetadataFolders.Count == 0
                    && !string.IsNullOrWhiteSpace(registryFrameworkDir))
                {
                    result.ReferenceMetadataFolders.Add(registryFrameworkDir.Trim());
                }
                if (string.IsNullOrWhiteSpace(result.FrameworkDirectory))
                    result.FrameworkDirectory = registryFrameworkDir?.Trim() ?? result.ReferenceMetadataFolders.FirstOrDefault();
            }
            catch
            {
                // Registry or file access failure — return empty config.
                // The user can still set paths manually via the Options page.
            }

            return result;
        }

        private static XppConfigJson DeserializeConfig(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(XppConfigJson),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(ms) as XppConfigJson;
            }
        }
    }

    /// <summary>
    /// Shape matching the D365FO JSON config files under XPPConfig.
    /// Uses DataContract so unknown properties are silently ignored.
    /// </summary>
    [DataContract]
    internal sealed class XppConfigJson
    {
        [DataMember(Name = "ModelStoreFolder", IsRequired = false)]
        public string ModelStoreFolder { get; set; }

        [DataMember(Name = "FrameworkDirectory", IsRequired = false)]
        public string FrameworkDirectory { get; set; }

        [DataMember(Name = "ReferencePackagesPaths", IsRequired = false)]
        public List<string> ReferencePackagesPaths { get; set; }

        [DataMember(Name = "CrossReferencesDbServerName", IsRequired = false)]
        public string CrossReferencesDbServerName { get; set; }

        [DataMember(Name = "CrossReferencesDatabaseName", IsRequired = false)]
        public string CrossReferencesDatabaseName { get; set; }

        [DataMember(Name = "DefaultModelForNewProjects", IsRequired = false)]
        public string DefaultModelForNewProjects { get; set; }

        [DataMember(Name = "RuntimePackagesDirectory", IsRequired = false)]
        public string RuntimePackagesDirectory { get; set; }

        [DataMember(Name = "DatabaseServer", IsRequired = false)]
        public string DatabaseServer { get; set; }

        [DataMember(Name = "BusinessDatabaseName", IsRequired = false)]
        public string BusinessDatabaseName { get; set; }

        [DataMember(Name = "RuntimeHostType", IsRequired = false)]
        public int? RuntimeHostType { get; set; }

        [DataMember(Name = "CloudInstanceURL", IsRequired = false)]
        public string CloudInstanceURL { get; set; }
    }

    internal sealed class DynamicsMetadataConfig
    {
        public string ConfigurationName { get; set; }
        public string ConfigurationFilePath { get; set; }
        public string CustomMetadataFolder { get; set; }
        public string FrameworkDirectory { get; set; }
        public string DefaultModelForNewProjects { get; set; }
        public string CrossReferencesDbServerName { get; set; }
        public string CrossReferencesDatabaseName { get; set; }
        public string RuntimePackagesDirectory { get; set; }
        public string DatabaseServer { get; set; }
        public string BusinessDatabaseName { get; set; }
        public int? RuntimeHostType { get; set; }
        public string CloudInstanceUrl { get; set; }
        // Unified developer environments (cloud runtime, local metadata only) use host type 4.
        public bool IsUnifiedDeveloperEnvironment => RuntimeHostType == 4 || !string.IsNullOrEmpty(RuntimePackagesDirectory);
        public List<string> ReferenceMetadataFolders { get; } = new List<string>();

        public bool HasCustom => !string.IsNullOrWhiteSpace(CustomMetadataFolder);
        public bool HasReference => ReferenceMetadataFolders.Count > 0;

        /// <summary>
        /// Cloud-hosted and VHD environments keep every package in PackagesLocalDirectory, which is then both the custom
        /// and the reference folder; the user's own packages are told apart by their descriptors.
        /// </summary>
        public bool SharedStore => HasCustom && ReferenceMetadataFolders.Contains(CustomMetadataFolder, StringComparer.OrdinalIgnoreCase);

        /// <summary>The custom folder, then the reference folders, each once (a shared store is both).</summary>
        public IEnumerable<string> MetadataRoots() =>
            (HasCustom ? new[] { CustomMetadataFolder } : new string[0]).Concat(ReferenceMetadataFolders)
                .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Package folders of the user's own models.</summary>
        public IEnumerable<string> CustomPackageFolders()
        {
            if (!HasCustom || !Directory.Exists(CustomMetadataFolder)) return Enumerable.Empty<string>();
            return Directory.GetDirectories(CustomMetadataFolder)
                .Where(p => Directory.Exists(Path.Combine(p, "Descriptor")) && (!SharedStore || !IsStandardPackage(p)));
        }

        /// <summary>True when the file or folder belongs to one of the user's own packages.</summary>
        public bool IsCustomPath(string path)
        {
            if (!HasCustom) return false;
            string root = Path.GetFullPath(CustomMetadataFolder).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (!SharedStore) return true;
            string package = full.Substring(root.Length).Split('\\')[0];
            return package.Length > 0 && !IsStandardPackage(Path.Combine(root, package));
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> StandardPackages =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Not the user's when every model in it is published by Microsoft or sealed (Customization=DoNotAllow, as ISVs ship).
        private static bool IsStandardPackage(string packageFolder)
        {
            string key = Path.GetFullPath(packageFolder).TrimEnd('\\');
            if (StandardPackages.TryGetValue(key, out bool known)) return known;
            var models = new List<System.Xml.Linq.XElement>();
            try
            {
                foreach (string descriptor in Directory.EnumerateFiles(Path.Combine(key, "Descriptor"), "*.xml"))
                    models.Add(System.Xml.Linq.XDocument.Load(descriptor).Root);
            }
            catch
            {
                // Unreadable now (e.g. being written): treated as standard, so nothing is edited, and asked again next time.
                return true;
            }
            bool standard = models.Count > 0 && models.All(m =>
                DescriptorValue(m, "Publisher").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(DescriptorValue(m, "Customization"), "DoNotAllow", StringComparison.OrdinalIgnoreCase));
            return StandardPackages[key] = standard;
        }

        private static string DescriptorValue(System.Xml.Linq.XElement model, string name) =>
            model?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
    }
}
