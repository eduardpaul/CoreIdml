using System.Globalization;
using System.Text.Json;
using CoreIdml.Pdf.Imaging;

namespace CoreIdml.Pdf.Tests;

/// <summary>Per-page result of comparing two rasters.</summary>
/// <param name="DiffRatio">Fraction of pixels whose largest channel difference exceeds the tolerance (0..1).</param>
/// <param name="MeanError">Mean absolute channel difference over all pixels (0..1).</param>
/// <param name="SizeMismatch">The rasters had different sizes; the extra area counts as different.</param>
internal sealed record PageDiff(int Page, int Width, int Height, double DiffRatio, double MeanError, bool SizeMismatch)
{
    /// <summary>Differing pixels per region of the reference page (text, images, other = vector art and empty paper).</summary>
    public IReadOnlyDictionary<string, (long Differing, long Area)> Regions { get; init; } = new Dictionary<string, (long, long)>();

    public string RegionReport => string.Join(", ", Regions.Select(r => string.Create(CultureInfo.InvariantCulture,
        $"{r.Key} {(r.Value.Area == 0 ? 0 : 100.0 * r.Value.Differing / r.Value.Area):0.0}% of its area ({(DiffRatio == 0 ? 0 : 100.0 * r.Value.Differing / (DiffRatio * Width * Height)):0}% of all differences)")));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"page {Page}: {DiffRatio * 100,6:0.000}% pixels differ, mean error {MeanError * 100,6:0.000}% ({Width}x{Height}{(SizeMismatch ? ", SIZE MISMATCH" : string.Empty)})");
}

/// <summary>
/// Pixel comparison for the visual harness. Both images are composited on white; a pixel "differs" when any
/// channel differs by more than <c>tolerance</c> (0-255), which absorbs anti-aliasing noise but not misplaced glyphs.
/// </summary>
internal static class VisualDiff
{
    public static PageDiff Compare(int page, RasterImage expected, RasterImage actual, int tolerance, string? diffPath = null, byte[]? regions = null)
    {
        string[] regionNames = ["other", "images", "text"];
        var regionDiff = new long[3];
        var regionArea = new long[3];
        var a = Flatten(expected);
        var b = Flatten(actual);
        var width = Math.Max(expected.Width, actual.Width);
        var height = Math.Max(expected.Height, actual.Height);
        var mismatch = expected.Width != actual.Width || expected.Height != actual.Height;
        long differing = 0;
        double errorSum = 0;
        var diff = diffPath is null ? null : new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inA = x < expected.Width && y < expected.Height;
                var inB = x < actual.Width && y < actual.Height;
                var maxDelta = 255;
                double sum = 3 * 255;
                if (inA && inB)
                {
                    var ia = ((y * expected.Width) + x) * 3;
                    var ib = ((y * actual.Width) + x) * 3;
                    maxDelta = 0;
                    sum = 0;
                    for (var c = 0; c < 3; c++)
                    {
                        var delta = Math.Abs(a[ia + c] - b[ib + c]);
                        maxDelta = Math.Max(maxDelta, delta);
                        sum += delta;
                    }
                }
                errorSum += sum / (3 * 255.0);
                var differs = maxDelta > tolerance;
                if (differs)
                {
                    differing++;
                }
                if (regions is not null && x < expected.Width && y < expected.Height)
                {
                    var region = regions[(y * expected.Width) + x];
                    regionArea[region]++;
                    if (differs)
                    {
                        regionDiff[region]++;
                    }
                }
                if (diff is not null)
                {
                    // Differences in red over a faded copy of the expected page.
                    var i = ((y * width) + x) * 3;
                    var gray = inA ? (byte)(200 + (a[(((y * expected.Width) + x) * 3)] / 5)) : (byte)255;
                    // Heat: small differences orange, large ones deep red.
                    var heat = (byte)Math.Clamp(200 - maxDelta, 0, 200);
                    diff[i] = differs ? (byte)255 : gray;
                    diff[i + 1] = differs ? heat : gray;
                    diff[i + 2] = differs ? (byte)0 : gray;
                }
            }
        }
        if (diff is not null)
        {
            File.WriteAllBytes(diffPath!, PngCodec.Encode(new RasterImage(width, height, 3, diff)));
        }
        var total = (double)width * height;
        return new PageDiff(page, width, height, differing / total, errorSum / total, mismatch)
        {
            Regions = regions is null ? new Dictionary<string, (long, long)>() : Enumerable.Range(0, 3).ToDictionary(i => regionNames[i], i => (regionDiff[i], regionArea[i])),
        };
    }

    /// <summary>
    /// Classifies each pixel of a reference page: 2 = text (glyph boxes), 1 = images, 0 = everything else, from the
    /// reference PDF's content (PdfPig). Lets a report say where the differences are.
    /// </summary>
    public static byte[] RegionMap(byte[] pdf, int pageIndex, int width, int height)
    {
        var map = new byte[width * height];
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var page = document.GetPage(pageIndex + 1);
        var sx = width / page.Width;
        var sy = height / page.Height;
        void Fill(UglyToad.PdfPig.Core.PdfRectangle box, byte value, double grow)
        {
            var x0 = Math.Max(0, (int)Math.Floor((box.Left - grow) * sx));
            var x1 = Math.Min(width - 1, (int)Math.Ceiling((box.Right + grow) * sx));
            var y0 = Math.Max(0, (int)Math.Floor((page.Height - box.Top - grow) * sy));
            var y1 = Math.Min(height - 1, (int)Math.Ceiling((page.Height - box.Bottom + grow) * sy));
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    map[(y * width) + x] = Math.Max(map[(y * width) + x], value);
                }
            }
        }
        foreach (var image in page.GetImages())
        {
            Fill(image.BoundingBox, 1, 0);
        }
        foreach (var letter in page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)))
        {
            Fill(letter.BoundingBox, 2, 1);
        }
        return map;
    }

    /// <summary>RGB bytes with alpha composited on white.</summary>
    private static byte[] Flatten(RasterImage image)
    {
        var rgba = image.ToRgba();
        var rgb = new byte[image.Width * image.Height * 3];
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            var alpha = rgba[(4 * i) + 3] / 255.0;
            for (var c = 0; c < 3; c++)
            {
                rgb[(3 * i) + c] = (byte)Math.Round((rgba[(4 * i) + c] * alpha) + (255 * (1 - alpha)));
            }
        }
        return rgb;
    }
}

/// <summary>
/// A drop-in visual sample: a folder in COREIDML_PDF_SAMPLES_DIR, or in the git-ignored <c>tests/CoreIdml.Pdf.Tests/Samples</c> (see the README there).
/// </summary>
internal sealed class VisualSample
{
    private VisualSample(string directory)
    {
        Directory = directory;
        Name = Path.GetFileName(directory);
        var config = Path.Combine(directory, "sample.json");
        if (File.Exists(config))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            var root = json.RootElement;
            Dpi = root.TryGetProperty("dpi", out var dpi) ? dpi.GetInt32() : Dpi;
            Threshold = root.TryGetProperty("threshold", out var threshold) ? threshold.GetDouble() : Threshold;
            Tolerance = root.TryGetProperty("tolerance", out var tolerance) ? tolerance.GetInt32() : Tolerance;
            SystemFonts = root.TryGetProperty("systemFonts", out var system) && system.GetBoolean();
            Skip = root.TryGetProperty("skip", out var skip) ? skip.GetString() : null;
            if (root.TryGetProperty("fontDirectory", out var fontDirectory) && fontDirectory.GetString() is { Length: > 0 } fonts)
            {
                FontDirectory = Path.GetFullPath(Path.Combine(directory, fonts));
            }
            if (root.TryGetProperty("linkDirectories", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    LinkDirectories.Add(link.GetString() ?? string.Empty);
                }
            }
            if (root.TryGetProperty("pages", out var pages))
            {
                Pages = pages.EnumerateArray().Select(p => p.GetInt32()).ToList();
            }
            if (root.TryGetProperty("substitutions", out var substitutions))
            {
                foreach (var property in substitutions.EnumerateObject())
                {
                    Substitutions[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        Idml = System.IO.Directory.EnumerateFiles(directory, "*.idml").Order(StringComparer.Ordinal).FirstOrDefault();
        var pdfs = System.IO.Directory.EnumerateFiles(directory, "*.pdf").Order(StringComparer.Ordinal).ToList();
        ReferencePdf = pdfs.FirstOrDefault(p => Path.GetFileName(p).Equals("reference.pdf", StringComparison.OrdinalIgnoreCase)) ?? pdfs.FirstOrDefault();
        ReferencePngs = System.IO.Directory.EnumerateFiles(directory, "*.png")
            .Select(path => (Path: path, Page: PageNumber(Path.GetFileNameWithoutExtension(path))))
            .Where(entry => entry.Page > 0)
            .ToDictionary(entry => entry.Page, entry => entry.Path);
    }

    public string Directory { get; }

    public string Name { get; }

    public string? Idml { get; }

    public string? ReferencePdf { get; }

    /// <summary>reference-1.png, page-2.png, ... by page number.</summary>
    public IReadOnlyDictionary<int, string> ReferencePngs { get; }

    public int Dpi { get; } = 96;

    public double Threshold { get; } = 0.05;

    public int Tolerance { get; } = 64;

    public bool SystemFonts { get; }

    public string? Skip { get; }

    public IReadOnlyList<int>? Pages { get; }

    public Dictionary<string, string> Substitutions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra font folder (relative to the sample), besides its own fonts/ folder.</summary>
    public string? FontDirectory { get; }

    /// <summary>Extra folders searched for links by file name (absolute, or relative to the sample folder).</summary>
    public List<string> LinkDirectories { get; } = [];

    /// <summary>IDML present: export it and compare with the reference. Only a PDF: import, re-export, compare.</summary>
    public bool IsExportCase => Idml is not null;

    public static string Root
    {
        get
        {
            if (Environment.GetEnvironmentVariable("COREIDML_PDF_SAMPLES_DIR") is { Length: > 0 } custom)
            {
                return custom;
            }
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tests", "CoreIdml.Pdf.Tests", "Samples");
                if (System.IO.Directory.Exists(candidate))
                {
                    return candidate;
                }
                if (File.Exists(Path.Combine(directory.FullName, "CoreIdml.Pdf.Tests.csproj")))
                {
                    return Path.Combine(directory.FullName, "Samples");
                }
            }
            return Path.Combine(AppContext.BaseDirectory, "Samples");
        }
    }

    public static IReadOnlyList<VisualSample> Discover() =>
        System.IO.Directory.Exists(Root)
            ? System.IO.Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal).Select(d => new VisualSample(d))
                .Where(s => s.Idml is not null || s.ReferencePdf is not null).ToList()
            : [];

    public PdfFontOptions FontOptions()
    {
        var options = new PdfFontOptions { UseSystemFonts = SystemFonts };
        var own = Path.Combine(Directory, "fonts");
        if (System.IO.Directory.Exists(own))
        {
            options.FontDirectories.Add(own);
        }
        if (FontDirectory is not null)
        {
            options.FontDirectories.Add(FontDirectory);
        }
        options.FontDirectories.Add(TestSupport.FontDirectory);
        options.Substitutions["Minion Pro"] = "Liberation Serif";
        options.Substitutions["Myriad Pro"] = "Liberation Sans";
        foreach (var (from, to) in Substitutions)
        {
            options.Substitutions[from] = to;
        }
        return options;
    }

    private static int PageNumber(string name)
    {
        foreach (var prefix in (string[])["reference-", "page-", "reference_", "page_"])
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
            {
                return page;
            }
        }
        return 0;
    }
}
