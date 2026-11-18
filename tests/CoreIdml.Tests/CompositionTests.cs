using CoreIdml.Dom;

namespace CoreIdml.Tests;

/// <summary>SimpleIDML parity: combining pages from several documents (add_page_from_idml / insert_idml).</summary>
public class CompositionTests
{
    [Fact]
    public void Pages_from_another_real_file_are_copied_with_text_images_and_tags()
    {
        using var target = IdmlDocument.Open(TestFiles.Editorial);
        using var source = IdmlDocument.Open(TestFiles.Article);
        var pagesBefore = target.Pages.Count;

        var added = target.ImportPages(source);

        Assert.Single(added);
        Assert.Equal(pagesBefore + 1, target.Pages.Count);
        Assert.Contains(target.Stories, story => story.Text == "THE HEADLINE HERE");
        Assert.Contains(target.Links, link => link.Uri.EndsWith("default.jpg", StringComparison.Ordinal));
        var headline = Assert.Single(target.Tags.FindElements("headline"));
        Assert.Equal("THE HEADLINE HERE", headline.Text);
        Assert.NotEmpty(added[0].PageItems);
        CreateDocumentTests.AssertValid(target);
        CreateDocumentTests.AssertValid(IdmlDocument.Open(target.ToArray()));
    }

    [Fact]
    public void The_same_template_can_be_appended_many_times_and_filled_per_page()
    {
        using var template = IdmlDocument.Create();
        template.Pages[0].AddText(new Rect(50, 50, 300, 40), "{{nombre}}", name: "nombre");
        var bytes = template.ToArray();

        using var book = IdmlDocument.Open(bytes);
        using var source = IdmlDocument.Open(bytes);
        book.ImportPages(source);
        book.ImportPages(source);

        // Each copy is independent: fill page by page through its own frame.
        var frames = book.AllPageItems.OfType<TextFrame>().Where(frame => frame.Name == "nombre").ToList();
        Assert.Equal(3, frames.Count);
        Assert.Equal(3, frames.Select(frame => frame.StoryId).Distinct().Count());
        frames[1].Story.SetText("Ana");
        Assert.Equal(["{{nombre}}", "Ana", "{{nombre}}"], frames.Select(frame => frame.Story.Text));
        // Frames keep their position on the new pages.
        Assert.All(frames, frame => Assert.Equal(50, frame.Bounds.X, 6));
        CreateDocumentTests.AssertValid(book);
    }

    [Fact]
    public void Styles_and_swatches_missing_in_the_target_are_merged_once()
    {
        using var source = IdmlDocument.Create();
        var color = source.Swatches.AddCmyk("Exclusivo", 10, 20, 30, 40);
        var style = source.Styles.AddParagraphStyle("Solo origen", new ParagraphFormat { FillColor = color });
        source.Pages[0].AddText(new Rect(0, 0, 100, 20), "x", style);
        using var target = IdmlDocument.Create();

        target.ImportPages(source);
        target.ImportPages(source);

        Assert.Single(target.Styles.ParagraphStyles, s => s.Name == "Solo origen");
        Assert.Single(target.Swatches.All, s => s.Name == "Exclusivo");
        CreateDocumentTests.AssertValid(target);
    }

    [Fact]
    public void Selected_pages_only_and_bad_indices_fail_clearly()
    {
        using var source = IdmlDocument.Create(new DocumentOptions { PageCount = 3 });
        for (var i = 0; i < 3; i++)
        {
            source.Pages[i].AddText(new Rect(0, 0, 100, 20), $"página {i + 1}");
        }
        using var target = IdmlDocument.Create();

        target.ImportPages(source, 2, 0);

        Assert.Equal(["página 3", "página 1"], target.Pages.Skip(1).Select(page => ((TextFrame)page.PageItems.Single()).Story.Text));
        Assert.Throws<IdmlException>(() => target.ImportPages(source, 7));
    }
}
