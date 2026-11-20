using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Paragraphs that span or split columns, balanced columns and auto-sizing text frames, with the rules measured in
/// InDesign 20's export of the doc-samples 06-frame-span-balance and 06-frame-autosize packages.
/// </summary>
public sealed class FrameColumnTests
{
    // Liberation Serif 10 on 12 pt leading.
    private static readonly ParagraphFormat Body = new() { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12, Hyphenation = false };

    private const string Headline = "Netherfield Park is let at last";

    private static IdmlDocument NewDocument() => IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });

    /// <summary>One paragraph of these lines, separated by forced line breaks (U+2028).</summary>
    private static string Lines(int count) => string.Join(char.ConvertFromUtf32(0x2028), Enumerable.Range(1, count).Select(i => $"line {i}"));

    private static TextFrame Frame(IdmlDocument document, Rect bounds, int columns = 1)
    {
        var frame = document.Pages[0].AddTextFrame(bounds);
        frame.Columns = columns;
        frame.ColumnGutter = 12;
        return frame;
    }

    private static void SetPreference(TextFrame frame, string name, string value) =>
        frame.Element.Element("TextFramePreference")!.SetAttributeValue(name, value);

    /// <summary>A 16/20 pt headline with 6 pt space after that spans <paramref name="count"/> columns.</summary>
    private static ParagraphStyle SpanStyle(IdmlDocument document, string count = "All")
    {
        var style = document.Styles.AddParagraphStyle("Span" + count, new ParagraphFormat
        {
            FontFamily = "Liberation Serif",
            PointSize = 16,
            Leading = 20,
            SpaceAfter = 6,
            Hyphenation = false,
        });
        style.Element.SetAttributeValue("SpanColumnType", "SpanColumns");
        if (style.Element.Element("Properties") is not { } properties)
        {
            style.Element.Add(properties = new XElement("Properties"));
        }
        properties.Add(new XElement("SpanSplitColumnCount", new XAttribute("type", count == "All" ? "enumeration" : "short"), count));
        return style;
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    private static List<LineInfo> Body10(StoryLayout layout) => layout.Lines.Where(line => line.Text.StartsWith("line", StringComparison.Ordinal)).ToList();

    [Fact]
    public void A_headline_spanning_all_columns_is_set_across_them_and_every_column_starts_below_it()
    {
        var document = NewDocument();
        var frame = Frame(document, new Rect(0, 0, 300, 80), columns: 3);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        frame.Story.AddParagraph(Headline, SpanStyle(document));
        frame.Story.AddParagraph(Lines(12), body);

        var layout = Layout(document, frame);
        var lines = Body10(layout);

        // One line across the frame, where a 92 pt column would take three.
        Assert.Equal(Headline, layout.Lines[0].Text);
        // 06-frame-span-balance p1: each column's first line is the span's space after and a leading below it.
        var first = layout.Lines[0].Baseline + 6 + 12;
        Assert.Equal([0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2], lines.Select(line => line.Column));
        var tops = lines.GroupBy(line => line.Column).Select(column => column.First()).ToList();
        Assert.All(tops, line => Assert.Equal(first, line.Baseline, 6));
        Assert.Equal([0, 104, 208], tops.Select(line => line.Left - frame.InnerBounds.X));
        Assert.False(layout.Overset);
    }

    [Fact]
    public void Text_above_a_spanning_headline_is_balanced_and_the_headline_starts_below_its_longest_column()
    {
        var document = NewDocument();
        var frame = Frame(document, new Rect(0, 0, 300, 300), columns: 3);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        frame.Story.AddParagraph(Lines(7), body);
        frame.Story.AddParagraph(Headline, SpanStyle(document));
        frame.Story.AddParagraph(Lines(2), body);

        var layout = Layout(document, frame);
        var above = Body10(layout).Take(7).ToList();
        var head = layout.Lines.Single(line => line.Text == Headline);

        // 06-frame-span-balance p2: 13 lines go 5, 5 and 3; here 7 go 3, 3 and 1.
        Assert.Equal([0, 0, 0, 1, 1, 1, 2], above.Select(line => line.Column));
        Assert.Equal(above[2].Baseline, above[5].Baseline, 6);
        Assert.Equal(above[2].Baseline + 20, head.Baseline, 6);
        Assert.Equal(0, head.Column);
        // The text below starts in the first column again.
        Assert.All(Body10(layout).Skip(7), line => Assert.Equal(0, line.Column));
        Assert.Equal(head.Baseline + 6 + 12, Body10(layout)[7].Baseline, 6);
    }

    [Fact]
    public void A_headline_spanning_two_of_three_columns_leaves_the_third_from_the_top()
    {
        var document = NewDocument();
        var frame = Frame(document, new Rect(0, 0, 300, 80), columns: 3);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        frame.Story.AddParagraph("Headline", SpanStyle(document, "2"));
        frame.Story.AddParagraph(Lines(12), body);

        var layout = Layout(document, frame);
        var lines = Body10(layout);

        var below = layout.Lines[0].Baseline + 6 + 12;
        Assert.Equal(below, lines.First(line => line.Column == 1).Baseline, 6);
        // The third column starts at the frame's top, at the first-baseline offset of its first line.
        var third = lines.Where(line => line.Column == 2).ToList();
        Assert.NotEmpty(third);
        Assert.True(third[0].Baseline < layout.Lines[0].Baseline);
        Assert.Equal(208, third[0].Left - frame.InnerBounds.X, 6);
    }

    [Fact]
    public void A_split_paragraph_is_balanced_across_its_sub_columns_and_the_text_goes_on_below()
    {
        var document = NewDocument();
        var frame = Frame(document, new Rect(0, 0, 300, 300));
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var split = document.Styles.AddParagraphStyle("Split", new ParagraphFormat(), body);
        split.Element.SetAttributeValue("SpanColumnType", "SplitColumns");
        split.Element.SetAttributeValue("SplitColumnInsideGutter", "12");
        split.Element.SetAttributeValue("SplitColumnOutsideGutter", "6");
        frame.Story.AddParagraph(Lines(7), split);
        frame.Story.AddParagraph("line after", body);

        var lines = Body10(Layout(document, frame));

        // 06-frame-span-balance p4: 7 lines go 4 and 3; sub-columns of (300 - 2 * 6 - 12) / 2 = 138 pt.
        Assert.Equal([6, 6, 6, 6, 156, 156, 156, 0], lines.Select(line => line.Left - frame.InnerBounds.X));
        Assert.Equal(lines[0].Baseline, lines[4].Baseline, 6);
        Assert.Equal(lines[3].Baseline + 12, lines[7].Baseline, 6);
    }

    [Theory]
    [InlineData(false, new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(true, new[] { 0, 0, 0, 1, 1, 1, 2, 2, 2 })]
    public void Balanced_columns_end_at_the_same_height(bool balance, int[] columns)
    {
        var document = NewDocument();
        var frame = Frame(document, new Rect(0, 0, 300, 300), columns: 3);
        if (balance)
        {
            SetPreference(frame, "VerticalBalanceColumns", "true");
        }
        frame.Story.AddParagraph(Lines(9), document.Styles.AddParagraphStyle("Body", Body));

        var lines = Body10(Layout(document, frame));

        // 06-frame-span-balance p5: 18 lines go 6, 6 and 6 with balance on, all into the first column without.
        Assert.Equal(columns, lines.Select(line => line.Column));
    }

    [Theory]
    [InlineData("TopCenterPoint", 0)]
    [InlineData("CenterPoint", 0.5)]
    [InlineData("BottomCenterPoint", 1)]
    public void A_height_auto_sizing_frame_grows_from_its_reference_point_and_is_not_overset(string point, double moved)
    {
        var document = NewDocument();
        var frame = Frame(document, new Rect(0, 100, 200, 30));
        SetPreference(frame, "AutoSizingType", "HeightOnly");
        SetPreference(frame, "AutoSizingReferencePoint", point);
        frame.Story.AddParagraph(Lines(6), document.Styles.AddParagraphStyle("Body", Body));
        using var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());

        var areas = frame.Story.Frames.Select(f => renderer.FrameArea(f, "1")).ToList();
        var (layout, fitted) = renderer.ComposeFitted(frame.Story, frame.Story.Frames, areas);

        // 06-frame-autosize p1, p2: the frame ends at its last baseline (no inset here).
        Assert.False(layout.Overset);
        Assert.Equal(6, layout.Lines.Count);
        var bounds = fitted[0].Bounds;
        var height = layout.Lines[^1].Baseline - bounds.Y;
        Assert.Equal(height, bounds.Height, 6);
        Assert.Equal(frame.InnerBounds.Y - ((height - 30) * moved), bounds.Y, 6);
        using var measurer = new TextMeasurer(document, TestSupport.Fonts());
        Assert.False(measurer.CheckFit(frame.Story).Overset);
    }

    [Fact]
    public void A_frame_that_already_has_the_size_of_its_text_keeps_it()
    {
        var document = NewDocument();
        var first = Frame(document, new Rect(0, 100, 200, 300));
        SetPreference(first, "AutoSizingType", "HeightOnly");
        first.Story.AddParagraph(Lines(3), document.Styles.AddParagraphStyle("Body", Body));
        using var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        var (_, shrunk) = renderer.ComposeFitted(first.Story, first.Story.Frames, [renderer.FrameArea(first, "1")]);
        var size = shrunk[0].Bounds;
        // Shorter text shrinks it around its centre, the default reference point.
        Assert.Equal(first.InnerBounds.Y + 150, size.Y + (size.Height / 2), 6);

        var exact = Frame(document, new Rect(250, 100, 200, size.Height));
        SetPreference(exact, "AutoSizingType", "HeightOnly");
        exact.Story.AddParagraph(Lines(3), document.Styles.FindParagraphStyle("Body"));
        var stored = renderer.FrameArea(exact, "1");
        var (_, kept) = renderer.ComposeFitted(exact.Story, exact.Story.Frames, [stored]);

        Assert.Same(stored, kept[0]);
    }

    [Fact]
    public void A_width_auto_sizing_frame_without_line_breaks_takes_the_width_of_its_line()
    {
        var document = NewDocument();
        var frame = Frame(document, new Rect(50, 100, 60, 20));
        SetPreference(frame, "AutoSizingType", "WidthOnly");
        SetPreference(frame, "AutoSizingReferencePoint", "LeftCenterPoint");
        SetPreference(frame, "UseNoLineBreaksForAutoSizing", "true");
        frame.Story.AddParagraph(Headline, document.Styles.AddParagraphStyle("Body", Body));
        using var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());

        var (layout, fitted) = renderer.ComposeFitted(frame.Story, frame.Story.Frames, [renderer.FrameArea(frame, "1")]);

        // 06-frame-autosize p3: one line, the frame's left edge kept.
        var line = Assert.Single(layout.Lines);
        Assert.Equal(Headline, line.Text);
        Assert.Equal(frame.InnerBounds.X, fitted[0].Bounds.X, 6);
        Assert.Equal(line.Width, fitted[0].Bounds.Width, 1);
    }
}
