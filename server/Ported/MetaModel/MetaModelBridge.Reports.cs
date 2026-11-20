using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using XppVsCode.Server.Host.Reports;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>
    /// SSRS reports: designs, datasets and parameters, the precision design's RDL (AxReportPrecisionDesign.Text), and a local
    /// preview with Microsoft's report processing engine. Design text is saved through the metadata API, as Visual Studio's
    /// designer import does (Text plus the RDL's embedded images on the report).
    /// </summary>
    public sealed partial class MetaModelBridge : IReportDesigns
    {
        private const int MaxPreviewPages = 20;

        private AxReport ReadReport(string reportName, out string model, out string error)
        {
            error = null;
            model = null;
            if (string.IsNullOrWhiteSpace(reportName))
            {
                error = "Pass reportName.";
                return null;
            }
            var report = ReadElementCached("AxReport", reportName, out model) as AxReport;
            if (report == null) error = "Report '" + reportName + "' not found.";
            return report;
        }

        private static AxReportDesign FindDesign(AxReport report, string designName) =>
            report.Designs.Cast<AxReportDesign>().FirstOrDefault(d => string.IsNullOrEmpty(designName) || string.Equals(d.Name, designName, StringComparison.OrdinalIgnoreCase));

        private static List<ReportDataSetInfo> DataSetInfos(AxReport report) => report.DataSets.Cast<AxReportDataSet>().Select(ds => new ReportDataSetInfo
        {
            Name = ds.Name,
            DataSourceType = ds.DataSourceType.ToString(),
            Query = ds.Query,
            Fields = ds.Fields.Cast<AxReportDataSetField>().Select(f => new KeyValuePair<string, string>(f.Name, string.IsNullOrEmpty(f.DataType) ? "System.String" : f.DataType)).ToList(),
        }).ToList();

        private static List<ReportParameterInfo> ParameterInfos(AxReport report) => (report.Parameters ?? Enumerable.Empty<AxReportParameter>())
            .OfType<AxReportParameter>().Select(p => new ReportParameterInfo
            {
                Name = p.Name,
                DataType = p.DataType,
                MultiValue = p.MultiValue,
                Visible = p.UserVisibility.ToString() == "Visible",
                Prompt = p.PromptString,
            }).ToList();

        private static List<KeyValuePair<string, KeyValuePair<string, string>>> EmbeddedImages(AxReport report) =>
            (report.EmbeddedImages as IEnumerable ?? new object[0]).Cast<object>()
                .Select(i => new { Name = i.GetType().GetProperty("Name")?.GetValue(i) as string, Mime = i.GetType().GetProperty("MimeType")?.GetValue(i) as string, Data = i.GetType().GetProperty("Base64Data")?.GetValue(i) as string })
                .Where(i => !string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(i.Data))
                .Select(i => new KeyValuePair<string, KeyValuePair<string, string>>(i.Name, new KeyValuePair<string, string>(i.Mime ?? "image/png", i.Data))).ToList();

        public string ReportInfoJson(string reportName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var report = ReadReport(reportName, out string model, out string error);
            if (report == null) return json.Serialize(Failure(error));
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = report.Name,
                ["model"] = model,
                ["isCustom"] = IsCustomModel(model),
                ["designs"] = report.Designs.Cast<AxReportDesign>().Select(d => new Dictionary<string, object>
                {
                    ["name"] = d.Name,
                    ["kind"] = d is AxReportPrecisionDesign ? "precision" : d is AxReportAutoDesign ? "auto" : d.GetType().Name,
                }).ToList(),
                ["dataSets"] = DataSetInfos(report).Select(d => new Dictionary<string, object>
                {
                    ["name"] = d.Name, ["dataSourceType"] = d.DataSourceType, ["query"] = d.Query,
                    ["fields"] = d.Fields.Select(f => new Dictionary<string, object> { ["name"] = f.Key, ["type"] = f.Value }).ToList(),
                }).ToList(),
                ["parameters"] = ParameterInfos(report).Select(p => new Dictionary<string, object>
                {
                    ["name"] = p.Name, ["type"] = p.DataType, ["visible"] = p.Visible, ["multiValue"] = p.MultiValue, ["prompt"] = ResolveLabel(p.Prompt),
                }).ToList(),
                ["embeddedImages"] = EmbeddedImages(report).Select(i => i.Key).ToList(),
            });
        }

        public string GetReportDesignJson(string reportName, string designName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var report = ReadReport(reportName, out string model, out string error);
            if (report == null) return json.Serialize(Failure(error));
            var design = FindDesign(report, designName);
            if (design == null) return json.Serialize(Failure("Report '" + reportName + "' has no design '" + designName + "'."));
            if (!(design is AxReportPrecisionDesign precision))
                return json.Serialize(Failure("Design '" + design.Name + "' is an auto design: its layout is generated. Create a precision design from it (create_precision_design, or Create Precision Design on the design in the element designer), or add one with xpp_set_report_design."));
            var doc = RdlPreview.Parse(precision.Text, out string problem);
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["report"] = report.Name,
                ["design"] = design.Name,
                ["model"] = model,
                ["isCustom"] = IsCustomModel(model),
                ["rdl"] = precision.Text ?? "",
            };
            if (doc == null)
            {
                result["warnings"] = new List<string> { problem };
                return json.Serialize(result);
            }
            result["namespace"] = doc.Root.Name.NamespaceName;
            result["page"] = PageJson(RdlPreview.Page(doc));
            result["items"] = RdlPreview.ItemMap(doc).Select(ItemJson).ToList();
            result["warnings"] = RdlPreview.Check(doc);
            // For the designer: the label texts the design uses, the fonts D365FO offers (and which are on this machine), embedded images.
            result["labels"] = System.Text.RegularExpressions.Regex.Matches(precision.Text ?? "", @"Labels!(@[A-Za-z]\w*(?::\w+)?)")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2000).ToDictionary(id => id, id => (object)(LabelTextIn(id, "en-US") ?? id));
            var installed = RdlPreview.InstalledFonts();
            result["fonts"] = RdlPreview.SupportedFonts().OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new Dictionary<string, object> { ["name"] = f, ["installed"] = installed.Contains(f) }).ToList();
            result["images"] = EmbeddedImages(report).ToDictionary(i => i.Key, i => (object)new Dictionary<string, object> { ["mimeType"] = i.Value.Key, ["data"] = i.Value.Value });
            result["dataSets"] = DataSetInfos(report).Select(d => new Dictionary<string, object>
            {
                ["name"] = d.Name, ["dataSourceType"] = d.DataSourceType,
                ["fields"] = d.Fields.Select(f => new Dictionary<string, object> { ["name"] = f.Key, ["type"] = f.Value }).ToList(),
            }).ToList();
            result["parameters"] = ParameterInfos(report).Select(p => new Dictionary<string, object> { ["name"] = p.Name, ["type"] = p.DataType, ["visible"] = p.Visible }).ToList();
            return json.Serialize(result);
        }

        public string SetReportDesignJson(string reportName, string designName, string rdl)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (string.IsNullOrWhiteSpace(designName) || !System.Text.RegularExpressions.Regex.IsMatch(designName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                return json.Serialize(Failure("Pass designName (letters, digits and underscores)."));
            var doc = RdlPreview.Parse(rdl, out string problem);
            if (doc == null) return json.Serialize(Failure(problem));

            var report = ReadEditableElement("AxReport", reportName, out string model, out string error) as AxReport;
            if (report == null) return json.Serialize(Failure(error ?? "Report '" + reportName + "' not found."));
            var design = FindDesign(report, designName);
            bool created = false;
            if (design == null || !string.Equals(design.Name, designName, StringComparison.OrdinalIgnoreCase))
            {
                design = new AxReportPrecisionDesign { Name = designName };
                report.Designs.Add(design);
                created = true;
            }
            if (!(design is AxReportPrecisionDesign precision))
                return json.Serialize(Failure("Design '" + design.Name + "' is an auto design; its layout is generated. Save the RDL under a new design name."));
            precision.Text = rdl.TrimStart('\uFEFF');
            int images = ImportEmbeddedImages(report, doc);

            string saved = SaveElement(json, "AxReport", report, model,
                (created ? "Created precision design " : "Saved precision design ") + report.Name + "." + designName + (images > 0 ? " (" + images + " embedded image(s))" : "") + ".");
            var result = json.Deserialize<Dictionary<string, object>>(saved);
            if (result.TryGetValue("success", out object ok) && ok is bool success && success)
                result["warnings"] = RdlPreview.Check(doc);
            return json.Serialize(result);
        }

        /// <summary>As Visual Studio's designer import: images embedded in the RDL become the report's embedded images.</summary>
        private static int ImportEmbeddedImages(AxReport report, XDocument doc)
        {
            int images = 0;
            foreach (var image in doc.Root.Elements().Where(e => e.Name.LocalName == "EmbeddedImages").Elements().Where(e => e.Name.LocalName == "EmbeddedImage"))
            {
                string name = image.Attribute("Name")?.Value;
                string data = image.Elements().FirstOrDefault(e => e.Name.LocalName == "ImageData")?.Value;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(data)) continue;
                var existing = report.EmbeddedImages.Cast<AxReportEmbeddedImage>().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new AxReportEmbeddedImage { Name = name };
                    report.EmbeddedImages.Add(existing);
                }
                existing.MimeType = image.Elements().FirstOrDefault(e => e.Name.LocalName == "MIMEType")?.Value ?? "image/png";
                existing.Base64Data = data;
                images++;
            }
            return images;
        }

        private static readonly System.Text.RegularExpressions.Regex DesignNamePattern = new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

        public string CreatePrecisionDesignJson(string reportName, string autoDesignName, string newDesignName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (string.IsNullOrWhiteSpace(autoDesignName))
                return json.Serialize(Failure("Pass designName (the auto design)."));
            var report = ReadEditableElement("AxReport", reportName, out string model, out string error) as AxReport;
            if (report == null) return json.Serialize(Failure(error ?? "Report '" + reportName + "' not found."));
            var auto = report.Designs.Cast<AxReportDesign>().FirstOrDefault(d => string.Equals(d.Name, autoDesignName, StringComparison.OrdinalIgnoreCase));
            if (auto == null) return json.Serialize(Failure("Report '" + report.Name + "' has no design '" + autoDesignName + "'."));
            if (!(auto is AxReportAutoDesign autoDesign))
                return json.Serialize(Failure("Design '" + auto.Name + "' is already a precision design."));
            if (autoDesign.DataRegions.Count == 0)
                return json.Serialize(Failure("Auto design '" + auto.Name + "' has no data regions yet: add one (Add Data Region from Data Set…) before creating the precision design."));
            string name = string.IsNullOrWhiteSpace(newDesignName) ? auto.Name + "Precision" : newDesignName.Trim();
            if (!DesignNamePattern.IsMatch(name))
                return json.Serialize(Failure("'" + name + "' is not a valid design name (letters, digits and underscores)."));
            if (report.Designs.Cast<AxReportDesign>().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                return json.Serialize(Failure("Report '" + report.Name + "' already has a design '" + name + "'."));

            // The generator works on its own copy of the report (it sets the copy's model info).
            string rdl;
            try
            {
                var copy = MetadataProvider.Reports.Read(report.Name);
                var copyDesign = copy.Designs.Cast<AxReportDesign>().First(d => string.Equals(d.Name, auto.Name, StringComparison.OrdinalIgnoreCase));
                rdl = ReportDesignGenerator.GenerateRdl(_host.Config, MetadataProvider, copy, copyDesign);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return json.Serialize(Failure("The report generator could not generate " + report.Name + "." + auto.Name + ": " + ex.Message));
            }
            var doc = RdlPreview.Parse(rdl, out string problem);
            if (doc == null) return json.Serialize(Failure("The generated design is not valid RDL: " + problem));

            var precision = new AxReportPrecisionDesign { Name = name, Text = doc.Declaration != null ? doc.Declaration + doc.ToString(SaveOptions.DisableFormatting) : doc.ToString(SaveOptions.DisableFormatting) };
            // As Visual Studio: a data region shown in the document map carries over to the precision design.
            if (autoDesign.DataRegions.Cast<AxReportDataRegion>().Any(r => r.DataNavigation.ToString() == "DocumentMap"))
            {
                var navigation = typeof(AxReportPrecisionDesign).GetProperty("DataNavigation");
                navigation.SetValue(precision, Enum.Parse(navigation.PropertyType, "DocumentMap"));
            }
            report.Designs.Add(precision);
            int images = ImportEmbeddedImages(report, doc);
            string saved = SaveElement(json, "AxReport", report, model, "Created precision design " + report.Name + "." + name + " from auto design " + auto.Name
                + (images > 0 ? " (" + images + " embedded image(s))" : "") + ".");
            var result = json.Deserialize<Dictionary<string, object>>(saved);
            result["design"] = name;
            return json.Serialize(result);
        }

        public string AddReportDataRegionJson(string reportName, string designName, string dataSetName, string layout)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            bool list = string.Equals(layout, "List", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(layout) && !list && !string.Equals(layout, "Table", StringComparison.OrdinalIgnoreCase))
                return json.Serialize(Failure("layout is Table or List."));
            string name = string.IsNullOrWhiteSpace(designName) ? "Report" : designName.Trim();
            if (!DesignNamePattern.IsMatch(name))
                return json.Serialize(Failure("'" + name + "' is not a valid design name (letters, digits and underscores)."));
            var report = ReadEditableElement("AxReport", reportName, out string model, out string error) as AxReport;
            if (report == null) return json.Serialize(Failure(error ?? "Report '" + reportName + "' not found."));
            var dataSet = report.DataSets.Cast<AxReportDataSet>().FirstOrDefault(d => string.Equals(d.Name, dataSetName, StringComparison.OrdinalIgnoreCase));
            if (dataSet == null)
                return json.Serialize(Failure("Report '" + report.Name + "' has no dataset '" + dataSetName + "'. Datasets: " + string.Join(", ", report.DataSets.Cast<AxReportDataSet>().Select(d => d.Name)) + "."));

            var design = report.Designs.Cast<AxReportDesign>().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            bool created = false;
            if (design == null)
            {
                // As the New Report wizard and Visual Studio's template: Letter, half-inch margins, the standard layout template.
                design = new AxReportAutoDesign
                {
                    Name = name,
                    LayoutTemplate = "ReportLayoutStyleTemplate",
                    PageSize = new AxReportSize { Width = "8.5in", Height = "11in" },
                    InteractiveSize = new AxReportSize { Width = "8.5in", Height = "11in" },
                    Margin = new AxReportMargin { Left = "0.5in", Right = "0.5in", Top = "0.5in", Bottom = "0.5in" },
                };
                report.Designs.Add(design);
                created = true;
            }
            if (!(design is AxReportAutoDesign auto))
                return json.Serialize(Failure("Design '" + design.Name + "' is a precision design: lay it out in the Report Designer."));

            string regionName = UniqueName(auto.DataRegions.Cast<AxReportDataRegion>().Select(r => r.Name), list ? "List1" : "Table1");
            var fields = dataSet.Fields.Cast<AxReportDataSetField>().Where(f => !f.DisableAutoCreateInDataRegion).ToList();
            if (list)
            {
                var region = new AxReportList { Name = regionName, DataSet = dataSet.Name, StyleTemplate = "ListStyleTemplate", Data = new AxReportListData { Name = "Data" } };
                foreach (var f in fields)
                    region.Data.Fields.Add(new AxReportListFieldData { Name = f.Name, Expression = "=Fields!" + f.Name + ".Value" });
                auto.DataRegions.Add(region);
            }
            else
            {
                var region = new AxReportTable { Name = regionName, DataSet = dataSet.Name, StyleTemplate = "TableStyleTemplate", Data = new AxReportTableData { Name = "Data" } };
                foreach (var f in fields)
                    region.Data.DetailData.Add(new AxReportTableDetailData { Name = f.Name, Expression = "=Fields!" + f.Name + ".Value" });
                auto.DataRegions.Add(region);
            }
            string saved = SaveElement(json, "AxReport", report, model, (created ? "Created auto design " + report.Name + "." + name + " with " : "Added ")
                + (list ? "list " : "table ") + regionName + " (" + fields.Count + " fields of " + dataSet.Name + ")" + (created ? "" : " to " + report.Name + "." + name) + ".");
            var result = json.Deserialize<Dictionary<string, object>>(saved);
            result["design"] = name;
            result["region"] = regionName;
            return json.Serialize(result);
        }

        private static string UniqueName(IEnumerable<string> taken, string wanted)
        {
            var set = new HashSet<string>(taken.Where(t => t != null), StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(wanted)) return wanted;
            // Table1, Table2...: count on from a trailing number.
            string stem = wanted.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            for (int i = stem.Length < wanted.Length ? 2 : 1; ; i++)
                if (!set.Contains(stem + i)) return stem + i;
        }

        public string RenderReportJson(string body)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> args;
            try { args = json.Deserialize<Dictionary<string, object>>(body ?? "{}") ?? new Dictionary<string, object>(); }
            catch (ArgumentException ex) { return json.Serialize(Failure("Invalid request: " + ex.Message)); }
            string Arg(string key) => args.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
            int Int(string key, int fallback, int min, int max) => int.TryParse(Arg(key), out int v) ? Math.Max(min, Math.Min(max, v)) : fallback;

            var report = ReadReport(Arg("reportName"), out string model, out string error);
            if (report == null) return json.Serialize(Failure(error));

            string rdl = Arg("rdl");
            string designName = Arg("designName");
            if (string.IsNullOrEmpty(rdl))
            {
                var design = FindDesign(report, designName);
                if (design == null) return json.Serialize(Failure("Report '" + report.Name + "' has no design '" + designName + "'."));
                if (!(design is AxReportPrecisionDesign precision))
                    return json.Serialize(Failure("Design '" + design.Name + "' is an auto design; the preview renders precision designs."));
                rdl = precision.Text;
                designName = design.Name;
            }
            var doc = RdlPreview.Parse(rdl, out string problem);
            if (doc == null) return json.Serialize(Failure(problem));

            string language = Arg("language") ?? "en-US";
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Func<string, string> label = id => labels.TryGetValue(id, out string t) ? t : labels[id] = LabelTextIn(id, language);

            var rows = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
            if (args.TryGetValue("data", out object data) && data is Dictionary<string, object> sets)
                foreach (var set in sets)
                    if (set.Value is ArrayList list)
                        rows[set.Key] = list.OfType<Dictionary<string, object>>().ToList();
            var parameterValues = (args.TryGetValue("parameters", out object p) && p is Dictionary<string, object> pv)
                ? pv.ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                : null;
            string pdfPath = Arg("pdfPath");
            if (!string.IsNullOrEmpty(pdfPath))
            {
                pdfPath = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(pdfPath));
                if (!pdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    return json.Serialize(Failure("pdfPath must end with .pdf."));
                if (new Uri(pdfPath).IsUnc)
                    return json.Serialize(Failure("pdfPath must be on this machine, not a network path."));
                if (!System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(pdfPath)))
                    return json.Serialize(Failure("The folder of pdfPath doesn't exist."));
                // Never replace a file the user has unless asked to.
                if (System.IO.File.Exists(pdfPath) && !"true".Equals(Arg("overwrite"), StringComparison.OrdinalIgnoreCase))
                    return json.Serialize(Failure(pdfPath + " exists; pass overwrite true to replace it, or another path."));
            }

            if (!ReportRuntime.IsAvailable(out string missing)) return json.Serialize(Failure(missing));
            var prepared = RdlPreview.Prepare(doc, DataSetInfos(report), ParameterInfos(report), label, rows, Int("sampleRows", 1, 0, 200),
                parameterValues, language, Arg("company") ?? "USMF", EmbeddedImages(report));
            RenderedReport rendered;
            try
            {
                rendered = ReportRenderer.Render(prepared, Int("firstPage", 1, 1, 10000), Int("maxPages", 1, 1, MaxPreviewPages), Int("dpi", 96, 48, 300), pdfPath);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return json.Serialize(new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["message"] = "The report engine could not render " + report.Name + "." + designName + ": " + ReportRenderer.Describe(ex),
                    ["notes"] = prepared.Notes,
                });
            }
            var warnings = RdlPreview.Check(doc);
            warnings.AddRange(prepared.Notes);
            warnings.AddRange(rendered.Warnings.Where(w => !w.Contains("FOS_") && !(w.StartsWith("rsInvalidColor", StringComparison.Ordinal) && w.Contains("Transparent"))));
            // Items from the prepared report: labels are resolved there, so each textbox's printed text is known where it is static.
            var preparedDoc = RdlPreview.Parse(prepared.Rdl, out _) ?? doc;
            var items = RdlPreview.ItemMap(preparedDoc).Select(i =>
            {
                var item = ItemJson(i);
                item["text"] = i.Type == "Textbox" ? RdlPreview.StaticText(i.Value, prepared.Data) : null;
                return item;
            }).ToList();
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["report"] = report.Name,
                ["design"] = designName,
                ["totalPages"] = rendered.TotalPages,
                ["firstPage"] = Int("firstPage", 1, 1, 10000),
                ["pages"] = rendered.Pages.Select(Convert.ToBase64String).ToList(),
                ["dpi"] = Int("dpi", 96, 48, 300),
                ["page"] = PageJson(RdlPreview.Page(doc)),
                ["items"] = items,
                ["dataSets"] = prepared.Data.Select(d => new Dictionary<string, object>
                {
                    ["name"] = d.Key,
                    ["fields"] = d.Value.Columns.Cast<System.Data.DataColumn>().Select(c => new Dictionary<string, object> { ["name"] = c.ColumnName, ["type"] = c.DataType.Name }).ToList(),
                    ["rows"] = d.Value.Rows.Count,
                }).ToList(),
                ["warnings"] = warnings,
                ["pdfPath"] = rendered.PdfPath,
            });
        }

        private readonly Dictionary<string, Dictionary<string, LabelEntry>> _reportLabelTexts = new Dictionary<string, Dictionary<string, LabelEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _reportLabelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Text of "@File:Id" or legacy "@SYS123" in a language (falls back to en-US), or null.</summary>
        private string LabelTextIn(string labelReference, string language)
        {
            if (string.IsNullOrEmpty(labelReference) || labelReference[0] != '@') return null;
            int colon = labelReference.IndexOf(':');
            string file, id;
            if (colon > 1) { file = labelReference.Substring(1, colon - 1); id = labelReference.Substring(colon + 1); }
            else
            {
                int digits = 1;
                while (digits < labelReference.Length && !char.IsDigit(labelReference[digits])) digits++;
                file = labelReference.Substring(1, digits - 1);
                id = labelReference;
            }
            foreach (string lang in new[] { language, "en-US" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string pathKey = file + "|" + lang;
                string path;
                lock (_reportLabelPaths)
                    _reportLabelPaths.TryGetValue(pathKey, out path);
                if (path == null || !System.IO.File.Exists(path))
                {
                    // Only found files are remembered: a label file created later is found on the next lookup.
                    path = _labels.FindLabelFile(file, lang);
                    if (path == null) continue;
                    lock (_reportLabelPaths)
                        _reportLabelPaths[pathKey] = path;
                }
                // Cached per file and last write, so labels created or changed since are read again.
                DateTime written = System.IO.File.GetLastWriteTimeUtc(path);
                string key = path + "|" + written.Ticks;
                Dictionary<string, LabelEntry> texts;
                lock (_reportLabelTexts)
                {
                    if (!_reportLabelTexts.TryGetValue(key, out texts))
                    {
                        foreach (string stale in _reportLabelTexts.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                            _reportLabelTexts.Remove(stale);
                        _reportLabelTexts[key] = texts = _labels.ReadAll(path);
                    }
                }
                if (texts.TryGetValue(id, out var entry)) return entry.Text;
            }
            return null;
        }

        private static Dictionary<string, object> PageJson(ReportPageInfo p) => new Dictionary<string, object>
        {
            ["width"] = Round(p.PageWidth), ["height"] = Round(p.PageHeight),
            ["leftMargin"] = Round(p.LeftMargin), ["rightMargin"] = Round(p.RightMargin), ["topMargin"] = Round(p.TopMargin), ["bottomMargin"] = Round(p.BottomMargin),
            ["bodyWidth"] = Round(p.BodyWidth), ["bodyHeight"] = Round(p.BodyHeight), ["headerHeight"] = Round(p.HeaderHeight), ["footerHeight"] = Round(p.FooterHeight),
        };

        private static Dictionary<string, object> ItemJson(ReportItemBox i) => new Dictionary<string, object>
        {
            ["name"] = i.Name, ["type"] = i.Type, ["section"] = i.Section, ["container"] = i.Container,
            ["x"] = Round(i.X), ["y"] = Round(i.Y), ["width"] = Round(i.Width), ["height"] = Round(i.Height),
            ["value"] = i.Value, ["font"] = i.Font, ["fontSize"] = i.FontSize.HasValue ? (object)Math.Round(i.FontSize.Value, 1) : null,
            ["canGrow"] = i.CanGrow, ["canShrink"] = i.CanShrink, ["conditionallyHidden"] = i.ConditionallyHidden,
        };

        private static double Round(double mm) => Math.Round(mm, 2);
    }
}
