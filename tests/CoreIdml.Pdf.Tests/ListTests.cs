using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>Bullets and numbering: the markers InDesign generates in front of list paragraphs.</summary>
public sealed class ListTests
{
    private static (IdmlDocument Document, Page Page) NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        return (document, document.Pages[0]);
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    private static List<GlyphRunOp> Runs(StoryLayout layout) => layout.OpsByFrame.Values.SelectMany(ops => ops).OfType<GlyphRunOp>().ToList();

    /// <summary>A hanging list: the marker at the first-line indent, the text at the left indent.</summary>
    private static ParagraphFormat Hanging(ListType list) => new()
    {
        FontFamily = "Liberation Serif", PointSize = 10, Leading = 12, LeftIndent = 18, FirstLineIndent = -18, ListType = list,
    };

    [Fact]
    public void Bullets_hang_and_the_text_starts_at_the_left_indent()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Bullet", Hanging(ListType.Bullet));
        var frame = page.AddText(new Rect(0, 0, 120, 400), "Alpha\nBeta with enough words to wrap onto a second line", style);
        var layout = Layout(document, frame);

        Assert.Equal("•\tAlpha", layout.Lines[0].Text);
        Assert.StartsWith("•\tBeta", layout.Lines[1].Text);
        Assert.Equal(2, layout.Lines[0].MarkerLength);
        Assert.Equal(0, layout.Lines[2].MarkerLength);
        var left = frame.InnerBounds.X;
        var runs = Runs(layout);
        Assert.Equal(left, runs.First(run => run.Text == "•").X, 3);
        Assert.Equal(left + 18, runs.First(run => run.Text == "Alpha").X, 3);
        // Wrapped lines start at the left indent too, under the text.
        Assert.Equal(left + 18, layout.Lines[2].Left, 3);
    }

    [Theory]
    [InlineData(48, -48)]
    [InlineData(78, -36)]
    public void The_tab_after_a_marker_skips_default_stops_before_the_left_indent(double leftIndent, double firstLineIndent)
    {
        // InDesign (08-para-bullets-numbering p4, p5): the text starts at the left indent even when a default stop
        // (every 36 pt) comes first; a default stop is not a reason to stop short of the hanging indent.
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Step", new ParagraphFormat
        {
            FontFamily = "Liberation Serif", PointSize = 11, LeftIndent = leftIndent, FirstLineIndent = firstLineIndent,
            ListType = ListType.Numbered, NumberingExpression = "Step ^#:^t",
        });
        var frame = page.AddText(new Rect(0, 0, 400, 400), "Mr. Bennet made no answer.", style);

        var text = Runs(Layout(document, frame)).First(run => run.Text.StartsWith("Mr.", StringComparison.Ordinal));
        Assert.Equal(frame.InnerBounds.X + leftIndent, text.X, 3);
    }

    [Fact]
    public void An_explicit_tab_stop_before_the_left_indent_still_comes_first()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Bullet", new ParagraphFormat
        {
            FontFamily = "Liberation Serif", PointSize = 10, LeftIndent = 48, FirstLineIndent = -48, ListType = ListType.Bullet,
        });
        var frame = page.AddText(new Rect(0, 0, 400, 400), "Alpha", style);
        var range = frame.Story.Element.Descendants("ParagraphStyleRange").First();
        Assert.Null(range.Element("Properties"));
        range.AddFirst(new XElement("Properties", new XElement("TabList",
            new XElement("ListItem", new XElement("Alignment", "LeftAlign"), new XElement("Position", "20")))));

        var text = Runs(Layout(document, frame)).First(run => run.Text == "Alpha");
        Assert.Equal(frame.InnerBounds.X + 20, text.X, 3);
    }

    [Theory]
    [InlineData("true", new[] { "1.\tOne", "2.\tTwo" }, new[] { "3.\tThree", "4.\tFour" })]
    [InlineData("false", new[] { "1.\tOne", "2.\tTwo" }, new[] { "1.\tThree", "2.\tFour" })]
    public void A_named_list_continues_into_the_next_story_when_it_says_so(string acrossStories, string[] first, string[] second)
    {
        // As InDesign draws 08-para-bullets-numbering p6: "list A, frame 2: continues (4, 5)". The second frame is a
        // separate story placed below the first one.
        var (document, page) = NewDocument();
        document.DesignMap.Add(new XElement("NumberingList", new XAttribute("Self", "NumberingList/List A"), new XAttribute("Name", "List A"),
            new XAttribute("ContinueNumbersAcrossStories", acrossStories), new XAttribute("ContinueNumbersAcrossDocuments", "false")));
        var style = document.Styles.AddParagraphStyle("A", Hanging(ListType.Numbered));
        style.Element.Element("Properties")!.Add(new XElement("AppliedNumberingList", new XAttribute("type", "object"), "NumberingList/List A"));
        // Created in the opposite order to where they stand: layout order decides, not story order.
        var lower = page.AddText(new Rect(0, 200, 300, 100), "Three\nFour", style);
        var upper = page.AddText(new Rect(0, 0, 300, 100), "One\nTwo", style);

        var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        Assert.Equal(first, renderer.LayoutStory(upper.Story).Lines.Select(line => line.Text));
        Assert.Equal(second, renderer.LayoutStory(lower.Story).Lines.Select(line => line.Text));
    }

    [Fact]
    public void Numbers_count_up_and_continue_past_paragraphs_outside_the_list()
    {
        var (document, page) = NewDocument();
        var numbered = document.Styles.AddParagraphStyle("Numbered", Hanging(ListType.Numbered));
        var body = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10 });
        var frame = page.AddTextFrame(new Rect(0, 0, 300, 400));
        frame.Story.AddParagraph("One", numbered);
        frame.Story.AddParagraph("Two", numbered);
        frame.Story.AddParagraph("A note", body);
        frame.Story.AddParagraph("Three", numbered);
        frame.Story.AddParagraph(numbered, new ParagraphFormat { NumberingContinue = false, NumberingStartAt = 7 }).AddText("Seven");
        frame.Story.AddParagraph("Eight", numbered);

        var texts = Layout(document, frame).Lines.Select(line => line.Text).Where(text => text.Length > 0).ToList();
        Assert.Equal(["1.\tOne", "2.\tTwo", "A note", "3.\tThree", "7.\tSeven", "8.\tEight"], texts);
    }

    [Fact]
    public void Levels_restart_after_a_higher_level_and_show_other_levels_numbers()
    {
        var (document, page) = NewDocument();
        var first = document.Styles.AddParagraphStyle("Level 1", Hanging(ListType.Numbered));
        var second = document.Styles.AddParagraphStyle("Level 2", new ParagraphFormat
        {
            FontFamily = "Liberation Serif", PointSize = 10, LeftIndent = 36, FirstLineIndent = -18, ListType = ListType.Numbered,
            NumberingLevel = 2, NumberingFormat = NumberingFormats.LowerLetters, NumberingExpression = "^1.^#)\t",
        });
        var frame = page.AddTextFrame(new Rect(0, 0, 300, 400));
        foreach (var (text, style) in new[] { ("Fruit", first), ("Apple", second), ("Pear", second), ("Vegetables", first), ("Leek", second) })
        {
            frame.Story.AddParagraph(text, style);
        }

        var texts = Layout(document, frame).Lines.Select(line => line.Text).ToList();
        Assert.Equal(["1.\tFruit", "1.a)\tApple", "1.b)\tPear", "2.\tVegetables", "2.a)\tLeek"], texts);
    }

    [Theory]
    [InlineData(4, NumberingFormats.Arabic, "4")]
    [InlineData(4, NumberingFormats.LowerRoman, "iv")]
    [InlineData(2009, NumberingFormats.UpperRoman, "MMIX")]
    [InlineData(3, NumberingFormats.ArabicTwoDigits, "03")]
    [InlineData(12, NumberingFormats.ArabicThreeDigits, "012")]
    [InlineData(2, NumberingFormats.UpperLetters, "B")]
    [InlineData(27, NumberingFormats.LowerLetters, "aa")]
    [InlineData(0, NumberingFormats.LowerRoman, "0")]
    public void Numbering_formats(int value, string format, string expected) =>
        Assert.Equal(expected, ListMarkers.Format(value, format));

    [Fact]
    public void Metacharacters_in_the_text_after_become_spaces_tabs_and_breaks()
    {
        var style = new RunStyle { Font = TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular") };
        var items = new List<InlineItem>();
        ListMarkers.Expand("^#^>^m^^x^t^y^n^3", style, items, level => level == 0 ? "5" : "?");
        Assert.Collection(items,
            item => Assert.Equal("5  ^x", Assert.IsType<TextItem>(item).Text),
            item => Assert.False(Assert.IsType<TabItem>(item).RightIndentTab),
            item => Assert.True(Assert.IsType<TabItem>(item).RightIndentTab),
            item => Assert.IsType<LineBreakItem>(item),
            item => Assert.Equal("?", Assert.IsType<TextItem>(item).Text));
        Assert.All(items, item => Assert.True(item.Generated));

        // Outside a numbering expression ^3 is a third space, not a level number.
        items.Clear();
        ListMarkers.Expand("^3", style, items, number: null);
        Assert.Equal(" ", Assert.IsType<TextItem>(Assert.Single(items)).Text);
    }

    [Fact]
    public void The_marker_takes_the_first_characters_formatting_and_its_own_bullet()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Bullet", Hanging(ListType.Bullet));
        var frame = page.AddTextFrame(new Rect(0, 0, 300, 400));
        frame.Story.AddParagraph(style, new ParagraphFormat { BulletCharacter = "–", BulletTextAfter = "^m" })
            .AddText("Big", format: new CharacterFormat { PointSize = 20 })
            .AddText(" small");

        var layout = Layout(document, frame);
        Assert.Equal("– Big small", layout.Lines[0].Text);
        var marker = Runs(layout).First(run => run.Text.StartsWith('–'));
        Assert.Equal(20, marker.Size);
    }

    [Fact]
    public void Justification_keeps_the_em_space_after_a_bullet_and_widens_word_spaces()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Bullet", new ParagraphFormat
        {
            FontFamily = "Liberation Serif", PointSize = 10, ListType = ListType.Bullet, BulletTextAfter = "^m", Justification = Justification.FullyJustified,
        });
        var frame = page.AddText(new Rect(0, 0, 300, 400), "One two three", style);

        var glyphs = Runs(Layout(document, frame)).SelectMany(run => run.Glyphs).ToList();
        Assert.Equal(10, glyphs.Single(glyph => glyph.Text == " ").Advance, 3);
        Assert.All(glyphs.Where(glyph => glyph.Text == " "), glyph => Assert.True(glyph.Advance > glyph.NaturalAdvance + 10));
    }

    [Fact]
    public void Markers_are_not_counted_as_overset_text()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Numbered", Hanging(ListType.Numbered));
        var frame = page.AddText(new Rect(0, 0, 200, 14), "First\nSecond\nThird", style);
        var layout = Layout(document, frame);
        Assert.True(layout.Overset);
        // The overset paragraphs after the one that broke off, without their "3." marker.
        Assert.Equal("Third".Length, layout.OversetCharacters);
    }

    [Fact]
    public void Paragraphs_that_are_not_lists_are_unchanged()
    {
        var (document, page) = NewDocument();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, ListType = ListType.None });
        var frame = page.AddText(new Rect(0, 0, 300, 400), "Plain", style);
        var line = Assert.Single(Layout(document, frame).Lines);
        Assert.Equal("Plain", line.Text);
        Assert.Equal(0, line.MarkerLength);
    }
}
