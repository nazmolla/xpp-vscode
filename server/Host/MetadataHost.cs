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
        private readonly object _gate = new object();
        private volatile IMetadataProvider _provider;
        private volatile IMetaModelService _metaService;

        public MetadataHost(DynamicsMetadataConfig config)
        {
            _config = config;
        }

        public DynamicsMetadataConfig Config => _config;

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
            lock (_gate)
            {
                _metaService = null;
                _provider = null;
            }
        }

        // Background work (the form extension validator warm-up) can ask for metadata while a request loads it: loading
        // twice at once fails inside the disk provider's model descriptor cache ("An item with the same key has already
        // been added"), so one thread loads and the pair is published only when complete.
        private void EnsureLoaded()
        {
            if (_metaService != null) return;
            lock (_gate)
            {
                if (_metaService != null) return;

                var disk = new DiskProviderConfiguration();
                foreach (string path in _config.ReferenceMetadataFolders.Concat(new[] { _config.CustomMetadataFolder })
                             .Where(p => !string.IsNullOrEmpty(p))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    disk.AddMetadataPath(path);
                }
                // New packages (Create model) go to the custom metadata folder, as in Visual Studio.
                if (!string.IsNullOrEmpty(_config.CustomMetadataFolder))
                    disk.MetadataPath = _config.CustomMetadataFolder;

                var provider = new MetadataProviderFactory().CreateDiskProvider(disk);
                var metaService = new MetaModelServiceFactory().Create(provider);
                _provider = provider;
                _metaService = metaService;
            }
        }
    }
}
