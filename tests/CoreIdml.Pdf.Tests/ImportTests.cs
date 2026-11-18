using CoreIdml.Dom;
using CoreIdml.Pdf.Import;

namespace CoreIdml.Pdf.Tests;

/// <summary>PDF → IDML: grouping glyphs into lines, blocks and paragraphs, and the full round trip.</summary>
public sealed class ImportTests
{
    private static readonly ResolvedColor Black = new(ColorSpaceKind.Cmyk, [0, 0, 0, 1]);

    /// <summary>Glyphs for a word sequence at a baseline, 0.5 em per character, 0.25 em spaces.</summary>
    private static IEnumerable<TextGlyph> Words(string text, double x, double baseline, double size = 10, double wordGap = 2.5, string style = "Regular")
    {
        foreach (var c in text)
        {
            if (c == ' ')
            {
                yield return new TextGlyph(" ", x, baseline, wordGap, size, "Liberation Serif", style, Black);
                x += wordGap;
                continue;
            }
            yield return new TextGlyph(c.ToString(), x, baseline, size * 0.5, size, "Liberation Serif", style, Black);
            x += size * 0.5;
        }
    }

    [Fact]
    public void Glyphs_on_one_baseline_form_a_line_with_spaces_restored()
    {
        var glyphs = Words("Hola mundo", 10, 100).Where(g => g.Text != " ").ToList();
        var lines = TextGrouping.BuildLines(glyphs);
        var line = Assert.Single(lines);
        Assert.Equal("Hola mundo", line.Text);
        Assert.Equal(100, line.Baseline, 6);
    }

    [Fact]
    public void A_gutter_shared_by_neighbouring_lines_splits_columns()
    {
        var glyphs = new List<TextGlyph>();
        for (var i = 0; i < 4; i++)
        {
            glyphs.AddRange(Words("left column text", 0, 100 + (i * 12)));
            glyphs.AddRange(Words("right column", 100, 100 + (i * 12)));
        }
        var lines = TextGrouping.BuildLines(glyphs);
        Assert.Equal(8, lines.Count);
        var blocks = TextGrouping.BuildBlocks(lines, 1.6);
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(4, block.Lines.Count));
    }

    [Fact]
    public void Wide_word_spaces_of_one_justified_line_do_not_split_it()
    {
        var glyphs = new List<TextGlyph>();
        glyphs.AddRange(Words("aaaa bbbb cccc", 0, 100, wordGap: 9));
        glyphs.AddRange(Words("dddddd eeeeeee ffff", 0, 112));
        glyphs.AddRange(Words("gggg hhhhhh iiiiii", 0, 124));
        var lines = TextGrouping.BuildLines(glyphs);
        Assert.Equal(3, lines.Count);
        Assert.Equal("aaaa bbbb cccc", lines[0].Text);
    }

    [Fact]
    public void A_rule_between_lines_keeps_them_in_separate_blocks()
    {
        var glyphs = Words("Código", 0, 100).Concat(Words("Altura", 0, 112)).ToList();
        var lines = TextGrouping.BuildLines(glyphs);
        Assert.Single(TextGrouping.BuildBlocks(lines, 1.6));
        var ruled = TextGrouping.BuildBlocks(lines, 1.6, [new Rule(-5, 104, 80, 104)]);
        Assert.Equal(2, ruled.Count);
    }

    [Fact]
    public void Paragraphs_start_after_wider_gaps_and_short_sentence_endings()
    {
        var glyphs = new List<TextGlyph>();
        glyphs.AddRange(Words("aaaa bbbb cccc dddd", 0, 100));
        glyphs.AddRange(Words("eeee ffff.", 0, 112));
        glyphs.AddRange(Words("gggg hhhh iiii jjjj", 0, 124));
        glyphs.AddRange(Words("kkkk", 0, 136));
        glyphs.AddRange(Words("llll mmmm nnnn oooo", 0, 154));
        var block = Assert.Single(TextGrouping.BuildBlocks(TextGrouping.BuildLines(glyphs), 1.6));
        Assert.Equal([0, 2, 4], block.ParagraphStarts);
        Assert.Equal(12, block.Leading, 6);
    }

    [Fact]
    public void Alignment_is_inferred_from_line_edges()
    {
        var centred = new List<TextGlyph>();
        centred.AddRange(Words("aaaaaaaa", 20, 100));
        centred.AddRange(Words("bbbb", 30, 112));
        centred.AddRange(Words("cccccc", 25, 124));
        Assert.Equal("CenterAlign", Assert.Single(TextGrouping.BuildBlocks(TextGrouping.BuildLines(centred), 1.6)).Alignment);

        var right = new List<TextGlyph>();
        right.AddRange(Words("aaaaaaaa", 20, 100));
        right.AddRange(Words("bbbb", 40, 112));
        Assert.Equal("RightAlign", Assert.Single(TextGrouping.BuildBlocks(TextGrouping.BuildLines(right), 1.6)).Alignment);
    }

    [Theory]
    [InlineData("ABCDEF+LiberationSerif-Bold", "Liberation Serif", "Bold")]
    [InlineData("TimesNewRomanPS-BoldItalicMT", "Times New Roman", "Bold Italic")]
    [InlineData("MinionPro-Regular", "Minion Pro", "Regular")]
    [InlineData("Helvetica", "Helvetica", "Regular")]
    [InlineData("ArialMT", "Arial", "Regular")]
    public void PDF_font_names_split_into_family_and_style(string name, string family, string style)
    {
        Assert.Equal((family, style), PdfImporter.SplitFontName(name, bold: false, italic: false));
    }

    /// <summary>IDML → PDF → IDML: text, geometry, styles and colours survive within tolerance.</summary>
    [Fact]
    public void Round_trip_keeps_text_geometry_styles_and_colours()
    {
        var directory = TestSupport.OutputDirectory("import-roundtrip");
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4, PageCount = 2 });
        var brand = document.Swatches.AddCmyk("Marca", 75, 45, 0, 10);
        var heading = document.Styles.AddParagraphStyle("Titular", new ParagraphFormat { FontFamily = "Liberation Sans", FontStyle = "Bold", PointSize = 20, Leading = 24, FillColor = brand });
        var body = document.Styles.AddParagraphStyle("Texto", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 13 });
        var bold = document.Styles.AddCharacterStyle("Negrita", new CharacterFormat { FontStyle = "Bold" });
        var page = document.Pages[0];
        page.AddText(new Rect(50, 40, 400, 30), "Informe de inspección", heading);
        var frame = page.AddTextFrame(new Rect(50, 100, 250, 200));
        frame.Story.AddParagraph(body).AddText("El elemento está en ").AddText("buen estado", bold).AddText(" y no requiere intervención inmediata según el equipo de campo.");
        frame.Story.AddParagraph("Segundo párrafo con más texto para comprobar que los párrafos se separan.", body);
        page.AddRectangle(new Rect(320, 100, 150, 60), new ShapeStyle { Fill = brand, Stroke = Swatch.None, StrokeWeight = 0 });
        page.AddLine(new Point(50, 320), new Point(470, 320), new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 1.5 });
        page.AddImage(new Rect(320, 180, 120, 90), TestSupport.WriteTestPng(Path.Combine(directory, "foto.png"), 120, 90), ImageFit.Fill);
        document.Pages[1].AddText(new Rect(50, 50, 300, 40), "Segunda página", body);

        var pdf = TestSupport.Export(document);
        var result = IdmlPdf.Import(new MemoryStream(pdf), new PdfImportOptions { ImageDirectory = Path.Combine(directory, "links") });
        using var imported = result.Document;
        imported.Save(Path.Combine(directory, "imported.idml"));

        Assert.Equal(2, imported.Pages.Count);
        Assert.Equal(1, result.ImageFrames);
        Assert.True(IdmlValidator.Validate(imported).IsValid, IdmlValidator.Validate(imported).ToString());

        // Text, normalized for whitespace.
        static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var frames = imported.Pages[0].PageItems.OfType<TextFrame>().ToList();
        var texts = frames.Select(f => Normalize(f.Story.Text)).ToList();
        Assert.Contains("Informe de inspección", texts);
        Assert.Contains(texts, t => t.StartsWith("El elemento está en buen estado y no requiere", StringComparison.Ordinal) && t.Contains("Segundo párrafo", StringComparison.Ordinal));
        Assert.Contains("Segunda página", imported.Pages[1].PageItems.OfType<TextFrame>().Select(f => Normalize(f.Story.Text)));

        // Two paragraphs in the body frame; the bold run became a character style.
        var bodyFrame = frames.Single(f => f.Story.Text.Contains("elemento", StringComparison.Ordinal));
        Assert.Equal(2, bodyFrame.Story.Text.TrimEnd('\n').Split('\n').Length);
        var boldRun = bodyFrame.Story.Runs.Single(r => r.Text.Contains("buen estado", StringComparison.Ordinal));
        Assert.Contains("Bold", boldRun.CharacterStyle ?? string.Empty, StringComparison.Ordinal);

        // Geometry: text starts where it was; first baseline is fixed at the PDF's.
        Assert.Equal(50, bodyFrame.Bounds.X, 0.5);
        var headingFrame = frames.Single(f => f.Story.Text.Contains("Informe", StringComparison.Ordinal));
        Assert.Equal(50, headingFrame.Bounds.X, 0.5);

        // Styles: size, family and colour inferred.
        var headingStyle = imported.Styles.ParagraphStyles.Single(s => s.Name.StartsWith("Liberation Sans Bold 20", StringComparison.Ordinal));
        Assert.Equal("20", headingStyle.GetAttribute("PointSize"));
        Assert.Contains(imported.Swatches.All, s => s.Name == "C=75 M=45 Y=0 K=10");

        // Shapes: the filled rectangle and the line.
        var rectangle = imported.Pages[0].PageItems.OfType<GraphicFrame>().Single(f => f.Graphic is null && Math.Abs(f.Bounds.Width - 150) < 0.5);
        Assert.Equal(320, rectangle.Bounds.X, 0.5);
        Assert.Equal(100, rectangle.Bounds.Y, 0.5);
        Assert.Equal(60, rectangle.Bounds.Height, 0.5);
        var line = imported.Pages[0].PageItems.OfType<GraphicLine>().Single();
        Assert.Equal(420, line.Bounds.Width, 0.5);

        // The image frame keeps the frame (clip) bounds.
        var image = imported.Pages[0].PageItems.OfType<GraphicFrame>().Single(f => f.Graphic is not null);
        Assert.Equal(320, image.Bounds.X, 0.5);
        Assert.Equal(180, image.Bounds.Y, 0.5);
        Assert.Equal(120, image.Bounds.Width, 0.5);
        Assert.True(File.Exists(Assert.Single(result.Images)));
    }
}
