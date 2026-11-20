using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;
using CoreIdml.Xml;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Footnotes and endnotes, as InDesign 20 sets them in doc-samples/11-long-footnotes-endnotes: reference numbers in
/// the text, footnotes at the foot of the column under the rule, the column ending above them, split footnotes going
/// on under the continuing rule, numbering that restarts per page, endnote numbers and the endnote frame.
/// </summary>
public sealed class FootnoteTests
{
    // Liberation Serif 10 on 12 pt leading: a frame 50 pt high holds 4 lines (baselines at 8.9, 20.9, 32.9, 44.9).
    private const double FourLines = 50;

    private static readonly ParagraphFormat Body = new() { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 };

    private static readonly ParagraphFormat Note = new() { FontFamily = "Liberation Serif", PointSize = 8, Leading = 10 };

    // A forced line break (U+2028).
    private const char LineBreak = (char)0x2028;

    /// <summary>A document with the sample's footnote options (4 pt between footnotes, a 0.75 pt rule 4 pt above them).</summary>
    private static IdmlDocument Document(string restart = "PageRestart", bool splitting = true)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var option = document.Package.GetXml("Resources/Preferences.xml").Root!.Element("FootnoteOption")!;
        option.SetAttributeValue("SpaceBetween", "4");
        option.SetAttributeValue("RuleLineWeight", "0.75");
        option.SetAttributeValue("RuleOffset", "4");
        option.SetAttributeValue("RuleWidth", "120");
        option.SetAttributeValue("ContinuingRuleWidth", "200");
        option.SetAttributeValue("NoSplitting", splitting ? "false" : "true");
        option.Element("Properties")!.Element("RestartNumbering")!.Value = restart;
        return document;
    }

    /// <summary>A footnote whose text is its number, a tab and <paramref name="text"/> (U+2028 breaks its lines).</summary>
    private static XElement Footnote(ParagraphStyle style, string text) =>
        new("Footnote", new XElement("ParagraphStyleRange", new XAttribute("AppliedParagraphStyle", style.Reference),
            new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle),
                new XElement("Content", new XProcessingInstruction("ACE", "4"), "\t" + text))));

    /// <summary>Appends a paragraph "<paramref name="text"/>", followed by a reference to a footnote reading <paramref name="note"/>.</summary>
    private static void AddWithNote(Story story, string text, ParagraphStyle body, ParagraphStyle noteStyle, string note)
    {
        var paragraph = story.AddParagraph(text, body);
        paragraph.Element.Add(new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle), Footnote(noteStyle, note)));
    }

    private static (StoryLayout Layout, IdmlRenderer Renderer) Lay(IdmlDocument document, TextFrame frame)
    {
        var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        return (renderer.LayoutStory(frame.Story), renderer);
    }

    private static int FrameOf(StoryLayout layout, string text) => layout.Lines.Single(line => line.Text.StartsWith(text, StringComparison.Ordinal)).FrameIndex;

    /// <summary>The footnote lines drawn in a frame (runs in the 8 pt footnote text, without the space drawn for the tab), by baseline.</summary>
    private static List<(double Y, string Text)> NoteLines(StoryLayout layout, TextFrame frame) =>
        layout.OpsByFrame[frame.Id].OfType<GlyphRunOp>().Where(run => run.Size == 8 && run.Text.Trim().Length > 0)
            .GroupBy(run => Math.Round(run.Y, 3)).Select(line => (line.Key, string.Concat(line.Select(run => run.Text)).Trim())).ToList();

    [Fact]
    public void Footnotes_stack_at_the_foot_of_the_column_under_the_rule()
    {
        using var document = Document();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var note = document.Styles.AddParagraphStyle("Note", Note);
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 200));
        AddWithNote(frame.Story, "One", body, note, "First note.");
        AddWithNote(frame.Story, "Two", body, note, "Second note.");
        var (layout, renderer) = Lay(document, frame);
        var bottom = renderer.FrameArea(frame, "1").Columns[0].Bottom;

        // The references: superscript numbers (58.3% of 10 pt), 1 and 2.
        var references = layout.OpsByFrame[frame.Id].OfType<GlyphRunOp>().Where(run => Math.Abs(run.Size - 5.83) < 0.01).Select(run => run.Text).ToList();
        Assert.Equal(["1", "2"], references);
        Assert.Equal(["One1", "Two2"], layout.Lines.Select(line => line.Text));
        // The last footnote's baseline on the column's bottom, the first a leading and the space between above it.
        var notes = NoteLines(layout, frame);
        Assert.Equal(2, notes.Count);
        Assert.Equal(["1First note.", "2Second note."], notes.Select(line => line.Text));
        Assert.Equal(bottom, notes[1].Y, 3);
        Assert.Equal(bottom - 14, notes[0].Y, 3);
        // The rule 4 pt above where the first footnote starts (a leading above its baseline), 120 pt wide.
        var rule = Assert.Single(layout.OpsByFrame[frame.Id].OfType<TextRuleOp>());
        Assert.Equal(notes[0].Y - 10 - 4, rule.From.Y, 3);
        Assert.Equal(120, rule.Length);
        Assert.Equal(0.75, rule.Weight);
        Assert.False(layout.Overset);
    }

    [Fact]
    public void A_footnote_shrinks_its_column_and_the_text_after_it_moves_on()
    {
        using var document = Document();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var note = document.Styles.AddParagraphStyle("Note", Note);
        var page = document.Pages[0];
        var first = page.AddTextFrame(new Rect(0, 0, 300, FourLines));
        first.ThreadTo(page.AddTextFrame(new Rect(0, 100, 300, FourLines)));
        AddWithNote(first.Story, "One", body, note, "A note.");
        foreach (var text in new[] { "Two", "Three", "Four" })
        {
            first.Story.AddParagraph(text, body);
        }
        var (layout, _) = Lay(document, first);

        // The footnote takes 10 pt and its rule 4.4 pt above that: the fourth line (baseline 44.9) no longer fits.
        Assert.Equal(0, FrameOf(layout, "Three"));
        Assert.Equal(1, FrameOf(layout, "Four"));
        Assert.Single(NoteLines(layout, first));
        Assert.False(layout.Overset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_reference_goes_on_with_its_line_when_its_footnote_does_not_fit_below_it(bool splitting)
    {
        using var document = Document(splitting: splitting);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var note = document.Styles.AddParagraphStyle("Note", Note);
        var page = document.Pages[0];
        var first = page.AddTextFrame(new Rect(0, 0, 300, FourLines));
        var second = page.AddTextFrame(new Rect(0, 100, 300, FourLines));
        first.ThreadTo(second);
        foreach (var text in new[] { "One", "Two", "Three" })
        {
            first.Story.AddParagraph(text, body);
        }
        AddWithNote(first.Story, "Four", body, note, "A note.");
        var (layout, _) = Lay(document, first);

        // With its footnote below it, the fourth line would end below the frame: it moves on, and the footnote with it.
        Assert.Equal(0, FrameOf(layout, "Three"));
        Assert.Equal(1, FrameOf(layout, "Four"));
        Assert.Empty(NoteLines(layout, first));
        Assert.Single(NoteLines(layout, second));
    }

    [Fact]
    public void A_footnote_that_does_not_fit_goes_on_under_the_continuing_rule()
    {
        using var document = Document();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var note = document.Styles.AddParagraphStyle("Note", Note);
        var page = document.Pages[0];
        var first = page.AddTextFrame(new Rect(0, 0, 300, FourLines));
        var second = page.AddTextFrame(new Rect(0, 100, 300, FourLines));
        first.ThreadTo(second);
        AddWithNote(first.Story, "One", body, note, string.Join(LineBreak, "a", "b", "c", "d", "e", "f"));
        first.Story.AddParagraph("Two", body);
        var (layout, _) = Lay(document, first);

        // 3 of its 6 lines fit below "One": baselines 10, 20, 30 of the 36.7 pt left under its baseline (8.9) and the rule
        // (4.4 pt above the footnote); 3 go on.
        Assert.Equal(3, NoteLines(layout, first).Count);
        Assert.Equal(["d", "e", "f"], NoteLines(layout, second).Select(line => line.Text));
        Assert.Equal(120, Assert.Single(layout.OpsByFrame[first.Id].OfType<TextRuleOp>()).Length);
        Assert.Equal(200, Assert.Single(layout.OpsByFrame[second.Id].OfType<TextRuleOp>()).Length);
        // The first column is full down to its reference: the text goes on above the rest of the footnote.
        Assert.Equal(1, FrameOf(layout, "Two"));
        Assert.False(layout.Overset);
    }

    [Theory]
    [InlineData("PageRestart", "1")]
    [InlineData("DontRestart", "2")]
    public void Footnote_numbers_restart_on_every_page(string restart, string second)
    {
        using var document = Document(restart);
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var note = document.Styles.AddParagraphStyle("Note", Note);
        // One line and its footnote per frame, one frame per page.
        var first = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 25));
        var next = document.AddPage().AddTextFrame(new Rect(0, 0, 300, 25));
        first.ThreadTo(next);
        AddWithNote(first.Story, "One", body, note, "First note.");
        AddWithNote(first.Story, "Two", body, note, "Second note.");
        var (layout, _) = Lay(document, first);

        Assert.Equal(["One1", "Two" + second], layout.Lines.Select(line => line.Text));
        Assert.Equal(second + "Second note.", Assert.Single(NoteLines(layout, next)).Text);
        Assert.False(layout.Overset);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void Fit_checks_count_the_room_footnotes_take(int lines, bool overset)
    {
        using var document = Document();
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var note = document.Styles.AddParagraphStyle("Note", Note);
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, FourLines));
        AddWithNote(frame.Story, "One", body, note, "A note.");
        foreach (var text in ((string[])["Two", "Three", "Four"]).Take(lines - 1))
        {
            frame.Story.AddParagraph(text, body);
        }

        using var measurer = new TextMeasurer(document, TestSupport.ExportOptions().Fonts);
        var fit = measurer.CheckFit(frame.Story);

        Assert.Equal(overset, fit.Overset);
        Assert.Equal(overset ? 1 : 0, fit.OverflowLines);
    }

    [Fact]
    public void Endnote_references_are_numbered_in_the_order_of_the_endnote_story_and_its_frame_is_drawn()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        document.DesignMap.Add(new XElement("EndnoteOption", new XAttribute("StartEndnoteNumberAt", "5")));
        var body = document.Styles.AddParagraphStyle("Body", Body);
        var text = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 100));
        var paragraph = text.Story.AddParagraph("One", body);
        XElement Reference(string id, string range) => new("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle),
            new XElement("Endnote", new XAttribute("Self", id), new XAttribute("EndnoteTextRange", range)));
        paragraph.Element.Add(Reference("e1", "r1"));
        paragraph.AddText(" two");
        paragraph.Element.Add(Reference("e2", "r2"));
        // The endnote story, in an EndnoteTextFrame as InDesign makes it.
        var endnotes = document.Pages[0].AddTextFrame(new Rect(0, 200, 300, 100));
        endnotes.Story.Element.SetAttributeValue("IsEndnoteStory", "true");
        var list = endnotes.Story.AddParagraph(string.Empty, body);
        foreach (var (range, source) in new[] { ("r1", "e1"), ("r2", "e2") })
        {
            list.Element.Add(new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle),
                new XElement("EndnoteRange", new XAttribute("Self", range), new XAttribute("SourceEndnote", source),
                    new XElement("Content", (char)0xFEFF + "Note " + source)), new XElement("Br")));
        }
        endnotes.Element.Name = "EndnoteTextFrame";

        var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        Assert.Equal(["One5 two6"], renderer.LayoutStory(text.Story).Lines.Select(line => line.Text));
        var frame = Assert.Single(document.Pages[0].PageItems.OfType<TextFrame>(), item => item.Kind == "EndnoteTextFrame");
        Assert.Equal(["Note e1", "Note e2"], renderer.LayoutStory(frame.Story).Lines.Select(line => line.Text).Where(line => line.Length > 0));
    }
}
