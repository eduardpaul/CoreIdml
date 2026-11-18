using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Tests;

/// <summary>Page, item, style and table edits on existing documents (DOM and the MCP tools), and smaller summaries.</summary>
public class LayoutEditingTests
{
    private static IdmlDocument ThreePages(bool facing = false, int pages = 3)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageCount = pages, FacingPages = facing });
        for (var i = 0; i < pages; i++)
        {
            document.Pages[i].AddText(new Rect(50, 60, 200, 40), "Página " + (i + 1), name: "texto" + (i + 1));
        }
        return document;
    }

    /// <summary>The text on each page, in page order.</summary>
    private static List<string> TextsByPage(IdmlDocument document) =>
        document.Pages.Select(page => string.Join("|", page.PageItems.OfType<TextFrame>().Select(frame => frame.Story.Text))).ToList();

    private static void AssertValid(IdmlDocument document)
    {
        var reloaded = IdmlDocument.Open(document.ToArray());
        var result = IdmlValidator.Validate(reloaded);
        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Move_page_takes_its_items_and_renumbers()
    {
        using var document = ThreePages();

        document.MovePage(2, 0);

        Assert.Equal(["Página 3", "Página 1", "Página 2"], TextsByPage(document));
        Assert.Equal(["1", "2", "3"], document.Pages.Select(page => page.Name));
        var frame = document.FindPageItem("texto3")!;
        Assert.Equal(0, frame.Page!.Index);
        Assert.Equal(50, frame.Bounds.X, 3);
        Assert.Equal(60, frame.Bounds.Y, 3);
        AssertValid(document);
    }

    [Fact]
    public void Delete_and_insert_pages_keep_sections_and_spreads_consistent()
    {
        using var document = ThreePages();

        document.DeletePage(1);
        Assert.Equal(["Página 1", "Página 3"], TextsByPage(document));
        Assert.Null(document.FindPageItem("texto2"));
        Assert.Equal(2, document.Spreads.Count);
        var inserted = document.InsertPage(1);

        Assert.Equal(1, inserted.Index);
        Assert.Equal(["Página 1", "", "Página 3"], TextsByPage(document));
        var section = document.DesignMap.Elements("Section").Single();
        Assert.Equal("3", section.Attribute("Length")!.Value);
        Assert.Equal(document.Pages[0].Id, section.Attribute("PageStart")!.Value);
        Assert.Throws<IdmlException>(() => document.DeletePage(7));
        AssertValid(document);
    }

    [Fact]
    public void Facing_pages_are_laid_out_again_in_spreads()
    {
        using var document = ThreePages(facing: true, pages: 4);

        document.ReorderPages([3, 2, 1, 0]);

        Assert.Equal(["Página 4", "Página 3", "Página 2", "Página 1"], TextsByPage(document));
        Assert.Equal([1, 2, 1], document.Spreads.Select(spread => spread.Pages.Count));
        Assert.True(document.Pages[1].IsLeftPage);
        Assert.False(document.Pages[2].IsLeftPage);
        Assert.Equal(60, document.FindPageItem("texto1")!.Bounds.Y, 3);
        Assert.Equal(50, document.FindPageItem("texto1")!.Bounds.X, 3);
        Assert.Throws<IdmlException>(() => document.ReorderPages([0, 0, 1, 2]));
        AssertValid(document);
    }

    [Fact]
    public void Reordering_a_real_export_keeps_it_valid()
    {
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        var before = TextsByPage(document);

        document.MovePage(1, 0);

        Assert.Equal([before[1], before[0]], TextsByPage(document));
        AssertValid(document);
    }

    [Fact]
    public void Duplicate_copies_a_frame_and_its_story()
    {
        using var document = ThreePages();
        var frame = document.FindPageItem("texto1")!;

        var copy = (TextFrame)document.DuplicatePageItem(frame, document.Pages[2], offsetX: 10);

        Assert.NotEqual(frame.Id, copy.Id);
        Assert.NotEqual(((TextFrame)frame).StoryId, copy.StoryId);
        Assert.Equal("Página 1", copy.Story.Text);
        Assert.Equal(2, copy.Page!.Index);
        Assert.Equal(60, copy.Bounds.X, 3);
        copy.Story.SetText("Copia");
        Assert.Equal("Página 1", ((TextFrame)frame).Story.Text);
        AssertValid(document);
    }

    [Fact]
    public void Paragraph_and_character_styles_apply_to_one_paragraph_or_run()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddText(new Rect(36, 36, 300, 200), "Primero\nSegundo\nTercero");
        var title = document.Styles.AddParagraphStyle("Título", new ParagraphFormat { PointSize = 20 });
        var bold = document.Styles.AddCharacterStyle("Negrita", new CharacterFormat { FontStyle = "Bold" });
        var story = frame.Story;

        story.ApplyParagraphStyle(1, title);
        var paragraphs = story.Paragraphs();

        Assert.Equal(["Primero", "Segundo", "Tercero"], paragraphs.Select(paragraph => paragraph.Text));
        Assert.Equal("Título", paragraphs[1].ParagraphStyle);
        Assert.NotEqual("Título", paragraphs[0].ParagraphStyle);
        Assert.NotEqual("Título", paragraphs[2].ParagraphStyle);
        Assert.Equal("Primero\nSegundo\nTercero", story.Text);

        var run = paragraphs[2].FirstRun;
        story.ApplyCharacterStyle(run, bold);
        Assert.Equal("Negrita", story.ContentRuns[run].CharacterStyle);
        Assert.NotEqual("Negrita", story.ContentRuns[0].CharacterStyle);
        Assert.Equal("Primero\nSegundo\nTercero", story.Text);
        Assert.Throws<IdmlException>(() => story.ApplyParagraphStyle(5, title));
        AssertValid(document);
    }

    [Fact]
    public void Styles_apply_inside_real_runs()
    {
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        var story = document.Stories.First(candidate => candidate.Paragraphs().Count(paragraph => paragraph.Text.Length > 0) >= 2);
        var text = story.Text;
        var style = document.Styles.AddParagraphStyle("Nuevo estilo");
        var target = story.Paragraphs().First(paragraph => paragraph.Index > 0 && paragraph.Text.Length > 0).Index;

        story.ApplyParagraphStyle(target, style);

        Assert.Equal(text, story.Text);
        Assert.Equal("Nuevo estilo", story.Paragraphs()[target].ParagraphStyle);
        AssertValid(document);
    }

    // ---- tools ------------------------------------------------------------------------------------

    private static string Created(McpTestClient client)
    {
        var path = TestFiles.TempPath(".idml");
        client.CallText("idml_create", new JsonObject
        {
            ["spec"] = JsonNode.Parse("""
                {"pages":[
                  {"items":[{"type":"text","name":"titulo","x":36,"y":36,"width":300,"height":40,"text":"Uno\nDos"},
                            {"type":"table","name":"tabla","x":36,"y":100,"width":300,"height":80,"rows":[["A","B"],["1","2"]]}]},
                  {"items":[{"type":"rectangle","name":"caja","x":10,"y":10,"width":50,"height":50}]}]}
                """),
            ["outPath"] = path,
        });
        return path;
    }

    [Fact]
    public void Item_tools_move_resize_relayer_duplicate_and_delete()
    {
        var client = new McpTestClient();
        var path = Created(client);
        var moved = TestFiles.TempPath(".idml");

        var edited = client.CallJson("idml_edit_item", new JsonObject { ["path"] = path, ["name"] = "titulo", ["x"] = 100, ["height"] = 80, ["layer"] = "Textos", ["createLayer"] = true, ["outPath"] = moved });
        var duplicated = client.CallJson("idml_duplicate_item", new JsonObject { ["path"] = moved, ["name"] = "caja", ["pageIndex"] = 0, ["offsetY"] = 20, ["inPlace"] = true });
        var onPageOne = client.CallJson("idml_edit_item", new JsonObject { ["path"] = moved, ["itemId"] = duplicated.GetProperty("id").GetString(), ["pageIndex"] = 1, ["inPlace"] = true });
        client.CallText("idml_delete_item", new JsonObject { ["path"] = moved, ["name"] = "caja", ["itemId"] = duplicated.GetProperty("id").GetString(), ["inPlace"] = true });

        Assert.Equal(100, edited.GetProperty("x").GetDouble(), 3);
        Assert.Equal(36, edited.GetProperty("y").GetDouble(), 3);
        Assert.Equal(300, edited.GetProperty("width").GetDouble(), 3);
        Assert.Equal(80, edited.GetProperty("height").GetDouble(), 3);
        Assert.Equal("Textos", edited.GetProperty("layer").GetString());
        Assert.Equal(0, duplicated.GetProperty("pageIndex").GetInt32());
        Assert.Equal(30, duplicated.GetProperty("y").GetDouble(), 3);
        Assert.Equal(1, onPageOne.GetProperty("pageIndex").GetInt32());
        Assert.Equal(30, onPageOne.GetProperty("y").GetDouble(), 3);
        using var document = IdmlDocument.Open(moved);
        Assert.Single(document.FindPageItems("caja"));
        Assert.Contains("No layer", client.CallError("idml_edit_item", new JsonObject { ["path"] = moved, ["name"] = "titulo", ["layer"] = "Nada", ["inPlace"] = true }));
        Assert.Contains("No page item", client.CallError("idml_delete_item", new JsonObject { ["path"] = moved, ["itemId"] = "nope", ["inPlace"] = true }));
        Assert.True(client.CallJson("idml_validate", new JsonObject { ["path"] = moved }).GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public void Page_tools_add_move_and_delete()
    {
        var client = new McpTestClient();
        var path = Created(client);

        var added = client.CallJson("idml_add_page", new JsonObject { ["path"] = path, ["at"] = 0, ["inPlace"] = true });
        var moved = client.CallJson("idml_move_page", new JsonObject { ["path"] = path, ["from"] = 0, ["to"] = 2, ["inPlace"] = true });
        var reordered = client.CallJson("idml_move_page", new JsonObject { ["path"] = path, ["order"] = new JsonArray(1, 0, 2), ["inPlace"] = true });
        var deleted = client.CallJson("idml_delete_page", new JsonObject { ["path"] = path, ["pageIndex"] = 2, ["inPlace"] = true });

        Assert.Equal(0, added.GetProperty("pageIndex").GetInt32());
        Assert.Equal(3, added.GetProperty("pageCount").GetInt32());
        Assert.Equal(2, moved.GetProperty("pageIndex").GetInt32());
        Assert.Equal(3, reordered.GetProperty("pageCount").GetInt32());
        Assert.Equal(2, deleted.GetProperty("pageCount").GetInt32());
        using var document = IdmlDocument.Open(path);
        Assert.Equal(1, document.FindPageItem("titulo")!.Page!.Index);
        Assert.Equal(0, document.FindPageItem("caja")!.Page!.Index);
        Assert.Contains("does not exist", client.CallError("idml_delete_page", new JsonObject { ["path"] = path, ["pageIndex"] = 9, ["inPlace"] = true }));
        Assert.True(client.CallJson("idml_validate", new JsonObject { ["path"] = path }).GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public void Style_colour_and_table_tools()
    {
        var client = new McpTestClient();
        var path = Created(client);
        using (var created = IdmlDocument.Open(path))
        {
            Assert.NotNull(created.FindPageItem("titulo"));
        }
        string StoryOf(string name)
        {
            using var document = IdmlDocument.Open(path);
            return ((TextFrame)document.FindPageItem(name)!).StoryId;
        }
        var title = StoryOf("titulo");
        var table = StoryOf("tabla");

        client.CallText("idml_add_color", new JsonObject { ["path"] = path, ["name"] = "Marca", ["cmyk"] = new JsonArray(75, 45, 0, 10), ["inPlace"] = true });
        client.CallText("idml_define_style", new JsonObject { ["path"] = path, ["kind"] = "paragraph", ["name"] = "Destacado", ["size"] = 18, ["color"] = "Marca", ["inPlace"] = true });
        client.CallText("idml_define_style", new JsonObject { ["path"] = path, ["kind"] = "character", ["name"] = "Fuerte", ["fontStyle"] = "Bold", ["inPlace"] = true });
        client.CallText("idml_apply_style", new JsonObject { ["path"] = path, ["storyId"] = title, ["paragraphIndex"] = 1, ["paragraphStyle"] = "Destacado", ["inPlace"] = true });
        client.CallText("idml_apply_style", new JsonObject { ["path"] = path, ["storyId"] = title, ["runIndex"] = 0, ["characterStyle"] = "Fuerte", ["inPlace"] = true });
        var tableResult = client.CallJson("idml_edit_table", new JsonObject
        {
            ["path"] = path,
            ["storyId"] = table,
            ["operations"] = JsonNode.Parse("""[{"op":"setCell","row":1,"column":1,"text":"dos"},{"op":"addRow","values":["3","tres"]},{"op":"setColumnWidth","column":0,"width":120},{"op":"setRowHeight","row":0,"height":30}]"""),
            ["inPlace"] = true,
        });
        var story = client.CallJson("idml_get_story", new JsonObject { ["path"] = path, ["storyId"] = title, ["includeParagraphs"] = true });

        Assert.Equal(3, tableResult.GetProperty("rows").GetInt32());
        Assert.Equal(120, tableResult.GetProperty("columnWidths")[0].GetDouble(), 3);
        Assert.Equal("Destacado", story.GetProperty("paragraphs")[1].GetProperty("style").GetString());
        Assert.Equal("Fuerte", story.GetProperty("runs")[0].GetProperty("characterStyle").GetString());
        using var document = IdmlDocument.Open(path);
        Assert.Equal("dos", document.RequireStory(table).Tables[0].Cell(1, 1).Text);
        Assert.Equal("tres", document.RequireStory(table).Tables[0].Cell(2, 1).Text);
        Assert.Contains("No paragraph style", client.CallError("idml_apply_style", new JsonObject { ["path"] = path, ["storyId"] = title, ["paragraphIndex"] = 0, ["paragraphStyle"] = "Nada", ["inPlace"] = true }));
        Assert.Contains("Row 7", client.CallError("idml_edit_table", new JsonObject { ["path"] = path, ["storyId"] = table, ["operations"] = JsonNode.Parse("""[{"op":"setRow","row":7,"values":["x"]}]"""), ["inPlace"] = true }));
        Assert.True(client.CallJson("idml_validate", new JsonObject { ["path"] = path }).GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public void Find_locates_items_and_stories()
    {
        var client = new McpTestClient();
        var byTag = client.CallJson("idml_find", new JsonObject { ["path"] = TestFiles.Article, ["tag"] = "headline" });
        var byText = client.CallJson("idml_find", new JsonObject { ["path"] = TestFiles.Article, ["text"] = "headline here" });
        var byKind = client.CallJson("idml_find", new JsonObject { ["path"] = TestFiles.Article, ["kind"] = "rectangle", ["maxResults"] = 1 });

        Assert.Contains(byTag.GetProperty("items").EnumerateArray(), item => item.GetProperty("kind").GetString() == "TextFrame");
        var story = Assert.Single(byText.GetProperty("stories").EnumerateArray());
        Assert.Equal(0, story.GetProperty("matches")[0].GetProperty("runIndex").GetInt32());
        Assert.Equal([0], story.GetProperty("pages").EnumerateArray().Select(page => page.GetInt32()));
        Assert.True(byKind.GetProperty("items").GetArrayLength() <= 1);
        Assert.Contains("at least one", client.CallError("idml_find", new JsonObject { ["path"] = TestFiles.Article }));
    }

    [Fact]
    public void Summary_filters_pages_and_sections()
    {
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        var full = IdmlJson.SummarizeToJson(document);
        var pageOne = IdmlJson.Summarize(document, new SummaryOptions { Pages = [1] });
        var storiesOnly = JsonElement.Parse(IdmlJson.SummarizeToJson(document, new SummaryOptions { Sections = ["stories"] }));

        Assert.Equal(1, Assert.Single(pageOne.Pages!).Index);
        Assert.Null(pageOne.OffPage);
        Assert.All(pageOne.Links!, link => Assert.Equal(1, link.PageIndex));
        Assert.True(pageOne.Stories!.Count < IdmlJson.Summarize(document).Stories!.Count);
        Assert.False(storiesOnly.TryGetProperty("pages", out _));
        Assert.False(storiesOnly.TryGetProperty("links", out _));
        Assert.False(storiesOnly.TryGetProperty("paragraphStyles", out _));
        Assert.True(storiesOnly.GetProperty("stories").GetArrayLength() > 0);
        Assert.Equal(full, IdmlJson.SummarizeToJson(document, new SummaryOptions { Sections = [.. SummaryOptions.SectionNames] }));
        Assert.Throws<IdmlException>(() => IdmlJson.Summarize(document, new SummaryOptions { Sections = ["nope"] }));
        Assert.Throws<IdmlException>(() => IdmlJson.Summarize(document, new SummaryOptions { Pages = [5] }));
    }

    [Fact]
    public void Real_sample_page_summary_is_much_smaller()
    {
        if (TestFiles.RealSample is not { } sample)
        {
            return; // real_samples_fichas is not in git.
        }
        var client = new McpTestClient();

        var full = client.CallText("idml_summarize", new JsonObject { ["path"] = sample });
        var page = client.CallText("idml_summarize", new JsonObject { ["path"] = sample, ["pages"] = new JsonArray(0), ["sections"] = new JsonArray("pages"), ["maxTextLength"] = 40 });

        Assert.True(page.Length * 4 < full.Length, $"{page.Length} vs {full.Length}");
    }
}
