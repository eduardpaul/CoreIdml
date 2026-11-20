using System.Text.Json;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Json;
using CoreIdml.Xml;

namespace CoreIdml.Tests;

/// <summary>
/// Text recorded with Track Changes, stored as in InDesign 20's doc-samples 11-long-tracked-changes: deleted text
/// is not part of the story's current text, inserted and moved text is; deleted runs keep their run indices.
/// </summary>
public sealed class TrackedChangesTests
{
    private static XElement Change(string type, params object[] content) =>
        new("Change", new XAttribute("ChangeType", type), new XAttribute("UserName", "Test"), content);

    /// <summary>
    /// "It is a truth [+generally][-universally, a deleted paragraph break and more] acknowledged,[moved: wisely]":
    /// deleted text wrapped in its own paragraph and character ranges, as InDesign writes it.
    /// </summary>
    private static Story TrackedStory(IdmlDocument document)
    {
        var frame = document.Pages[0].AddText(new Rect(0, 0, 300, 100), "It is a truth ");
        var run = frame.Story.Element.Descendants("CharacterStyleRange").First();
        run.Add(
            Change("InsertedText", new XElement("Content", "generally")),
            Change("DeletedText", new XElement("ParagraphStyleRange", new XAttribute("AppliedParagraphStyle", IdmlNames.BasicParagraphStyle),
                new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle),
                    new XElement("Content", "universally truth"), new XElement("Br"), new XElement("Content", "more truth")))),
            new XElement("Content", " acknowledged,"),
            Change("MovedText", new XElement("Content", " wisely")));
        return frame.Story;
    }

    [Fact]
    public void Story_text_leaves_out_deleted_text_and_keeps_inserted_and_moved_text()
    {
        using var document = IdmlDocument.Create();
        var story = TrackedStory(document);

        Assert.Equal("It is a truth generally acknowledged, wisely", story.Text);
        Assert.Equal(7, story.WordCount);
    }

    [Fact]
    public void Deleted_runs_keep_their_index_and_say_what_changed()
    {
        using var document = IdmlDocument.Create();
        var story = TrackedStory(document);

        var runs = story.ContentRuns;

        Assert.Equal(["It is a truth ", "generally", "universally truth", "more truth", " acknowledged,", " wisely"], runs.Select(run => run.Text));
        Assert.Equal([null, "inserted", "deleted", "deleted", null, "moved"], runs.Select(run => run.TrackedChange));

        var json = JsonElement.Parse(IdmlJson.GetStoryJson(document, story.Id));
        var change = json.GetProperty("runs").EnumerateArray()
            .Select(run => run.TryGetProperty("change", out var value) ? value.GetString() : null).ToList();
        Assert.Equal([null, "inserted", "deleted", "deleted", null, "moved"], change);
        Assert.Equal(7, json.GetProperty("wordCount").GetInt32());
    }

    [Fact]
    public void A_deleted_paragraph_break_does_not_end_a_paragraph()
    {
        using var document = IdmlDocument.Create();
        var story = TrackedStory(document);

        var paragraph = Assert.Single(story.Paragraphs());

        Assert.Equal("It is a truth generally acknowledged, wisely", paragraph.Text);
        Assert.Equal((0, 5), (paragraph.FirstRun, paragraph.LastRun));
    }

    [Fact]
    public void Replacing_text_leaves_deleted_text_alone()
    {
        using var document = IdmlDocument.Create();
        var story = TrackedStory(document);

        Assert.Equal(1, story.ReplaceText("truth", "fact"));

        Assert.Equal("It is a fact generally acknowledged, wisely", story.Text);
        Assert.Equal(["universally truth", "more truth"], story.ContentRuns.Where(run => run.TrackedChange == "deleted").Select(run => run.Text));
    }

    [Fact]
    public void The_overset_estimate_ignores_deleted_text()
    {
        using var document = IdmlDocument.Create();
        var story = TrackedStory(document);
        var estimate = TextFit.Estimate(story);
        story.Element.Descendants("Change").First(change => change.Attribute("ChangeType")?.Value == "DeletedText")
            .Descendants("Content").First().Value = string.Concat(Enumerable.Repeat("a long deleted passage ", 200));

        Assert.Equal(estimate, TextFit.Estimate(story));
    }
}
