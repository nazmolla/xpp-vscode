using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace XppVsCode.Server.Host.Reports
{
    /// <summary>
    /// Microsoft's report processing engine (the Report Viewer runtime, NuGet Microsoft.ReportingServices.ReportViewerControl.Winforms)
    /// is not shipped with F&O Studio: the extension downloads it after the user accepts Microsoft's licence (src/reports/reportRuntime.ts).
    /// It is loaded only when every file matches the hash pinned here, so nothing else placed in that folder is ever loaded.
    /// </summary>
    internal static class ReportRuntime
    {
        public const string Version = "150.1652.0";

        // SHA-256 of lib/net40 of the pinned packages (keep in step with src/reports/reportRuntime.ts).
        private static readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.ReportViewer.Common.dll"] = "8BF52BD4CE91B38CB594253BC41C7029A0125AA7537A0553637DE5C09906CA18",
            ["Microsoft.ReportViewer.Common.resources.dll"] = "DECFEEB53F81550CD80E43B5A99FDCE1FCB8C195FE752E81B829648D405FD608",
            ["Microsoft.ReportViewer.DataVisualization.dll"] = "23A342D494C13275CA646DE14F9F2BD350C7BC5477B6F17CDF3B0F69EB58DAC6",
            ["Microsoft.ReportViewer.DataVisualization.resources.dll"] = "17AFD0731EDF134AB7CA888D54834135DACB36A1A9F3F352F383C7C31439A9F8",
            ["Microsoft.ReportViewer.ProcessingObjectModel.dll"] = "D27D058701D989930249647D249F62FE5AEB65CDBE5E14B2594839E931756A0E",
            ["Microsoft.ReportViewer.WinForms.dll"] = "1B927C2D312E864EA73AD50711E5F6C2F58113869597E3ABF33D3B38DF5F23EA",
            ["Microsoft.ReportViewer.WinForms.resources.dll"] = "4EE0941CEBDDE8BAC38AC8463EA78220A3E1C7086CB943406C1297BC38B43BAE",
            ["Microsoft.SqlServer.Types.dll"] = "30108FF222575A0FEC8DEC65BC92D1B0773E1429ABEF918D969DFFEB7B5BB2CA",
        };

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FOStudio", "ReportViewer", Version);

        private static readonly object Gate = new object();
        private static bool _verified;
        private static bool _resolverAdded;

        /// <summary>True when the runtime is installed and intact; otherwise problem says what to do.</summary>
        public static bool IsAvailable(out string problem)
        {
            lock (Gate)
            {
                problem = null;
                if (_verified) return true;
                if (!Directory.Exists(Folder))
                {
                    problem = "The report preview needs Microsoft's Report Viewer runtime, which is not installed. In VS Code run 'F&O Studio: Install Report Preview Runtime' (it downloads Microsoft's package after you accept its licence).";
                    return false;
                }
                foreach (var file in Files)
                {
                    string path = Path.Combine(Folder, file.Key);
                    if (!File.Exists(path) || !string.Equals(Sha256(path), file.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        problem = "The Report Viewer runtime in " + Folder + " is incomplete or was changed (" + file.Key + "). Run 'F&O Studio: Install Report Preview Runtime' again.";
                        return false;
                    }
                }
                if (!_resolverAdded)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                    _resolverAdded = true;
                }
                _verified = true;
                return true;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            if (!Files.ContainsKey(name)) return null;
            string path = Path.Combine(Folder, name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }
}
