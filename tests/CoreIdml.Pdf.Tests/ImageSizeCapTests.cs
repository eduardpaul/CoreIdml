using System.Runtime.CompilerServices;
using System.Text;
using CoreIdml.Dom;
using CoreIdml.Pdf.Imaging;
using CoreIdml.Pdf.Json;

namespace CoreIdml.Pdf.Tests;

/// <summary>The opt-in image size cap (MaxImageDpi), its JPEG codec, and export progress and cancellation.</summary>
public sealed class ImageSizeCapTests
{
    /// <summary>The repository root (this file is idml/tests/CoreIdml.Pdf.Tests/ImageSizeCapTests.cs).</summary>
    private static string RepoRoot([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", ".."));

    private static RasterImage Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 3;
                pixels[at] = (byte)(x * 255 / width);
                pixels[at + 1] = (byte)(y * 255 / height);
                pixels[at + 2] = (byte)((x + y) % 256 < 128 ? 40 : 200);
            }
        }
        return new RasterImage(width, height, 3, pixels);
    }

    private static double MeanError(RasterImage a, RasterImage b)
    {
        Assert.Equal((a.Width, a.Height, a.Channels), (b.Width, b.Height, b.Channels));
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i++)
        {
            sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        }
        return (double)sum / a.Pixels.Length;
    }

    [Fact]
    public void Jpeg_encoder_and_decoder_round_trip()
    {
        var image = Gradient(203, 101);
        var gray = new RasterImage(64, 40, 1, [.. Enumerable.Range(0, 64 * 40).Select(i => (byte)(i % 64 * 4))]);

        var decoded = JpegDecoder.Decode(JpegEncoder.Encode(image, 90));
        var decodedGray = JpegDecoder.Decode(JpegEncoder.Encode(gray, 90));

        Assert.True(MeanError(image, decoded) < 6, $"{MeanError(image, decoded)}");
        Assert.True(MeanError(gray, decodedGray) < 4, $"{MeanError(gray, decodedGray)}");
        Assert.True(JpegEncoder.Encode(image, 30).Length < JpegEncoder.Encode(image, 95).Length);
        Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode([0xFF, 0xD8, 0xFF, 0xD9]));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void Scaled_decoding_looks_like_full_decoding_downscaled(int channels)
    {
        var color = Gradient(208, 112);
        var image = channels == 3 ? color : new RasterImage(208, 112, 1, [.. Enumerable.Range(0, 208 * 112).Select(i => color.Pixels[i * 3])]);
        var jpeg = JpegEncoder.Encode(image, 95);
        var full = JpegDecoder.Decode(jpeg);

        foreach (var scale in (int[])[4, 2, 1])
        {
            var scaled = JpegDecoder.Decode(jpeg, scale);

            Assert.Equal((208 * scale / 8, 112 * scale / 8, channels), (scaled.Width, scaled.Height, scaled.Channels));
            var expected = ImageScaler.Downscale(full, scaled.Width, scaled.Height);
            Assert.True(MeanError(expected, scaled) < 3, $"scale {scale}: {MeanError(expected, scaled)}");
        }
        // Odd sizes round up (the last block is partly padding); 8 is the full size.
        var odd = JpegDecoder.Decode(JpegEncoder.Encode(Gradient(203, 101), 90), 1);
        Assert.Equal((26, 13), (odd.Width, odd.Height));
        Assert.Equal(full.Pixels, JpegDecoder.Decode(jpeg, 8).Pixels);
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegDecoder.Decode(jpeg, 3));
    }

    [Fact]
    public void Scale_is_the_smallest_that_still_gives_the_target_size()
    {
        Assert.Equal(1, JpegDecoder.ScaleFor(4608, 3456, 561, 421)); // 576×432 at an eighth
        Assert.Equal(2, JpegDecoder.ScaleFor(4608, 3456, 600, 400));
        Assert.Equal(4, JpegDecoder.ScaleFor(3264, 2448, 1240, 930));
        Assert.Equal(8, JpegDecoder.ScaleFor(3264, 2448, 2000, 1500));
        Assert.Equal(1, JpegDecoder.ScaleFor(100, 50, 1, 1));
    }

    [Fact]
    public void Damaged_jpegs_are_kept_as_they_are()
    {
        var folder = Path.Combine(Path.GetTempPath(), "idml-pdf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var jpeg = JpegEncoder.Encode(Gradient(1600, 1000), 90);
        // The Huffman table segment claims more bytes than the file has left.
        var tables = jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xC4]);
        var damaged = jpeg[..(tables + 40)];
        var path = Path.Combine(folder, "rota.jpg");
        File.WriteAllBytes(path, damaged);
        using var document = IdmlDocument.Create();
        document.Pages[0].AddImage(new Rect(36, 36, 144, 90), path);
        var options = TestSupport.ExportOptions();
        options.MaxImageDpi = 150;

        Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode(damaged));
        var pdf = TestSupport.Export(document, options);
        Assert.Equal([1600], ImageWidths(pdf));
        Assert.True(pdf.AsSpan().IndexOf(damaged) >= 0);
    }

    [Fact]
    public void Downscale_averages_areas()
    {
        var image = new RasterImage(4, 2, 1, [0, 100, 200, 255, 0, 100, 200, 255]);

        var half = ImageScaler.Downscale(image, 2, 1);

        Assert.Equal([50, 228], half.Pixels);
        Assert.Same(image, ImageScaler.Downscale(image, 4, 2));
    }

    private static (IdmlDocument Document, string Folder) PlacedPhoto(int width, int height, double placedWidth)
    {
        var folder = Path.Combine(Path.GetTempPath(), "idml-pdf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var jpeg = Path.Combine(folder, "foto.jpg");
        File.WriteAllBytes(jpeg, JpegEncoder.Encode(Gradient(width, height), 92));
        var png = Path.Combine(folder, "plano.png");
        File.WriteAllBytes(png, PngCodec.Encode(Gradient(width, height)));
        var document = IdmlDocument.Create();
        document.Pages[0].AddImage(new Rect(36, 36, placedWidth, placedWidth * height / width), jpeg);
        document.Pages[0].AddImage(new Rect(36, 400, placedWidth, placedWidth * height / width), png);
        return (document, folder);
    }

    private static int[] ImageHeights(byte[] pdf) =>
        [.. System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Subtype /Image /Width \d+ /Height (\d+)").Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Where each image is drawn on the first page (rounded to 0.01 pt).</summary>
    private static string[] ImageBoxes(byte[] pdf)
    {
        using var reader = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return [.. reader.GetPage(1).GetImages().Select(image => image.BoundingBox).Select(box => FormattableString.Invariant($"{box.Left:F2} {box.Bottom:F2} {box.Width:F2} {box.Height:F2}"))];
    }

    private static int[] ImageWidths(byte[] pdf) =>
        [.. System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Subtype /Image /Width (\d+)").Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public void Max_image_dpi_downsamples_large_images_only_when_asked()
    {
        var (document, _) = PlacedPhoto(1600, 1000, 144); // 2 inches wide: 800 ppi
        using var _document = document;

        var full = TestSupport.Export(document);
        var options = TestSupport.ExportOptions();
        options.MaxImageDpi = 150;
        using var output = new MemoryStream();
        IdmlPdf.Export(document, output, options);
        var capped = output.ToArray();

        Assert.Equal([1600, 1600], ImageWidths(full).Order());
        Assert.All(ImageWidths(capped), width => Assert.InRange(width, 299, 302));
        Assert.All(ImageHeights(capped), height => Assert.InRange(height, 186, 189));
        Assert.Equal(2, ImageWidths(capped).Length);
        Assert.True(capped.Length * 2 < full.Length, $"{capped.Length} vs {full.Length}");
        // Placed exactly where the full-size images are; the JPEG re-encoded, the PNG still lossless.
        Assert.Equal(ImageBoxes(full), ImageBoxes(capped));
        Assert.Contains("/Filter /DCTDecode", Encoding.Latin1.GetString(capped), StringComparison.Ordinal);
        Assert.Contains("/Filter /FlateDecode", Encoding.Latin1.GetString(capped), StringComparison.Ordinal);
        // Images are prepared on other cores: the output must not depend on which finishes first.
        using (var again = new MemoryStream())
        {
            IdmlPdf.Export(document, again, options);
            Assert.Equal(capped, again.ToArray());
        }
        // Same picture: the downsampled page looks like the original one.
        var before = Rasterizer.Render(full, 0, 40);
        var after = Rasterizer.Render(capped, 0, 40);
        var diff = VisualDiff.Compare(0, before, after, tolerance: 40);
        Assert.True(diff.DiffRatio < 0.02, $"{diff.DiffRatio:P2} of pixels differ");
    }

    [Fact]
    public void Jpeg_quality_and_the_largest_placement_set_the_result()
    {
        var (document, folder) = PlacedPhoto(1600, 1000, 144);
        using var _document = document;
        // The same photo again, twice as large: written once, at the size the larger placement needs (600 px).
        document.Pages[0].AddImage(new Rect(200, 36, 288, 180), Path.Combine(folder, "foto.jpg"));

        byte[] Export(int quality)
        {
            var options = TestSupport.ExportOptions();
            options.MaxImageDpi = 150;
            options.JpegQuality = quality;
            return TestSupport.Export(document, options);
        }
        var low = Export(40);
        var high = Export(95);

        Assert.Equal(2, ImageWidths(high).Length);
        Assert.Contains(ImageWidths(high), width => width is >= 599 and <= 602);
        Assert.True(low.Length < high.Length, $"{low.Length} vs {high.Length}");
    }

    [Fact]
    public void Images_within_the_cap_are_kept_as_they_are()
    {
        var (document, _) = PlacedPhoto(300, 200, 144); // 150 ppi
        using var _document = document;
        var options = TestSupport.ExportOptions();
        options.MaxImageDpi = 150;

        using var output = new MemoryStream();
        IdmlPdf.Export(document, output, options);

        Assert.Equal(TestSupport.Export(document), output.ToArray());
    }

    [Fact]
    public void Max_image_dpi_comes_from_json_options()
    {
        var options = PdfJson.ParseExportOptions("""{"maxImageDpi":120,"jpegQuality":70}""");

        Assert.Equal(120, options.MaxImageDpi);
        Assert.Equal(70, options.JpegQuality);
        Assert.Null(PdfJson.ParseExportOptions("{}").MaxImageDpi);
    }

    [Fact]
    public void Real_photos_decode_like_the_reference_renderer()
    {
        var links = Path.Combine(RepoRoot(), "real_samples_fichas", "Links");
        if (!Directory.Exists(links))
        {
            return; // real_samples_fichas is not in git.
        }
        // A progressive and a baseline 4:2:2 camera JPEG.
        foreach (var name in (string[])["P1010096.JPG", "P1010260.JPG"])
        {
            var path = Path.Combine(links, name);
            if (!File.Exists(path))
            {
                continue;
            }
            using var document = IdmlDocument.Create();
            document.Pages[0].AddImage(new Rect(36, 36, 400, 300), path, ImageFit.Fit);
            var options = TestSupport.ExportOptions();
            var full = TestSupport.Export(document);
            options.MaxImageDpi = 100;
            using var output = new MemoryStream();
            IdmlPdf.Export(document, output, options);

            var diff = VisualDiff.Compare(0, Rasterizer.Render(full, 0, 50), Rasterizer.Render(output.ToArray(), 0, 50), tolerance: 48);
            Assert.True(diff.DiffRatio < 0.03, $"{name}: {diff.DiffRatio:P2} of pixels differ");
            Assert.True(output.Length * 10 < full.Length, $"{name}: {output.Length} vs {full.Length}");
        }
    }

    [Fact]
    public void Export_reports_progress_and_can_be_cancelled()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageCount = 3 });
        var calls = new List<(int, int)>();
        var options = TestSupport.ExportOptions();
        options.Progress = (done, total) =>
        {
            calls.Add((done, total));
            return true;
        };
        IdmlPdf.Export(document, new MemoryStream(), options);

        var cancel = TestSupport.ExportOptions();
        cancel.Progress = (done, _) => done < 2;
        using var source = new CancellationTokenSource();
        source.Cancel();
        var token = TestSupport.ExportOptions();
        token.CancellationToken = source.Token;

        Assert.Equal([(1, 3), (2, 3), (3, 3)], calls);
        Assert.Throws<OperationCanceledException>(() => IdmlPdf.Export(document, new MemoryStream(), cancel));
        Assert.ThrowsAny<OperationCanceledException>(() => IdmlPdf.Export(document, new MemoryStream(), token));
    }

    [Fact]
    public void Capped_export_reports_progress_and_can_be_cancelled()
    {
        var (document, folder) = PlacedPhoto(1600, 1000, 144);
        using var _document = document;
        for (var page = 0; page < 2; page++)
        {
            var photo = Path.Combine(folder, $"foto{page}.jpg");
            File.WriteAllBytes(photo, JpegEncoder.Encode(Gradient(1200 + (page * 100), 900), 90));
            document.AddPage().AddImage(new Rect(36, 36, 144, 108), photo);
        }
        PdfExportOptions Capped(Func<int, int, bool>? progress = null, CancellationToken token = default)
        {
            var options = TestSupport.ExportOptions();
            options.MaxImageDpi = 150;
            options.Progress = progress;
            options.CancellationToken = token;
            return options;
        }
        var calls = new List<(int, int)>();
        using var source = new CancellationTokenSource();
        source.Cancel();

        var pdf = TestSupport.Export(document, Capped((done, total) =>
        {
            calls.Add((done, total));
            return true;
        }, TestContext.Current.CancellationToken));

        Assert.Equal([(1, 3), (2, 3), (3, 3)], calls);
        Assert.Equal(4, ImageWidths(pdf).Length);
        Assert.Throws<OperationCanceledException>(() => IdmlPdf.Export(document, new MemoryStream(), Capped((done, _) => done < 2, TestContext.Current.CancellationToken)));
        Assert.ThrowsAny<OperationCanceledException>(() => IdmlPdf.Export(document, new MemoryStream(), Capped(token: source.Token)));
    }
}
