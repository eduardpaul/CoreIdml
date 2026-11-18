using System.Globalization;
using System.Runtime.CompilerServices;
using CoreIdml.Dom;
using CoreIdml.Pdf;
using CoreIdml.Samples;

namespace CoreIdml.Benchmarks;

/// <summary>
/// The inputs of the benchmarks and of the golden-output check. The studio sample (real_samples_fichas, never
/// committed) is optional: everything that needs it is left out when it is missing.
/// </summary>
public static class Corpus
{
    /// <summary>The repository root (the folder holding CoreIdml.slnx).</summary>
    public static string RepoRoot { get; } = FindRoot();

    /// <summary>
    /// The studio's real documents, never committed: the folder named by COREIDML_SAMPLES_DIR, or real_samples_fichas
    /// at the repository root.
    /// </summary>
    public static string RealSamples =>
        Environment.GetEnvironmentVariable("COREIDML_SAMPLES_DIR") is { Length: > 0 } configured ? configured : Path.Combine(RepoRoot, "real_samples_fichas");

    /// <summary>The studio's InDesign file, or null when real_samples_fichas is not there.</summary>
    public static string? RealIdml => Existing(Path.Combine(RealSamples, "FICHA_CORESCEM_AP-ML.idml"));

    public static string RealLinks => Path.Combine(RealSamples, "Links");

    /// <summary>The studio's photos (large camera JPEGs), empty when real_samples_fichas is not there.</summary>
    public static IReadOnlyList<string> Photos { get; } = FindPhotos();

    /// <summary>A PNG logo from the studio's links, or null.</summary>
    public static string? Logo => Existing(Path.Combine(RealLinks, "Logo.png"));

    public static string TestData(string name) => Path.Combine(RepoRoot, "tests", "fixtures", name);

    /// <summary>IDML files to open, edit, save and export, by name.</summary>
    public static IReadOnlyDictionary<string, string> Documents { get; } = FindDocuments();

    /// <summary>
    /// Reproducible fonts: the bundled free fonts (Liberation from the PDF tests, Raleway, Montserrat and Jost from
    /// COREIDML_BENCH_FONTS), InDesign's defaults mapped onto them. System fonts only when asked for, as the app does.
    /// </summary>
    public static PdfFontOptions Fonts(bool systemFonts = false)
    {
        var options = new PdfFontOptions { UseSystemFonts = systemFonts };
        options.FontDirectories.Add(Path.Combine(RepoRoot, "tests", "fixtures", "fonts"));
        // The free fonts of the studio's app (Raleway, Montserrat, Jost), when COREIDML_BENCH_FONTS names their folder.
        if (Environment.GetEnvironmentVariable("COREIDML_BENCH_FONTS") is { Length: > 0 } appFonts)
        {
            options.FontDirectories.Add(appFonts);
        }
        options.Substitutions["Minion Pro"] = "Liberation Serif";
        options.Substitutions["Myriad Pro"] = "Liberation Sans";
        return options;
    }

    /// <summary>The image cap of the capped-export scenarios (MaxImageDpi), as the app asks for it.</summary>
    public const double CappedDpi = 150;

    public static PdfExportOptions ExportOptions(string? documentPath = null, bool systemFonts = false)
    {
        var options = new PdfExportOptions { Fonts = Fonts(systemFonts), DocumentPath = documentPath };
        if (Directory.Exists(RealLinks))
        {
            options.LinkDirectories.Add(RealLinks);
        }
        return options;
    }

    /// <summary>
    /// Edits an opened document the way the app and agents do: replaces text in every story, rewrites runs, fills a
    /// template slot and adds a page with text, a table and an image.
    /// </summary>
    public static void Edit(IdmlDocument document)
    {
        document.ReplaceText("de ", "DE ");
        foreach (var story in document.Stories.Take(10))
        {
            if (story.ContentRuns.Count > 0)
            {
                story.SetRunText(0, "Texto editado " + story.Id, force: true);
            }
        }
        var page = document.AddPage();
        var style = document.Styles.AddParagraphStyle("Bench body", new ParagraphFormat { FontFamily = "Raleway", PointSize = 9, Leading = 11 });
        page.AddText(new Rect(40, 40, 300, 200), LoremIpsum(40), style, name: "bench-text");
        var table = page.AddTextFrame(new Rect(40, 260, 300, 200)).AddTable(6, 3, new TableOptions { HeaderRows = 1 });
        for (var row = 0; row < 6; row++)
        {
            table.SetRow(row, "Campo " + row.ToString(CultureInfo.InvariantCulture), "Valor", "Observación larga " + row.ToString(CultureInfo.InvariantCulture));
        }
        if (Photos.Count > 0)
        {
            page.AddImage(new Rect(360, 40, 200, 150), Photos[0], ImageFit.Fill, name: "bench-photo");
        }
    }

    /// <summary>The sample app's ficha demo, with a studio photo when there is one.</summary>
    public static IdmlDocument FichaDemo() => Samples.FichaDemo.Build(Photos.Count > 0 ? Photos[0] : null);

    /// <summary>
    /// A document like the app's reports (FormReport): A4 sheets with a banner, bars and outlined boxes, label/value
    /// fields sized with <see cref="TextMeasurer"/> through one scratch frame, and four large photos per sheet.
    /// </summary>
    public static IdmlDocument Report(int sheets, PdfFontOptions fonts)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4, Margins = Spacing.All(28.35) });
        var dark = document.Swatches.AddCmyk("Oscuro", 0, 0, 0, 80);
        var light = document.Swatches.AddCmyk("Claro", 0, 0, 0, 10);
        var accent = document.Swatches.AddCmyk("Marca", 0, 90, 85, 0);
        var label = document.Styles.AddParagraphStyle("Etiqueta", new ParagraphFormat { FontFamily = "Raleway", FontStyle = "SemiBold", PointSize = 7.5, Leading = 9 });
        var value = document.Styles.AddParagraphStyle("Valor", new ParagraphFormat { FontFamily = "Raleway", PointSize = 7.5, Leading = 9, Justification = Justification.LeftJustified, Hyphenation = true, Language = "$ID/Spanish: Castilian" });
        var bar = document.Styles.AddParagraphStyle("Barra", new ParagraphFormat { FontFamily = "Montserrat", FontStyle = "SemiBold", PointSize = 8, FillColor = Swatch.Paper, Capitalization = Capitalization.AllCaps });
        var code = document.Styles.AddParagraphStyle("Código", new ParagraphFormat { FontFamily = "Jost", FontStyle = "Medium", PointSize = 20 });
        var outline = new ShapeStyle { Stroke = dark, StrokeWeight = 0.5, Fill = Swatch.None };

        var scratch = document.Pages[0].AddTextFrame(new Rect(0, 0, 100, 20));
        using (var measurer = new TextMeasurer(document, fonts))
        {
            double Height(string text, ParagraphStyle style, double width)
            {
                scratch.SetBounds(new Rect(0, 0, width, 20));
                scratch.Inset = Spacing.All(2);
                scratch.Story.SetText(text, style);
                return measurer.Measure(scratch).Height;
            }

            for (var sheet = 0; sheet < sheets; sheet++)
            {
                var page = sheet == 0 ? document.Pages[0] : document.AddPage();
                var sheetCode = "AGS.DES." + (sheet + 1).ToString("00", CultureInfo.InvariantCulture);
                page.AddRectangle(new Rect(28, 20, 539, 40), new ShapeStyle { Fill = light });
                page.AddText(new Rect(34, 26, 200, 30), sheetCode, code, name: "code-" + sheet.ToString(CultureInfo.InvariantCulture));
                if (Logo is { } logo)
                {
                    page.AddImage(new Rect(480, 24, 80, 32), logo, ImageFit.Fit);
                }

                // Left column: sections with label/value rows sized to their text.
                var y = 70.0;
                for (var section = 0; section < 4; section++)
                {
                    page.AddRectangle(new Rect(28, y, 260, 14), new ShapeStyle { Fill = dark });
                    page.AddText(new Rect(32, y + 2, 250, 12), "Sección " + (section + 1).ToString(CultureInfo.InvariantCulture) + " del ensayo", bar);
                    y += 16;
                    for (var field = 0; field < 6; field++)
                    {
                        var text = Field(sheet, section, field);
                        var height = Math.Max(Height("Etiqueta del campo " + field.ToString(CultureInfo.InvariantCulture), label, 90), Height(text, value, 170));
                        page.AddRectangle(new Rect(28, y, 90, height), outline);
                        page.AddRectangle(new Rect(118, y, 170, height), outline);
                        page.AddText(new Rect(28, y, 90, height), "Etiqueta del campo " + field.ToString(CultureInfo.InvariantCulture), label).Inset = Spacing.All(2);
                        page.AddText(new Rect(118, y, 170, height), text, value).Inset = Spacing.All(2);
                        y += height;
                    }
                    y += 6;
                }

                // Right column: four photos with captions and a red mark.
                for (var photo = 0; photo < 4 && Photos.Count > 0; photo++)
                {
                    var top = 70 + (photo * 185);
                    page.AddRectangle(new Rect(298, top, 269, 14), new ShapeStyle { Fill = light });
                    page.AddText(new Rect(302, top + 2, 260, 12), "Fotografía " + (photo + 1).ToString(CultureInfo.InvariantCulture), bar);
                    page.AddImage(new Rect(298, top + 14, 269, 160), Photos[((sheet * 4) + photo) % Photos.Count], ImageFit.Fill);
                    page.AddRectangle(new Rect(380, top + 60, 60, 40), new ShapeStyle { Stroke = accent, StrokeWeight = 1, StrokeKind = StrokeKind.Dashed, Fill = Swatch.None });
                }
            }
        }
        scratch.Remove();
        return document;
    }

    /// <summary>Texts of every length, Spanish, some long enough to hyphenate and wrap over several lines.</summary>
    public static string Field(int sheet, int section, int field)
    {
        var words = 2 + (((sheet * 7) + (section * 5) + (field * 3)) % 40);
        return LoremIpsum(words, seed: (sheet * 31) + (section * 7) + field);
    }

    public static string LoremIpsum(int words, int seed = 0)
    {
        string[] vocabulary =
        [
            "la", "conductividad", "inicial", "es", "ligeramente", "superior", "en", "comparación", "con", "el", "ensayo",
            "anterior", "se", "observa", "desprendimiento", "del", "mortero", "de", "cal", "hidráulica", "natural", "sobre",
            "la", "superficie", "pétrea", "tras", "la", "limpieza", "con", "agua", "nebulizada", "y", "cepillado", "suave",
            "consolidación", "fachada", "principal", "muestra", "NO₃⁻", "pH", "7,4", "mS/cm", "(cont.)", "característico",
        ];
        var parts = new string[words];
        for (var i = 0; i < words; i++)
        {
            parts[i] = vocabulary[((i * 17) + (seed * 13) + (i * i)) % vocabulary.Length];
        }
        return string.Join(' ', parts);
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static List<string> FindPhotos() =>
        Directory.Exists(RealLinks)
            ? Directory.EnumerateFiles(RealLinks)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg")
                .Where(path => new FileInfo(path).Length > 1_000_000)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    private static Dictionary<string, string> FindDocuments()
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RealIdml is { } real)
        {
            documents["ficha-real"] = real;
        }
        documents["magazineA-edito"] = TestData("magazineA-edito.idml");
        documents["article-1photo"] = TestData("article-1photo.idml");
        return documents;
    }

    private static string FindRoot([CallerFilePath] string source = "")
    {
        // This file is benchmarks/CoreIdml.Benchmarks/Corpus.cs.
        for (var directory = Path.GetDirectoryName(source); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "CoreIdml.slnx")))
            {
                return directory;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
