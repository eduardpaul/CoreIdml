using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>The text composer: line breaking, leading, alignment, spacing, columns, threads, tables.</summary>
public sealed class LayoutTests
{
    private const string Lorem = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.";

    private static (IdmlDocument Document, Page Page) NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        return (document, document.Pages[0]);
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame)
    {
        var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        return renderer.LayoutStory(frame.Story);
    }

    private static double SerifAscent(double size)
    {
        var face = TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular");
        return face.Ascent * size;
    }

    [Fact]
    public void Lines_break_at_spaces_within_the_column_and_keep_every_word()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12, Leading = 14 });
        var frame = page.AddText(new Rect(50, 50, 150, 300), Lorem, style);
        var layout = Layout(document, frame);

        Assert.True(layout.Lines.Count >= 4, $"expected several lines, got {layout.Lines.Count}");
        Assert.All(layout.Lines, line => Assert.True(line.Width <= 150 + 0.01, $"line '{line.Text}' is {line.Width} pt wide"));
        Assert.Equal(Lorem, string.Join(" ", layout.Lines.Select(l => l.Text)));
        Assert.False(layout.Overset);
    }

    [Fact]
    public void First_baseline_is_the_ascent_and_following_lines_step_by_the_leading()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 20 });
        var frame = page.AddText(new Rect(0, 0, 120, 400), Lorem, style);
        var layout = Layout(document, frame);
        var top = frame.InnerBounds.Y;

        Assert.Equal(top + SerifAscent(10), layout.Lines[0].Baseline, 3);
        for (var i = 1; i < layout.Lines.Count; i++)
        {
            Assert.Equal(20, layout.Lines[i].Baseline - layout.Lines[i - 1].Baseline, 6);
        }
    }

    [Fact]
    public void Auto_leading_is_120_percent_of_the_size()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10 });
        var layout = Layout(document, page.AddText(new Rect(0, 0, 100, 400), Lorem, style));
        Assert.Equal(12, layout.Lines[1].Baseline - layout.Lines[0].Baseline, 6);
    }

    [Theory]
    [InlineData(Justification.Left, 0)]
    [InlineData(Justification.Center, 0.5)]
    [InlineData(Justification.Right, 1)]
    public void Alignment_positions_the_line_in_the_column(Justification justification, double factor)
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12, Justification = justification });
        var frame = page.AddText(new Rect(0, 0, 300, 100), "Short line", style);
        var layout = Layout(document, frame);
        var run = layout.OpsByFrame[frame.Id].OfType<GlyphRunOp>().Single();
        var free = 300 - run.Width;
        Assert.Equal(frame.InnerBounds.X + (free * factor), run.X, 3);
    }

    [Fact]
    public void Justified_lines_fill_the_column_except_the_last()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 11, Justification = Justification.LeftJustified });
        var frame = page.AddText(new Rect(0, 0, 160, 400), Lorem, style);
        var layout = Layout(document, frame);
        foreach (var line in layout.Lines.Take(layout.Lines.Count - 1))
        {
            Assert.Equal(160, line.Width, 3);
        }
        Assert.True(layout.Lines[^1].Width < 160);
    }

    [Fact]
    public void Space_before_is_ignored_at_the_top_of_a_frame_and_applied_between_paragraphs()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12, SpaceBefore = 9, SpaceAfter = 4 });
        var frame = page.AddText(new Rect(0, 0, 400, 400), "One\nTwo", style);
        var layout = Layout(document, frame);
        Assert.Equal(frame.InnerBounds.Y + SerifAscent(10), layout.Lines[0].Baseline, 3);
        Assert.Equal(12 + 4 + 9, layout.Lines[1].Baseline - layout.Lines[0].Baseline, 6);
    }

    [Fact]
    public void Indents_shift_and_narrow_the_lines()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, LeftIndent = 20, FirstLineIndent = 15, RightIndent = 30 });
        var frame = page.AddText(new Rect(0, 0, 200, 400), Lorem, style);
        var layout = Layout(document, frame);
        Assert.Equal(35, layout.Lines[0].Left - frame.InnerBounds.X, 6);
        Assert.Equal(20, layout.Lines[1].Left - frame.InnerBounds.X, 6);
        Assert.All(layout.Lines.Skip(1), line => Assert.True(line.Width <= 150 + 0.01));
    }

    [Fact]
    public void Text_flows_through_columns_and_threaded_frames_then_reports_overset()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var first = page.AddTextFrame(new Rect(0, 0, 300, 60));
        first.Columns = 2;
        first.ColumnGutter = 10;
        var second = page.AddTextFrame(new Rect(0, 100, 300, 40));
        first.ThreadTo(second);
        for (var i = 0; i < 6; i++)
        {
            first.Story.AddParagraph(Lorem, style);
        }
        var layout = Layout(document, first);

        Assert.Contains(layout.Lines, line => line.FrameIndex == 0 && line.Column == 0);
        Assert.Contains(layout.Lines, line => line.FrameIndex == 0 && line.Column == 1);
        Assert.Contains(layout.Lines, line => line.FrameIndex == 1);
        var secondColumn = layout.Lines.First(line => line.FrameIndex == 0 && line.Column == 1);
        Assert.Equal(155, secondColumn.Left - first.InnerBounds.X, 6);
        Assert.True(layout.Overset);
        Assert.All(layout.Lines.Where(l => l.FrameIndex == 0), line => Assert.True(line.Baseline <= first.InnerBounds.Bottom));
    }

    [Fact]
    public void Vertical_justification_centres_the_text()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var frame = page.AddText(new Rect(0, 0, 200, 100), "Centred", style);
        frame.VerticalJustification = VerticalJustification.Center;
        var layout = Layout(document, frame);
        var run = layout.OpsByFrame[frame.Id].OfType<GlyphRunOp>().Single();
        var face = TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular");
        // Like InDesign cells (measured on real InDesign exports), the text block ends at the last baseline.
        var textHeight = face.Ascent * 10;
        Assert.Equal(frame.InnerBounds.Y + ((100 - textHeight) / 2) + (face.Ascent * 10), run.Y, 3);
    }

    [Theory]
    [InlineData(null, "fi")]
    [InlineData("false", "f|i")]
    public void Standard_ligatures_replace_fi_unless_turned_off(string? ligatures, string expected)
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12 });
        var frame = page.AddText(new Rect(0, 0, 300, 100), "fin", style);
        frame.Story.Element.Descendants("CharacterStyleRange").Single().SetAttributeValue("Ligatures", ligatures);
        var options = new PdfExportOptions { Fonts = new PdfFontOptions { UseSystemFonts = false }.AddFont("LiberationSerif-Regular.ttf", SerifWithStandardLigatures()) };

        var run = Assert.Single(new IdmlRenderer(document, options).LayoutStory(frame.Story).OpsByFrame[frame.Id].OfType<GlyphRunOp>());
        Assert.Equal(expected, string.Join("|", run.Glyphs.Take(run.Glyphs.Count - 1).Select(g => g.Text)));
        Assert.Equal("fin", run.Text);
        if (ligatures is null)
        {
            // One glyph, narrower than f and i set apart.
            var separate = Layout(document, frame).OpsByFrame[frame.Id].OfType<GlyphRunOp>().Single();
            Assert.True(run.Glyphs[0].Advance < separate.Glyphs[0].Advance + separate.Glyphs[1].Advance);
        }
    }

    /// <summary>
    /// Liberation Serif has an fi glyph (U+FB01) but no standard ligature using it; its one ligature lookup (under
    /// dlig) is rewritten to make f + i -> fi a liga ligature, the way fonts such as Raleway define it.
    /// </summary>
    private static byte[] SerifWithStandardLigatures()
    {
        var face = TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular");
        Assert.True(face.TryMap('f', out var f));
        Assert.True(face.TryMap('i', out var i));
        Assert.True(face.TryMap(0xFB01, out var fi));
        var data = File.ReadAllBytes(Path.Combine(TestSupport.FontDirectory, "LiberationSerif-Regular.ttf"));
        int U16(int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        void Set(int at, int value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)value);
        var gsub = Enumerable.Range(0, U16(4)).Select(t => 12 + (16 * t)).Single(record => System.Text.Encoding.ASCII.GetString(data, record, 4) == "GSUB");
        var table = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(gsub + 8));
        var features = table + U16(table + 6);
        var lookups = table + U16(table + 8);
        var record = Enumerable.Range(0, U16(features)).Select(n => features + 2 + (6 * n)).Single(r => System.Text.Encoding.ASCII.GetString(data, r, 4) == "dlig");
        "liga"u8.CopyTo(data.AsSpan(record));
        var feature = features + U16(record + 4);
        var lookup = lookups + U16(lookups + 2 + (2 * U16(feature + 4)));
        var subtable = lookup + U16(lookup + 6);
        Set(lookup + 2, 0); // lookup flags: none
        Set(subtable + U16(subtable + 2) + 4, f); // coverage (format 1, one glyph): the first component
        var set = subtable + U16(subtable + 6);
        var ligature = set + U16(set + 2);
        Set(ligature, fi);
        Assert.Equal(2, U16(ligature + 2));
        Set(ligature + 4, i);
        return data;
    }

    [Fact]
    public void Kerning_applies_across_a_hyphenation_point_that_is_not_used()
    {
        // LA-VA-DO-RA: a Spanish hyphenation point sits inside the kerned pair AV.
        double Width(bool hyphenation)
        {
            var (document, page) = NewDocument();
            var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12, Language = Languages.Spanish, Hyphenation = hyphenation });
            var frame = page.AddText(new Rect(0, 0, 300, 100), "LAVADORA", style);
            return Assert.Single(Layout(document, frame).Lines).Width;
        }
        Assert.Equal(Width(hyphenation: false), Width(hyphenation: true), 6);
    }

    [Fact]
    public void Pair_kerning_tightens_AV()
    {
        var face = TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular");
        Assert.True(face.TryMap('A', out var a));
        Assert.True(face.TryMap('V', out var v));
        Assert.True(face.Kerning(a, v) < 0);
    }

    [Fact]
    public void Tables_place_rows_at_least_their_stored_height_and_grow_to_fit()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Cell", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var frame = page.AddTextFrame(new Rect(0, 0, 200, 300));
        var table = frame.AddTable(2, 2, new TableOptions { RowHeight = 20, CellParagraphStyle = style, CellInset = 4 });
        table.SetRow(0, "A", "B");
        table.SetRow(1, "A long cell text that needs several lines in a narrow column", "C");
        var layout = Layout(document, frame);
        var ops = layout.OpsByFrame[frame.Id];

        var horizontalEdges = ops.OfType<LineOp>().Where(l => Math.Abs(l.From.Y - l.To.Y) < 1e-6 && l.From.X == frame.InnerBounds.X).Select(l => l.From.Y).Distinct().Order().ToList();
        Assert.Equal(20, horizontalEdges[1] - horizontalEdges[0], 6);
        Assert.True(horizontalEdges[^1] - horizontalEdges[1] > 20, "the second row grows to fit its text");
        var cellText = ops.OfType<GlyphRunOp>().Where(r => r.Text.Contains("long", StringComparison.Ordinal)).Single();
        Assert.True(cellText.X >= frame.InnerBounds.X + 4 - 1e-6);
        Assert.True(cellText.X + cellText.Width <= frame.InnerBounds.X + 100 - 4 + 1e-6);
    }

    [Fact]
    public void A_merged_cell_without_an_edge_weight_leaves_each_segment_to_its_neighbours()
    {
        var (document, page) = NewDocument();
        var frame = page.AddTextFrame(new Rect(0, 0, 200, 300));
        var table = frame.AddTable(3, 2, new TableOptions { ColumnWidths = [20, 180], RowHeight = 20, StrokeWeight = 1 });
        // Column 1 merged over the three rows, as InDesign writes it: no Left/Right weight because the segments
        // differ (only the middle cell of column 0, a checkbox, has a right edge).
        var merged = table[0, 1].Element;
        merged.SetAttributeValue("RowSpan", "3");
        merged.SetAttributeValue("LeftEdgeStrokeWeight", null);
        merged.SetAttributeValue("RightEdgeStrokeWeight", null);
        table[1, 1].Element.Remove();
        table[2, 1].Element.Remove();
        for (var row = 0; row < 3; row++)
        {
            table[row, 0].Element.SetAttributeValue("RightEdgeStrokeWeight", row == 1 ? "0.5" : "0");
        }
        var ops = Layout(document, frame).OpsByFrame[frame.Id];

        var x = frame.InnerBounds.X;
        var vertical = ops.OfType<LineOp>().Where(l => Math.Abs(l.From.X - l.To.X) < 1e-6).ToList();
        var between = Assert.Single(vertical, l => Math.Abs(l.From.X - (x + 20)) < 1e-6);
        Assert.Equal(0.5, between.Width, 6);
        Assert.Equal(20, Math.Abs(between.To.Y - between.From.Y), 6);
        Assert.DoesNotContain(vertical, l => Math.Abs(l.From.X - (x + 200)) < 1e-6);
    }

    [Fact]
    public void A_rule_shared_by_a_merged_cell_and_the_cells_below_is_drawn_once()
    {
        var (document, page) = NewDocument();
        var frame = page.AddTextFrame(new Rect(0, 0, 200, 300));
        var table = frame.AddTable(2, 2, new TableOptions { ColumnWidths = [80, 120], RowHeight = 20, StrokeWeight = 0.75 });
        // A heading merged across both columns, like the sample's section titles.
        table[0, 0].Element.SetAttributeValue("ColumnSpan", "2");
        table[0, 1].Element.Remove();
        var ops = Layout(document, frame).OpsByFrame[frame.Id];

        var y = frame.InnerBounds.Y + 20;
        var rule = Assert.Single(ops.OfType<LineOp>(), l => Math.Abs(l.From.Y - y) < 1e-6 && Math.Abs(l.To.Y - y) < 1e-6);
        Assert.Equal(frame.InnerBounds.X, Math.Min(rule.From.X, rule.To.X), 6);
        Assert.Equal(frame.InnerBounds.X + 200, Math.Max(rule.From.X, rule.To.X), 6);
    }

    [Fact]
    public void Rows_joined_by_a_merged_cell_go_overset_together()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Cell", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 8, Leading = 10 });
        var frame = page.AddTextFrame(new Rect(0, 0, 200, 50));
        var table = frame.AddTable(3, 2, new TableOptions { RowHeight = 20, CellParagraphStyle = style, CellInset = 2, StrokeWeight = 1 });
        table.SetRow(0, "First", "Row");
        table.SetRow(1, "Box", "Merged");
        table.SetRow(2, "Box", "");
        // The second column of the last two rows is one cell; the pair needs 40 pt and only 30 are left.
        table[1, 1].Element.SetAttributeValue("RowSpan", "2");
        table[2, 1].Element.Remove();
        var layout = Layout(document, frame);
        var ops = layout.OpsByFrame[frame.Id];

        Assert.True(layout.Overset);
        Assert.DoesNotContain(ops.OfType<GlyphRunOp>(), run => run.Text is "Merged" or "Box");
        Assert.All(ops.OfType<LineOp>(), line => Assert.True(Math.Max(line.From.Y, line.To.Y) <= frame.InnerBounds.Y + 20 + 1e-6, $"edge drawn at {line.From.Y}..{line.To.Y}"));
    }

    [Fact]
    public void Overset_master_page_stories_are_reported()
    {
        var (document, page) = NewDocument();
        var master = Assert.IsType<MasterSpread>(page.AppliedMaster);
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12, Leading = 14 });
        var frame = master.Pages[0].AddText(new Rect(50, 50, 60, 20), Lorem, style);

        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(document, stream, TestSupport.ExportOptions());
        Assert.Contains(frame.StoryId, result.OversetStories);
    }

    [Fact]
    public void Characters_the_document_font_lacks_are_left_out_and_reported()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12 });
        var frame = page.AddText(new Rect(0, 0, 300, 100), "A\u4E2DB", style);

        // Like InDesign: no glyph from another font, no space taken.
        var run = Assert.Single(Layout(document, frame).OpsByFrame[frame.Id].OfType<GlyphRunOp>());
        Assert.Equal("AB", run.Text);
        Assert.Equal(TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular").DisplayName, run.Font.DisplayName);

        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(document, stream, TestSupport.ExportOptions());
        Assert.Contains(result.Warnings, warning => warning.Contains("U+4E2D", StringComparison.Ordinal) && warning.Contains("Liberation Serif", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_fonts_fall_back_and_are_reported()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Futura PT", FontStyle = "Bold", PointSize = 10 });
        page.AddText(new Rect(0, 0, 200, 100), "Hola", style);
        var options = TestSupport.ExportOptions();
        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(document, stream, options);
        var substitution = Assert.Single(result.FontSubstitutions, s => s.RequestedFamily == "Futura PT");
        Assert.Equal("Liberation Sans Bold", substitution.UsedFont);
        Assert.Equal(["1"], substitution.Pages);
        Assert.Single(substitution.Stories);
        Assert.Contains("\"missingFonts\":[{\"requestedFamily\":\"Futura PT\"", Json.PdfJson.ToJson(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Fonts_load_from_memory_and_restricted_licences_are_not_embedded()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Sans", PointSize = 10 });
        page.AddText(new Rect(0, 0, 200, 100), "Hola", style);

        // From bytes, as an app would pass a font picked by the user or read from its assets.
        var bytes = File.ReadAllBytes(Path.Combine(TestSupport.FontDirectory, "LiberationSans-Regular.ttf"));
        var options = new PdfExportOptions { Fonts = new PdfFontOptions { UseSystemFonts = false }.AddFont("LiberationSans-Regular.ttf", bytes) };
        using (var stream = new MemoryStream())
        {
            Assert.Empty(IdmlPdf.Export(document, stream, options).FontSubstitutions);
        }

        // The same font with OS/2 fsType = 2 (restricted licence): found, but neither embedded nor used.
        var restricted = (byte[])bytes.Clone();
        var font = Fonts.OpenTypeFont.Load(restricted);
        var os2 = font.Tables["OS/2"].Offset;
        restricted[os2 + 8] = 0;
        restricted[os2 + 9] = 2;
        options = new PdfExportOptions { Fonts = new PdfFontOptions { UseSystemFonts = false }.AddFont("restricted.ttf", restricted) };
        using (var stream = new MemoryStream())
        {
            var substitution = Assert.Single(IdmlPdf.Export(document, stream, options).FontSubstitutions);
            Assert.True(substitution.EmbeddingRestricted);
            Assert.Equal("Helvetica", substitution.UsedFont);
        }
    }

    [Fact]
    public void Without_any_font_files_the_standard_pdf_fonts_are_used()
    {
        var (document, page) = NewDocument();
        page.AddText(new Rect(0, 0, 200, 100), "Hola, señor");
        var options = new PdfExportOptions { Fonts = new PdfFontOptions { UseSystemFonts = false } };
        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(document, stream, options);
        Assert.Contains(result.FontSubstitutions, s => s.UsedFont == "Times-Roman");
        using var reader = UglyToad.PdfPig.PdfDocument.Open(stream.ToArray());
        Assert.Contains("señor", reader.GetPage(1).Text, StringComparison.Ordinal);
    }
}
