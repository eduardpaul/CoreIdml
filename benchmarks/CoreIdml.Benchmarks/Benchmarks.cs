using BenchmarkDotNet.Attributes;
using CoreIdml.Dom;
using CoreIdml.Json;
using CoreIdml.Pdf;
using CoreIdml.Pdf.Imaging;

namespace CoreIdml.Benchmarks;

/// <summary>Reading and editing IDML: open, DOM queries, styles, templating, save.</summary>
[MemoryDiagnoser]
public class IdmlPackageBenchmarks
{
    private byte[] data = [];

    public static IEnumerable<string> Names => Corpus.Documents.Keys;

    [ParamsSource(nameof(Names))]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup() => data = File.ReadAllBytes(Corpus.Documents[Document]);

    /// <summary>Unzips the package and parses designmap.xml (parts parse lazily).</summary>
    [Benchmark]
    public int Open()
    {
        using var document = IdmlDocument.Open(data);
        return document.Package.PartPaths.Count;
    }

    /// <summary>Walks the model: every page item, story text, frame threads, resolved styles.</summary>
    [Benchmark]
    public int Query()
    {
        using var document = IdmlDocument.Open(data);
        var count = 0;
        foreach (var item in document.AllPageItems)
        {
            count += item.Name?.Length ?? 0;
            _ = item.Bounds;
        }
        foreach (var story in document.Stories)
        {
            count += story.Text.Length + story.Frames.Count;
        }
        var resolver = new AttributeResolver(document);
        foreach (var run in document.Stories.SelectMany(story => story.Runs))
        {
            count += resolver.Text(run.Element, "AppliedFont")?.Length ?? 0;
            count += resolver.Text(run.Element, "PointSize")?.Length ?? 0;
        }
        return count;
    }

    /// <summary>The agent-facing summary (IdmlJson.Summarize with runs).</summary>
    [Benchmark]
    public int Summarize()
    {
        using var document = IdmlDocument.Open(data);
        return IdmlJson.SummarizeToJson(document, new SummaryOptions { IncludeRuns = true }).Length;
    }

    /// <summary>Open, edit text in every story, add a page with text, a table and an image, save.</summary>
    [Benchmark]
    public long EditAndSave()
    {
        using var document = IdmlDocument.Open(data);
        Corpus.Edit(document);
        using var output = new MemoryStream();
        document.Save(output);
        return output.Length;
    }

    /// <summary>Open and save without changes (every part written back byte for byte).</summary>
    [Benchmark]
    public long SaveUnchanged()
    {
        using var document = IdmlDocument.Open(data);
        using var output = new MemoryStream();
        document.Save(output);
        return output.Length;
    }
}

/// <summary>IDML to PDF: composition, fonts, images, PDF writing.</summary>
[MemoryDiagnoser]
public class PdfExportBenchmarks
{
    private IdmlDocument? document;
    private PdfExportOptions options = new();

    public static IEnumerable<string> Names => Corpus.Documents.Keys.Append("ficha-demo");

    [ParamsSource(nameof(Names))]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        if (Document == "ficha-demo")
        {
            document = Corpus.FichaDemo();
            options = Corpus.ExportOptions();
        }
        else
        {
            document = IdmlDocument.Open(Corpus.Documents[Document]);
            options = Corpus.ExportOptions(Corpus.Documents[Document]);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => document?.Dispose();

    [Benchmark]
    public long Export()
    {
        using var output = new MemoryStream();
        IdmlPdf.Export(document!, output, options);
        return output.Length;
    }
}

/// <summary>
/// IDML to PDF with the opt-in image size cap (<see cref="PdfExportOptions.MaxImageDpi"/> 150): photos decoded,
/// downsampled and re-encoded. Only when the studio sample is there (real_samples_fichas).
/// </summary>
[MemoryDiagnoser]
public class CappedPdfExportBenchmarks
{
    private IdmlDocument? document;
    private PdfExportOptions options = new();

    /// <summary>The studio file and the report with its photos; none without real_samples_fichas.</summary>
    public static IEnumerable<string> Names =>
        [.. Corpus.Documents.Keys.Where(name => name == "ficha-real"), .. Corpus.Photos.Count > 0 ? ["report"] : Array.Empty<string>()];

    [ParamsSource(nameof(Names))]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        (document, options) = Document == "report"
            ? (Corpus.Report(4, Corpus.Fonts()), Corpus.ExportOptions())
            : (IdmlDocument.Open(Corpus.Documents[Document]), Corpus.ExportOptions(Corpus.Documents[Document]));
        options.MaxImageDpi = Corpus.CappedDpi;
    }

    [GlobalCleanup]
    public void Cleanup() => document?.Dispose();

    [Benchmark]
    public long Export()
    {
        using var output = new MemoryStream();
        IdmlPdf.Export(document!, output, options);
        return output.Length;
    }
}

/// <summary>The image cap's JPEG codec on a studio photo (a 3264×2448 4:2:2 camera JPEG).</summary>
[MemoryDiagnoser]
public class JpegCodecBenchmarks
{
    private byte[] photo = [];
    private RasterImage? quarter;

    /// <summary>The first studio photo; empty without real_samples_fichas.</summary>
    public static IEnumerable<string> Photos => Corpus.Photos.Take(1).Select(Path.GetFileName)!;

    [ParamsSource(nameof(Photos))]
    public string Photo { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        photo = File.ReadAllBytes(Path.Combine(Corpus.RealLinks, Photo));
        quarter = JpegDecoder.Decode(photo, photo.Length, 2);
    }

    [Benchmark]
    public int DecodeFullSize() => JpegDecoder.Decode(photo, photo.Length, 8).Pixels.Length;

    /// <summary>Decoding at an eighth of the size (DC only), as the cap does for photos placed small.</summary>
    [Benchmark]
    public int DecodeEighth() => JpegDecoder.Decode(photo, photo.Length, 1).Pixels.Length;

    /// <summary>Re-encoding the photo at a quarter of its size (816×612), quality 85.</summary>
    [Benchmark]
    public int Encode() => JpegEncoder.Encode(quarter!, 85).Length;
}

/// <summary>TextMeasurer as the report layout uses it: one scratch frame, new text and width each time.</summary>
[MemoryDiagnoser]
public class TextMeasurerBenchmarks
{
    private IdmlDocument? document;
    private TextMeasurer? measurer;
    private TextFrame? scratch;
    private ParagraphStyle? style;
    private readonly string[] texts = Enumerable.Range(0, 100).Select(i => Corpus.Field(i / 24, (i / 6) % 4, i % 6)).ToArray();

    [GlobalSetup]
    public void Setup()
    {
        document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        style = document.Styles.AddParagraphStyle("Valor", new ParagraphFormat
        {
            FontFamily = "Raleway",
            PointSize = 7.5,
            Leading = 9,
            Justification = Justification.LeftJustified,
            Hyphenation = true,
            Language = Languages.Spanish,
        });
        // A page full of frames, like a report being built (the measurer's lookups walk the document).
        for (var i = 0; i < 60; i++)
        {
            document.Pages[0].AddText(new Rect(20, 10 + (i * 12), 200, 12), texts[i % texts.Length], style);
        }
        scratch = document.Pages[0].AddTextFrame(new Rect(0, 0, 100, 20));
        measurer = new TextMeasurer(document, Corpus.Fonts());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        measurer?.Dispose();
        document?.Dispose();
    }

    /// <summary>100 measurements of different texts at different widths.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public double Measure100()
    {
        var total = 0.0;
        for (var i = 0; i < texts.Length; i++)
        {
            scratch!.SetBounds(new Rect(0, 0, 120 + (i % 5 * 20), 20));
            scratch.Inset = Spacing.All(2);
            scratch.Story.SetText(texts[i], style);
            total += measurer!.Measure(scratch).Height;
        }
        return total;
    }

    /// <summary>
    /// A new measurer's first measurement, as each report starts: font scan (app fonts and system fonts, as
    /// ReportFonts does), loading the faces, and a fallback font for characters the text font lacks (₃, ⁻).
    /// </summary>
    [Benchmark]
    public double FirstMeasureWithSystemFonts()
    {
        using var created = new TextMeasurer(document!, Corpus.Fonts(systemFonts: true));
        scratch!.SetBounds(new Rect(0, 0, 150, 20));
        scratch.Story.SetText("Nitratos NO₃⁻ en la muestra: 12 mg/l", style);
        return created.Measure(scratch).Height;
    }

    /// <summary>Creating a measurer: font scan of the app's sources plus the system fonts (as ReportFonts does).</summary>
    [Benchmark]
    public int CreateWithSystemFonts()
    {
        using var created = new TextMeasurer(document!, Corpus.Fonts(systemFonts: true));
        return created.GetHashCode();
    }
}

/// <summary>A report like the app's: sheets of measured fields and four large camera JPEGs each.</summary>
[MemoryDiagnoser]
public class ReportBenchmarks
{
    private IdmlDocument? document;

    /// <summary>Only when the studio photos are there (real_samples_fichas).</summary>
    public static IEnumerable<int> SheetCounts => Corpus.Photos.Count > 0 ? [4] : [];

    [ParamsSource(nameof(SheetCounts))]
    public int Sheets { get; set; }

    [GlobalSetup]
    public void Setup() => document = Corpus.Report(Sheets, Corpus.Fonts());

    [GlobalCleanup]
    public void Cleanup() => document?.Dispose();

    /// <summary>Builds the document, measuring every field.</summary>
    [Benchmark]
    public int Build()
    {
        using var built = Corpus.Report(Sheets, Corpus.Fonts());
        return built.Pages.Count;
    }

    /// <summary>Exports the built document (JPEG passthrough, PNG logo, fonts, text).</summary>
    [Benchmark]
    public long Export()
    {
        using var output = new MemoryStream();
        IdmlPdf.Export(document!, output, Corpus.ExportOptions());
        return output.Length;
    }
}
