using System;
using System.Linq;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Microsoft.Dynamics.AX.Metadata.Service;
using Microsoft.Dynamics.AX.Metadata.Storage;
using Microsoft.Dynamics.AX.Metadata.Storage.DiskProvider;

namespace XppVsCode.Server.Host
{
    /// <summary>Headless replacement for the metadata services Visual Studio hosts.</summary>
    internal sealed class MetadataHost
    {
        private readonly DynamicsMetadataConfig _config;
        private IMetadataProvider _provider;
        private IMetaModelService _metaService;

        public MetadataHost(DynamicsMetadataConfig config)
        {
            _config = config;
        }

        public IMetadataProvider Provider
        {
            get { EnsureLoaded(); return _provider; }
        }

        public IMetaModelService MetaService
        {
            get { EnsureLoaded(); return _metaService; }
        }

        /// <summary>Drops cached metadata so the next access sees files changed outside this process.</summary>
        public void Reset()
        {
            _provider = null;
            _metaService = null;
        }

        private void EnsureLoaded()
        {
            if (_metaService != null) return;

            var disk = new DiskProviderConfiguration();
            foreach (string path in _config.ReferenceMetadataFolders.Concat(new[] { _config.CustomMetadataFolder })
                         .Where(p => !string.IsNullOrEmpty(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                disk.AddMetadataPath(path);
            }

            _provider = new MetadataProviderFactory().CreateDiskProvider(disk);
            _metaService = new MetaModelServiceFactory().Create(_provider);
        }
    }
}
