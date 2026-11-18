using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Xml;

namespace CoreIdml.Tests;

/// <summary>FindStory (and TextFrame.Story) keep up with every way a document's stories can change.</summary>
public class StoryLookupTests
{
    [Fact]
    public void Stories_added_and_removed_are_found_and_forgotten()
    {
        using var document = IdmlDocument.Create();
        var first = document.Pages[0].AddText(new Rect(0, 0, 100, 20), "uno");
        Assert.Equal("uno", first.Story.Text);
        var second = document.Pages[0].AddText(new Rect(0, 30, 100, 20), "dos");
        Assert.Equal("dos", second.Story.Text);
        var id = second.StoryId;

        second.Remove();

        Assert.Null(document.FindStory(id));
        Assert.Equal("uno", first.Story.Text);
    }

    [Fact]
    public void A_story_part_replaced_or_unlisted_is_looked_up_again()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddText(new Rect(0, 0, 100, 20), "antes");
        var story = frame.Story;

        // The part replaced by new XML: the old element is no longer the story.
        var replacement = new XDocument(document.Package.GetXml(story.PartPath));
        replacement.Root!.Element("Story")!.Descendants("Content").Single().Value = "después";
        document.Package.SetXml(story.PartPath, replacement);
        Assert.Equal("después", frame.Story.Text);

        // Removed from designmap.xml by hand: no longer a story of the document.
        document.DesignMap.Elements(IdmlNames.PackagingNamespace + "Story").Single(e => e.Attribute("src")?.Value == story.PartPath).Remove();
        Assert.Null(document.FindStory(frame.StoryId));
    }

    [Fact]
    public void A_story_whose_id_changes_is_found_by_its_new_id()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddText(new Rect(0, 0, 100, 20), "texto");
        var oldId = frame.StoryId;
        Assert.NotNull(document.FindStory(oldId));

        frame.Story.Element.SetAttributeValue("Self", "renamed");

        Assert.Null(document.FindStory(oldId));
        Assert.Equal("texto", document.FindStory("renamed")!.Text);
    }
}
