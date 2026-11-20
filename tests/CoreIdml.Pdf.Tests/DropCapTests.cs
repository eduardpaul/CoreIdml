using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>Drop caps, with the rules measured in InDesign 20's export of the doc-samples 08-para-drop-caps package.</summary>
public sealed class DropCapTests
{
    private const string Text = "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife. "
        + "However little known the feelings or views of such a man may be on his first entering a neighbourhood.";

    private static (IdmlDocument Document, Page Page) NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        return (document, document.Pages[0]);
    }

    /// <summary>11 pt Liberation Serif on 14 pt leading, as in the sample, with a drop cap.</summary>
    private static ParagraphStyle DropCapStyle(IdmlDocument document, int lines, int characters, int detail = 1, double firstLineIndent = 0)
    {
        var style = document.Styles.AddParagraphStyle($"DC{lines}x{characters}d{detail}i{firstLineIndent}", new ParagraphFormat
        {
            FontFamily = "Liberation Serif",
            PointSize = 11,
            Leading = 14,
            Hyphenation = false,
            FirstLineIndent = firstLineIndent,
        });
        style.Element.SetAttributeValue("DropCapLines", lines);
        style.Element.SetAttributeValue("DropCapCharacters", characters);
        style.Element.SetAttributeValue("DropcapDetail", detail);
        return style;
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    private static List<GlyphRunOp> Runs(StoryLayout layout) => layout.OpsByFrame.Values.SelectMany(ops => ops).OfType<GlyphRunOp>().ToList();

    private static FontFace Serif() => TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular");

    private static double Advance(FontFace font, char c, double size) => font.TryMap(c, out var glyph) ? font.Advance(glyph) * size : 0;

    private static double LeftSideBearing(FontFace font, char c, double size) => font.TryMap(c, out var glyph) ? font.LeftSideBearing(glyph) * size : 0;

    [Fact]
    public void A_three_line_drop_cap_reaches_from_the_first_cap_height_to_the_third_baseline()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 250, 200), Text, DropCapStyle(document, lines: 3, characters: 1));

        var layout = Layout(document, frame);
        var cap = Runs(layout).Single(run => run.Size > 11);
        var serif = Serif();
        // 08-para-drop-caps p1: 53.76 pt for 3 lines of 11/14 Liberation Serif (cap height 1341/2048).
        Assert.Equal(11 + (28 / serif.CapHeight), cap.Size, 6);
        Assert.Equal(53.76, cap.Size, 2);
        Assert.Equal("I", cap.Text);
        Assert.Equal(layout.Lines[2].Baseline, cap.Y, 6);
        Assert.Equal(layout.Lines[0].Baseline - (serif.CapHeight * 11), cap.Y - (serif.CapHeight * cap.Size), 6);
        // Align Left Edge: the outline, not the origin, starts at the frame's edge.
        var left = frame.InnerBounds.X;
        Assert.Equal(left - LeftSideBearing(serif, 'I', cap.Size), cap.X, 6);
        // Lines 1 to 3 start after the drop cap; the first line's text loses its "I"; line 4 is back at the edge.
        var textStart = cap.X + Advance(serif, 'I', cap.Size);
        Assert.All(layout.Lines.Take(3), line => Assert.Equal(textStart, line.Left, 6));
        Assert.Equal(left, layout.Lines[3].Left, 6);
        Assert.StartsWith("It is a truth", layout.Lines[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Runs(layout).Where(run => run.Size == 11), run => run.Text.StartsWith('I'));
        Assert.Equal(14, layout.Lines[1].Baseline - layout.Lines[0].Baseline, 6);
    }

    [Fact]
    public void Without_align_left_edge_the_drop_cap_origin_is_at_the_edge()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 250, 200), "gentlemen are in want of a wife and that is a truth universally acknowledged by every neighbourhood.",
            DropCapStyle(document, lines: 3, characters: 1, detail: 0));

        var layout = Layout(document, frame);
        var cap = Runs(layout).Single(run => run.Size > 11);
        Assert.Equal(frame.InnerBounds.X, cap.X, 6);
        Assert.Equal(cap.X + Advance(Serif(), 'g', cap.Size), layout.Lines[0].Left, 6);
    }

    [Fact]
    public void Spaces_right_after_a_two_character_drop_cap_take_no_room()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 250, 200), Text, DropCapStyle(document, lines: 3, characters: 2));

        var layout = Layout(document, frame);
        var cap = Runs(layout).Single(run => run.Size > 11);
        var serif = Serif();
        Assert.Equal("It", cap.Text);
        // 08-para-drop-caps p2: "is" starts right after "It".
        Assert.Equal(cap.X + Advance(serif, 'I', cap.Size) + Advance(serif, 't', cap.Size), layout.Lines[0].Left, 6);
        Assert.StartsWith("is a truth", Runs(layout).First(run => run.Size == 11).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_under_the_drop_cap_also_take_the_first_line_indent()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 250, 200), Text, DropCapStyle(document, lines: 3, characters: 1, firstLineIndent: 20));

        var layout = Layout(document, frame);
        var cap = Runs(layout).Single(run => run.Size > 11);
        var left = frame.InnerBounds.X;
        Assert.Equal(left + 20 - LeftSideBearing(Serif(), 'I', cap.Size), cap.X, 6);
        Assert.All(layout.Lines.Take(3), line => Assert.Equal(layout.Lines[0].Left, line.Left, 6));
        Assert.Equal(left, layout.Lines[3].Left, 6);
    }

    [Fact]
    public void A_paragraph_shorter_than_its_drop_cap_still_drops_it_the_full_depth()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 250, 200), "Mr. Bennet made no answer.", DropCapStyle(document, lines: 4, characters: 1));

        var layout = Layout(document, frame);
        var cap = Runs(layout).Single(run => run.Size > 11);
        Assert.Single(layout.Lines);
        Assert.Equal(layout.Lines[0].Baseline + 42, cap.Y, 6);
        Assert.Equal("Mr. Bennet made no answer.", layout.Lines[0].Text);
    }

    [Fact]
    public void A_nested_style_through_the_drop_cap_styles_exactly_its_characters_and_sets_its_font()
    {
        var (document, page) = NewDocument();
        var red = document.Swatches.AddCmyk("Drop Red", 0, 100, 100, 0);
        var dropRed = document.Styles.AddCharacterStyle("DropRed", new CharacterFormat { FontFamily = "Liberation Sans", FontStyle = "Bold", FillColor = red });
        var style = DropCapStyle(document, lines: 3, characters: 2);
        style.Element.Element("Properties")!.Add(new XElement("AllNestedStyles", new XAttribute("type", "list"),
            new XElement("ListItem", new XAttribute("type", "record"),
                new XElement("AppliedCharacterStyle", new XAttribute("type", "object"), dropRed.Reference),
                new XElement("Delimiter", new XAttribute("type", "enumeration"), "Dropcap"),
                new XElement("Repetition", new XAttribute("type", "long"), 1),
                new XElement("Inclusive", new XAttribute("type", "boolean"), "true"))));
        var frame = page.AddText(new Rect(0, 0, 250, 200), Text, style);

        var runs = Runs(Layout(document, frame));
        var cap = runs.Single(run => run.Size > 11);
        Assert.Equal("It", cap.Text);
        Assert.Equal("Liberation Sans Bold", cap.Font.DisplayName);
        Assert.Equal([0, 1, 1, 0], cap.Color!.Components);
        // 08-para-drop-caps p3: the size follows the drop cap font's cap height (51.70 pt for Liberation Sans Bold).
        Assert.Equal(11 + (28 / cap.Font.CapHeight), cap.Size, 6);
        Assert.All(runs.Where(run => run.Size == 11), run => Assert.Equal("Liberation Serif Regular", run.Font.DisplayName));
    }
}
