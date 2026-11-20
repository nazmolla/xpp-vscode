using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XppVsCode.Server.Host.Reports
{
    /// <summary>A dataset of the report as the AxReport metadata defines it.</summary>
    internal sealed class ReportDataSetInfo
    {
        public string Name;
        public string DataSourceType;
        public string Query;
        public List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>(); // name, .NET type
    }

    /// <summary>A report parameter as the AxReport metadata defines it.</summary>
    internal sealed class ReportParameterInfo
    {
        public string Name;
        public string DataType;
        public bool MultiValue;
        public bool Visible;
        public string Prompt;
    }

    /// <summary>One report item and where the design puts it on the first page, in millimetres from the page's top-left corner.</summary>
    internal sealed class ReportItemBox
    {
        public string Name, Type, Section, Container, Value, Font;
        public double X, Y, Width, Height;
        public double? FontSize;
        public bool CanGrow, CanShrink, ConditionallyHidden;
    }

    /// <summary>Page geometry of a design, in millimetres.</summary>
    internal sealed class ReportPageInfo
    {
        public double PageWidth, PageHeight, LeftMargin, RightMargin, TopMargin, BottomMargin, BodyWidth, BodyHeight, HeaderHeight, FooterHeight;
    }

    /// <summary>
    /// Turns a D365FO precision design (RDL as stored in AxReportPrecisionDesign.Text, any of the 2005-2016 schemas) into an RDL
    /// that Microsoft's report processing engine renders locally: datasets and parameters from the report's metadata (as Visual
    /// Studio regenerates them), labels resolved, and the D365FO helper functions, which need an AOS session, replaced by local
    /// stand-ins with the same output.
    /// </summary>
    internal static class RdlPreview
    {
        public const string RdNamespace = "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner";

        /// <summary>An empty precision design: Letter portrait, half-inch margins (2016 schema, as Visual Studio's designer saves).</summary>
        public const string BlankDesign = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<Report xmlns=\"http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition\" xmlns:rd=\"http://schemas.microsoft.com/SQLServer/reporting/reportdesigner\">\r\n"
            + "  <ReportSections>\r\n    <ReportSection>\r\n      <Body>\r\n        <Height>9in</Height>\r\n        <Style />\r\n      </Body>\r\n      <Width>7.5in</Width>\r\n"
            + "      <Page>\r\n        <PageHeight>11in</PageHeight>\r\n        <PageWidth>8.5in</PageWidth>\r\n        <LeftMargin>0.5in</LeftMargin>\r\n        <RightMargin>0.5in</RightMargin>\r\n"
            + "        <TopMargin>0.5in</TopMargin>\r\n        <BottomMargin>0.5in</BottomMargin>\r\n        <Style />\r\n      </Page>\r\n    </ReportSection>\r\n  </ReportSections>\r\n"
            + "  <rd:ReportUnitType>Inch</rd:ReportUnitType>\r\n</Report>";

        public static readonly string[] KnownNamespaces =
        {
            "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition",
            "http://schemas.microsoft.com/sqlserver/reporting/2010/01/reportdefinition",
            "http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition",
            "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition",
        };

        private static readonly string[] FrameworkParameters =
            { "AX_PartitionKey", "AX_CompanyName", "AX_UserContext", "AX_RenderingCulture", "AX_ReportContext", "AX_RdpPreProcessedId" };

        // ── sizes ──

        /// <summary>An RDL size ("2.5in", "10pt", "6.35mm", "1cm", "1pc") in millimetres; 0 when empty or invalid.</summary>
        public static double ToMm(string size)
        {
            if (string.IsNullOrWhiteSpace(size)) return 0;
            var m = Regex.Match(size.Trim(), @"^(-?\d+(?:\.\d+)?)\s*(in|cm|mm|pt|pc)?$", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "in": return v * 25.4;
                case "cm": return v * 10;
                case "pt": return v * 25.4 / 72;
                case "pc": return v * 25.4 / 6;
                default: return v;
            }
        }

        /// <summary>Millimetres as an RDL size in the unit given (in, cm, mm, pt), rounded the way the designer writes them.</summary>
        public static string FromMm(double mm, string unit)
        {
            switch ((unit ?? "mm").ToLowerInvariant())
            {
                case "in": return Math.Round(mm / 25.4, 5).ToString(CultureInfo.InvariantCulture) + "in";
                case "cm": return Math.Round(mm / 10, 4).ToString(CultureInfo.InvariantCulture) + "cm";
                case "pt": return Math.Round(mm * 72 / 25.4, 2).ToString(CultureInfo.InvariantCulture) + "pt";
                default: return Math.Round(mm, 3).ToString(CultureInfo.InvariantCulture) + "mm";
            }
        }

        // ── loading ──

        /// <summary>Parses design text; returns null with a reason when it isn't a report definition this tooling knows.</summary>
        public static XDocument Parse(string rdl, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(rdl)) { problem = "The design is empty."; return null; }
            XDocument doc;
            try
            {
                var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
                using (var reader = System.Xml.XmlReader.Create(new StringReader(rdl.TrimStart('\uFEFF')), settings))
                    doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException ex)
            {
                problem = "The design is not valid XML: " + ex.Message;
                return null;
            }
            if (doc.Root == null || doc.Root.Name.LocalName != "Report" || !KnownNamespaces.Contains(doc.Root.Name.NamespaceName))
            {
                problem = "The design is not a report definition (root <Report> in one of the RDL 2005-2016 namespaces).";
                return null;
            }
            return doc;
        }

        private static XElement Child(XElement e, string name) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
        private static IEnumerable<XElement> Children(XElement e, string name) => e == null ? Enumerable.Empty<XElement>() : e.Elements().Where(c => c.Name.LocalName == name);
        private static string Text(XElement e, string name) => Child(e, name)?.Value;

        /// <summary>The section that holds Body and Page: ReportSections/ReportSection (2010+) or the report itself (2005/2008).</summary>
        public static XElement Section(XDocument doc) => Child(Child(doc.Root, "ReportSections"), "ReportSection") ?? doc.Root;

        public static ReportPageInfo Page(XDocument doc)
        {
            XElement section = Section(doc);
            XElement page = Child(section, "Page") ?? section;
            XElement body = Child(section, "Body");
            var info = new ReportPageInfo
            {
                PageWidth = ToMm(Text(page, "PageWidth") ?? Text(doc.Root, "PageWidth")),
                PageHeight = ToMm(Text(page, "PageHeight") ?? Text(doc.Root, "PageHeight")),
                LeftMargin = ToMm(Text(page, "LeftMargin") ?? Text(doc.Root, "LeftMargin")),
                RightMargin = ToMm(Text(page, "RightMargin") ?? Text(doc.Root, "RightMargin")),
                TopMargin = ToMm(Text(page, "TopMargin") ?? Text(doc.Root, "TopMargin")),
                BottomMargin = ToMm(Text(page, "BottomMargin") ?? Text(doc.Root, "BottomMargin")),
                BodyWidth = ToMm(Text(section, "Width") ?? Text(doc.Root, "Width")),
                BodyHeight = ToMm(Text(body, "Height")),
                HeaderHeight = ToMm(Text(Child(page, "PageHeader"), "Height")),
                FooterHeight = ToMm(Text(Child(page, "PageFooter"), "Height")),
            };
            // SSRS defaults: Letter portrait.
            if (info.PageWidth <= 0) info.PageWidth = 215.9;
            if (info.PageHeight <= 0) info.PageHeight = 279.4;
            return info;
        }

        // ── item map ──

        private static readonly HashSet<string> ItemTypes = new HashSet<string>
            { "Textbox", "Rectangle", "Line", "Image", "Tablix", "Subreport", "Chart", "GaugePanel", "Map", "CustomReportItem", "List", "Table", "Matrix" };

        /// <summary>Every report item with its first-page position as designed (before growth), page-relative in mm.</summary>
        public static List<ReportItemBox> ItemMap(XDocument doc)
        {
            var page = Page(doc);
            XElement section = Section(doc);
            XElement pageElement = Child(section, "Page") ?? section;
            var items = new List<ReportItemBox>();
            var header = Child(pageElement, "PageHeader");
            var footer = Child(pageElement, "PageFooter");
            if (header != null) Collect(Child(header, "ReportItems"), "PageHeader", "", page.LeftMargin, page.TopMargin, items);
            Collect(Child(Child(section, "Body"), "ReportItems"), "Body", "", page.LeftMargin, page.TopMargin + page.HeaderHeight, items);
            if (footer != null) Collect(Child(footer, "ReportItems"), "PageFooter", "", page.LeftMargin, page.PageHeight - page.BottomMargin - page.FooterHeight, items);
            return items;
        }

        private static void Collect(XElement reportItems, string section, string container, double x0, double y0, List<ReportItemBox> items)
        {
            if (reportItems == null) return;
            foreach (var e in reportItems.Elements().Where(e => ItemTypes.Contains(e.Name.LocalName)))
            {
                var box = Box(e, section, container, x0 + ToMm(Text(e, "Left")), y0 + ToMm(Text(e, "Top")));
                items.Add(box);
                string path = container.Length == 0 ? box.Name : container + "/" + box.Name;
                if (e.Name.LocalName == "Rectangle")
                    Collect(Child(e, "ReportItems"), section, path, box.X, box.Y, items);
                else if (e.Name.LocalName == "Tablix")
                    CollectTablix(e, section, path, box.X, box.Y, items);
            }
        }

        private static ReportItemBox Box(XElement e, string section, string container, double x, double y)
        {
            var style = Child(e, "Style");
            var run = e.Descendants().FirstOrDefault(d => d.Name.LocalName == "TextRun");
            var runStyle = Child(run, "Style");
            string font = Text(runStyle, "FontFamily") ?? Text(style, "FontFamily");
            string size = Text(runStyle, "FontSize") ?? Text(style, "FontSize");
            string value = e.Name.LocalName == "Textbox"
                ? string.Join("", e.Descendants().Where(d => d.Name.LocalName == "TextRun").Select(r => Text(r, "Value") ?? ""))
                : e.Name.LocalName == "Image" ? Text(e, "Value") : null;
            if (value != null && value.Length > 160) value = value.Substring(0, 157) + "...";
            return new ReportItemBox
            {
                Name = e.Attribute("Name")?.Value ?? "",
                Type = e.Name.LocalName,
                Section = section,
                Container = container,
                X = x,
                Y = y,
                Width = ToMm(Text(e, "Width")),
                Height = ToMm(Text(e, "Height")),
                Value = value,
                Font = font,
                FontSize = size != null ? ToMm(size) * 72 / 25.4 : (double?)null,
                CanGrow = string.Equals(Text(e, "CanGrow"), "true", StringComparison.OrdinalIgnoreCase),
                CanShrink = string.Equals(Text(e, "CanShrink"), "true", StringComparison.OrdinalIgnoreCase),
                ConditionallyHidden = (Text(Child(e, "Visibility"), "Hidden") ?? "").StartsWith("="),
            };
        }

        /// <summary>Tablix cells: row headers on the left, column headers on top, then the body grid (first instance of each row).</summary>
        private static void CollectTablix(XElement tablix, string section, string container, double x0, double y0, List<ReportItemBox> items)
        {
            var body = Child(tablix, "TablixBody");
            var widths = Children(Child(body, "TablixColumns"), "TablixColumn").Select(c => ToMm(Text(c, "Width"))).ToList();
            var rows = Children(Child(body, "TablixRows"), "TablixRow").ToList();
            double rowHeaderWidth = HeaderDepth(Child(tablix, "TablixRowHierarchy"));
            double columnHeaderHeight = HeaderDepth(Child(tablix, "TablixColumnHierarchy"));
            double y = y0 + columnHeaderHeight;
            foreach (var row in rows)
            {
                double height = ToMm(Text(row, "Height"));
                double x = x0 + rowHeaderWidth;
                int column = 0;
                foreach (var cell in Children(Child(row, "TablixCells"), "TablixCell"))
                {
                    var contents = Child(cell, "CellContents");
                    int span = int.TryParse(Text(contents, "ColSpan"), out int s) && s > 1 ? s : 1;
                    double width = widths.Skip(column).Take(span).Sum();
                    var item = contents?.Elements().FirstOrDefault(c => ItemTypes.Contains(c.Name.LocalName));
                    if (item != null)
                    {
                        var box = Box(item, section, container, x, y);
                        box.Width = width;
                        box.Height = height;
                        items.Add(box);
                        if (item.Name.LocalName == "Rectangle")
                            Collect(Child(item, "ReportItems"), section, container + "/" + box.Name, x, y, items);
                    }
                    x += width;
                    column += span;
                }
                y += height;
            }
        }

        /// <summary>Size taken by the headers of a hierarchy (row header columns or column header rows), along its first branch.</summary>
        private static double HeaderDepth(XElement hierarchy)
        {
            double total = 0;
            var members = Child(hierarchy, "TablixMembers");
            while (members != null)
            {
                var member = Children(members, "TablixMember").FirstOrDefault();
                if (member == null) break;
                total += ToMm(Text(Child(member, "TablixHeader"), "Size"));
                members = Child(member, "TablixMembers");
            }
            return total;
        }

        // ── fonts and layout checks ──

        private static HashSet<string> _supportedFonts;

        /// <summary>The font families D365FO's cloud rendering offers (Microsoft Learn, "Supported fonts").</summary>
        public static HashSet<string> SupportedFonts()
        {
            if (_supportedFonts != null) return _supportedFonts;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("XppVsCode.Server.ReportFonts.txt"))
                if (stream != null)
                    using (var reader = new StreamReader(stream))
                        for (string line; (line = reader.ReadLine()) != null;)
                            if (line.Trim().Length > 0 && !line.StartsWith("#")) set.Add(line.Trim());
            return _supportedFonts = set;
        }

        private static HashSet<string> _installedFonts;

        public static HashSet<string> InstalledFonts()
        {
            if (_installedFonts != null) return _installedFonts;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                foreach (var family in fonts.Families) set.Add(family.Name);
            return _installedFonts = set;
        }

        /// <summary>Problems a preprinted layout can't afford: page overflow, fonts the cloud doesn't have or this machine lacks.</summary>
        public static List<string> Check(XDocument doc)
        {
            var warnings = new List<string>();
            var page = Page(doc);
            double usable = page.PageWidth - page.LeftMargin - page.RightMargin;
            if (page.BodyWidth > usable + 0.01)
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Body width {0:0.##} mm + margins {1:0.##} mm is wider than the page ({2:0.##} mm): every page gets an extra blank page.",
                    page.BodyWidth, page.LeftMargin + page.RightMargin, page.PageWidth));
            double usableHeight = page.PageHeight - page.TopMargin - page.BottomMargin - page.HeaderHeight - page.FooterHeight;
            if (page.BodyHeight > usableHeight + 0.01)
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Body height {0:0.##} mm is taller than the space between header and footer ({1:0.##} mm): the body breaks onto another page.",
                    page.BodyHeight, usableHeight));

            var fonts = doc.Descendants().Where(e => e.Name.LocalName == "FontFamily").Select(e => e.Value.Trim())
                .Where(f => f.Length > 0 && !f.StartsWith("=")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string font in fonts)
            {
                if (!SupportedFonts().Contains(font))
                    warnings.Add("Font '" + font + "' is not in Microsoft's list of fonts available to D365FO reports; the cloud substitutes another font.");
                else if (!InstalledFonts().Contains(font))
                    warnings.Add("Font '" + font + "' is not installed on this machine, so the preview uses a substitute; the environment renders it with the real font (check line breaks and widths there).");
            }

            foreach (var item in ItemMap(doc).Where(i => i.Section == "Body" && i.Container.Length == 0))
                if (item.X + item.Width > page.LeftMargin + page.BodyWidth + 0.01)
                    warnings.Add(string.Format(CultureInfo.InvariantCulture, "{0} '{1}' ends {2:0.##} mm right of the body's edge.",
                        item.Type, item.Name, item.X + item.Width - page.LeftMargin - page.BodyWidth));
            return warnings;
        }

        // ── preparing a renderable report ──

        internal sealed class Prepared
        {
            public string Rdl;
            public Dictionary<string, DataTable> Data = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Notes = new List<string>();
        }

        private static readonly Regex HelperCall = new Regex(
            @"(?<![\w.])(?:[A-Za-z_]\w*\.)*(?<cls>ColorUtilities|ConversionUtilities|CustomServiceProvider|DataProvider|DrillThroughProvider|FormattingUtilities|LabelUtilities|DataMethodUtility|BuiltInMethods)\.(?<method>\w+)\s*\(",
            RegexOptions.Compiled);

        // Implemented in the Code block below (VB names are case-insensitive).
        private static readonly HashSet<string> Shimmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PostDataMethodEvaluation", "UpdateAxContextPartition", "UpdateAxContext", "ReportInitialize", "UpdateCompanyNameParameter",
            "GetExtendedDataTypeFormat", "ConvertUtcToAxUserTimeZoneForUser", "ConvertUtcToAxUserTimeZone", "GetFullCompanyNameForUser",
            "GetFullCompanyName", "GetDefaultCompanyName", "GetUserCompany", "CurrentUserCompany", "ToDisplayStringAmount", "ToDisplayStringDate",
            "MapAXLanguageToCultureSupportedBySSRS", "GetAXLanguageCultureInfo", "IsArabicLanguage", "SplitReportItem", "NumeralsToTxt",
            "CultureNumeralVariant", "CultureTextRTL", "CultureReplaceServerDate", "ReplaceDateByCultureDate", "GetEnumLabelFromValue",
            "ConvertAXTimeToDateTime", "AosUtcNow", "DisplayAmountWithDecimals", "GetDateTimeParameterLabel", "GetDoubleParameterLabel",
            "GetUserPreferredCalendar", "TimeFormat",
        };

        private const string ShimCode = @"
' F&O Studio preview: local stand-ins for the D365FO report functions, which need an AOS session.
Private Function FOS_Culture(ByVal name As Object) As System.Globalization.CultureInfo
    Try
        If name Is Nothing OrElse CStr(name) = """" Then Return System.Globalization.CultureInfo.GetCultureInfo(""en-US"")
        Return System.Globalization.CultureInfo.GetCultureInfo(CStr(name))
    Catch
        Return System.Globalization.CultureInfo.GetCultureInfo(""en-US"")
    End Try
End Function
Public Function FOS_PostDataMethodEvaluation(ByVal context As Object, ByVal value As Object) As Object
    Return value
End Function
Public Function FOS_UpdateAxContextPartition(ByVal ParamArray args() As Object) As String
    Return """"
End Function
Public Function FOS_UpdateAxContext(ByVal ParamArray args() As Object) As String
    Return """"
End Function
Public Function FOS_ReportInitialize() As Boolean
    Return True
End Function
Public Function FOS_UpdateCompanyNameParameter(ByVal company As Object) As Object
    Return company
End Function
Public Function FOS_GetExtendedDataTypeFormat(ByVal edt As Object, ByVal culture As Object) As String
    Dim n As String = LCase(CStr(edt))
    If n.Contains(""date"") Then Return ""d""
    If n.Contains(""amount"") OrElse n.Contains(""price"") OrElse n.Contains(""mst"") Then Return ""N2""
    If n.Contains(""qty"") OrElse n.Contains(""quantity"") Then Return ""N2""
    Return """"
End Function
Public Function FOS_ConvertUtcToAxUserTimeZoneForUser(ByVal company As Object, ByVal user As Object, ByVal value As Object, ByVal format As Object, ByVal culture As Object) As String
    If value Is Nothing OrElse IsDBNull(value) Then Return """"
    Dim f As String = CStr(format)
    If f = """" Then f = ""g""
    Return CDate(value).ToString(f, FOS_Culture(culture))
End Function
Public Function FOS_ConvertUtcToAxUserTimeZone(ByVal company As Object, ByVal value As Object) As Object
    Return value
End Function
Public Function FOS_GetFullCompanyNameForUser(ByVal company As Object, ByVal user As Object) As String
    Return CStr(company)
End Function
Public Function FOS_GetFullCompanyName(ByVal company As Object) As String
    Return CStr(company)
End Function
Public Function FOS_GetDefaultCompanyName() As String
    Return CStr(Report.Parameters(""AX_CompanyName"").Value)
End Function
Public Function FOS_GetUserCompany(ByVal ParamArray args() As Object) As String
    Return CStr(Report.Parameters(""AX_CompanyName"").Value)
End Function
Public Function FOS_CurrentUserCompany() As String
    Return CStr(Report.Parameters(""AX_CompanyName"").Value)
End Function
Public Function FOS_ToDisplayStringAmount(ByVal culture As Object, ByVal amount As Double, ByVal displayZero As Boolean) As String
    If amount <> 0 OrElse displayZero Then Return amount.ToString(""#,0.00"", FOS_Culture(culture))
    Return """"
End Function
Public Function FOS_DisplayAmountWithDecimals(ByVal culture As Object, ByVal amount As Double, ByVal decimals As Integer, ByVal displayZero As Boolean) As String
    If amount <> 0 OrElse displayZero Then Return amount.ToString(""N"" & decimals, FOS_Culture(culture))
    Return """"
End Function
Public Function FOS_ToDisplayStringDate(ByVal culture As Object, ByVal value As Object, ByVal format As Object) As String
    If value Is Nothing OrElse IsDBNull(value) Then Return """"
    Dim d As Date = CDate(value)
    If d.Ticks = 0 OrElse d.Year <= 1900 Then Return """"
    Return d.ToString(CStr(format), FOS_Culture(culture))
End Function
Public Function FOS_MapAXLanguageToCultureSupportedBySSRS(ByVal culture As Object) As System.Globalization.CultureInfo
    Return FOS_Culture(culture)
End Function
Public Function FOS_GetAXLanguageCultureInfo(ByVal culture As Object) As System.Globalization.CultureInfo
    Return FOS_Culture(culture)
End Function
Public Function FOS_IsArabicLanguage(ByVal culture As Object) As Boolean
    Return LCase(CStr(culture)).StartsWith(""ar"")
End Function
Public Function FOS_SplitReportItem(ByVal number As Double, ByVal index As Integer) As String
    Dim s As String = Convert.ToString(Convert.ToInt64(number))
    If index >= 1 AndAlso index <= s.Length Then Return s.Substring(s.Length - index, 1)
    Return """"
End Function
Private Function FOS_Words(ByVal n As Long) As String
    Dim ones() As String = {""Zero"", ""One"", ""Two"", ""Three"", ""Four"", ""Five"", ""Six"", ""Seven"", ""Eight"", ""Nine"", ""Ten"", ""Eleven"", ""Twelve"", ""Thirteen"", ""Fourteen"", ""Fifteen"", ""Sixteen"", ""Seventeen"", ""Eighteen"", ""Nineteen""}
    Dim tens() As String = {"""", """", ""Twenty"", ""Thirty"", ""Forty"", ""Fifty"", ""Sixty"", ""Seventy"", ""Eighty"", ""Ninety""}
    If n < 20 Then Return ones(CInt(n))
    If n < 100 Then Return tens(CInt(n \ 10)) & IIf(n Mod 10 > 0, "" "" & ones(CInt(n Mod 10)), """")
    If n < 1000 Then Return ones(CInt(n \ 100)) & "" Hundred"" & IIf(n Mod 100 > 0, "" "" & FOS_Words(n Mod 100), """")
    If n < 1000000 Then Return FOS_Words(n \ 1000) & "" Thousand"" & IIf(n Mod 1000 > 0, "" "" & FOS_Words(n Mod 1000), """")
    If n < 1000000000 Then Return FOS_Words(n \ 1000000) & "" Million"" & IIf(n Mod 1000000 > 0, "" "" & FOS_Words(n Mod 1000000), """")
    Return FOS_Words(n \ 1000000000) & "" Billion"" & IIf(n Mod 1000000000 > 0, "" "" & FOS_Words(n Mod 1000000000), """")
End Function
Public Function FOS_NumeralsToTxt(ByVal number As Double, ByVal ParamArray args() As Object) As String
    Dim whole As Long = CLng(Math.Floor(Math.Abs(number)))
    Dim cents As Integer = CInt(Math.Round((Math.Abs(number) - whole) * 100))
    Return FOS_Words(whole) & "" and "" & cents.ToString(""00"") & ""/100""
End Function
Public Function FOS_CultureNumeralVariant(ByVal ParamArray args() As Object) As Integer
    Return 1
End Function
Public Function FOS_CultureTextRTL(ByVal ParamArray args() As Object) As String
    Return ""LTR""
End Function
Public Function FOS_CultureReplaceServerDate(ByVal text As Object, ByVal ParamArray args() As Object) As Object
    Return text
End Function
Public Function FOS_ReplaceDateByCultureDate(ByVal text As Object, ByVal ParamArray args() As Object) As Object
    Return text
End Function
Public Function FOS_GetEnumLabelFromValue(ByVal enumName As Object, ByVal value As Object, ByVal ParamArray args() As Object) As String
    Return CStr(value)
End Function
Public Function FOS_ConvertAXTimeToDateTime(ByVal seconds As Integer) As Date
    Return New Date(1900, 1, 1).AddSeconds(seconds)
End Function
Public Function FOS_AosUtcNow(ByVal ParamArray args() As Object) As Date
    Return Date.UtcNow
End Function
Public Function FOS_GetDateTimeParameterLabel(ByVal label As Object, ByVal value As Object, ByVal ParamArray args() As Object) As String
    Return CStr(label) & "" "" & CStr(value)
End Function
Public Function FOS_GetDoubleParameterLabel(ByVal label As Object, ByVal value As Object, ByVal ParamArray args() As Object) As String
    Return CStr(label) & "" "" & CStr(value)
End Function
Public Function FOS_GetUserPreferredCalendar(ByVal ParamArray args() As Object) As Object
    Return Nothing
End Function
Public Function FOS_TimeFormat(ByVal seconds As Object) As String
    Dim t As TimeSpan = TimeSpan.FromSeconds(CDbl(seconds))
    Return CInt(Math.Floor(t.TotalHours)).ToString(""00"") & "":"" & t.Minutes.ToString(""00"")
End Function
Public Function FOS_Unsupported(ByVal name As String, ByVal ParamArray args() As Object) As Object
    Return Nothing
End Function
";

        /// <summary>Replaces label references and D365FO helper calls in an expression; collects the helpers the preview can't run.</summary>
        public static string RewriteExpression(string text, Func<string, string> label, HashSet<string> unsupported)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // reportsc quotes labels ("Labels!@SYS123"); designs use them bare (=Labels!@SYS123).
            text = Regex.Replace(text, "\"Labels!(@[A-Za-z]\\w*(?::\\w+)?)\"", m => VbString(label(m.Groups[1].Value) ?? m.Groups[1].Value));
            text = Regex.Replace(text, @"Labels!(@[A-Za-z]\w*(?::\w+)?)", m => VbString(label(m.Groups[1].Value) ?? m.Groups[1].Value));
            if (!text.StartsWith("=")) return text;
            return HelperCall.Replace(text, m =>
            {
                string method = m.Groups["method"].Value;
                if (Shimmed.Contains(method)) return "Code.FOS_" + method + "(";
                unsupported.Add(m.Groups["cls"].Value + "." + method);
                int next = m.Index + m.Length;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                bool noArgs = next < text.Length && text[next] == ')';
                return "Code.FOS_Unsupported(\"" + m.Groups["cls"].Value + "." + method + "\"" + (noArgs ? "" : ", ");
            });
        }

        private static string VbString(string text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";

        private static readonly Regex FieldValue = new Regex(@"^=\s*(?:First\s*\(\s*)?Fields!(\w+)\.Value(?:\s*,\s*""[^""]*"")?\s*\)?\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex StringLiteral = new Regex(@"^=\s*(?<fn>UCase|LCase)?\s*\(?\s*""(?<text>(?:[^""]|"""")*)""\s*\)?\s*$", RegexOptions.IgnoreCase);

        /// <summary>
        /// The text a textbox prints when its value is a literal, a (resolved) label or a plain field of the data's first row;
        /// null for anything computed. Lets a comparison tie words on a page to the textbox that printed them.
        /// </summary>
        public static string StaticText(string value, IDictionary<string, DataTable> data)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (!value.StartsWith("=")) return value;
            var literal = StringLiteral.Match(value);
            if (literal.Success)
            {
                string text = literal.Groups["text"].Value.Replace("\"\"", "\"");
                string fn = literal.Groups["fn"].Value.ToLowerInvariant();
                return fn == "ucase" ? text.ToUpperInvariant() : fn == "lcase" ? text.ToLowerInvariant() : text;
            }
            var field = FieldValue.Match(value);
            if (field.Success && data != null)
                foreach (var table in data.Values)
                    if (table.Columns.Contains(field.Groups[1].Value) && table.Rows.Count > 0)
                        return Convert.ToString(table.Rows[0][field.Groups[1].Value], CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>
        /// The renderable report: datasets and parameters from metadata, sample or given rows, labels in the chosen language.
        /// rows: per dataset, the rows to use (field name to value); missing datasets get sampleRows generated rows.
        /// </summary>
        public static Prepared Prepare(XDocument design, IList<ReportDataSetInfo> dataSets, IList<ReportParameterInfo> parameters,
            Func<string, string> label, Dictionary<string, List<Dictionary<string, object>>> rows, int sampleRows,
            Dictionary<string, string> parameterValues, string language, string company,
            IList<KeyValuePair<string, KeyValuePair<string, string>>> embeddedImages)
        {
            var doc = new XDocument(design);
            XNamespace ns = doc.Root.Name.Namespace;
            XNamespace rd = RdNamespace;
            var result = new Prepared();
            var root = doc.Root;

            foreach (string name in new[] { "CodeModules", "Classes", "Code", "DataSources", "DataSets", "ReportParameters", "ReportParametersLayout" })
                Children(root, name).ToList().ForEach(e => e.Remove());

            var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in root.Descendants().Where(e => !e.HasElements && e.Value.Length > 0).ToList())
            {
                string before = e.Value;
                string after = RewriteExpression(before, label, unsupported);
                // A plain label reference as literal text (no expression) is resolved too, as the AOS does.
                if (e.Name.LocalName == "Value" && !after.StartsWith("=") && Regex.IsMatch(after, @"^@[A-Za-z]\w*(:\w+)?$"))
                    after = label(after) ?? after;
                if (before != after) e.Value = after;
            }
            if (unsupported.Count > 0)
                result.Notes.Add("Shown empty in the preview (they need the environment): " + string.Join(", ", unsupported.OrderBy(u => u)) + ".");

            // Fields and parameters the design uses but the metadata doesn't list would stop the whole render.
            string allText = string.Concat(root.Descendants().Where(e => !e.HasElements).Select(e => e.Value + "\n"));
            var usedParameters = new HashSet<string>(Regex.Matches(allText, @"Parameters!(\w+)").Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
            var usedFields = new HashSet<string>(Regex.Matches(allText, @"Fields!(\w+)").Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);

            root.Add(new XElement(ns + "DataSources",
                new XElement(ns + "DataSource", new XAttribute("Name", "FOStudioPreview"),
                    new XElement(ns + "ConnectionProperties",
                        new XElement(ns + "DataProvider", "System.Data.DataSet"),
                        new XElement(ns + "ConnectString", "/* Local Connection */")))));

            var dataSetsElement = new XElement(ns + "DataSets");
            var designDataSets = new HashSet<string>(root.Descendants().Where(e => e.Name.LocalName == "DataSetName").Select(e => e.Value), StringComparer.OrdinalIgnoreCase);
            var sets = dataSets.ToList();
            foreach (string missing in designDataSets.Where(n => !sets.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))))
            {
                sets.Add(new ReportDataSetInfo { Name = missing });
                result.Notes.Add("Dataset '" + missing + "' is used by the design but not defined on the report.");
            }
            foreach (var set in sets)
            {
                var fields = set.Fields.ToList();
                foreach (string f in usedFields.Where(f => !fields.Any(x => string.Equals(x.Key, f, StringComparison.OrdinalIgnoreCase))))
                    fields.Add(new KeyValuePair<string, string>(f, "System.String"));
                if (fields.Count == 0) fields.Add(new KeyValuePair<string, string>("Placeholder", "System.String"));
                dataSetsElement.Add(new XElement(ns + "DataSet", new XAttribute("Name", set.Name),
                    new XElement(ns + "Query",
                        new XElement(ns + "DataSourceName", "FOStudioPreview"),
                        new XElement(ns + "CommandText", "/* Local Query */")),
                    new XElement(ns + "Fields", fields.Select(f => new XElement(ns + "Field", new XAttribute("Name", f.Key),
                        new XElement(ns + "DataField", f.Key), new XElement(rd + "TypeName", NetType(f.Value).FullName))))));
                result.Data[set.Name] = Table(set.Name, fields, rows != null && rows.TryGetValue(set.Name, out var given) ? given : null, sampleRows);
            }
            root.Add(dataSetsElement);

            var parameterElement = new XElement(ns + "ReportParameters");
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = parameters.Concat(FrameworkParameters.Concat(usedParameters)
                .Where(n => !parameters.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)))
                .Select(n => new ReportParameterInfo { Name = n, DataType = "System.String" }));
            foreach (var p in all)
            {
                if (!declared.Add(p.Name)) continue;
                string type = ParameterType(p.DataType);
                parameterElement.Add(new XElement(ns + "ReportParameter", new XAttribute("Name", p.Name),
                    new XElement(ns + "DataType", type),
                    new XElement(ns + "Nullable", "true"),
                    type == "String" ? new XElement(ns + "AllowBlank", "true") : null,
                    // An empty prompt makes the engine treat the parameter as read-only.
                    new XElement(ns + "Prompt", string.IsNullOrWhiteSpace(p.Prompt) ? p.Name : p.Prompt),
                    new XElement(ns + "Hidden", "true"),
                    p.MultiValue ? new XElement(ns + "MultiValue", "true") : null));
                string value = parameterValues != null && parameterValues.TryGetValue(p.Name, out string v) ? v
                    : p.Name == "AX_CompanyName" ? company
                    : p.Name == "AX_RenderingCulture" ? language
                    : type == "Boolean" ? "false" : type == "Integer" || type == "Float" ? "0"
                    : type == "DateTime" ? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
                result.Parameters[p.Name] = value;
            }
            root.Add(parameterElement);

            if (embeddedImages != null && embeddedImages.Count > 0)
            {
                Children(root, "EmbeddedImages").ToList().ForEach(e => e.Remove());
                root.Add(new XElement(ns + "EmbeddedImages", embeddedImages.Select(i => new XElement(ns + "EmbeddedImage", new XAttribute("Name", i.Key),
                    new XElement(ns + "MIMEType", i.Value.Key), new XElement(ns + "ImageData", i.Value.Value)))));
            }
            root.Add(new XElement(ns + "Code", ShimCode));
            result.Rdl = doc.ToString(SaveOptions.DisableFormatting);
            return result;
        }

        private static string ParameterType(string netType)
        {
            switch ((netType ?? "").Replace("System.", ""))
            {
                case "Boolean": return "Boolean";
                case "DateTime": return "DateTime";
                case "Int16": case "Int32": case "Int64": case "Byte": return "Integer";
                case "Double": case "Decimal": case "Single": return "Float";
                default: return "String";
            }
        }

        private static Type NetType(string name)
        {
            switch ((name ?? "").Replace("System.", ""))
            {
                case "Boolean": return typeof(bool);
                case "DateTime": return typeof(DateTime);
                case "Int16": case "Int32": return typeof(int);
                case "Int64": return typeof(long);
                case "Double": case "Single": return typeof(double);
                case "Decimal": return typeof(decimal);
                case "Byte[]": return typeof(byte[]);
                default: return typeof(string);
            }
        }

        private static DataTable Table(string name, List<KeyValuePair<string, string>> fields, List<Dictionary<string, object>> given, int sampleRows)
        {
            var table = new DataTable(name);
            foreach (var f in fields)
                if (!table.Columns.Contains(f.Key)) table.Columns.Add(f.Key, NetType(f.Value));
            if (given != null)
            {
                foreach (var source in given)
                {
                    var row = table.NewRow();
                    foreach (DataColumn c in table.Columns)
                        row[c] = source != null && source.TryGetValue(c.ColumnName, out object v) && v != null ? ConvertValue(v, c.DataType) : Sample(c, table.Rows.Count);
                    table.Rows.Add(row);
                }
                return table;
            }
            for (int i = 0; i < Math.Max(0, sampleRows); i++)
            {
                var row = table.NewRow();
                foreach (DataColumn c in table.Columns) row[c] = Sample(c, i);
                table.Rows.Add(row);
            }
            return table;
        }

        private static object ConvertValue(object value, Type type)
        {
            try
            {
                if (type == typeof(byte[])) return value is string s ? Convert.FromBase64String(s) : (object)DBNull.Value;
                if (type == typeof(DateTime)) return DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return DBNull.Value;
            }
        }

        /// <summary>A plausible value: the field name for text, sizeable amounts for numbers, so layouts show realistic widths.</summary>
        private static object Sample(DataColumn column, int row)
        {
            Type t = column.DataType;
            if (t == typeof(string)) return row == 0 ? column.ColumnName : column.ColumnName + " " + (row + 1);
            if (t == typeof(DateTime)) return new DateTime(2026, 1, 31).AddDays(row);
            if (t == typeof(bool)) return false;
            if (t == typeof(int) || t == typeof(long)) return Convert.ChangeType(row + 1, t, CultureInfo.InvariantCulture);
            if (t == typeof(double) || t == typeof(decimal)) return Convert.ChangeType(12345.67 * (row + 1), t, CultureInfo.InvariantCulture);
            return DBNull.Value;
        }
    }
}
