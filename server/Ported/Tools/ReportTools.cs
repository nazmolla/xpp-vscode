using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using XppVsCode.Server.Host.Reports;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Agent tools for SSRS precision designs: read and save the design (RDL), render it with Microsoft's report engine, and
    /// measure scanned forms and compare a rendered page with a scan, so an agent can place every field where a preprinted
    /// form needs it.
    /// </summary>
    internal sealed class ReportTools
    {
        private readonly BridgeClient _bridge;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private const int MaxAgentPages = 4;
        private const int MaxRdlChars = 400000;

        public ReportTools(BridgeClient bridge)
        {
            _bridge = bridge;
        }

        private static Dictionary<string, object> Args(string json) =>
            Json.Deserialize<Dictionary<string, object>>(JsonHelpers.ExtractToolArgumentsObject(json) ?? "{}") ?? new Dictionary<string, object>();

        private static string Str(Dictionary<string, object> a, string key) =>
            a.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        private static double? Num(Dictionary<string, object> a, string key) =>
            double.TryParse(Str(a, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private Dictionary<string, object> Call(string action, object body) =>
            Json.Deserialize<Dictionary<string, object>>(_bridge.Call(action, body is string s ? s : Json.Serialize(body)));

        private static bool Ok(Dictionary<string, object> result) => result.TryGetValue("success", out object ok) && ok is bool b && b;

        private static string Message(Dictionary<string, object> result) => result.TryGetValue("message", out object m) ? m as string : "Failed.";

        // ── xpp_get_report_design ──

        public string HandleGetDesign(string id, string json)
        {
            var a = Args(json);
            if (!_bridge.IsAvailable()) return JsonHelpers.BuildToolResult(id, "MetaModel bridge is not available.", isError: true);
            var info = Call("report_info", new { reportName = Str(a, "reportName") });
            if (!Ok(info)) return JsonHelpers.BuildToolResult(id, Message(info), isError: true);
            var sb = new StringBuilder();
            sb.Append("Report ").Append(info["name"]).Append(" (model ").Append(info["model"]).Append(Convert.ToBoolean(info["isCustom"]) ? ", yours" : ", standard: copy it into your model before changing it").AppendLine(")");
            sb.Append("Designs: ").AppendLine(string.Join(", ", ((ArrayList)info["designs"]).Cast<Dictionary<string, object>>().Select(d => d["name"] + " (" + d["kind"] + ")")));
            foreach (Dictionary<string, object> ds in (ArrayList)info["dataSets"])
                sb.Append("Dataset ").Append(ds["name"]).Append(" [").Append(ds["dataSourceType"]).Append("] ").Append(ds["query"]).Append(": ")
                  .AppendLine(string.Join(", ", ((ArrayList)ds["fields"]).Cast<Dictionary<string, object>>().Select(f => f["name"] + " " + ((string)f["type"]).Replace("System.", ""))));
            var parameters = ((ArrayList)info["parameters"]).Cast<Dictionary<string, object>>().Where(p => !((string)p["name"]).StartsWith("AX_", StringComparison.Ordinal)).ToList();
            if (parameters.Count > 0)
                sb.Append("Parameters: ").AppendLine(string.Join(", ", parameters.Select(p => p["name"] + " " + ((string)p["type"] ?? "").Replace("System.", "") + (Convert.ToBoolean(p["visible"]) ? "" : " (hidden)"))));

            string designName = Str(a, "designName");
            if (string.IsNullOrEmpty(designName))
                designName = ((ArrayList)info["designs"]).Cast<Dictionary<string, object>>().Select(d => (string)d["name"]).FirstOrDefault();
            var design = Call("get_report_design", new { reportName = Str(a, "reportName"), designName });
            if (!Ok(design)) return JsonHelpers.BuildToolResult(id, sb + "\n" + Message(design));
            AppendLayout(sb, design);
            string rdl = design["rdl"] as string ?? "";
            if (!"false".Equals(Str(a, "includeRdl"), StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine().Append("RDL of ").Append(designName).Append(" (").Append(design.TryGetValue("namespace", out object ns) ? ns : "").AppendLine("); save changes with xpp_set_report_design:");
                sb.AppendLine(rdl.Length > MaxRdlChars ? rdl.Substring(0, MaxRdlChars) + "\n... truncated (" + rdl.Length + " chars)" : rdl);
            }
            return JsonHelpers.BuildToolResult(id, sb.ToString());
        }

        private static void AppendLayout(StringBuilder sb, Dictionary<string, object> design)
        {
            if (design.TryGetValue("page", out object p) && p is Dictionary<string, object> page)
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "Page {0} x {1} mm, margins L{2} R{3} T{4} B{5}, body {6} x {7} mm, header {8} mm, footer {9} mm.\n",
                    page["width"], page["height"], page["leftMargin"], page["rightMargin"], page["topMargin"], page["bottomMargin"],
                    page["bodyWidth"], page["bodyHeight"], page["headerHeight"], page["footerHeight"]);
            if (design.TryGetValue("warnings", out object w) && w is ArrayList warnings && warnings.Count > 0)
                foreach (var warning in warnings) sb.Append("WARNING: ").AppendLine(Convert.ToString(warning, CultureInfo.InvariantCulture));
            if (design.TryGetValue("items", out object i) && i is ArrayList items)
            {
                sb.AppendLine("Items (page position in mm from the sheet's top-left, as designed; grow = CanGrow):");
                foreach (Dictionary<string, object> item in items.Cast<Dictionary<string, object>>().Take(400))
                    sb.Append("- ").Append(item["type"]).Append(' ').Append(item["name"]).Append(" [").Append(item["section"])
                      .Append(string.IsNullOrEmpty(item["container"] as string) ? "" : "/" + item["container"]).Append("] x=").Append(item["x"]).Append(" y=").Append(item["y"])
                      .Append(" w=").Append(item["width"]).Append(" h=").Append(item["height"])
                      .Append(item["font"] != null ? " font=" + item["font"] + (item["fontSize"] != null ? " " + Convert.ToString(item["fontSize"], CultureInfo.InvariantCulture) + "pt" : "") : "")
                      .Append(Convert.ToBoolean(item["canGrow"]) ? " grow" : "").Append(Convert.ToBoolean(item["conditionallyHidden"]) ? " hidden-if" : "")
                      .Append(item["value"] is string v && v.Length > 0 ? " value: " + v : "").AppendLine();
                if (items.Count > 400) sb.Append("... ").Append(items.Count - 400).AppendLine(" more items.");
            }
        }

        // ── xpp_set_report_design ──

        public string HandleSetDesign(string id, string json)
        {
            var a = Args(json);
            if (!_bridge.IsAvailable()) return JsonHelpers.BuildToolResult(id, "MetaModel bridge is not available.", isError: true);
            var sb = new StringBuilder();
            string duplicateOf = Str(a, "duplicateOf");
            if (!string.IsNullOrWhiteSpace(duplicateOf))
            {
                if (string.IsNullOrWhiteSpace(Str(a, "modelName")))
                    return JsonHelpers.BuildToolResult(id, "Pass modelName: your model for the copy of " + duplicateOf + ".", isError: true);
                // Microsoft's extension pattern for a standard report's layout: a copy in your model, with the standard data provider.
                var copy = Call("duplicate_object", new { objectType = "AxReport", objectName = duplicateOf, newName = Str(a, "reportName"), modelName = Str(a, "modelName") });
                if (!Ok(copy)) return JsonHelpers.BuildToolResult(id, Message(copy), isError: true);
                sb.AppendLine(Message(copy)).AppendLine("Add it to the project with xpp_add_to_project, and route to it: print management (PrintMgmtDocType.getDefaultReportFormatDelegate, PrintMgmtReportFormatPopulator.addDocuments) or the controller (parmReportName).");
                if (string.IsNullOrEmpty(Str(a, "rdl")) && string.IsNullOrEmpty(Str(a, "fromAutoDesign"))) return JsonHelpers.BuildToolResult(id, sb.ToString());
            }
            string fromAuto = Str(a, "fromAutoDesign");
            if (!string.IsNullOrWhiteSpace(fromAuto))
            {
                var generated = Call("create_precision_design", new { objectType = "AxReport", objectName = Str(a, "reportName"), designName = fromAuto, newDesignName = Str(a, "designName") });
                if (!Ok(generated)) return JsonHelpers.BuildToolResult(id, sb + Message(generated), isError: true);
                sb.AppendLine(Message(generated))
                  .Append("Next: xpp_get_report_design on ").Append(generated.TryGetValue("design", out object d) ? d : Str(a, "designName"))
                  .Append(" to read the generated layout, adjust it with xpp_set_report_design, and check it with xpp_render_report.");
                return JsonHelpers.BuildToolResult(id, sb.ToString());
            }
            if (string.IsNullOrEmpty(Str(a, "rdl")) || string.IsNullOrEmpty(Str(a, "designName")))
                return JsonHelpers.BuildToolResult(id, "Pass designName and rdl (or fromAutoDesign to generate it from an auto design, or duplicateOf to copy a report).", isError: true);
            var result = Call("set_report_design", new { objectType = "AxReport", objectName = Str(a, "reportName"), designName = Str(a, "designName"), rdl = Str(a, "rdl") });
            if (!Ok(result))
                return JsonHelpers.BuildToolResult(id, sb + Message(result) + (Message(result).Contains("can't change")
                    ? " Reports have no extensions: copy it into your model with duplicateOf (the standard report), reportName (the copy) and modelName, then change the copy." : ""), isError: true);
            sb.AppendLine(Message(result));
            if (result.TryGetValue("warnings", out object w) && w is ArrayList warnings)
                foreach (var warning in warnings) sb.Append("WARNING: ").AppendLine(Convert.ToString(warning, CultureInfo.InvariantCulture));
            sb.Append("Next: xpp_render_report to check the layout; xpp_build compiles the report (reportsc) into the package for deployment.");
            return JsonHelpers.BuildToolResult(id, sb.ToString());
        }

        // ── xpp_render_report ──

        public string HandleRender(string id, string json)
        {
            var a = Args(json);
            if (!_bridge.IsAvailable()) return JsonHelpers.BuildToolResult(id, "MetaModel bridge is not available.", isError: true);
            int maxPages = (int)Math.Max(1, Math.Min(MaxAgentPages, Num(a, "maxPages") ?? 1));
            a["maxPages"] = maxPages;
            if (!a.ContainsKey("dpi")) a["dpi"] = 96;
            var result = Call("render_report", a);
            if (!Ok(result)) return JsonHelpers.BuildToolResult(id, Message(result), isError: true);
            var sb = new StringBuilder();
            var pages = ((ArrayList)result["pages"]).Cast<string>().Select(Convert.FromBase64String).ToList();
            sb.Append(result["report"]).Append('.').Append(result["design"]).Append(": pages ").Append(result["firstPage"]).Append('-')
              .Append(Convert.ToInt32(result["firstPage"]) + pages.Count - 1).Append(" of ").Append(result["totalPages"])
              .Append(" at ").Append(result["dpi"]).AppendLine(" dpi, rendered by Microsoft's report engine as PDF and print render (hard page breaks).");
            foreach (Dictionary<string, object> ds in (ArrayList)result["dataSets"])
                sb.Append("Data ").Append(ds["name"]).Append(": ").Append(ds["rows"]).AppendLine(" row(s) (pass data to use your own values).");
            if (result["pdfPath"] is string pdf && pdf.Length > 0) sb.Append("PDF: ").AppendLine(pdf);
            if (!"false".Equals(Str(a, "includeItems"), StringComparison.OrdinalIgnoreCase))
                AppendLayout(sb, result);
            else if (result.TryGetValue("warnings", out object w) && w is ArrayList warnings)
                foreach (var warning in warnings) sb.Append("WARNING: ").AppendLine(Convert.ToString(warning, CultureInfo.InvariantCulture));
            return JsonHelpers.BuildToolResultWithImages(id, sb.ToString(), pages);
        }

        // ── xpp_measure_image ──

        private static MeasureOptions Calibration(Dictionary<string, object> a, string dpiKey = "dpi")
        {
            var options = new MeasureOptions
            {
                Paper = Str(a, "paper"),
                Dpi = Num(a, dpiKey),
                Deskew = !"false".Equals(Str(a, "deskew"), StringComparison.OrdinalIgnoreCase),
            };
            if (a.TryGetValue("referencePoints", out object r) && r is Dictionary<string, object> reference)
            {
                double Get(string k) => reference.TryGetValue(k, out object v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN;
                options.ReferencePx = new[] { Get("x1"), Get("y1"), Get("x2"), Get("y2") };
                options.ReferenceMm = Get("distanceMm");
                if (options.ReferencePx.Any(double.IsNaN) || double.IsNaN(options.ReferenceMm) || options.ReferenceMm <= 0)
                    throw new ArgumentException("referencePoints needs x1, y1, x2, y2 (image pixels) and distanceMm.");
            }
            return options;
        }

        private static double[] Rect(Dictionary<string, object> a, string key)
        {
            if (!(a.TryGetValue(key, out object c) && c is Dictionary<string, object> crop)) return null;
            double Get(string k) => crop.TryGetValue(k, out object v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
            return new[] { Get("x"), Get("y"), Get("width"), Get("height") };
        }

        public string HandleMeasure(string id, string json)
        {
            var a = Args(json);
            try
            {
                using (var image = ImageMeasure.Load(Str(a, "imagePath"), Calibration(a)))
                {
                    var sb = new StringBuilder();
                    sb.AppendFormat(CultureInfo.InvariantCulture, "Image {0} x {1} px = {2:0.##} x {3:0.##} mm ({4}; {5:0.###} px/mm across, {6:0.###} down). Coordinates: mm from the top-left corner of the {7}.\n",
                        image.Image.Width, image.Image.Height, image.WidthMm, image.HeightMm, image.Calibration, image.PxPerMmX, image.PxPerMmY,
                        image.Calibration.StartsWith("whole sheet", StringComparison.Ordinal) ? "sheet" : "image");
                    foreach (string note in image.Notes) sb.Append("NOTE: ").AppendLine(note);
                    double[] crop = Rect(a, "crop");
                    bool inCrop(double x, double y) => crop == null || x >= crop[0] && x <= crop[0] + crop[2] && y >= crop[1] && y <= crop[1] + crop[3];

                    var lines = ImageMeasure.Lines(image, Num(a, "minLineMm") ?? 10).Where(l => inCrop(l.X1, l.Y1)).ToList();
                    var boxes = ImageMeasure.Boxes(image, lines, 1.5);
                    sb.Append("Boxes (").Append(boxes.Count).AppendLine("): x, y, width, height");
                    foreach (var b in boxes.Take(150)) sb.Append("- ").Append(F(b.X)).Append(", ").Append(F(b.Y)).Append(", ").Append(F(b.Width)).Append(", ").AppendLine(F(b.Height));
                    sb.Append("Rules (").Append(lines.Count).AppendLine("): from - to, thickness");
                    foreach (var l in lines.OrderBy(l => l.Horizontal ? 0 : 1).ThenBy(l => l.Y1).ThenBy(l => l.X1).Take(250))
                        sb.Append(l.Horizontal ? "- horizontal y=" + F(l.Y1) + " x " + F(l.X1) + " - " + F(l.X2) : "- vertical x=" + F(l.X1) + " y " + F(l.Y1) + " - " + F(l.Y2))
                          .Append(" (").Append(F(l.ThicknessMm)).AppendLine(" mm)");

                    if (!"false".Equals(Str(a, "ocr"), StringComparison.OrdinalIgnoreCase))
                    {
                        var words = ImageMeasure.Ocr(image.Image, image.PxPerMmX, image.PxPerMmY, Str(a, "language"), out string problem);
                        if (words == null) sb.Append("Text: ").AppendLine(problem);
                        else
                        {
                            var shown = words.Where(w => inCrop(w.X, w.Y)).ToList();
                            sb.Append("Text lines (").Append(shown.Select(w => w.Line).Distinct().Count())
                              .AppendLine("): left, top, right, bottom of the line's words, height (cap to descender, about the font size in mm), text; then each word's x");
                            foreach (var line in shown.GroupBy(w => w.Line).Take(300))
                            {
                                double left = line.Min(w => w.X), top = line.Min(w => w.Y), right = line.Max(w => w.X + w.Width), bottom = line.Max(w => w.Y + w.Height);
                                string text = string.Join(" ", line.Select(w => w.Text));
                                sb.Append("- ").Append(F(left)).Append(", ").Append(F(top)).Append(", ").Append(F(right)).Append(", ").Append(F(bottom))
                                  .Append(" h=").Append(F(bottom - top)).Append(" (~").Append(EstimatedPoints(text, bottom - top).ToString("0.#", CultureInfo.InvariantCulture)).Append("pt): ")
                                  .Append(text).Append("  [")
                                  .Append(string.Join(" ", line.Select(w => F(w.X)))).AppendLine("]");
                            }
                        }
                    }
                    byte[] grid = ImageMeasure.GridImage(image, crop, (int)Math.Max(600, Math.Min(2000, Num(a, "imageWidth") ?? 1400)), Num(a, "gridMm") ?? 0);
                    sb.Append("The picture shows the ").Append(crop == null ? "whole image" : "cropped area").AppendLine(" with rulers in mm (red lines every 10 or 50 mm, blue every grid step). Pass crop {x, y, width, height} in mm to zoom in for finer readings.");
                    return JsonHelpers.BuildToolResultWithImages(id, sb.ToString(), new[] { grid });
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException || ex is InvalidOperationException)
            {
                return JsonHelpers.BuildToolResult(id, ex is OutOfMemoryException ? "The image could not be read (corrupt or unsupported format)." : ex.Message, isError: true);
            }
        }

        // ── xpp_compare_layout ──

        /// <summary>
        /// Font size from the height of a line's ink: capitals and ascenders reach about 0.71 em above the baseline, descenders
        /// about 0.22 em below, lowercase without ascenders about 0.52 em (typical of the sans serif fonts reports use).
        /// </summary>
        internal static double EstimatedPoints(string text, double inkHeightMm)
        {
            bool tall = text.Any(c => char.IsUpper(c) || char.IsDigit(c) || "bdfhklt/()[]{}|!?%#&$@".IndexOf(c) >= 0);
            bool descends = text.Any(c => "gjpqy,;()[]{}|".IndexOf(c) >= 0);
            double em = (tall ? 0.71 : 0.52) + (descends ? 0.22 : 0);
            return inkHeightMm / em / 25.4 * 72;
        }

        public string HandleCompare(string id, string json)
        {
            var a = Args(json);
            if (!_bridge.IsAvailable()) return JsonHelpers.BuildToolResult(id, "MetaModel bridge is not available.", isError: true);
            int dpi = (int)Math.Max(72, Math.Min(200, Num(a, "dpi") ?? 120));
            int pageNumber = (int)Math.Max(1, Num(a, "page") ?? 1);
            double tolerance = Num(a, "toleranceMm") ?? 0.5;
            try
            {
                var renderArgs = new Dictionary<string, object>
                {
                    ["reportName"] = Str(a, "reportName"), ["designName"] = Str(a, "designName"), ["rdl"] = Str(a, "rdl"),
                    ["data"] = a.TryGetValue("data", out object d) ? d : null, ["parameters"] = a.TryGetValue("parameters", out object p) ? p : null,
                    ["sampleRows"] = Num(a, "sampleRows") ?? 1, ["language"] = Str(a, "language"), ["company"] = Str(a, "company"),
                    ["firstPage"] = pageNumber, ["maxPages"] = 1, ["dpi"] = dpi,
                };
                var rendered = Call("render_report", renderArgs);
                if (!Ok(rendered)) return JsonHelpers.BuildToolResult(id, Message(rendered), isError: true);
                var page = (Dictionary<string, object>)rendered["page"];
                double pageW = Convert.ToDouble(page["width"], CultureInfo.InvariantCulture), pageH = Convert.ToDouble(page["height"], CultureInfo.InvariantCulture);
                byte[] png = Convert.FromBase64String(((ArrayList)rendered["pages"]).Cast<string>().First());
                double[] offset = Rect(a, "scanOffsetMm");

                using (var scan = ImageMeasure.Load(Str(a, "imagePath"), Calibration(a, "scanDpi")))
                using (var renderStream = new MemoryStream(png))
                using (var render = new Bitmap(renderStream))
                using (var scanOnPage = ImageMeasure.OnPage(scan, pageW, pageH, dpi, offset?[0] ?? 0, offset?[1] ?? 0))
                {
                    var sb = new StringBuilder();
                    sb.AppendFormat(CultureInfo.InvariantCulture, "Scan ({0}) placed on the {1:0.##} x {2:0.##} mm page of {3}.{4} page {5}, at {6} dpi.\n",
                        scan.Calibration, pageW, pageH, rendered["report"], rendered["design"], pageNumber, dpi);
                    foreach (string note in scan.Notes) sb.Append("NOTE: ").AppendLine(note);
                    byte[] overlay = ImageMeasure.Overlay(scanOnPage, render);
                    double pxPerMm = dpi / 25.4;
                    var scanWords = ImageMeasure.Ocr(scanOnPage, pxPerMm, pxPerMm, Str(a, "language"), out string problem);
                    var renderWords = scanWords == null ? null : ImageMeasure.Ocr(render, pxPerMm, pxPerMm, Str(a, "language"), out problem);
                    if (scanWords == null || renderWords == null)
                    {
                        sb.Append("Text positions were not compared: ").AppendLine(problem);
                    }
                    else
                    {
                        AppendWordOffsets(sb, scanWords, renderWords, ((ArrayList)rendered["items"]).Cast<Dictionary<string, object>>().ToList(), tolerance);
                    }
                    sb.AppendLine("Picture: scan ink red, report ink blue, both black. Where red and blue sit side by side, move the report item by the offset listed.");
                    return JsonHelpers.BuildToolResultWithImages(id, sb.ToString(), new[] { overlay });
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException || ex is InvalidOperationException)
            {
                return JsonHelpers.BuildToolResult(id, ex is OutOfMemoryException ? "The image could not be read (corrupt or unsupported format)." : ex.Message, isError: true);
            }
        }

        /// <summary>Words that read the same on the scan and the render, matched once each; their offsets, per report item.</summary>
        private static void AppendWordOffsets(StringBuilder sb, List<OcrWord> scanWords, List<OcrWord> renderWords, List<Dictionary<string, object>> items, double tolerance)
        {
            Dictionary<string, List<OcrWord>> ByText(List<OcrWord> words) => words.Where(w => ImageMeasure.Normalize(w.Text).Length >= 2)
                .GroupBy(w => ImageMeasure.Normalize(w.Text)).ToDictionary(g => g.Key, g => g.ToList());
            var scanByText = ByText(scanWords);
            var renderByText = ByText(renderWords);
            var pairs = new List<(OcrWord scan, OcrWord render)>();
            foreach (var entry in renderByText)
                if (entry.Value.Count == 1 && scanByText.TryGetValue(entry.Key, out var match) && match.Count == 1)
                    pairs.Add((match[0], entry.Value[0]));
            if (pairs.Count == 0)
            {
                sb.AppendLine("No word reads the same on the scan and the rendered page. Render with data that shows the values printed on the scan (data parameter), then compare again.");
                return;
            }
            double Median(IEnumerable<double> values) { var v = values.OrderBy(x => x).ToList(); return v[v.Count / 2]; }
            double mx = Median(pairs.Select(q => q.render.X - q.scan.X)), my = Median(pairs.Select(q => q.render.Y + q.render.Height - q.scan.Y - q.scan.Height));
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} word(s) matched. Median offset report - scan: dx {1:0.##} mm, dy {2:0.##} mm (baseline). ", pairs.Count, mx, my);
            sb.AppendLine(Math.Abs(mx) > tolerance || Math.Abs(my) > tolerance
                ? "Everything is shifted the same way: check the page margins and the scan's calibration first."
                : "No overall shift.");

            // By text first (a textbox whose literal, label or data value holds the word), nearest to where it was read;
            // by designed position only when no static text matches (growth above an item moves it from its design position).
            var textboxes = items.Where(i => (string)i["type"] == "Textbox").ToList();
            double D(Dictionary<string, object> i, string k) => Convert.ToDouble(i[k], CultureInfo.InvariantCulture);
            string ItemOf(OcrWord w)
            {
                string word = ImageMeasure.Normalize(w.Text);
                var byText = textboxes.Where(i => i.TryGetValue("text", out object t) && t is string s && ImageMeasure.Normalize(s).Contains(word))
                    .OrderBy(i => Math.Abs(D(i, "y") - w.Y) + Math.Abs(D(i, "x") - w.X)).FirstOrDefault();
                if (byText != null) return (string)byText["name"];
                double cx = w.X + w.Width / 2, cy = w.Y + w.Height / 2;
                var hit = textboxes
                    .Where(i => cx >= D(i, "x") && cx <= D(i, "x") + D(i, "width") && cy >= D(i, "y") && cy <= D(i, "y") + D(i, "height"))
                    .OrderBy(i => D(i, "width") * D(i, "height")).FirstOrDefault();
                return hit != null ? (string)hit["name"] + " (by position)" : "(no textbox at its designed position: grown or moved by an item above)";
            }
            var byItem = pairs.GroupBy(q => ItemOf(q.render)).Select(g => new
            {
                Item = g.Key,
                Dx = g.Average(q => q.render.X - q.scan.X),
                Dy = g.Average(q => q.render.Y + q.render.Height - q.scan.Y - q.scan.Height),
                Words = string.Join(" ", g.Select(q => q.render.Text).Take(6)),
            }).OrderByDescending(x => Math.Max(Math.Abs(x.Dx), Math.Abs(x.Dy))).ToList();
            var off = byItem.Where(x => Math.Abs(x.Dx) > tolerance || Math.Abs(x.Dy) > tolerance).ToList();
            sb.Append("Items off by more than ").Append(F(tolerance)).Append(" mm (").Append(off.Count).Append(" of ").Append(byItem.Count)
              .AppendLine("); move each by minus its offset (dx < 0: report is left of the scan, move right):");
            foreach (var x in off.Take(80))
                sb.Append("- ").Append(x.Item).Append(": dx ").Append(F(x.Dx)).Append(" mm, dy ").Append(F(x.Dy)).Append(" mm; after the overall shift dx ")
                  .Append(F(x.Dx - mx)).Append(", dy ").Append(F(x.Dy - my)).Append(" (").Append(x.Words).AppendLine(")");
            foreach (var x in byItem.Except(off).Take(40))
                sb.Append("- ok ").Append(x.Item).Append(": dx ").Append(F(x.Dx)).Append(", dy ").Append(F(x.Dy)).AppendLine();
            var unmatched = scanByText.Where(kv => !renderByText.ContainsKey(kv.Key)).SelectMany(kv => kv.Value).OrderBy(w => w.Y).ThenBy(w => w.X).Take(60).ToList();
            if (unmatched.Count > 0)
                sb.Append("Printed on the scan but not on the rendered page (preprinted text, or values your data doesn't show): ")
                  .AppendLine(string.Join(", ", unmatched.Select(w => w.Text + " @" + F(w.X) + "," + F(w.Y))));
        }
    }
}
