using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Reporting.WinForms;

namespace XppVsCode.Server.Host.Reports
{
    [Serializable]
    internal sealed class RenderedReport
    {
        public List<byte[]> Pages = new List<byte[]>();
        public int TotalPages;
        public List<string> Warnings = new List<string>();
        public string PdfPath;
    }

    /// <summary>
    /// Renders a prepared report with Microsoft's report processing engine (hard page breaks, as PDF and print do). The engine
    /// compiles report expressions in a sandbox domain that probes the creating domain's base folder, so rendering runs in a
    /// domain whose base folder is the verified runtime folder (nothing is copied next to the server).
    /// </summary>
    internal static class ReportRenderer
    {
        private static readonly object Gate = new object();
        private static AppDomain _domain;
        private static int _renders;
        // The engine leaves its expression domains behind; a fresh render domain now and then keeps memory bounded.
        private const int RendersPerDomain = 50;

        public static RenderedReport Render(RdlPreview.Prepared prepared, int firstPage, int maxPages, int dpi, string pdfPath)
        {
            lock (Gate)
            {
                if (_domain != null && _renders >= RendersPerDomain)
                {
                    var old = _domain;
                    _domain = null;
                    // An engine thread that doesn't stop makes the unload fail; that domain is abandoned and a new one used.
                    try { AppDomain.Unload(old); }
                    catch (CannotUnloadAppDomainException ex) { Log.Write("Report render domain could not be unloaded: " + ex.Message); }
                }
                if (_domain == null)
                {
                    _domain = AppDomain.CreateDomain("FOStudioReports", null, new AppDomainSetup
                    {
                        ApplicationBase = ReportRuntime.Folder,
                        ApplicationName = "FOStudioReports",
                        DisallowCodeDownload = true,
                    });
                    _renders = 0;
                }
                _renders++;
                var worker = (RenderWorker)_domain.CreateInstanceFromAndUnwrap(typeof(RenderWorker).Assembly.Location, typeof(RenderWorker).FullName);
                return worker.Render(prepared.Rdl, prepared.Data, prepared.Parameters, firstPage, maxPages, dpi, pdfPath);
            }
        }

        /// <summary>The first error of a failed render, without the engine's stack of wrapped exceptions.</summary>
        public static string Describe(Exception ex)
        {
            var messages = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
                if (!string.IsNullOrWhiteSpace(e.Message) && !messages.Contains(e.Message)) messages.Add(e.Message);
            return string.Join(" ", messages);
        }
    }

    /// <summary>Runs in the render domain; only Report Viewer and framework types are used here.</summary>
    internal sealed class RenderWorker : MarshalByRefObject
    {
        public override object InitializeLifetimeService() => null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public RenderedReport Render(string rdl, Dictionary<string, DataTable> data, Dictionary<string, string> parameters, int firstPage, int maxPages, int dpi, string pdfPath)
        {
            var result = new RenderedReport();
            var report = new LocalReport { EnableExternalImages = false, EnableHyperlinks = false };
            try
            {
                using (var reader = new StringReader(rdl))
                    report.LoadReportDefinition(reader);
                foreach (var table in data)
                    report.DataSources.Add(new ReportDataSource(table.Key, table.Value));
                var known = new HashSet<string>(report.GetParameters().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                report.SetParameters(parameters.Where(p => known.Contains(p.Key))
                    .Select(p => new ReportParameter(p.Key, string.IsNullOrEmpty(p.Value) ? new string[] { null } : new[] { p.Value })).ToList());

                Warning[] warnings;
                string mime, encoding, extension;
                string[] streams;
                if (!string.IsNullOrEmpty(pdfPath))
                {
                    File.WriteAllBytes(pdfPath, report.Render("PDF", null, out mime, out encoding, out extension, out streams, out warnings));
                    result.PdfPath = pdfPath;
                    Collect(warnings, result.Warnings);
                }
                int page = Math.Max(1, firstPage);
                for (int i = 0; i < Math.Max(1, maxPages); i++, page++)
                {
                    string deviceInfo = string.Format(CultureInfo.InvariantCulture,
                        "<DeviceInfo><OutputFormat>PNG</OutputFormat><DpiX>{0}</DpiX><DpiY>{0}</DpiY><StartPage>{1}</StartPage><EndPage>{1}</EndPage></DeviceInfo>", dpi, page);
                    byte[] png = report.Render("IMAGE", deviceInfo, out mime, out encoding, out extension, out streams, out warnings);
                    if (i == 0) Collect(warnings, result.Warnings);
                    if (png == null || png.Length == 0) break;
                    result.Pages.Add(png);
                    // Known after a render; 0 while the engine is still estimating.
                    int total = report.GetTotalPages(out PageCountMode _);
                    result.TotalPages = Math.Max(total, page);
                    if (total > 0 && page >= total) break;
                }
                return result;
            }
            catch (Exception ex)
            {
                // Engine exceptions may not cross domains; their messages do.
                throw new InvalidOperationException(ReportRenderer.Describe(ex));
            }
            finally
            {
                report.ReleaseSandboxAppDomain();
            }
        }

        private static void Collect(Warning[] warnings, List<string> into)
        {
            if (warnings == null) return;
            foreach (var w in warnings)
            {
                string text = w.Code + ": " + w.Message;
                if (!into.Contains(text)) into.Add(text);
            }
        }
    }
}
