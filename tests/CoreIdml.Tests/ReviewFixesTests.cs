using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Packaging;

namespace CoreIdml.Tests;

/// <summary>Regressions for the defects found while comparing with idmlkit and SimpleIDML.</summary>
public class ReviewFixesTests
{
    [Fact]
    public void Tabs_and_line_breaks_inside_attributes_survive_a_modified_part()
    {
        var original = IdmlPackage.Open(TestFiles.Editorial);
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        // Any change to Preferences.xml forces it to be re-serialized (AddPage updates PagesPerDocument).
        document.AddPage();

        var reopened = IdmlPackage.Open(document.ToArray());

        var before = Attributes(original.GetXml("Resources/Preferences.xml"));
        var after = Attributes(reopened.GetXml("Resources/Preferences.xml"));
        var changed = before.Where(pair => after.TryGetValue(pair.Key, out var value) && value != pair.Value).Select(pair => pair.Key).ToList();
        Assert.Equal(["DocumentPreference/PagesPerDocument"], changed);
        Assert.Contains(before, pair => pair.Value.Contains('\t', StringComparison.Ordinal));
    }

    [Fact]
    public void A_modified_part_keeps_the_original_ending()
    {
        var original = IdmlPackage.Open(TestFiles.Editorial);
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        var story = document.Stories.First(s => s.Text.Contains("Némard", StringComparison.Ordinal));
        story.ReplaceText("Jean", "Juan");

        var bytes = document.Package.GetBytes(story.PartPath);

        Assert.Equal(original.GetBytes(story.PartPath).Span[^1], bytes.Span[^1]);
    }

    [Fact]
    public void Reading_frame_properties_does_not_modify_the_document()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);
        foreach (var frame in document.AllPageItems.OfType<TextFrame>())
        {
            _ = (frame.Columns, frame.ColumnGutter, frame.Inset, frame.VerticalJustification);
        }

        Assert.DoesNotContain(document.Package.PartPaths, document.Package.IsModified);
    }

    [Fact]
    public void Setting_a_runs_text_keeps_tables_and_anchored_objects_in_it()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 300));
        frame.Story.AddParagraph("antes");
        frame.AddTable(1, 1);
        var runWithTable = frame.Story.Runs.First(run => run.Element.Element("Table") is not null);

        runWithTable.SetText("nuevo");

        Assert.NotNull(runWithTable.Element.Element("Table"));
        Assert.Contains("nuevo", runWithTable.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Filling_a_rich_text_tag_leaves_no_stale_formatted_text()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportedNestedXml);
        var article = document.Tags.FindElements("article").Single();
        Assert.Contains("Steve Zissou", article.Text, StringComparison.Ordinal);

        article.SetText("Texto nuevo");

        Assert.Equal("Texto nuevo", article.Text);
        Assert.Empty(document.Tags.FindElements("bold"));
        Assert.Empty(document.Tags.FindElements("sup"));
    }

    [Fact]
    public void Clearing_a_tagged_story_keeps_its_tag()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);
        var story = document.Tags.FindElements("headline").Single().BoundStory!;

        story.Clear();

        Assert.Equal(string.Empty, story.Text);
        Assert.Single(document.Tags.FindElements("headline"));
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Removing_items_leaves_no_dangling_tags_or_threads()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);
        var picture = (GraphicFrame)document.Tags.FindElements("main_picture").Single().BoundPageItem!;
        var headline = document.Tags.FindElements("headline").Single().BoundStory!.Frames.Single();

        picture.Remove();
        headline.Remove();

        CreateDocumentTests.AssertValid(document);
        Assert.Null(document.Tags.FindElements("main_picture").Single().BoundPageItem);
    }

    [Fact]
    public void Removing_a_middle_frame_joins_the_thread()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageCount = 3 });
        var frames = document.Pages.Select(page => page.AddTextFrame(new Rect(0, 0, 100, 100))).ToList();
        frames[0].ThreadTo(frames[1]);
        frames[1].ThreadTo(frames[2]);

        frames[1].Remove();

        Assert.Equal(frames[2].Id, frames[0].NextFrameId);
        Assert.Equal(frames[0].Id, frames[2].PreviousFrameId);
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Clearing_an_image_moves_its_tag_to_the_frame()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);
        var node = document.Tags.FindElements("main_picture").Single();
        var frame = (GraphicFrame)node.BoundPageItem!;

        frame.ClearContent();

        Assert.Equal(frame.Id, document.Tags.FindElements("main_picture").Single().ContentId);
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Replacing_an_image_keeps_its_graphic_and_link_ids()
    {
        var photo = TestFiles.TempPath(".png");
        File.WriteAllBytes(photo, TestFiles.Png(10, 10));
        using var document = IdmlDocument.Open(TestFiles.Article);
        var frame = document.AllPageItems.OfType<GraphicFrame>().Single(item => item.LinkUri is not null);
        var (graphicId, linkId) = (frame.Graphic!.Attribute("Self")!.Value, frame.LinkId);

        frame.PlaceImage(ImageSource.FromFile(photo));

        Assert.Equal(graphicId, frame.Graphic!.Attribute("Self")!.Value);
        Assert.Equal(linkId, frame.LinkId);
    }

    [Fact]
    public void Replacements_never_span_special_characters()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 50));
        frame.Story.AddParagraph().AddText("ab").AddPageNumber().AddText("cd");

        Assert.Equal(0, frame.Story.ReplaceText("bc", "X"));
        Assert.Equal(1, frame.Story.ReplaceText("cd", "X"));
    }

    private static Dictionary<string, string> Attributes(XDocument document) =>
        document.Root!.Descendants()
            .Where(element => element.Parent == document.Root)
            .SelectMany(element => element.DescendantsAndSelf().SelectMany(e => e.Attributes().Select(a => (Key: $"{e.Name.LocalName}/{a.Name.LocalName}", a.Value))))
            .GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => string.Join("|", group.Select(pair => pair.Value)));
}
