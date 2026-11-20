using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Nested styles, nested line styles and GREP styles, with the rules measured in InDesign 20's export of the
/// doc-samples 08-para-nested-grep-styles package.
/// </summary>
public sealed class NestedStyleTests
{
    private const char LeftQuote = (char)0x201C;
    private const char RightQuote = (char)0x201D;

    private static (IdmlDocument Document, Page Page) NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        return (document, document.Pages[0]);
    }

    private static List<GlyphRunOp> Runs(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story).OpsByFrame.Values.SelectMany(ops => ops).OfType<GlyphRunOp>().ToList();

    private static ParagraphStyle Body(IdmlDocument document, string name) =>
        document.Styles.AddParagraphStyle(name, new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12 });

    private static CharacterStyle Strong(IdmlDocument document) =>
        document.Styles.AddCharacterStyle("Strong", new CharacterFormat { FontStyle = "Bold" });

    private static CharacterStyle Emph(IdmlDocument document) =>
        document.Styles.AddCharacterStyle("Emph", new CharacterFormat { FontStyle = "Italic" });

    /// <summary>Adds a list property (<c>AllNestedStyles</c>, ...) to a paragraph style, as InDesign writes it.</summary>
    private static void SetList(ParagraphStyle style, string name, params XElement[] items) =>
        style.Element.Element("Properties")!.Add(new XElement(name, new XAttribute("type", "list"), items));

    private static XElement NestedItem(CharacterStyle style, string delimiter, bool enumerated, int repetition, bool inclusive) =>
        new("ListItem", new XAttribute("type", "record"),
            new XElement("AppliedCharacterStyle", new XAttribute("type", "object"), style.Reference),
            new XElement("Delimiter", new XAttribute("type", enumerated ? "enumeration" : "string"), delimiter),
            new XElement("Repetition", new XAttribute("type", "long"), repetition),
            new XElement("Inclusive", new XAttribute("type", "boolean"), inclusive ? "true" : "false"));

    private static XElement GrepItem(CharacterStyle style, string expression) =>
        new("ListItem", new XAttribute("type", "record"),
            new XElement("AppliedCharacterStyle", new XAttribute("type", "object"), style.Reference),
            new XElement("GrepExpression", new XAttribute("type", "string"), expression));

    private static XElement LineItem(CharacterStyle style, int lines) =>
        new("ListItem", new XAttribute("type", "record"),
            new XElement("AppliedCharacterStyle", new XAttribute("type", "object"), style.Reference),
            new XElement("LineCount", new XAttribute("type", "long"), lines),
            new XElement("RepeatLast", new XAttribute("type", "long"), -1));

    /// <summary>The text drawn in runs whose font is the given style ("Bold", "Italic", "Regular"), in order.</summary>
    private static string TextIn(IEnumerable<GlyphRunOp> runs, string fontStyle) =>
        string.Concat(runs.Where(run => run.Font.DisplayName.EndsWith(fontStyle, StringComparison.Ordinal)).Select(run => run.Text));

    [Fact]
    public void A_nested_style_through_a_delimiter_styles_the_run_in_head()
    {
        var (document, page) = NewDocument();
        var style = Body(document, "Run-in");
        SetList(style, "AllNestedStyles", NestedItem(Strong(document), ":", enumerated: false, 1, inclusive: true));
        var frame = page.AddText(new Rect(0, 0, 400, 100), "Netherfield Park: let at last.", style);

        var runs = Runs(document, frame);
        Assert.Equal("Netherfield Park:", TextIn(runs, "Bold"));
        Assert.Equal(" let at last.", TextIn(runs, "Regular"));
    }

    [Fact]
    public void A_delimiter_that_never_comes_styles_the_whole_paragraph()
    {
        var (document, page) = NewDocument();
        var style = Body(document, "Run-in");
        SetList(style, "AllNestedStyles", NestedItem(Strong(document), ":", enumerated: false, 1, inclusive: true));
        var frame = page.AddText(new Rect(0, 0, 400, 100), "What a fine thing for our girls!", style);

        Assert.Equal("What a fine thing for our girls!", TextIn(Runs(document, frame), "Bold"));
    }

    [Fact]
    public void Nested_styles_follow_each_other_and_up_to_leaves_the_delimiter_to_the_next()
    {
        var (document, page) = NewDocument();
        var style = Body(document, "Sequence");
        SetList(style, "AllNestedStyles",
            NestedItem(Strong(document), "AnyWord", enumerated: true, 3, inclusive: false),
            NestedItem(Emph(document), ",", enumerated: false, 1, inclusive: false));
        var frame = page.AddText(new Rect(0, 0, 400, 100), "Netherfield Park is let at last, said she.", style);

        var runs = Runs(document, frame);
        Assert.Equal("Netherfield Park is", TextIn(runs, "Bold"));
        Assert.Equal(" let at last", TextIn(runs, "Italic"));
        Assert.Equal(", said she.", TextIn(runs, "Regular"));
    }

    [Fact]
    public void Grep_styles_layer_in_order_each_setting_only_its_own_attributes()
    {
        var (document, page) = NewDocument();
        var red = document.Swatches.AddCmyk("Number Red", 0, 100, 100, 0);
        var number = document.Styles.AddCharacterStyle("Number", new CharacterFormat { FillColor = red });
        var style = Body(document, "Grep");
        SetList(style, "AllGREPStyles", GrepItem(Emph(document), @"\x{201C}[^\x{201D}]+\x{201D}"), GrepItem(number, @"\d+"));
        var frame = page.AddText(new Rect(0, 0, 400, 100), $"said she, {LeftQuote}and took 12 horses{RightQuote} 3", style);

        var runs = Runs(document, frame);
        var twelve = runs.Single(run => run.Text == "12");
        Assert.EndsWith("Italic", twelve.Font.DisplayName, StringComparison.Ordinal);
        Assert.Equal([0, 1, 1, 0], twelve.Color!.Components);
        var three = runs.Single(run => run.Text == "3");
        Assert.EndsWith("Regular", three.Font.DisplayName, StringComparison.Ordinal);
        Assert.Equal([0, 1, 1, 0], three.Color!.Components);
        Assert.Equal($"{LeftQuote}and took 12 horses{RightQuote}", TextIn(runs, "Italic"));
    }

    [Fact]
    public void Local_formatting_stays_above_a_grep_style()
    {
        var (document, page) = NewDocument();
        var style = Body(document, "Grep");
        SetList(style, "AllGREPStyles", GrepItem(document.Styles.AddCharacterStyle("Paper digits", new CharacterFormat { FontStyle = "Bold", FillColor = Swatch.Paper }), @"\d+"));
        var frame = page.AddText(new Rect(0, 0, 400, 100), "In 1813 they came.", style);
        frame.Story.Element.Descendants("CharacterStyleRange").First().SetAttributeValue("FillColor", "Color/Black");

        var year = Runs(document, frame).Single(run => run.Text == "1813");
        Assert.EndsWith("Bold", year.Font.DisplayName, StringComparison.Ordinal);
        Assert.Equal([0, 0, 0, 1], year.Color!.Components);
    }

    [Fact]
    public void A_line_style_styles_the_composed_line_and_its_breaks_use_its_widths()
    {
        var (document, page) = NewDocument();
        var caps = document.Styles.AddCharacterStyle("Caps", new CharacterFormat { FontFamily = "Liberation Sans", FontStyle = "Bold", Capitalization = Capitalization.AllCaps });
        var style = Body(document, "Lines");
        SetList(style, "AllLineStyles", LineItem(caps, 1));
        const string text = "Netherfield Park is let at last: Mr. Bingley is a single man of large fortune. Four or five thousand a year.";
        var frame = page.AddText(new Rect(0, 0, 250, 200), text, style);

        var lines = Runs(document, frame).Where(run => run.Text.Trim().Length > 0).GroupBy(run => Math.Round(run.Y, 1)).OrderBy(line => line.Key).ToList();
        Assert.True(lines.Count >= 3);
        var first = string.Concat(lines[0].Select(run => run.Text));
        Assert.All(lines[0], run => Assert.EndsWith("Bold", run.Font.DisplayName, StringComparison.Ordinal));
        Assert.Equal(first.ToUpperInvariant(), first);
        Assert.All(lines.Skip(1).SelectMany(line => line), run => Assert.EndsWith("Regular", run.Font.DisplayName, StringComparison.Ordinal));
        // Every character drawn once: restyling at the second line neither drops nor repeats text.
        var drawn = string.Concat(lines.SelectMany(line => line).Select(run => run.Text)).Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.Equal(text.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(), drawn.ToUpperInvariant());
        Assert.StartsWith(first.Replace(" ", string.Empty, StringComparison.Ordinal), text.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_line_break_starts_a_new_line_for_line_styles()
    {
        var (document, page) = NewDocument();
        var style = Body(document, "Lines");
        SetList(style, "AllLineStyles", LineItem(Strong(document), 1));
        var frame = page.AddText(new Rect(0, 0, 400, 100), $"First line{(char)0x2028}second line", style);

        var runs = Runs(document, frame);
        Assert.Equal("First line", TextIn(runs, "Bold"));
        Assert.Equal("second line", TextIn(runs, "Regular"));
    }

    [Theory]
    [InlineData("(")]
    [InlineData(@"\d++")]
    [InlineData("[[=a=]]")]
    [InlineData("~v")]
    [InlineData(@"(a|b\Kc)")]
    public void Unsupported_or_invalid_expressions_are_skipped(string expression)
    {
        Assert.Null(GrepExpression.Compile(expression));

        var (document, page) = NewDocument();
        var style = Body(document, "Grep");
        SetList(style, "AllGREPStyles", GrepItem(Strong(document), expression), GrepItem(Emph(document), @"\d+"));
        var frame = page.AddText(new Rect(0, 0, 400, 100), "In 1813 a ++ b", style);

        var runs = Runs(document, frame);
        Assert.Equal("1813", TextIn(runs, "Italic"));
        Assert.Empty(TextIn(runs, "Bold"));
    }

    [Theory]
    [InlineData(@"\b[A-Z][a-z]+", "In 1813 Mrs. Bennet", "In|Mrs|Bennet")]
    [InlineData(@"(?i)(bingley|DARCY)", "Bingley and Darcy", "Bingley|Darcy")]
    [InlineData(@"\u\l+", "Mr. Bingley", "Mr|Bingley")]
    [InlineData(@"[[:digit:]]+", "4 servants, 12 horses", "4|12")]
    [InlineData(@"Mr\. \K\u\l+", "Mr. Bingley and Mr. Darcy", "Bingley|Darcy")]
    [InlineData(@"(?i)mr\. \Kd\l+", "Mr. Bingley and MR. Darcy", "Darcy")]
    [InlineData(@"\<\l", "an apple", "a|a")]
    [InlineData(@"\l\>", "an apple", "n|e")]
    [InlineData(@"\Qa.b\E", "a.b axb", "a.b")]
    [InlineData(@"\d{2,}", "1 22 333", "22|333")]
    [InlineData(@"\w+$", "one two three", "three")]
    [InlineData(@"~=", "1990-2000 and 1990–2000", "–")]
    public void Grep_expressions_match_as_in_InDesign(string expression, string text, string expected)
    {
        var regex = GrepExpression.Compile(expression);
        Assert.NotNull(regex);
        Assert.Equal(expected, string.Join('|', regex.Matches(text).Select(match => match.Value)));
    }

    [Fact]
    public void InDesign_quote_metacharacters_match_straight_and_curly_quotes()
    {
        var regex = GrepExpression.Compile("~\"[^~\"]+~\"");
        Assert.NotNull(regex);
        Assert.Equal(["\"plain\"", $"{LeftQuote}curly{RightQuote}"], regex.Matches($"a \"plain\" and {LeftQuote}curly{RightQuote}").Select(match => match.Value));
    }
}
