using System.Globalization;
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
