using System.Globalization;
using System.Text;
using CoreIdml.Pdf.Imaging;
using CoreIdml.Samples;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// The visual diff harness: rasterize our PDF and a reference, compare page by page, fail above a threshold.
/// Diff images go to <c>bin/.../out/visual/&lt;sample&gt;/</c>. The per-page metrics are printed in the test output
/// (<c>dotnet test --logger "console;verbosity=detailed"</c>).
/// </summary>
public sealed class VisualTests(ITestOutputHelper output)
{
    /// <summary>Every real sample in COREIDML_PDF_SAMPLES_DIR (or the git-ignored Samples folder); none in the repository.</summary>
    [Fact]
    public void Drop_in_samples_match_their_references()
    {
        var samples = VisualSample.Discover();
        output.WriteLine($"samples folder: {VisualSample.Root} ({samples.Count} sample(s))");
        if (samples.Count == 0)
        {
            output.WriteLine("SKIPPED: no real samples. Set COREIDML_PDF_SAMPLES_DIR to a folder of samples (see Samples/README.md).");
            return;
        }
        var failures = new List<string>();
        foreach (var sample in samples)
        {
            if (sample.Skip is not null)
            {
                output.WriteLine($"[{sample.Name}] skipped: {sample.Skip}");
                continue;
            }
            var directory = TestSupport.OutputDirectory(Path.Combine("visual", sample.Name));
            try
            {
                var diffs = sample.IsExportCase ? RunExportSample(sample, directory) : RunImportSample(sample, directory);
                foreach (var diff in diffs)
                {
                    var pass = diff.DiffRatio <= sample.Threshold && !diff.SizeMismatch;
                    output.WriteLine($"[{sample.Name}] {diff} threshold {sample.Threshold * 100:0.###}% -> {(pass ? "PASS" : "FAIL")}");
                    if (!pass)
                    {
                        failures.Add($"{sample.Name} page {diff.Page}: {diff.DiffRatio * 100:0.###}% > {sample.Threshold * 100:0.###}%");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or IdmlException or InvalidOperationException)
            {
                failures.Add($"{sample.Name}: {exception.Message}");
                output.WriteLine($"[{sample.Name}] ERROR {exception}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private List<PageDiff> RunExportSample(VisualSample sample, string directory)
    {
        using var document = IdmlDocument.Open(sample.Idml!);
        var options = new PdfExportOptions { Fonts = sample.FontOptions(), DocumentPath = sample.Idml, Pages = sample.Pages };
        options.LinkDirectories.Add(Path.Combine(sample.Directory, "links"));
        foreach (var linkDirectory in sample.LinkDirectories)
        {
            var full = Path.IsPathRooted(linkDirectory) ? linkDirectory : Path.Combine(sample.Directory, linkDirectory);
            if (!Directory.Exists(full))
            {
                output.WriteLine($"[{sample.Name}] link folder not found: {full}");
            }
            options.LinkDirectories.Add(full);
        }
        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(document, stream, options);
        var ours = stream.ToArray();
        File.WriteAllBytes(Path.Combine(directory, "ours.pdf"), ours);
        Report(sample.Name, result);

        var diffs = new List<PageDiff>();
        var pageNumbers = sample.Pages ?? Enumerable.Range(1, result.PageCount).ToList();
        for (var i = 0; i < pageNumbers.Count; i++)
        {
            var page = pageNumbers[i];
            RasterImage reference;
            byte[]? regions = null;
            if (sample.ReferencePngs.TryGetValue(page, out var png))
            {
                reference = PngCodec.Decode(File.ReadAllBytes(png));
            }
            else if (sample.ReferencePdf is not null)
            {
                var referencePdf = File.ReadAllBytes(sample.ReferencePdf);
                reference = Rasterizer.Render(referencePdf, page - 1, sample.Dpi);
                regions = VisualDiff.RegionMap(referencePdf, page - 1, reference.Width, reference.Height);
            }
            else
            {
                throw new InvalidDataException($"No reference for page {page} (reference.pdf or reference-{page}.png).");
            }
            var actual = Rasterizer.Render(ours, i, sample.Dpi);
            File.WriteAllBytes(Path.Combine(directory, $"ours-{page}.png"), PngCodec.Encode(actual));
            File.WriteAllBytes(Path.Combine(directory, $"reference-{page}.png"), PngCodec.Encode(reference));
            var diff = VisualDiff.Compare(page, reference, actual, sample.Tolerance, Path.Combine(directory, $"diff-{page}.png"), regions);
            output.WriteLine($"[{sample.Name}] page {page} regions: {diff.RegionReport}");
            if (sample.ReferencePdf is not null && !sample.ReferencePngs.ContainsKey(page))
            {
                var text = TextPositionReport.Compare(File.ReadAllBytes(sample.ReferencePdf), ours, page, i + 1);
                output.WriteLine($"[{sample.Name}] text {text}; word width ratio by font: {text.WidthByFont}");
                if (Environment.GetEnvironmentVariable("COREIDML_PDF_WORDS") == page.ToString(CultureInfo.InvariantCulture))
                {
                    foreach (var match in text.Matches)
                    {
                        output.WriteLine($"    {match.Text,-24} x {match.RefX,7:0.0} base {match.RefBaseline,7:0.0} dx {match.DX,6:0.00} dy {match.DY,6:0.00} size {match.RefSize:0.0}/{match.Size:0.0}");
                    }
                }
            }
            diffs.Add(diff);
        }
        return diffs;
    }

    private List<PageDiff> RunImportSample(VisualSample sample, string directory)
    {
        var original = File.ReadAllBytes(sample.ReferencePdf!);
        var imported = IdmlPdf.Import(new MemoryStream(original), new PdfImportOptions { ImageDirectory = Path.Combine(directory, "links"), Pages = sample.Pages });
        imported.Document.Save(Path.Combine(directory, "imported.idml"));
        output.WriteLine($"[{sample.Name}] import: {imported.TextFrames} text frames, {imported.ImageFrames} images, {imported.Shapes} shapes; {string.Join("; ", imported.Warnings)}");
        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(imported.Document, stream, new PdfExportOptions { Fonts = sample.FontOptions() });
        Report(sample.Name, result);
        var ours = stream.ToArray();
        File.WriteAllBytes(Path.Combine(directory, "roundtrip.pdf"), ours);
        var pageNumbers = sample.Pages ?? Enumerable.Range(1, Rasterizer.PageCount(original)).ToList();
        var diffs = new List<PageDiff>();
        for (var i = 0; i < pageNumbers.Count; i++)
        {
            var reference = Rasterizer.Render(original, pageNumbers[i] - 1, sample.Dpi);
            var actual = Rasterizer.Render(ours, i, sample.Dpi);
            diffs.Add(VisualDiff.Compare(pageNumbers[i], reference, actual, sample.Tolerance, Path.Combine(directory, $"diff-{pageNumbers[i]}.png")));
        }
        return diffs;
    }

    private void Report(string name, PdfExportResult result)
    {
        foreach (var substitution in result.FontSubstitutions)
        {
            output.WriteLine($"[{name}] font {substitution.RequestedFamily} {substitution.RequestedStyle} -> {substitution.UsedFont} ({substitution.Reason})");
        }
        foreach (var image in result.MissingImages)
        {
            output.WriteLine($"[{name}] missing image {image}");
        }
        foreach (var story in result.OversetStories)
        {
            output.WriteLine($"[{name}] overset story {story}");
        }
    }

    /// <summary>
    /// Our own document through the whole pipeline: ficha demo → PDF (A) → IDML (import) → PDF (B). A against B
    /// measures how much the round trip loses; the threshold is a regression guard, not a fidelity claim.
    /// </summary>
    [Fact]
    public void Ficha_demo_round_trip_pixel_metric()
    {
        var directory = TestSupport.OutputDirectory(Path.Combine("visual", "ficha-roundtrip"));
        var photo = TestSupport.WriteTestPng(Path.Combine(directory, "foto.png"), 320, 200);
        using var document = FichaDemo.Build(photo);
        var first = TestSupport.Export(document);
        File.WriteAllBytes(Path.Combine(directory, "a.pdf"), first);

        var imported = IdmlPdf.Import(new MemoryStream(first), new PdfImportOptions { ImageDirectory = Path.Combine(directory, "links") });
        imported.Document.Save(Path.Combine(directory, "b.idml"));
        var second = TestSupport.Export(imported.Document);
        File.WriteAllBytes(Path.Combine(directory, "b.pdf"), second);
        output.WriteLine($"import: {imported.TextFrames} text frames, {imported.ImageFrames} image frames, {imported.Shapes} shapes");

        var report = new StringBuilder();
        for (var page = 0; page < 2; page++)
        {
            var a = Rasterizer.Render(first, page, 96);
            var b = Rasterizer.Render(second, page, 96);
            File.WriteAllBytes(Path.Combine(directory, $"a-{page + 1}.png"), PngCodec.Encode(a));
            File.WriteAllBytes(Path.Combine(directory, $"b-{page + 1}.png"), PngCodec.Encode(b));
            var diff = VisualDiff.Compare(page + 1, a, b, 64, Path.Combine(directory, $"diff-{page + 1}.png"));
            report.AppendLine(CultureInfo.InvariantCulture, $"ficha round trip {diff}");
            Assert.False(diff.SizeMismatch);
            Assert.True(diff.DiffRatio < 0.01, $"round trip page {page + 1} differs on {diff.DiffRatio:P2} of pixels");
        }
        output.WriteLine(report.ToString());
    }

    [Fact]
    public void Export_is_deterministic_to_the_byte_and_pixel()
    {
        using var document = FichaDemo.Build(null);
        var first = TestSupport.Export(document);
        var second = TestSupport.Export(document);
        Assert.Equal(first, second);
        var diff = VisualDiff.Compare(1, Rasterizer.Render(first, 0, 72), Rasterizer.Render(second, 0, 72), 0);
        output.WriteLine($"determinism {diff}");
        Assert.Equal(0, diff.DiffRatio);
    }

    /// <summary>Real InDesign exports render without errors; what cannot be honoured is reported.</summary>
    [Theory]
    [InlineData("article-1photo.idml")]
    [InlineData("magazineA-edito.idml")]
    public void InDesign_exports_render_and_report_gaps(string name)
    {
        using var document = IdmlDocument.Open(TestSupport.TestData(name));
        var directory = TestSupport.OutputDirectory(Path.Combine("indesign", Path.GetFileNameWithoutExtension(name)));
        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(document, stream, new PdfExportOptions { Fonts = TestSupport.Fonts(), MarkMissingImages = true });
        var pdf = stream.ToArray();
        File.WriteAllBytes(Path.Combine(directory, "ours.pdf"), pdf);
        Report(name, result);
        Assert.Equal(document.Pages.Count, result.PageCount);
        for (var page = 0; page < result.PageCount; page++)
        {
            File.WriteAllBytes(Path.Combine(directory, $"page-{page + 1}.png"), PngCodec.Encode(Rasterizer.Render(pdf, page, 72)));
        }
    }
}
