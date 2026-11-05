using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace XppVsCode.Server.Tests
{
    internal static class D365Assemblies
    {
        // The server resolves D365FO assemblies from PackagesLocalDirectory\bin at run time (Program.Main); tests do the same.
        [ModuleInitializer]
        internal static void Register()
        {
            string bin = Environment.GetEnvironmentVariable("XPP_BIN");
            if (string.IsNullOrEmpty(bin))
            {
                string framework = DynamicsConfigurationReader.ReadActiveConfiguration().FrameworkDirectory;
                bin = string.IsNullOrEmpty(framework) ? null : Path.Combine(framework, "bin");
            }
            if (bin == null) return;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string path = Path.Combine(bin, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }
    }
}
