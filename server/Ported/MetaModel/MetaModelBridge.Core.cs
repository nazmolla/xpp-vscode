using System.Collections.Generic;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Microsoft.Dynamics.AX.Metadata.Service;
using XppVsCode.Server.Host;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>What the metadata services share with the bridge: the metadata API, ownership, the active project, caches.</summary>
    internal interface IMetadataCore
    {
        IMetaModelService MetaService { get; }
        IMetadataProvider MetadataProvider { get; }
        bool IsCustomModel(string modelName);
        string ObjectModelName(string objectType, string objectName);
        XppProject ActiveProject { get; }
        Dictionary<string, AxTableField> EffectiveTableFields(string tableName);
        /// <summary>Label text of "@File:Id" (en-US); other values unchanged.</summary>
        string ResolveLabel(string value);
        /// <summary>Reloads the metadata API after models changed on disk.</summary>
        void ResetHost();
        void InvalidateCaches();
    }

    public sealed partial class MetaModelBridge : IMetadataCore
    {
        internal static string CustomFolder => DynamicsConfigurationReader.ReadActiveConfiguration().CustomMetadataFolder;

        IMetaModelService IMetadataCore.MetaService => MetaService;
        IMetadataProvider IMetadataCore.MetadataProvider => MetadataProvider;
        bool IMetadataCore.IsCustomModel(string modelName) => IsCustomModel(modelName);
        string IMetadataCore.ObjectModelName(string objectType, string objectName) => GetObjectModelName(objectType, objectName);
        XppProject IMetadataCore.ActiveProject => GetActiveProject();
        Dictionary<string, AxTableField> IMetadataCore.EffectiveTableFields(string tableName) => GetEffectiveTableFields(tableName);
        string IMetadataCore.ResolveLabel(string value) => ResolveLabel(value);
        void IMetadataCore.ResetHost() => _host.Reset();
        void IMetadataCore.InvalidateCaches() => InvalidateCaches();
    }
}
