using System.Text.Json.Nodes;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Packaging;

namespace CoreIdml.Tests;

/// <summary>Regressions for the defects found by the code review of the document model, the command line and packaging.</summary>
public class CodeReviewFindingsTests
{
    private static void AssertValid(IdmlDocument document)
    {
        var result = IdmlValidator.Validate(IdmlDocument.Open(document.ToArray()));
        Assert.True(result.IsValid, result.ToString());
    }

    /// <summary>A facing document of four pages with a rectangle centred exactly on the spine of the second spread.</summary>
    private static IdmlDocument WithSpineItem(out string itemName)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageCount = 4, FacingPages = true });
        for (var i = 0; i < 4; i++)
        {
            document.Pages[i].AddText(new Rect(50, 60, 200, 40), "Página " + (i + 1), name: "texto" + (i + 1));
        }
        var left = document.Pages[1];
        itemName = "lomo";
        left.AddRectangle(new Rect(left.Size.Width - 50, 100, 100, 100), name: itemName);
        Assert.Equal(0, document.FindPageItem(itemName)!.SpreadBounds.Center.X, 6);
        return document;
    }

    [Fact]
    public void An_item_on_the_spine_belongs_to_one_page_only()
    {
        using var document = WithSpineItem(out var name);

        Assert.Single(document.Pages[1].PageItems, item => item.Name == name);
        Assert.DoesNotContain(document.Pages[2].PageItems, item => item.Name == name);
    }

    [Fact]
    public void Reordering_pages_moves_an_item_on_the_spine_once()
    {
        using var document = WithSpineItem(out var name);

        document.MovePage(3, 0);
        document.InsertPage(2);
        document.ReorderPages([4, 3, 2, 1, 0]);

        Assert.Single(document.AllPageItems, item => item.Name == name);
        AssertValid(document);
    }

    [Fact]
    public void Importing_both_pages_of_a_spread_copies_an_item_on_the_spine_once()
    {
        using var source = WithSpineItem(out var name);
        using var target = IdmlDocument.Create();

        target.ImportPages(source, 1, 2);

        Assert.Single(target.AllPageItems, item => item.Name == name);
        AssertValid(target);
    }

    [Fact]
    public void Threading_into_threaded_frames_keeps_one_consistent_thread()
    {
        using var document = IdmlDocument.Create();
        var page = document.Pages[0];
        var a = page.AddText(new Rect(20, 20, 100, 40), "uno");
        var b = page.AddText(new Rect(20, 80, 100, 40), "dos");
        var c = page.AddText(new Rect(20, 140, 100, 40), "tres");
        var d = page.AddText(new Rect(20, 200, 100, 40), "cuatro");
        a.ThreadTo(b);
        c.ThreadTo(d);
        var oldStory = c.StoryId;

        a.ThreadTo(c);

        // C and its continuation D are inserted after A; B follows them.
        Assert.Equal(c.Id, a.NextFrameId);
        Assert.Equal(a.Id, c.PreviousFrameId);
        Assert.Equal(d.Id, c.NextFrameId);
        Assert.Equal(b.Id, d.NextFrameId);
        Assert.Equal(d.Id, b.PreviousFrameId);
        Assert.Null(b.NextFrameId);
        Assert.All([b, c, d], frame => Assert.Equal(a.StoryId, frame.StoryId));
        Assert.Null(document.FindStory(oldStory));
        Assert.Equal([a.Id, c.Id, d.Id, b.Id], a.Story.Frames.Select(frame => frame.Id));
        AssertValid(document);

        // Threading back to an earlier frame of the thread would make a loop.
        Assert.Throws<IdmlException>(() => b.ThreadTo(a));
        Assert.Throws<IdmlException>(() => a.ThreadTo(a));
    }

    [Fact]
    public void Threading_takes_a_frame_from_the_middle_of_another_thread()
    {
        using var document = IdmlDocument.Create();
        var page = document.Pages[0];
        var a = page.AddText(new Rect(20, 20, 100, 40), "uno");
        var p = page.AddText(new Rect(20, 80, 100, 40), "otra");
        var c = page.AddText(new Rect(20, 140, 100, 40), "tres");
        p.ThreadTo(c);
        var otherStory = p.StoryId;

        a.ThreadTo(c);

        Assert.Null(p.NextFrameId);
        Assert.Equal(otherStory, p.StoryId);
        Assert.NotNull(document.FindStory(otherStory));
        Assert.Equal(a.StoryId, c.StoryId);
        AssertValid(document);
    }

    [Fact]
    public void Removing_a_threaded_master_frame_keeps_the_story_of_the_others()
    {
        using var document = IdmlDocument.Create();
        var masterPage = document.MasterSpreads[0].Pages[0];
        var first = masterPage.AddText(new Rect(20, 20, 100, 40), "maestra");
        var second = masterPage.AddTextFrame(new Rect(20, 80, 100, 40));
        first.ThreadTo(second);
        var storyId = first.StoryId;

        first.Remove();

        Assert.NotNull(document.FindStory(storyId));
        Assert.Null(second.PreviousFrameId);
        Assert.Equal(storyId, second.StoryId);
        AssertValid(document);
    }

    [Fact]
    public void Deleting_a_page_joins_threads_through_every_frame_on_it()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageCount = 3 });
        var a = document.Pages[0].AddText(new Rect(20, 20, 100, 40), "uno");
        var b = document.Pages[1].AddTextFrame(new Rect(20, 20, 100, 40));
        var c = document.Pages[1].AddTextFrame(new Rect(20, 80, 100, 40));
        var d = document.Pages[2].AddTextFrame(new Rect(20, 20, 100, 40));
        var alone = document.Pages[1].AddText(new Rect(200, 20, 100, 40), "sola");
        a.ThreadTo(b);
        b.ThreadTo(c);
        c.ThreadTo(d);
        var aloneStory = alone.StoryId;

        document.DeletePage(1);

        Assert.Equal(d.Id, a.NextFrameId);
        Assert.Equal(a.Id, d.PreviousFrameId);
        Assert.NotNull(document.FindStory(a.StoryId));
        Assert.Null(document.FindStory(aloneStory));
        AssertValid(document);
    }

    [Fact]
    public void A_section_whose_first_page_is_deleted_starts_at_the_next_page()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageCount = 6 });
        var first = document.DesignMap.Elements("Section").Single();
        var roman = new XElement(first);
        roman.SetAttributeValue("Self", document.NewId());
        roman.SetAttributeValue("PageStart", document.Pages[4].Id);
        roman.SetAttributeValue("PageNumberStyle", "LowerRoman");
        first.SetAttributeValue("Length", "4");
        roman.SetAttributeValue("Length", "2");
        first.AddAfterSelf(roman);
        var sixth = document.Pages[5].Id;

        document.DeletePage(4);

        var sections = document.DesignMap.Elements("Section").ToList();
        Assert.Equal(2, sections.Count);
        Assert.Equal("LowerRoman", sections[1].Attribute("PageNumberStyle")!.Value);
        Assert.Equal(sixth, sections[1].Attribute("PageStart")!.Value);
        Assert.Equal("1", sections[1].Attribute("Length")!.Value);
        Assert.Equal("4", sections[0].Attribute("Length")!.Value);

        // The last page of a section deleted with nothing after it: the section goes.
        document.DeletePage(4);
        Assert.Single(document.DesignMap.Elements("Section"));
        AssertValid(document);
    }

    [Fact]
    public void Create_refuses_fewer_than_one_column()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IdmlDocument.Create(new DocumentOptions { Columns = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdmlDocument.Create(new DocumentOptions { Columns = -2 }));
    }

    [Fact]
    public void Fill_does_not_fill_placeholders_inside_values_written_for_other_keys()
    {
        using var document = IdmlDocument.Create();
        var first = document.Pages[0].AddText(new Rect(20, 20, 200, 40), "{{a}}");
        var second = document.Pages[0].AddText(new Rect(20, 80, 200, 40), "{{b}}");
        var named = document.Pages[0].AddText(new Rect(20, 140, 200, 40), "x", name: "c");

        var result = document.Fill(new TemplateData
        {
            Text = new(StringComparer.Ordinal) { ["c"] = "Frame {{b}}", ["a"] = "Use {{b}} syntax", ["b"] = "X" },
        });

        Assert.Equal("Use {{b}} syntax", first.Story.Text);
        Assert.Equal("X", second.Story.Text);
        Assert.Equal("Frame {{b}}", named.Story.Text);
        Assert.Equal(1, result.Filled["b"]);
        Assert.DoesNotContain(document.Stories, story => story.Text.Contains('\uE000', StringComparison.Ordinal));
    }

    [Fact]
    public void Threads_between_frames_anchored_in_a_story_are_not_broken()
    {
        using var document = IdmlDocument.Create();
        var host = document.Pages[0].AddText(new Rect(20, 20, 200, 200), "anclas");
        var range = host.Story.Element.Descendants("CharacterStyleRange").First();
        XElement Anchored(string self, string previous, string next) => new("TextFrame",
            new XAttribute("Self", self), new XAttribute("ParentStory", host.StoryId),
            new XAttribute("PreviousTextFrame", previous), new XAttribute("NextTextFrame", next),
            new XAttribute("ContentType", "TextType"));
        var one = document.NewId();
        var two = document.NewId();
        range.Add(Anchored(one, "n", two), Anchored(two, one, "n"));

        var result = IdmlValidator.Validate(IdmlDocument.Open(document.ToArray()));

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "broken-thread");
    }

    [Fact]
    public void File_uris_with_a_host_are_unc_paths()
    {
        Assert.Equal("//server/share/photo.jpg", DocumentPackager.LocalPath("file://server/share/photo.jpg"));
        Assert.Equal("C:/x.jpg", DocumentPackager.LocalPath("file://localhost/C:/x.jpg"));
        Assert.Equal("/srv/x.jpg", DocumentPackager.LocalPath("file://localhost/srv/x.jpg"));
        Assert.Equal("C:/x.jpg", DocumentPackager.LocalPath("file://C:/x.jpg"));
        Assert.Equal("C:/x.jpg", DocumentPackager.LocalPath("file:///C:/x.jpg"));
        Assert.Equal("C:/x.jpg", DocumentPackager.LocalPath("file:C:/x.jpg"));
        Assert.Equal("/srv/x.jpg", DocumentPackager.LocalPath("file:///srv/x.jpg"));
    }

    [Fact]
    public void Relink_folder_with_all_relinks_links_whose_file_still_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "idml-tests", Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root, "viejas");
        var updated = Path.Combine(root, "nuevas");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(updated);
        File.WriteAllBytes(Path.Combine(old, "uno.png"), TestFiles.Png(40, 30));
        File.WriteAllBytes(Path.Combine(updated, "uno.png"), TestFiles.Png(40, 30));
        var path = Path.Combine(root, "doc.idml");
        using (var document = IdmlDocument.Create())
        {
            document.Pages[0].AddImage(new Rect(36, 36, 100, 75), Path.Combine(old, "uno.png"), name: "foto");
            document.Save(path);
        }
        var client = new McpTestClient();

        var some = client.CallJson("idml_relink_folder", new JsonObject { ["path"] = path, ["folders"] = new JsonArray(updated), ["dryRun"] = true });
        var all = client.CallJson("idml_relink_folder", new JsonObject { ["path"] = path, ["folders"] = new JsonArray(updated), ["all"] = true, ["inPlace"] = true });

        Assert.Empty(some.GetProperty("result").GetProperty("relinked").EnumerateArray());
        Assert.Equal(1, all.GetProperty("relinked").GetArrayLength());
        using var relinked = IdmlDocument.Open(path);
        Assert.Equal(Path.Combine(updated, "uno.png"), Assert.Single(DocumentPackager.ResolveLinks(relinked, path, [])).Path);
    }

    [Fact]
    public void A_dry_run_to_stdout_prints_its_report()
    {
        var path = TestFiles.TempPath(".idml");
        File.Copy(TestFiles.Article, path);

        var (exitCode, output, error, _) = CliRunner.Run("replace", path, "the", "THE", "--dry-run", "-o", "-");

        Assert.True(exitCode == 0, error);
        Assert.StartsWith("{", output.TrimStart(), StringComparison.Ordinal);
    }
}
