using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIdml.Dom;

namespace CoreIdml.Tests;

/// <summary>Page previews, exact text fit and batched edits through the MCP tools and the CLI.</summary>
public class PreviewFitAndBatchTests
{
    private static string Overset(out string storyId)
    {
        var path = TestFiles.TempPath(".idml");
        using var document = IdmlDocument.Create();
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var frame = document.Pages[0].AddText(new Rect(50, 50, 150, 30), string.Join(" ", Enumerable.Repeat("Estado de conservación de la superficie.", 8)), style, name: "largo");
        document.Pages[0].AddText(new Rect(50, 300, 150, 30), "Corto", style, name: "corto");
        document.Pages[0].AddRectangle(new Rect(300, 50, 100, 100), new ShapeStyle { Fill = Swatch.Black });
        storyId = frame.StoryId;
        document.Save(path);
        return path;
    }

    [Fact]
    public void Render_page_returns_a_png_image()
    {
        var client = new McpTestClient();
        var result = client.Call("idml_render_page", new JsonObject { ["path"] = Overset(out _), ["dpi"] = 50, ["fonts"] = new JsonArray(TestFiles.Fonts) });

        Assert.False(result.TryGetProperty("isError", out _), McpTestClient.Text(result));
        var image = result.GetProperty("content").EnumerateArray().Single(content => content.GetProperty("type").GetString() == "image");
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        var png = Convert.FromBase64String(image.GetProperty("data").GetString()!);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        var info = result.GetProperty("structuredContent");
        Assert.True(info.GetProperty("rendered").GetBoolean());
        Assert.Equal(Math.Round(595.28 * 50 / 72), info.GetProperty("width").GetInt32(), 1);
        Assert.Single(info.GetProperty("oversetStories").EnumerateArray());
        Assert.Contains("does not exist", client.CallError("idml_render_page", new JsonObject { ["path"] = Overset(out _), ["pageIndex"] = 3 }));
    }

    [Fact]
    public void Cli_render_writes_the_png()
    {
        var output = TestFiles.TempPath(".png");

        var (exitCode, json, error, _) = CliRunner.Run("render", Overset(out _), "--dpi", "40", "-o", output);
        var refused = CliRunner.Run("render", Overset(out _));

        Assert.True(exitCode == 0, error);
        Assert.True(JsonElement.Parse(json).GetProperty("rendered").GetBoolean());
        Assert.Equal((byte)'P', File.ReadAllBytes(output)[1]);
        Assert.Equal(2, refused.ExitCode);
    }

    [Fact]
    public void Check_fit_lists_overflowing_stories_with_lines_and_points()
    {
        var client = new McpTestClient();
        var path = Overset(out var storyId);

        var fit = client.CallJson("idml_check_fit", new JsonObject { ["path"] = path, ["fonts"] = new JsonArray(TestFiles.Fonts), ["useSystemFonts"] = false });
        var all = client.CallJson("idml_check_fit", new JsonObject { ["path"] = path, ["all"] = true, ["fonts"] = new JsonArray(TestFiles.Fonts), ["useSystemFonts"] = false });

        Assert.Equal(2, fit.GetProperty("checked").GetInt32());
        Assert.Equal(1, fit.GetProperty("overset").GetInt32());
        var story = Assert.Single(fit.GetProperty("stories").EnumerateArray());
        Assert.Equal(storyId, story.GetProperty("storyId").GetString());
        Assert.True(story.GetProperty("overflowLines").GetInt32() >= 2);
        Assert.True(story.GetProperty("overflowPoints").GetDouble() > 12);
        Assert.Equal(2, all.GetProperty("stories").GetArrayLength());
        Assert.Empty(fit.GetProperty("missingFonts").EnumerateArray());

        // Growing the frame by the reported points makes it fit.
        var grown = TestFiles.TempPath(".idml");
        client.CallText("idml_edit_item", new JsonObject { ["path"] = path, ["name"] = "largo", ["height"] = 30 + story.GetProperty("overflowPoints").GetDouble() + 0.5, ["outPath"] = grown });
        Assert.Equal(0, client.CallJson("idml_check_fit", new JsonObject { ["path"] = grown, ["fonts"] = new JsonArray(TestFiles.Fonts), ["useSystemFonts"] = false }).GetProperty("overset").GetInt32());
    }

    [Fact]
    public void Batch_applies_every_operation_or_none()
    {
        var client = new McpTestClient();
        var path = Overset(out var storyId);
        var before = File.ReadAllBytes(path);
        var output = TestFiles.TempPath(".idml");
        JsonArray Operations(params JsonObject[] operations) => [.. operations];

        var dryRun = client.CallJson("idml_batch", new JsonObject
        {
            ["path"] = path,
            ["dryRun"] = true,
            ["operations"] = Operations(
                new JsonObject { ["tool"] = "idml_set_text", ["arguments"] = new JsonObject { ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "Breve" } },
                new JsonObject { ["tool"] = "idml_edit_item", ["arguments"] = new JsonObject { ["name"] = "corto", ["y"] = 400 } }),
        });
        var failed = client.CallError("idml_batch", new JsonObject
        {
            ["path"] = path,
            ["inPlace"] = true,
            ["operations"] = Operations(
                new JsonObject { ["tool"] = "idml_set_text", ["arguments"] = new JsonObject { ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "Breve" } },
                new JsonObject { ["tool"] = "idml_delete_item", ["arguments"] = new JsonObject { ["itemId"] = "nope" } }),
        });
        var done = client.CallJson("idml_batch", new JsonObject
        {
            ["path"] = path,
            ["outPath"] = output,
            ["operations"] = Operations(
                new JsonObject { ["tool"] = "idml_set_text", ["arguments"] = new JsonObject { ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "Breve" } },
                new JsonObject { ["tool"] = "idml_add_page" },
                new JsonObject { ["tool"] = "idml_duplicate_item", ["arguments"] = new JsonObject { ["name"] = "corto", ["pageIndex"] = 1 } }),
        });

        Assert.True(dryRun.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, dryRun.GetProperty("result").GetProperty("operations").GetInt32());
        Assert.Contains(dryRun.GetProperty("diff").GetProperty("changes").EnumerateArray(), change => change.GetProperty("after").GetString() == "Breve");
        Assert.Contains("Operation 1 (idml_delete_item)", failed);
        Assert.Contains("nothing was saved", failed);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(3, done.GetProperty("results").GetArrayLength());
        using var document = IdmlDocument.Open(output);
        Assert.Equal("Breve", document.RequireStory(storyId).Text);
        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(2, document.FindPageItems("corto").Count());
        Assert.Contains("not an edit tool", client.CallError("idml_batch", new JsonObject { ["path"] = path, ["dryRun"] = true, ["operations"] = Operations(new JsonObject { ["tool"] = "idml_summarize" }) }));
        Assert.Contains("belongs to the batch", client.CallError("idml_batch", new JsonObject
        {
            ["path"] = path,
            ["dryRun"] = true,
            ["operations"] = Operations(new JsonObject { ["tool"] = "idml_add_page", ["arguments"] = new JsonObject { ["outPath"] = output } }),
        }));
    }

    [Fact]
    public void Real_sample_preview_and_fit()
    {
        if (TestFiles.RealSample is not { } sample)
        {
            return; // real_samples_fichas is not in git.
        }
        var client = new McpTestClient();
        var fonts = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sample)!)!, "src", "Forms.Reports", "Fonts");

        var render = client.Call("idml_render_page", new JsonObject { ["path"] = sample, ["dpi"] = 30, ["fonts"] = new JsonArray(fonts) });
        var fit = client.CallJson("idml_check_fit", new JsonObject { ["path"] = sample, ["fonts"] = new JsonArray(fonts) });

        Assert.False(render.TryGetProperty("isError", out _), McpTestClient.Text(render));
        Assert.True(fit.GetProperty("checked").GetInt32() > 10);
    }
}
