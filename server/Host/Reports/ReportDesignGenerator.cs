using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace XppVsCode.Server.Host.Reports
{
    /// <summary>
    /// Generates the RDL of a report design with Microsoft's report generator (the one reportsc runs), in Designer mode: what
    /// Visual Studio's "Create Precision Design" imports from an auto design. The generator assembly is loaded from the
    /// user's PackagesLocalDirectory\bin, never shipped.
    /// </summary>
    internal static class ReportDesignGenerator
    {
        private const string BuildAssembly = "Microsoft.Dynamics.Framework.Tools.Reports.Build.dll";
        private const string Namespace = "Microsoft.Dynamics.Framework.Tools.Reports.Build.";

        public static string GenerateRdl(DynamicsMetadataConfig config, IMetadataProvider provider, AxReport report, AxReportDesign design)
        {
            string path = Path.Combine(config.FrameworkDirectory, "bin", BuildAssembly);
            if (!File.Exists(path))
                throw new FileNotFoundException("The report generator (" + BuildAssembly + ") is not in this environment's bin folder.", path);
            var assembly = Assembly.LoadFrom(path);
            Type contextType = assembly.GetType(Namespace + "ReportGeneratorContext", true);
            Type generatorType = assembly.GetType(Namespace + "ReportGenerator", true);

            object context = Activator.CreateInstance(contextType);
            contextType.GetProperty("MetadataProvider").SetValue(context, provider);
            var mode = contextType.GetProperty("GenerationMode");
            mode.SetValue(context, Enum.Parse(mode.PropertyType, "Designer"));
            contextType.GetProperty("ReportModelInfo").SetValue(context, provider.Reports.GetModelInfo(report.Name).First());
            // As Visual Studio: the runtime packages folder, then the reference packages.
            var folders = new[] { config.RuntimePackagesDirectory, config.FrameworkDirectory }.Concat(config.ReferenceMetadataFolders)
                .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            contextType.GetProperty("ReferencedAssembliesFolders").SetValue(context, folders);

            object generator = Activator.CreateInstance(generatorType);
            using (var stream = new MemoryStream())
            {
                try
                {
                    generatorType.GetMethod("GenerateRdlForDesign").Invoke(generator, new[] { context, (object)design, report, stream });
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
                }
                return Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');
            }
        }
    }
}
