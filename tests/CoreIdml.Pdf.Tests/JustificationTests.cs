using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Word spacing, letter spacing and glyph scaling (Paragraph &gt; Justification), as InDesign applies them (measured in
/// doc-samples/08-para-hyphenation-justification).
/// </summary>
public sealed class JustificationTests
{
    private const string Words = "Mr. Bennet replied that he had not. But it is, returned she, for Mrs. Long has just been here.";

    private static JustificationSettings Settings(double minimumWord = 80, double maximumWord = 133, double minimumLetter = 0, double maximumLetter = 0,
        double minimumGlyph = 100, double maximumGlyph = 100, string singleWord = "FullyJustified") =>
        JustificationSettings.FromPercentages(minimumWord, 100, maximumWord, minimumLetter, 0, maximumLetter, minimumGlyph, 100, maximumGlyph, singleWord);

    [Fact]
    public void Word_and_letter_spacing_stretch_by_the_same_share_of_their_ranges()
    {
        // 10 pt of word spaces may grow 33% (3.3 pt), 20 pt of letter bands 5% (1 pt): 2.15 pt is half of both.
        var spacing = Justifier.Solve(new LineSpace(100, 10, 20), 102.15, Settings(minimumLetter: -5, maximumLetter: 5), stretch: true, shrink: true);
        Assert.Equal(0.165, spacing.Word, 6);
        Assert.Equal(0.025, spacing.Letter, 6);
        Assert.Equal(1, spacing.Scale);
        Assert.Equal(0.5, spacing.Ratio, 6);
    }

    [Fact]
    public void Glyph_scaling_comes_after_word_spacing_reaches_its_limit()
    {
        var settings = Settings(minimumGlyph: 90, maximumGlyph: 110);
        var within = Justifier.Solve(new LineSpace(100, 10, 20), 102, settings, stretch: true, shrink: true);
        Assert.Equal(1, within.Scale);
        Assert.Equal(0.2, within.Word, 6);

        // At 80% word spacing the line is 98 pt: the glyphs shrink the rest of the way.
        var tight = Justifier.Solve(new LineSpace(100, 10, 20), 97, settings, stretch: true, shrink: true);
        Assert.Equal(-0.2, tight.Word, 6);
        Assert.Equal(97.0 / 98, tight.Scale, 6);
    }

    [Fact]
    public void Past_the_limits_word_spaces_take_what_is_left_and_a_single_word_is_letter_spaced()
    {
        // 100/100/100% word spacing: no range, the word spaces stretch alone (InDesign: 303% in a 120 pt column).
        var noRange = Justifier.Solve(new LineSpace(100, 10, 20), 110, Settings(minimumWord: 100, maximumWord: 100), stretch: true, shrink: true);
        Assert.Equal(1.0, noRange.Word, 6);
        Assert.Equal(0, noRange.Letter);
        Assert.True(double.IsPositiveInfinity(noRange.Ratio));

        var singleWord = Justifier.Solve(new LineSpace(60, 0, 11), 115, Settings(), stretch: true, shrink: true);
        Assert.Equal(5, singleWord.Letter, 6);
    }

    [Fact]
    public void Compression_stops_at_the_minimum_unless_the_line_is_wider_and_word_spaces_never_go_below_nothing()
    {
        var settings = Settings(minimumWord: 50, maximumWord: 150);
        Assert.Equal(95, Justifier.TightestWidth(new LineSpace(100, 10, 20), settings), 6);
        var past = Justifier.Solve(new LineSpace(100, 10, 20), 92.6, settings, stretch: true, shrink: true);
        Assert.Equal(-0.74, past.Word, 6);
        Assert.True(past.Ratio < -1);
        Assert.Equal(-1, Justifier.Solve(new LineSpace(100, 10, 20), 50, settings, stretch: true, shrink: true).Word, 6);
    }

    [Fact]
    public void Settings_read_through_the_paragraph_style_and_keep_their_ranges_in_order()
    {
        var settings = JustificationSettings.FromPercentages(90, 80, 70, 5, 0, -5, 120, 100, 80, "LeftAlign");
        Assert.Equal((0.8, 0.8, 0.8), (settings.MinimumWordSpacing, settings.DesiredWordSpacing, settings.MaximumWordSpacing));
        Assert.Equal((0.0, 0.0, 0.0), (settings.MinimumLetterSpacing, settings.DesiredLetterSpacing, settings.MaximumLetterSpacing));
        Assert.Equal((1.0, 1.0, 1.0), (settings.MinimumGlyphScaling, settings.DesiredGlyphScaling, settings.MaximumGlyphScaling));

        var (document, page) = NewDocument();
        var style = Style(document, "Justified", Justification.LeftJustified, ("MinimumWordSpacing", "50"), ("MaximumLetterSpacing", "5"), ("Composer", "HL Single"));
        var frame = page.AddText(new Rect(0, 0, 300, 100), Words, style);
        var paragraph = Assert.Single(new StoryReader(document, new AttributeResolver(document), TestSupport.FontLibrary()).Read(frame.Story.Element));
        Assert.Equal(0.5, paragraph.Format.Spacing.MinimumWordSpacing);
        Assert.Equal(0.05, paragraph.Format.Spacing.MaximumLetterSpacing);
        Assert.Equal(1.33, paragraph.Format.Spacing.MaximumWordSpacing);
    }

    [Fact]
    public void A_justified_line_compresses_word_spaces_to_take_a_word_that_would_otherwise_leave_it_too_loose()
    {
        // The column is 1.5 pt narrower than the first eight words: they fit at about 90% word spacing, while the
        // first seven would need more than the 133% maximum.
        var natural = NaturalWidth(string.Join(' ', Words.Split(' ').Take(8)));
        var (document, page) = NewDocument();
        var justified = Style(document, "Justified", Justification.LeftJustified, ("Composer", "HL Single"));
        var frame = page.AddText(new Rect(0, 0, natural - 1.5, 200), Words, justified);
        var layout = Layout(document, frame);
        Assert.Equal(string.Join(' ', Words.Split(' ').Take(8)), layout.Lines[0].Text);
        Assert.Equal(natural - 1.5, layout.Lines[0].Width, 3);

        // Ragged text keeps its desired spacing: the eighth word goes to the next line.
        var (raggedDocument, raggedPage) = NewDocument();
        var ragged = Style(raggedDocument, "Ragged", Justification.Left, ("Composer", "HL Single"));
        var raggedFrame = raggedPage.AddText(new Rect(0, 0, natural - 1.5, 200), Words, ragged);
        Assert.Equal(string.Join(' ', Words.Split(' ').Take(7)), Layout(raggedDocument, raggedFrame).Lines[0].Text);
    }

    [Fact]
    public void Desired_word_spacing_sets_the_spaces_of_ragged_text()
    {
        var (document, page) = NewDocument();
        var style = Style(document, "Wide", Justification.Left, ("DesiredWordSpacing", "150"), ("MaximumWordSpacing", "200"));
        var frame = page.AddText(new Rect(0, 0, 500, 100), "a b c", style);
        var width = Assert.Single(Layout(document, frame).Lines).Width;
        var space = TestSupport.FontLibrary().Resolve("Liberation Serif", "Regular");
        space.TryMap(' ', out var glyph);
        Assert.Equal(NaturalWidth("a b c") + (2 * 0.5 * space.Advance(glyph) * 12), width, 3);
    }

    [Theory]
    [InlineData("FullyJustified", true)]
    [InlineData("LeftAlign", false)]
    public void A_justified_line_holding_one_word_follows_single_word_justification(string singleWord, bool spread)
    {
        var (document, page) = NewDocument();
        var style = Style(document, "Justified", Justification.LeftJustified, ("SingleWordJustification", singleWord), ("Composer", "HL Single"));
        var frame = page.AddText(new Rect(0, 0, 100, 200), "Extraordinarily accomplished", style);
        var first = Layout(document, frame).Lines[0];
        Assert.Equal("Extraordinarily", first.Text);
        Assert.Equal(spread, Math.Abs(first.Width - 100) < 0.01);
    }

    private static (IdmlDocument Document, Page Page) NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        return (document, document.Pages[0]);
    }

    private static ParagraphStyle Style(IdmlDocument document, string name, Justification justification, params (string Name, string Value)[] attributes)
    {
        var style = document.Styles.AddParagraphStyle(name, new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12, Justification = justification, Hyphenation = false });
        foreach (var (attribute, value) in attributes)
        {
            style.Element.SetAttributeValue(attribute, value);
        }
        return style;
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    /// <summary>The width of <paramref name="text"/> on one line at its natural spacing.</summary>
    private static double NaturalWidth(string text)
    {
        var (document, page) = NewDocument();
        var style = Style(document, "Natural", Justification.Left);
        return Assert.Single(Layout(document, page.AddText(new Rect(0, 0, 500, 100), text, style)).Lines).Width;
    }
}
