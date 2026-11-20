using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// The Adobe Paragraph Composer (<c>Composer="HL Composer"</c>, InDesign's default): line breaks chosen for the whole
/// paragraph, against the Single-line Composer (<c>HL Single</c>) that breaks one line at a time.
/// </summary>
public sealed class ParagraphComposerTests
{
    private const string Text =
        "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife. "
        + "However little known the feelings or views of such a man may be on his first entering a neighbourhood, this truth is so "
        + "well fixed in the minds of the surrounding families, that he is considered the rightful property of some one or other of "
        + "their daughters.";

    private static (IdmlDocument Document, Page Page) NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        return (document, document.Pages[0]);
    }

    private static ParagraphStyle Style(IdmlDocument document, string composer, Justification justification, bool hyphenation = false, params (string Name, string Value)[] attributes)
    {
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat
        {
            FontFamily = "Liberation Serif", PointSize = 10, Leading = 12, Justification = justification, Hyphenation = hyphenation, Language = Languages.EnglishUs,
        });
        style.Element.SetAttributeValue("Composer", composer);
        foreach (var (name, value) in attributes)
        {
            style.Element.SetAttributeValue(name, value);
        }
        return style;
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    private static List<LineInfo> Lines(string composer, Justification justification, double width, bool hyphenation = false, params (string Name, string Value)[] attributes)
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, width, 500), Text, Style(document, composer, justification, hyphenation, attributes));
        return Layout(document, frame).Lines;
    }

    /// <summary>The cost the paragraph composer weighs a ragged line by: the cube of what it leaves empty.</summary>
    private static double Rag(List<LineInfo> lines, double width) => lines.Take(lines.Count - 1).Sum(line => Math.Pow(width - line.Width, 3));

    [Fact]
    public void Ragged_lines_are_evened_out_where_one_line_at_a_time_leaves_them_uneven()
    {
        var better = 0;
        for (var width = 100; width <= 220; width += 10)
        {
            var single = Lines("HL Single", Justification.Left, width);
            var paragraph = Lines("HL Composer", Justification.Left, width);
            Assert.Equal(string.Join(" ", single.Select(line => line.Text)), string.Join(" ", paragraph.Select(line => line.Text)));
            Assert.All(paragraph, line => Assert.True(line.Width <= width + 0.01));
            better += Rag(paragraph, width) < Rag(single, width) - 1 ? 1 : 0;
        }
        Assert.True(better >= 3, $"the paragraph composer evened out {better} of 13 widths");
    }

    [Fact]
    public void Justified_lines_fill_the_column_and_keep_every_word()
    {
        var lines = Lines("HL Composer", Justification.LeftJustified, 160, hyphenation: true);
        foreach (var line in lines.Take(lines.Count - 1))
        {
            Assert.Equal(160, line.Width, 2);
        }
        var text = string.Concat(lines.Select(line => line.Text.EndsWith('-') ? line.Text[..^1] : line.Text + " ")).Trim();
        Assert.Equal(Text, text);
    }

    [Fact]
    public void No_more_consecutive_lines_end_in_a_hyphen_than_the_ladder_limit()
    {
        var lines = Lines("HL Composer", Justification.LeftJustified, 70, hyphenation: true, ("HyphenateLadderLimit", "1"));
        Assert.Contains(lines, line => line.Text.EndsWith('-'));
        for (var i = 1; i < lines.Count; i++)
        {
            Assert.False(lines[i].Text.EndsWith('-') && lines[i - 1].Text.EndsWith('-'), $"lines {i} and {i + 1} both end in a hyphen");
        }
    }

    [Fact]
    public void A_paragraph_that_moves_to_a_narrower_frame_is_broken_again_at_that_width()
    {
        var (document, page) = NewDocument();
        var style = Style(document, "HL Composer", Justification.LeftJustified, hyphenation: true);
        var wide = page.AddTextFrame(new Rect(0, 0, 300, 30));
        var narrow = page.AddTextFrame(new Rect(0, 100, 120, 400));
        wide.ThreadTo(narrow);
        wide.Story.AddParagraph(Text, style);
        var lines = Layout(document, wide).Lines;
        Assert.Contains(lines, line => line.FrameIndex == 1);
        var last = lines.Count - 1;
        for (var i = 0; i < last; i++)
        {
            Assert.Equal(lines[i].FrameIndex == 0 ? 300 : 120, lines[i].Width, 2);
        }
    }

    [Fact]
    public void Composing_twice_gives_the_same_lines()
    {
        var first = Lines("HL Composer", Justification.LeftJustified, 133, hyphenation: true);
        var second = Lines("HL Composer", Justification.LeftJustified, 133, hyphenation: true);
        Assert.Equal(first.Select(line => line.Text), second.Select(line => line.Text));
    }
}
