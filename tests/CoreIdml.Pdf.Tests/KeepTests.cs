using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Keep options and Start Paragraph, as InDesign applies them (measured in doc-samples/08-para-keeps-breaks): where a
/// column break may fall, and where a paragraph starts.
/// </summary>
public sealed class KeepTests
{
    // Liberation Serif 10 on 12 pt leading: a frame 50 pt high holds 4 lines (baselines at 8.9, 20.9, 32.9, 44.9).
    private const double FourLines = 50;

    private static readonly ParagraphFormat Body = new() { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 };

    /// <summary>One paragraph of these lines, separated by forced line breaks (U+2028).</summary>
    private static string Lines(params string[] lines) => string.Join(char.ConvertFromUtf32(0x2028), lines);

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    /// <summary>Two threaded frames of four lines each on one page, the second one below the first.</summary>
    private static (IdmlDocument Document, TextFrame First) TwoFrames()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var page = document.Pages[0];
        var first = page.AddTextFrame(new Rect(0, 0, 300, FourLines));
        var second = page.AddTextFrame(new Rect(0, 100, 300, FourLines));
        first.ThreadTo(second);
        return (document, first);
    }

    private static int FrameOf(StoryLayout layout, string text) => layout.Lines.Single(line => line.Text == text).FrameIndex;

    /// <summary>The text drawn in frame <paramref name="frame"/>, line by line.</summary>
    private static List<string> Drawn(StoryLayout layout, TextFrame frame) =>
        layout.OpsByFrame[frame.Id].OfType<GlyphRunOp>().Select(run => run.Text).ToList();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void Keep_with_next_moves_a_heading_to_the_next_frame_with_its_paragraph(int keepWithNext, int headingFrame)
    {
        var (document, first) = TwoFrames();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var heading = document.Styles.AddParagraphStyle("Heading", new ParagraphFormat { KeepWithNext = keepWithNext }, body);
        foreach (var text in new[] { "One", "Two", "Three" })
        {
            first.Story.AddParagraph(text, body);
        }
        first.Story.AddParagraph("Heading", heading);
        first.Story.AddParagraph("Text", body);
        var layout = Layout(document, first);

        Assert.Equal(headingFrame, FrameOf(layout, "Heading"));
        Assert.Equal(1, FrameOf(layout, "Text"));
        Assert.False(layout.Overset);
        // What was laid out before the heading moved is not drawn twice.
        Assert.Equal(keepWithNext == 0 ? ["One", "Two", "Three", "Heading"] : ["One", "Two", "Three"], Drawn(layout, first));
    }

    [Fact]
    public void Keep_with_next_chains_through_consecutive_headings()
    {
        var (document, first) = TwoFrames();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var heading = document.Styles.AddParagraphStyle("Heading", new ParagraphFormat { KeepWithNext = 1 }, body);
        first.Story.AddParagraph("One", body);
        first.Story.AddParagraph("Two", body);
        first.Story.AddParagraph("Chapter", heading);
        first.Story.AddParagraph("Section", heading);
        first.Story.AddParagraph("Text", body);
        var layout = Layout(document, first);

        Assert.Equal(0, FrameOf(layout, "Two"));
        Assert.Equal(1, FrameOf(layout, "Chapter"));
        Assert.Equal(1, FrameOf(layout, "Section"));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void Keep_all_lines_together_moves_the_whole_paragraph(bool keep, int linesLeftBehind)
    {
        var (document, first) = TwoFrames();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var together = document.Styles.AddParagraphStyle("Together", new ParagraphFormat { KeepLinesTogether = keep, KeepAllLinesTogether = true }, body);
        first.Story.AddParagraph("One", body);
        first.Story.AddParagraph("Two", body);
        first.Story.AddParagraph(Lines("a", "b", "c", "d"), together);
        var layout = Layout(document, first);

        Assert.Equal(linesLeftBehind, layout.Lines.Count(line => line.FrameIndex == 0 && line.Text.Length == 1));
        Assert.Equal(4, layout.Lines.Count(line => line.Text.Length == 1));
    }

    [Theory]
    [InlineData(false, 3, 3, 2)]  // Keep Lines Together off: the first and last lines do not count.
    [InlineData(true, 3, 1, 0)]   // At least 3 lines before the break: neither of the 2 stays.
    [InlineData(true, 1, 3, 1)]   // At least 3 after it: one more line moves on.
    [InlineData(true, 2, 2, 2)]   // InDesign's defaults: 2 and 2 are met.
    [InlineData(true, 2, 3, 0)]   // 2 before and 3 after cannot both be met in 4 lines: the paragraph moves.
    public void Keep_first_and_last_lines_move_the_column_break(bool keep, int firstLines, int lastLines, int linesLeftBehind)
    {
        var (document, first) = TwoFrames();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var kept = document.Styles.AddParagraphStyle("Kept", new ParagraphFormat { KeepLinesTogether = keep, KeepFirstLines = firstLines, KeepLastLines = lastLines }, body);
        first.Story.AddParagraph("One", body);
        first.Story.AddParagraph("Two", body);
        first.Story.AddParagraph(Lines("a", "b", "c", "d"), kept);
        var layout = Layout(document, first);

        Assert.Equal(linesLeftBehind, layout.Lines.Count(line => line.FrameIndex == 0 && line.Text.Length == 1));
        Assert.Equal(4, layout.Lines.Count(line => line.Text.Length == 1));
        Assert.False(layout.Overset);
    }

    [Fact]
    public void A_paragraph_longer_than_a_column_breaks_after_all()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var page = document.Pages[0];
        var frames = Enumerable.Range(0, 3).Select(i => page.AddTextFrame(new Rect(0, i * 100, 300, FourLines))).ToList();
        frames[0].ThreadTo(frames[1]);
        frames[1].ThreadTo(frames[2]);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var together = document.Styles.AddParagraphStyle("Together", new ParagraphFormat { KeepLinesTogether = true, KeepAllLinesTogether = true }, body);
        frames[0].Story.AddParagraph("One", body);
        frames[0].Story.AddParagraph(Lines("a", "b", "c", "d", "e", "f"), together);
        var layout = Layout(document, frames[0]);

        // Moved once to the top of the next frame, then broken there: InDesign does not leave a column empty.
        Assert.Equal([0, 1, 1, 1, 1, 2, 2], layout.Lines.Select(line => line.FrameIndex));
        Assert.False(layout.Overset);
    }

    [Fact]
    public void Keeps_that_cannot_be_met_in_the_last_frame_push_the_lines_into_overset()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, FourLines));
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var heading = document.Styles.AddParagraphStyle("Heading", new ParagraphFormat { KeepWithNext = 1 }, body);
        foreach (var text in new[] { "One", "Two", "Three" })
        {
            frame.Story.AddParagraph(text, body);
        }
        frame.Story.AddParagraph("Heading", heading);
        frame.Story.AddParagraph("Text", body);
        var layout = Layout(document, frame);

        Assert.Equal(["One", "Two", "Three"], layout.Lines.Select(line => line.Text));
        Assert.Equal(["One", "Two", "Three"], Drawn(layout, frame));
        Assert.True(layout.Overset);
        Assert.True(new TextMeasurer(document, TestSupport.Fonts()).CheckFit(frame.Story).Overset);
    }

    [Fact]
    public void Start_paragraph_in_the_next_column_or_frame()
    {
        var (document, first) = TwoFrames();
        first.Columns = 2;
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var column = document.Styles.AddParagraphStyle("Column", new ParagraphFormat { StartParagraph = StartParagraph.NextColumn }, body);
        var frame = document.Styles.AddParagraphStyle("Frame", new ParagraphFormat { StartParagraph = StartParagraph.NextFrame }, body);
        // Already at the top of a column (the story's first): stays there.
        first.Story.AddParagraph("First", column);
        first.Story.AddParagraph("Second column", column);
        first.Story.AddParagraph("Two", body);
        first.Story.AddParagraph("Second frame", frame);
        var layout = Layout(document, first);

        var lines = layout.Lines.Select(line => (line.Text, line.FrameIndex, line.Column)).ToList();
        Assert.Equal([("First", 0, 0), ("Second column", 0, 1), ("Two", 0, 1), ("Second frame", 1, 0)], lines);
    }

    [Theory]
    [InlineData(StartParagraph.NextPage, 1)]
    [InlineData(StartParagraph.NextOddPage, 2)]
    [InlineData(StartParagraph.NextEvenPage, 1)]
    public void Start_paragraph_on_the_next_page_goes_to_the_story_frame_on_that_page(StartParagraph start, int frameIndex)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        document.AddPage();
        document.AddPage();
        var frames = document.Pages.Select(page => page.AddTextFrame(new Rect(0, 0, 300, 200))).ToList();
        frames[0].ThreadTo(frames[1]);
        frames[1].ThreadTo(frames[2]);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var starts = document.Styles.AddParagraphStyle("Starts", new ParagraphFormat { StartParagraph = start }, body);
        frames[0].Story.AddParagraph("One", body);
        frames[0].Story.AddParagraph("Starts here", starts);
        frames[0].Story.AddParagraph("Two", body);
        var layout = Layout(document, frames[0]);

        Assert.Equal(0, FrameOf(layout, "One"));
        Assert.Equal(frameIndex, FrameOf(layout, "Starts here"));
        Assert.Equal(frameIndex, FrameOf(layout, "Two"));
    }

    [Fact]
    public void The_first_paragraph_of_a_story_starting_on_the_next_page_stays_in_the_first_frame()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        document.AddPage();
        var frames = document.Pages.Select(page => page.AddTextFrame(new Rect(0, 0, 300, 200))).ToList();
        frames[0].ThreadTo(frames[1]);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var starts = document.Styles.AddParagraphStyle("Starts", new ParagraphFormat { StartParagraph = StartParagraph.NextPage }, body);
        frames[0].Story.AddParagraph("Chapter", starts);
        var layout = Layout(document, frames[0]);

        Assert.Equal(0, FrameOf(layout, "Chapter"));
    }
}
