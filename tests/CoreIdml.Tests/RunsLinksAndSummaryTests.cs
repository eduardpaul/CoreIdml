using System.Text.Json;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Tests;

/// <summary>idmlkit parity: indexed content runs, links by id, compact summaries, diff; plus overset estimate.</summary>
public class RunsLinksAndSummaryTests
{
    [Fact]
    public void Content_runs_are_indexed_in_document_order_including_table_cells()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 300));
        frame.Story.AddParagraph("Título");
        frame.AddTable(1, 2).SetRow(0, "A", "B");
        frame.Story.AddParagraph("Fin del texto");

        var runs = frame.Story.ContentRuns;

        Assert.Equal(["Título", "A", "B", "Fin del texto"], runs.Select(run => run.Text));
        Assert.Equal([0, 1, 2, 3], runs.Select(run => run.Index));
        Assert.Equal("cell 0:0", runs[1].Scope);
        Assert.Equal("[Basic Paragraph]", runs[0].ParagraphStyle);
        Assert.Equal(6, frame.Story.WordCount);
    }

    [Fact]
    public void Set_run_text_edits_one_run_and_keeps_indices()
    {
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        var story = document.Stories.First(s => s.ContentRuns.Count > 2);
        var before = story.ContentRuns.Select(run => run.Text).ToList();

        document.SetRunText(story.Id, 1, string.Empty);
        document.SetRunText(story.Id, 0, "Nuevo");

        var after = story.ContentRuns.Select(run => run.Text).ToList();
        Assert.Equal(before.Count, after.Count);
        Assert.Equal("Nuevo", after[0]);
        Assert.Equal(string.Empty, after[1]);
        Assert.Equal(before[2..], after[2..]);
    }

    [Fact]
    public void Set_run_text_reports_bad_indices_and_protects_special_characters()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 50));
        frame.Story.AddParagraph().AddText("Página ").AddPageNumber();

        var outOfRange = Assert.Throws<IdmlException>(() => document.SetRunText(frame.StoryId, 5, "x"));
        Assert.Equal($"Story \"{frame.StoryId}\" has 2 content run(s); run index 5 does not exist.", outOfRange.Message);
        Assert.Throws<IdmlException>(() => document.SetRunText(frame.StoryId, 1, "x"));
        document.SetRunText(frame.StoryId, 1, "x", force: true);
        Assert.Equal("Página x", frame.Story.Text);
        Assert.Contains("No story", Assert.Throws<IdmlException>(() => document.SetRunText("u-nope", 0, "x")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_are_listed_with_ids_and_relinked_by_id()
    {
        var photo = TestFiles.TempPath(".png");
        File.WriteAllBytes(photo, TestFiles.Png(200, 100));
        using var document = IdmlDocument.Open(TestFiles.Article);
        var link = Assert.Single(document.Links);
        Assert.EndsWith("default.jpg", link.Uri, StringComparison.Ordinal);
        Assert.Equal("$ID/JPEG", link.Format);

        var target = TestFiles.Nowhere("nueva foto.jpg");
        document.RelinkImage(link.Id, target);
        Assert.Equal(TestFiles.LinkUri(target), document.Links.Single().Uri);
        Assert.Contains("nueva%20foto.jpg", document.Links.Single().Uri, StringComparison.Ordinal);
        document.RelinkImage(link.Id, "https://example.org/a.jpg");
        Assert.Equal("https://example.org/a.jpg", document.Links.Single().Uri);
        document.RelinkImage(link.Id, photo, ImageFit.Fit);
        Assert.Equal(link.Id, document.Links.Single().Id);
        Assert.EndsWith(".png", document.Links.Single().Uri, StringComparison.Ordinal);

        var error = Assert.Throws<IdmlException>(() => document.RelinkImage("u-nope", "x.jpg"));
        Assert.Equal("No <Link> with Self=\"u-nope\" found in any spread or story.", error.Message);
    }

    [Fact]
    public void Summary_has_ids_links_tags_fonts_and_size_controls()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);

        var compact = IdmlJson.Summarize(document, new SummaryOptions { MaxTextLength = 0, IncludeResources = false });
        var full = IdmlJson.Summarize(document, new SummaryOptions { MaxTextLength = -1, IncludeRuns = true });

        var page = Assert.Single(full.Pages!);
        Assert.False(string.IsNullOrEmpty(page.Id));
        Assert.False(string.IsNullOrEmpty(page.SpreadId));
        var link = Assert.Single(full.Links!);
        Assert.Equal(0, link.PageIndex);
        Assert.Equal("spread", link.Location);
        Assert.Contains(page.Items, item => item.Tag == "headline" && item.Text == "THE HEADLINE HERE");
        Assert.Contains(page.Items, item => item.LinkId == link.Id);
        Assert.All(full.Stories!, story => Assert.Equal(story.RunCount, story.Runs!.Count));
        Assert.NotEmpty(full.Fonts!);
        Assert.All(compact.Stories!, story => Assert.Null(story.Text));
        Assert.Null(compact.ParagraphStyles);
        Assert.True(IdmlJson.SummarizeToJson(document, new SummaryOptions { MaxTextLength = 0, IncludeResources = false }).Length
            < IdmlJson.SummarizeToJson(document, new SummaryOptions { MaxTextLength = -1, IncludeRuns = true }).Length / 2);
    }

    [Fact]
    public void Off_page_items_are_reported_separately()
    {
        using var document = IdmlDocument.Create();
        document.Pages[0].AddRectangle(new Rect(-500, 0, 50, 50), name: "pasteboard");

        var summary = IdmlJson.Summarize(document);

        Assert.Empty(summary.Pages![0].Items);
        Assert.Equal("pasteboard", Assert.Single(summary.OffPage!).Name);
    }

    [Fact]
    public void Get_story_returns_every_run()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);
        var story = document.Tags.FindElements("headline").Single().BoundStory!;

        var json = JsonElement.Parse(IdmlJson.GetStoryJson(document, story.Id));

        Assert.Equal("THE HEADLINE HERE", json.GetProperty("text").GetString());
        Assert.Equal("THE HEADLINE HERE", json.GetProperty("runs")[0].GetProperty("text").GetString());
        Assert.Equal(0, json.GetProperty("runs")[0].GetProperty("index").GetInt32());
    }

    [Fact]
    public void Diff_reports_run_and_link_changes()
    {
        using var before = IdmlDocument.Open(TestFiles.Article);
        using var after = IdmlDocument.Open(TestFiles.Article);
        var story = after.Tags.FindElements("headline").Single().BoundStory!;
        after.SetRunText(story.Id, 0, "OTRO TITULAR");
        var target = TestFiles.Nowhere("y.jpg");
        after.RelinkImage(after.Links.Single().Id, target);

        var diff = IdmlJson.Diff(before, after);

        Assert.False(diff.Identical);
        Assert.Contains(diff.Changes, change => change is { Kind: "run-changed", RunIndex: 0, After: "OTRO TITULAR" } && change.Id == story.Id);
        Assert.Contains(diff.Changes, change => change.Kind == "link-changed" && change.After == TestFiles.LinkUri(target));
        Assert.True(IdmlJson.Diff(before, before).Identical);
    }

    [Fact]
    public void Text_fit_flags_text_that_cannot_fit()
    {
        using var document = IdmlDocument.Create();
        var small = document.Pages[0].AddText(new Rect(0, 0, 100, 30), string.Join(' ', Enumerable.Repeat("palabra", 200)));
        var roomy = document.Pages[0].AddText(new Rect(0, 100, 400, 300), "Texto corto");

        Assert.True(TextFit.Estimate(small.Story).LikelyOverset);
        Assert.False(TextFit.Estimate(roomy.Story).LikelyOverset);
        Assert.Contains(IdmlJson.Summarize(document).Pages![0].Items, item => item.Id == small.Id && item.LikelyOverset == true);
    }
}
