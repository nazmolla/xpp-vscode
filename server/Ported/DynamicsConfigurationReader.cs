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
        public List<string> ReferenceMetadataFolders { get; } = new List<string>();

        public bool HasCustom => !string.IsNullOrWhiteSpace(CustomMetadataFolder);
        public bool HasReference => ReferenceMetadataFolders.Count > 0;
    }
}
