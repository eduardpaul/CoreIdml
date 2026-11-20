using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;
using CoreIdml.Xml;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Track Changes: InDesign 20's export of the doc-samples 11-long-tracked-changes package draws the document's
/// current text (inserted text, moved text at its new place, no deleted text, no change marks).
/// </summary>
public sealed class TrackedChangeTests
{
    private static XElement Change(string type, params object[] content) =>
        new("Change", new XAttribute("ChangeType", type), new XAttribute("UserName", "Test"), content);

    /// <summary>A frame showing "It is a truth [+generally][-deleted] acknowledged.[moved: Truly.]".</summary>
    private static TextFrame TrackedFrame(IdmlDocument document, Rect bounds, string deleted)
    {
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var frame = document.Pages[0].AddText(bounds, "It is a truth ", style);
        frame.Story.Element.Descendants("CharacterStyleRange").First().Add(
            Change("InsertedText", new XElement("Content", "generally")),
            Change("DeletedText", new XElement("ParagraphStyleRange", new XAttribute("AppliedParagraphStyle", style.Reference),
                new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle),
                    new XElement("Content", deleted), new XElement("Br"), new XElement("Content", deleted)))),
            new XElement("Content", " acknowledged."),
            Change("MovedText", new XElement("Content", " Truly.")));
        return frame;
    }

    [Fact]
    public void Inserted_and_moved_text_is_drawn_and_deleted_text_is_not()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var frame = TrackedFrame(document, new Rect(0, 0, 400, 100), "universally");

        var runs = new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story).OpsByFrame.Values
            .SelectMany(ops => ops).OfType<GlyphRunOp>().Where(run => run.Text.Trim().Length > 0).ToList();

        // One line: the deleted paragraph break does not end the paragraph either.
        Assert.Single(runs.Select(run => run.Y).Distinct());
        Assert.Equal("It is a truth generally acknowledged. Truly.", string.Concat(runs.Select(run => run.Text)).Trim());
    }

    [Fact]
    public void Deleted_text_does_not_count_towards_overset()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var frame = TrackedFrame(document, new Rect(0, 0, 400, 20), string.Concat(Enumerable.Repeat("a long deleted passage ", 50)));

        using var measurer = new TextMeasurer(document, TestSupport.ExportOptions().Fonts);
        var fit = measurer.CheckFit(frame.Story);

        Assert.False(fit.Overset);
        Assert.Equal(1, fit.Lines);
    }
}
