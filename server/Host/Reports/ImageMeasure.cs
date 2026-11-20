using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace XppVsCode.Server.Host.Reports
{
    internal sealed class MeasureOptions
    {
        /// <summary>Letter, Legal, A4, A5, Executive, or "WIDTHxHEIGHTmm" / "WIDTHxHEIGHTin": the scan shows the whole sheet.</summary>
        public string Paper;
        /// <summary>Scan resolution when the scan does not show the whole sheet (and no reference is given).</summary>
        public double? Dpi;
        /// <summary>Two points in image pixels (x1, y1, x2, y2) and their real distance, for scans of part of a form.</summary>
        public double[] ReferencePx;
        public double ReferenceMm;
        public bool Deskew = true;
    }

    /// <summary>An image with its scale: millimetres from the image's top-left corner = pixels / PxPerMm.</summary>
    internal sealed class CalibratedImage : IDisposable
    {
        public Bitmap Image;
        public double PxPerMmX, PxPerMmY, SkewDegrees;
        public string Calibration;
        public List<string> Notes = new List<string>();
        public double WidthMm => Image.Width / PxPerMmX;
        public double HeightMm => Image.Height / PxPerMmY;
        public void Dispose() => Image?.Dispose();
    }

    internal sealed class MeasuredLine
    {
        public bool Horizontal;
        public double X1, Y1, X2, Y2, ThicknessMm;
        public double LengthMm => Horizontal ? X2 - X1 : Y2 - Y1;
    }

    internal sealed class MeasuredBox
    {
        public double X, Y, Width, Height;
    }

    internal sealed class OcrWord
    {
        public string Text;
        public int Line;
        public double X, Y, Width, Height;
    }

    /// <summary>
    /// Measures scanned forms for precision report designs: scale calibration, deskew, ruled lines and boxes, OCR words with
    /// their boxes (Windows' built-in OCR, on this machine), grid images to read positions from, and overlays of a rendered
    /// report on a scan. Every coordinate is in millimetres from the top-left corner of the (deskewed) image.
    /// </summary>
    internal static class ImageMeasure
    {
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };
        private const long MaxFileBytes = 200L * 1024 * 1024;

        public static readonly Dictionary<string, double[]> Papers = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Letter"] = new[] { 215.9, 279.4 },
            ["Legal"] = new[] { 215.9, 355.6 },
            ["A4"] = new[] { 210.0, 297.0 },
            ["A5"] = new[] { 148.0, 210.0 },
            ["Executive"] = new[] { 184.15, 266.7 },
        };

        // ── loading and calibration ──

        public static CalibratedImage Load(string path, MeasureOptions options)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Pass imagePath (a PNG, JPEG, TIFF or BMP scan; export a PDF page as an image first).");
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
            // Only local files: a network path would make the server contact another machine with the user's credentials.
            if (new Uri(path).IsUnc) throw new ArgumentException("imagePath must be a file on this machine, not a network path.");
            if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("imagePath must be a PNG, JPEG, TIFF, BMP or GIF image" + (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? " (export the PDF page as an image at 300 dpi first)." : "."));
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("Image not found: " + path);
            if (file.Length > MaxFileBytes) throw new ArgumentException("The image is larger than 200 MB.");

            Bitmap bitmap;
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            using (var source = new Bitmap(stream))
            {
                bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
                bitmap.SetResolution(source.HorizontalResolution, source.VerticalResolution);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.White);
                    g.DrawImage(source, 0, 0, source.Width, source.Height);
                }
            }
            var image = new CalibratedImage { Image = bitmap };
            options = options ?? new MeasureOptions();

            if (options.Deskew)
            {
                double angle = SkewAngle(bitmap);
                if (Math.Abs(angle) >= 0.05)
                {
                    image.Image = Rotate(bitmap, -angle);
                    bitmap.Dispose();
                    image.Notes.Add(string.Format(CultureInfo.InvariantCulture, "Straightened a {0:0.00}° skew.", angle));
                }
                image.SkewDegrees = angle;
            }
            Calibrate(image, options);
            return image;
        }

        private static void Calibrate(CalibratedImage image, MeasureOptions options)
        {
            int w = image.Image.Width, h = image.Image.Height;
            if (options.ReferencePx != null && options.ReferencePx.Length == 4 && options.ReferenceMm > 0)
            {
                double px = Math.Sqrt(Math.Pow(options.ReferencePx[2] - options.ReferencePx[0], 2) + Math.Pow(options.ReferencePx[3] - options.ReferencePx[1], 2));
                image.PxPerMmX = image.PxPerMmY = px / options.ReferenceMm;
                image.Calibration = string.Format(CultureInfo.InvariantCulture, "reference: {0:0.#} px = {1:0.##} mm", px, options.ReferenceMm);
                return;
            }
            double[] paper = PaperSize(options.Paper, w > h);
            if (paper == null && options.Dpi == null)
            {
                double metadataDpi = image.Image.HorizontalResolution;
                // 96 and 72 are what screenshots and many converters write, not a scanner's resolution.
                if (metadataDpi >= 100 && Math.Abs(metadataDpi - image.Image.VerticalResolution) < 1)
                    options.Dpi = metadataDpi;
                else
                    paper = ClosestPaper(w, h);
            }
            if (paper != null)
            {
                image.PxPerMmX = w / paper[0];
                image.PxPerMmY = h / paper[1];
                image.Calibration = string.Format(CultureInfo.InvariantCulture, "whole sheet {0:0.##} x {1:0.##} mm", paper[0], paper[1]);
                double ratio = image.PxPerMmX / image.PxPerMmY;
                if (Math.Abs(ratio - 1) > 0.015)
                    image.Notes.Add(string.Format(CultureInfo.InvariantCulture,
                        "The horizontal and vertical scales differ by {0:0.#}%: the scan is cropped or not the paper size given. Pass referencePoints (two points and their distance) or the scan's dpi instead.",
                        Math.Abs(ratio - 1) * 100));
                return;
            }
            if (options.Dpi == null)
                    throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                        "The image ({0} x {1} px) records no scan resolution and its shape matches no paper size, so its scale is unknown. Pass paper (the sheet's size, e.g. 8.5x3.5in for a cheque), the scan's dpi, or referencePoints (two points and their real distance in mm).",
                        w, h));
            image.PxPerMmX = image.PxPerMmY = options.Dpi.Value / 25.4;
            image.Calibration = string.Format(CultureInfo.InvariantCulture, "{0:0} dpi", options.Dpi.Value);
        }

        public static double[] PaperSize(string paper, bool landscape)
        {
            if (string.IsNullOrWhiteSpace(paper)) return null;
            double[] size;
            if (!Papers.TryGetValue(paper.Trim(), out size))
            {
                var m = System.Text.RegularExpressions.Regex.Match(paper.Trim(), @"^(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*(mm|in|cm)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) throw new ArgumentException("paper must be Letter, Legal, A4, A5, Executive or WIDTHxHEIGHT with mm, cm or in (e.g. 8.5x3.5in).");
                double factor = m.Groups[3].Value.ToLowerInvariant() == "in" ? 25.4 : m.Groups[3].Value.ToLowerInvariant() == "cm" ? 10 : 1;
                return new[] { double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * factor, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * factor };
            }
            return landscape ? new[] { size[1], size[0] } : size;
        }

        private static double[] ClosestPaper(int w, int h)
        {
            double aspect = (double)w / h;
            var best = Papers.Values.Select(p => w > h ? new[] { p[1], p[0] } : p)
                .OrderBy(p => Math.Abs(p[0] / p[1] - aspect)).First();
            return Math.Abs(best[0] / best[1] - aspect) < 0.03 ? best : null;
        }

        // ── pixels ──

        /// <summary>Grey levels (0 black..255 white) of the image, row by row.</summary>
        public static byte[] Gray(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var raw = new byte[data.Stride * bitmap.Height];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                var gray = new byte[bitmap.Width * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    int row = y * data.Stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        int i = row + x * 3;
                        gray[y * bitmap.Width + x] = (byte)((raw[i] * 29 + raw[i + 1] * 150 + raw[i + 2] * 77) >> 8);
                    }
                }
                return gray;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>Otsu's threshold: the grey level that best separates ink from paper.</summary>
        public static int Threshold(byte[] gray)
        {
            var histogram = new long[256];
            foreach (byte g in gray) histogram[g]++;
            long total = gray.Length;
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += i * (double)histogram[i];
            double sumB = 0, best = 0;
            long wB = 0;
            int threshold = 128;
            for (int i = 0; i < 256; i++)
            {
                wB += histogram[i];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;
                sumB += i * (double)histogram[i];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; threshold = i; }
            }
            // Pale scans: never treat light grey paper texture as ink.
            return Math.Min(threshold, 190);
        }

        private static Bitmap Scaled(Bitmap source, int maxWidth)
        {
            double scale = Math.Min(1.0, (double)maxWidth / source.Width);
            var result = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(source, 0, 0, result.Width, result.Height);
            }
            return result;
        }

        /// <summary>Skew in degrees (positive = clockwise), from the angle at which text rows and rules line up best (-3..3°).</summary>
        public static double SkewAngle(Bitmap bitmap)
        {
            using (var small = Scaled(bitmap, 1000))
            {
                byte[] gray = Gray(small);
                int threshold = Threshold(gray), w = small.Width, h = small.Height;
                var points = new List<int>();
                for (int i = 0; i < gray.Length; i++) if (gray[i] < threshold) points.Add(i);
                if (points.Count < 200) return 0;
                int step = Math.Max(1, points.Count / 150000);
                double bestAngle = 0, bestScore = double.MinValue;
                for (double angle = -3; angle <= 3.0001; angle += 0.1)
                {
                    double rad = angle * Math.PI / 180, sin = Math.Sin(rad), cos = Math.Cos(rad);
                    var bins = new int[h * 2];
                    for (int k = 0; k < points.Count; k += step)
                    {
                        int x = points[k] % w, y = points[k] / w;
                        int bin = (int)(y * cos - x * sin) + h / 2;
                        if (bin >= 0 && bin < bins.Length) bins[bin]++;
                    }
                    double score = bins.Sum(b => (double)b * b);
                    if (score > bestScore) { bestScore = score; bestAngle = angle; }
                }
                return Math.Round(bestAngle, 2);
            }
        }

        private static Bitmap Rotate(Bitmap source, double degrees)
        {
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            result.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TranslateTransform(source.Width / 2f, source.Height / 2f);
                g.RotateTransform((float)degrees);
                g.TranslateTransform(-source.Width / 2f, -source.Height / 2f);
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }
            return result;
        }

        // ── rules and boxes ──

        /// <summary>Horizontal and vertical rules at least minLengthMm long (text strokes are shorter), with their thickness.</summary>
        public static List<MeasuredLine> Lines(CalibratedImage image, double minLengthMm)
        {
            Bitmap bitmap = image.Image;
            int w = bitmap.Width, h = bitmap.Height;
            byte[] gray = Gray(bitmap);
            int threshold = Threshold(gray);
            var lines = new List<MeasuredLine>();
            int minH = Math.Max(5, (int)(minLengthMm * image.PxPerMmX)), minV = Math.Max(5, (int)(minLengthMm * image.PxPerMmY));
            int gap = Math.Max(1, (int)(0.4 * image.PxPerMmX));
            lines.AddRange(Runs(gray, w, h, threshold, minH, gap, true, image));
            lines.AddRange(Runs(gray, w, h, threshold, minV, Math.Max(1, (int)(0.4 * image.PxPerMmY)), false, image));
            return lines;
        }

        private static IEnumerable<MeasuredLine> Runs(byte[] gray, int w, int h, int threshold, int minRun, int maxGap, bool horizontal, CalibratedImage image)
        {
            int outer = horizontal ? h : w, inner = horizontal ? w : h;
            // Segments per scan line: start, end (inclusive) along the line.
            var open = new List<int[]>(); // start, end, firstScan, lastScan
            var done = new List<int[]>();
            for (int s = 0; s < outer; s++)
            {
                var segments = new List<int[]>();
                int start = -1, lastDark = -1;
                for (int t = 0; t <= inner; t++)
                {
                    bool dark = t < inner && gray[horizontal ? s * w + t : t * w + s] < threshold;
                    if (dark)
                    {
                        if (start < 0) start = t;
                        lastDark = t;
                    }
                    else if (start >= 0 && t - lastDark > maxGap)
                    {
                        if (lastDark - start + 1 >= minRun) segments.Add(new[] { start, lastDark });
                        start = -1;
                    }
                }
                // Rows of one thick rule merge with the previous row's segment when they overlap mostly.
                var next = new List<int[]>();
                foreach (var seg in segments)
                {
                    var match = open.FirstOrDefault(o => o[3] == s - 1 && Overlap(o[0], o[1], seg[0], seg[1]) >= 0.7 * Math.Min(o[1] - o[0], seg[1] - seg[0]));
                    if (match != null)
                    {
                        match[0] = Math.Min(match[0], seg[0]);
                        match[1] = Math.Max(match[1], seg[1]);
                        match[3] = s;
                        next.Add(match);
                    }
                    else next.Add(new[] { seg[0], seg[1], s, s });
                }
                done.AddRange(open.Where(o => !next.Contains(o)));
                open = next;
            }
            done.AddRange(open);
            foreach (var r in done)
            {
                double thicknessPx = r[3] - r[2] + 1;
                // Solid blocks (filled areas, logos) are not rules.
                if (thicknessPx > (horizontal ? image.PxPerMmY : image.PxPerMmX) * 3) continue;
                if (horizontal)
                    yield return new MeasuredLine
                    {
                        Horizontal = true,
                        X1 = r[0] / image.PxPerMmX, X2 = (r[1] + 1) / image.PxPerMmX,
                        Y1 = (r[2] + r[3] + 1) / 2.0 / image.PxPerMmY, Y2 = (r[2] + r[3] + 1) / 2.0 / image.PxPerMmY,
                        ThicknessMm = thicknessPx / image.PxPerMmY,
                    };
                else
                    yield return new MeasuredLine
                    {
                        Horizontal = false,
                        Y1 = r[0] / image.PxPerMmY, Y2 = (r[1] + 1) / image.PxPerMmY,
                        X1 = (r[2] + r[3] + 1) / 2.0 / image.PxPerMmX, X2 = (r[2] + r[3] + 1) / 2.0 / image.PxPerMmX,
                        ThicknessMm = thicknessPx / image.PxPerMmX,
                    };
            }
        }

        private static int Overlap(int a1, int a2, int b1, int b2) => Math.Min(a2, b2) - Math.Max(a1, b1);

        /// <summary>
        /// Rectangles: two horizontal rules with the same ends, nothing like them in between, and ink down both sides (checked
        /// in the pixels, so short boxes whose sides are below the rule length still count).
        /// </summary>
        public static List<MeasuredBox> Boxes(CalibratedImage image, List<MeasuredLine> lines, double toleranceMm)
        {
            byte[] gray = Gray(image.Image);
            int threshold = Threshold(gray), w = image.Image.Width, h = image.Image.Height;
            var hs = lines.Where(l => l.Horizontal).OrderBy(l => l.Y1).Take(400).ToList();
            bool SameEnds(MeasuredLine a, MeasuredLine b) => Math.Abs(a.X1 - b.X1) <= toleranceMm && Math.Abs(a.X2 - b.X2) <= toleranceMm;
            double SideInk(double xMm, double y1Mm, double y2Mm)
            {
                int y1 = (int)(y1Mm * image.PxPerMmY) + 2, y2 = (int)(y2Mm * image.PxPerMmY) - 2;
                if (y2 <= y1) return 0;
                int cx = (int)Math.Round(xMm * image.PxPerMmX);
                double best = 0;
                for (int x = Math.Max(0, cx - 3); x <= Math.Min(w - 1, cx + 3); x++)
                {
                    int dark = 0;
                    for (int y = Math.Max(0, y1); y <= Math.Min(h - 1, y2); y++) if (gray[y * w + x] < threshold) dark++;
                    best = Math.Max(best, (double)dark / (y2 - y1 + 1));
                }
                return best;
            }
            var boxes = new List<MeasuredBox>();
            for (int i = 0; i < hs.Count; i++)
                for (int j = i + 1; j < hs.Count; j++)
                {
                    var top = hs[i];
                    var bottom = hs[j];
                    double height = bottom.Y1 - top.Y1;
                    if (height < 1 || height > 250 || !SameEnds(top, bottom)) continue;
                    if (hs.Skip(i + 1).Take(j - i - 1).Any(m => SameEnds(m, top))) break;
                    double left = (top.X1 + bottom.X1) / 2, right = (top.X2 + bottom.X2) / 2;
                    if (SideInk(left, top.Y1, bottom.Y1) >= 0.85 && SideInk(right, top.Y1, bottom.Y1) >= 0.85)
                    {
                        boxes.Add(new MeasuredBox { X = left, Y = top.Y1, Width = right - left, Height = height });
                        break;
                    }
                }
            return boxes;
        }

        // ── OCR (Windows.Media.Ocr, local) ──

        /// <summary>Words with their boxes in mm, read by Windows' OCR engine; null with a problem when it isn't available.</summary>
        public static List<OcrWord> Ocr(Bitmap bitmap, double pxPerMmX, double pxPerMmY, string language, out string problem)
        {
            problem = null;
            string temp = Path.Combine(Path.GetTempPath(), "fos-ocr-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Type engineType = WinRtType("Windows.Media.Ocr.OcrEngine");
                if (engineType == null) { problem = "Windows OCR is not available on this machine (Windows 10 or later is needed)."; return null; }
                uint max = (uint)engineType.GetProperty("MaxImageDimension").GetValue(null);
                double scale = Math.Min(1.0, (double)max / Math.Max(bitmap.Width, bitmap.Height));
                using (var input = scale < 1 ? Scaled(bitmap, (int)(bitmap.Width * scale)) : (Bitmap)bitmap.Clone())
                    input.Save(temp, ImageFormat.Png);
                double sx = (double)bitmap.Width / Math.Max(1, (int)(bitmap.Width * scale)), sy = (double)bitmap.Height / Math.Max(1, (int)(bitmap.Height * scale));
                if (scale >= 1) sx = sy = 1;

                Type fileType = WinRtType("Windows.Storage.StorageFile");
                Type decoderType = WinRtType("Windows.Graphics.Imaging.BitmapDecoder");
                Type accessMode = WinRtType("Windows.Storage.FileAccessMode");
                object file = Invoke(fileType.GetMethod("GetFileFromPathAsync"), null, temp);
                object stream = Invoke(fileType.GetMethod("OpenAsync", new[] { accessMode }), file, Enum.Parse(accessMode, "Read"));
                object decoder = Invoke(decoderType.GetMethods().First(m => m.Name == "CreateAsync" && m.GetParameters().Length == 1), null, stream);
                object softwareBitmap = Invoke(decoderType.GetMethods().First(m => m.Name == "GetSoftwareBitmapAsync" && m.GetParameters().Length == 0), decoder);

                object engine = null;
                if (!string.IsNullOrEmpty(language))
                {
                    Type languageType = WinRtType("Windows.Globalization.Language");
                    object lang = Activator.CreateInstance(languageType, language);
                    engine = engineType.GetMethod("TryCreateFromLanguage").Invoke(null, new[] { lang });
                }
                engine = engine ?? engineType.GetMethod("TryCreateFromUserProfileLanguages").Invoke(null, null);
                if (engine == null) { problem = "Windows has no OCR language installed (Settings > Time & language > Language: add a language with its optical character recognition feature)."; return null; }
                object result = Invoke(engineType.GetMethod("RecognizeAsync"), engine, softwareBitmap);
                (stream as IDisposable)?.Dispose();
                (softwareBitmap as IDisposable)?.Dispose();

                var words = new List<OcrWord>();
                int lineIndex = 0;
                foreach (object line in (IEnumerable)result.GetType().GetProperty("Lines").GetValue(result))
                {
                    foreach (object word in (IEnumerable)line.GetType().GetProperty("Words").GetValue(line))
                    {
                        object rect = word.GetType().GetProperty("BoundingRect").GetValue(word);
                        double Get(string name) => Convert.ToDouble(rect.GetType().GetProperty(name).GetValue(rect), CultureInfo.InvariantCulture);
                        words.Add(new OcrWord
                        {
                            Text = (string)word.GetType().GetProperty("Text").GetValue(word),
                            Line = lineIndex,
                            X = Get("X") * sx / pxPerMmX,
                            Y = Get("Y") * sy / pxPerMmY,
                            Width = Get("Width") * sx / pxPerMmX,
                            Height = Get("Height") * sy / pxPerMmY,
                        });
                    }
                    lineIndex++;
                }
                return words;
            }
            catch (Exception ex) when (ex is TargetInvocationException || ex is InvalidOperationException || ex is COMException || ex is TypeLoadException || ex is NullReferenceException || ex is AggregateException || ex is FileNotFoundException || ex is UnauthorizedAccessException)
            {
                problem = "Windows OCR failed: " + (ex.InnerException ?? ex).Message;
                return null;
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static Type WinRtType(string name) =>
            Type.GetType(name + ", Windows.Foundation.UniversalApiContract, ContentType=WindowsRuntime")
            ?? Type.GetType(name + ", Windows.Foundation, ContentType=WindowsRuntime")
            ?? Type.GetType(name + ", Windows, ContentType=WindowsRuntime");

        private static MethodInfo _asTask;

        /// <summary>Calls a WinRT async method and waits for its result. The result type comes from the method's declared
        /// IAsyncOperation&lt;T&gt;: WinRT objects don't list their generic interfaces through reflection.</summary>
        private static object Invoke(MethodInfo method, object target, params object[] args)
        {
            object operation = method.Invoke(target, args);
            if (_asTask == null)
            {
                var assembly = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                _asTask = assembly.GetType("System.WindowsRuntimeSystemExtensions").GetMethods()
                    .First(m => m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1");
            }
            Type resultType = method.ReturnType.GetGenericArguments()[0];
            var task = (Task)_asTask.MakeGenericMethod(resultType).Invoke(null, new[] { operation });
            if (!task.Wait(TimeSpan.FromSeconds(60))) throw new InvalidOperationException("Timed out.");
            return task.GetType().GetProperty("Result").GetValue(task);
        }

        // ── pictures for the agent ──

        /// <summary>
        /// The image (or the cropped area, x/y/width/height in mm) with rulers and a millimetre grid, scaled to maxWidth pixels:
        /// what the agent's vision reads positions from.
        /// </summary>
        public static byte[] GridImage(CalibratedImage image, double[] cropMm, int maxWidth, double gridMm)
        {
            double x0 = 0, y0 = 0, wMm = image.WidthMm, hMm = image.HeightMm;
            if (cropMm != null && cropMm.Length == 4)
            {
                x0 = Math.Max(0, cropMm[0]);
                y0 = Math.Max(0, cropMm[1]);
                wMm = Math.Max(1, Math.Min(cropMm[2], image.WidthMm - x0));
                hMm = Math.Max(1, Math.Min(cropMm[3], image.HeightMm - y0));
            }
            const int Ruler = 34;
            double scale = Math.Min((maxWidth - Ruler) / wMm, (1800.0 - Ruler) / hMm); // output px per mm
            int width = (int)(wMm * scale) + Ruler, height = (int)(hMm * scale) + Ruler;
            if (gridMm <= 0) gridMm = scale >= 12 ? 1 : scale >= 5 ? 2 : 5;
            var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(result))
            using (var font = new Font("Segoe UI", 8f))
            using (var minor = new Pen(Color.FromArgb(70, 0, 120, 255), 1))
            using (var major = new Pen(Color.FromArgb(150, 255, 0, 0), 1))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var source = new RectangleF((float)(x0 * image.PxPerMmX), (float)(y0 * image.PxPerMmY), (float)(wMm * image.PxPerMmX), (float)(hMm * image.PxPerMmY));
                g.DrawImage(image.Image, new RectangleF(Ruler, Ruler, (float)(wMm * scale), (float)(hMm * scale)), source, GraphicsUnit.Pixel);
                double majorStep = gridMm < 2 ? 10 : gridMm < 5 ? 10 : 50;
                for (double mm = Math.Ceiling(x0 / gridMm) * gridMm; mm <= x0 + wMm + 1e-6; mm += gridMm)
                {
                    float px = Ruler + (float)((mm - x0) * scale);
                    bool isMajor = Math.Abs(mm / majorStep - Math.Round(mm / majorStep)) < 1e-6;
                    g.DrawLine(isMajor ? major : minor, px, Ruler, px, height);
                    if (isMajor) g.DrawString(mm.ToString("0", CultureInfo.InvariantCulture), font, Brushes.Black, px - 8, 2);
                }
                for (double mm = Math.Ceiling(y0 / gridMm) * gridMm; mm <= y0 + hMm + 1e-6; mm += gridMm)
                {
                    float py = Ruler + (float)((mm - y0) * scale);
                    bool isMajor = Math.Abs(mm / majorStep - Math.Round(mm / majorStep)) < 1e-6;
                    g.DrawLine(isMajor ? major : minor, Ruler, py, width, py);
                    if (isMajor) g.DrawString(mm.ToString("0", CultureInfo.InvariantCulture), font, Brushes.Black, 1, py - 7);
                }
                g.DrawString("mm", font, Brushes.DimGray, 2, 18);
            }
            return Png(result);
        }

        /// <summary>The scan resampled onto the page at dpi (scan mm = page mm, shifted by offsetMm).</summary>
        public static Bitmap OnPage(CalibratedImage scan, double pageWidthMm, double pageHeightMm, int dpi, double offsetXMm, double offsetYMm)
        {
            double px = dpi / 25.4;
            var result = new Bitmap((int)Math.Round(pageWidthMm * px), (int)Math.Round(pageHeightMm * px), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(scan.Image, new RectangleF((float)(offsetXMm * px), (float)(offsetYMm * px), (float)(scan.WidthMm * px), (float)(scan.HeightMm * px)));
            }
            return result;
        }

        /// <summary>Scan ink in red, report ink in blue, both in black: where they coincide the layout matches.</summary>
        public static byte[] Overlay(Bitmap scanOnPage, Bitmap render)
        {
            int w = scanOnPage.Width, h = scanOnPage.Height;
            using (var renderOnPage = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(renderOnPage))
                {
                    g.Clear(Color.White);
                    g.DrawImage(render, 0, 0, w, h);
                }
                byte[] a = Gray(scanOnPage), b = Gray(renderOnPage);
                int ta = Threshold(a), tb = Math.Min(200, Threshold(b));
                var result = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                var data = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var raw = new byte[data.Stride * h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            bool s = a[y * w + x] < ta, r = b[y * w + x] < tb;
                            int i = y * data.Stride + x * 3; // B, G, R
                            if (s && r) { raw[i] = 0; raw[i + 1] = 0; raw[i + 2] = 0; }
                            else if (s) { raw[i] = 90; raw[i + 1] = 90; raw[i + 2] = 255; }
                            else if (r) { raw[i] = 255; raw[i + 1] = 120; raw[i + 2] = 0; }
                            else { raw[i] = 255; raw[i + 1] = 255; raw[i + 2] = 255; }
                        }
                    Marshal.Copy(raw, 0, data.Scan0, raw.Length);
                }
                finally
                {
                    result.UnlockBits(data);
                }
                return Png(result);
            }
        }

        public static byte[] Png(Bitmap bitmap)
        {
            using (bitmap)
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        public static string Normalize(string text) =>
            new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
