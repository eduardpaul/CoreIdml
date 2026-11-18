using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIdml.Cli;

namespace CoreIdml.Tests;

/// <summary>The MCP server (idmlkit's idml-mcp equivalent) and the command line, driven in-process.</summary>
public class CliAndMcpTests
{
    [Fact]
    public void Initialize_and_list_tools()
    {
        var client = new McpTestClient();
        var list = client.Request("tools/list");

        Assert.Equal("2025-06-18", client.Initialize.GetProperty("protocolVersion").GetString());
        Assert.Equal("coreidml", client.Initialize.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Contains("idml_summarize", client.Initialize.GetProperty("instructions").GetString());
        var tools = list.GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(tool => tool.GetProperty("name").GetString()).ToList();
        Assert.Contains("idml_summarize", names);
        Assert.Contains("idml_set_text", names);
        Assert.Contains("idml_relink_image", names);
        Assert.Contains("idml_import_xml", names);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(tools, tool =>
        {
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(tool.GetProperty("title").GetString()));
            var required = tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(item => item.GetString()!);
            var properties = tool.GetProperty("inputSchema").GetProperty("properties");
            Assert.All(required, name => Assert.True(properties.TryGetProperty(name, out _), $"{tool.GetProperty("name")}: required '{name}' is not a property"));
            if (tool.TryGetProperty("outputSchema", out var output))
            {
                Assert.Equal("object", output.GetProperty("type").GetString());
            }
        });
    }

    [Fact]
    public void Tool_annotations_mark_read_only_and_destructive_tools()
    {
        var tools = new McpTestClient().Request("tools/list").GetProperty("tools").EnumerateArray()
            .ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => tool.GetProperty("annotations"));

        Assert.True(tools["idml_summarize"].GetProperty("readOnlyHint").GetBoolean());
        Assert.True(tools["idml_summarize"].GetProperty("idempotentHint").GetBoolean());
        Assert.False(tools["idml_summarize"].GetProperty("destructiveHint").GetBoolean());
        Assert.False(tools["idml_set_text"].GetProperty("readOnlyHint").GetBoolean());
        Assert.True(tools["idml_set_text"].GetProperty("destructiveHint").GetBoolean());
        Assert.False(tools["idml_create"].GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tools["idml_create"].GetProperty("destructiveHint").GetBoolean());
        Assert.All(tools.Values, annotations =>
        {
            Assert.False(string.IsNullOrEmpty(annotations.GetProperty("title").GetString()));
            Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
        });
    }

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("1999-01-01", "2025-11-25")]
    [InlineData("2099-12-31", "2025-11-25")]
    public void Initialize_negotiates_the_protocol_version(string requested, string expected)
    {
        var client = new McpTestClient(protocolVersion: requested);

        Assert.Equal(expected, client.Initialize.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void Older_protocol_versions_get_no_newer_fields()
    {
        var tools = new McpTestClient(protocolVersion: "2024-11-05").Request("tools/list").GetProperty("tools").EnumerateArray().ToList();

        Assert.All(tools, tool =>
        {
            Assert.False(tool.TryGetProperty("annotations", out _));
            Assert.False(tool.TryGetProperty("outputSchema", out _));
            Assert.False(tool.TryGetProperty("title", out _));
        });
    }

    [Fact]
    public void Summarize_get_story_and_set_text_through_tools()
    {
        var path = CopyOf(TestFiles.Article);
        var output = TestFiles.TempPath(".idml");
        var client = new McpTestClient();

        var summary = client.CallJson("idml_summarize", new JsonObject { ["path"] = path });
        var storyId = summary.GetProperty("pages")[0].GetProperty("items").EnumerateArray()
            .First(item => item.TryGetProperty("tag", out var tag) && tag.GetString() == "headline").GetProperty("storyId").GetString()!;
        var story = client.CallJson("idml_get_story", new JsonObject { ["path"] = path, ["storyId"] = storyId });
        client.CallText("idml_set_text", new JsonObject { ["path"] = path, ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "Nuevo titular", ["outPath"] = output });

        Assert.Equal("THE HEADLINE HERE", story.GetProperty("runs")[0].GetProperty("text").GetString());
        using var edited = IdmlDocument.Open(output);
        Assert.Equal("Nuevo titular", edited.RequireStory(storyId).Text);
        using var untouched = IdmlDocument.Open(path);
        Assert.Equal("THE HEADLINE HERE", untouched.RequireStory(storyId).Text);
    }

    [Fact]
    public void Edits_need_an_output_path_or_in_place()
    {
        var path = CopyOf(TestFiles.Article);
        var before = File.ReadAllBytes(path);
        var client = new McpTestClient();
        var storyId = HeadlineStory(path);

        var error = client.CallError("idml_set_text", new JsonObject { ["path"] = path, ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "x" });
        Assert.Contains("outPath", error);
        Assert.Contains("inPlace", error);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains("not both", client.CallError("idml_set_text", new JsonObject { ["path"] = path, ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "x", ["inPlace"] = true, ["outPath"] = TestFiles.TempPath(".idml") }));

        client.CallText("idml_set_text", new JsonObject { ["path"] = path, ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "En el sitio", ["inPlace"] = true });
        using var edited = IdmlDocument.Open(path);
        Assert.Equal("En el sitio", edited.RequireStory(storyId).Text);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Dry_run_reports_the_change_and_saves_nothing()
    {
        var path = CopyOf(TestFiles.Article);
        var before = File.ReadAllBytes(path);
        var storyId = HeadlineStory(path);

        var report = new McpTestClient().CallJson("idml_set_text", new JsonObject { ["path"] = path, ["storyId"] = storyId, ["runIndex"] = 0, ["text"] = "Prueba", ["dryRun"] = true });

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.True(report.GetProperty("result").GetProperty("ok").GetBoolean());
        var change = Assert.Single(report.GetProperty("diff").GetProperty("changes").EnumerateArray());
        Assert.Equal("run-changed", change.GetProperty("kind").GetString());
        Assert.Equal("Prueba", change.GetProperty("after").GetString());
        Assert.Contains(report.GetProperty("changedParts").EnumerateArray(), part => part.GetString()!.StartsWith("Stories/", StringComparison.Ordinal));
    }

    [Fact]
    public void Relative_paths_are_refused_unless_a_root_is_set()
    {
        var path = CopyOf(TestFiles.Article);
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);

        var error = new McpTestClient().CallError("idml_validate", new JsonObject { ["path"] = name });
        Assert.Contains("absolute", error);
        Assert.Contains(Path.GetFullPath(name), error);
        Assert.Contains("--root", error);

        var rooted = new McpTestClient(root: directory);
        Assert.True(rooted.CallJson("idml_validate", new JsonObject { ["path"] = name }).GetProperty("isValid").GetBoolean());
        var missing = rooted.CallError("idml_validate", new JsonObject { ["path"] = "nope.idml" });
        Assert.Contains(Path.Combine(directory, "nope.idml"), missing);
    }

    [Fact]
    public void Relative_paths_resolve_against_the_clients_roots()
    {
        var path = CopyOf(TestFiles.Article);
        var directory = Path.GetDirectoryName(path)!;
        var server = new McpServer();
        server.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"roots":{"listChanged":true}}}}""");

        var request = McpTestClient.Parse(Assert.Single(server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")));
        Assert.Equal("roots/list", request.GetProperty("method").GetString());
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request.GetProperty("id").GetString(),
            ["result"] = new JsonObject { ["roots"] = new JsonArray(new JsonObject { ["uri"] = new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri, ["name"] = "tests" }) },
        };
        Assert.Empty(server.Handle(response.ToJsonString()));
        var callRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "idml_validate", ["arguments"] = new JsonObject { ["path"] = Path.GetFileName(path) } },
        };
        var call = McpTestClient.Parse(Assert.Single(server.Handle(callRequest.ToJsonString())));

        Assert.Equal(directory, server.Paths.ClientRoots[0].TrimEnd(Path.DirectorySeparatorChar));
        Assert.False(call.GetProperty("result").TryGetProperty("isError", out _), call.ToString());
    }

    [Fact]
    public void Relink_accepts_link_id_and_its_link_self_alias()
    {
        var path = CopyOf(TestFiles.Article);
        var client = new McpTestClient();
        var linkId = client.CallJson("idml_summarize", new JsonObject { ["path"] = path }).GetProperty("links")[0].GetProperty("id").GetString()!;
        var first = TestFiles.TempPath(".idml");
        var second = TestFiles.TempPath(".idml");

        client.CallText("idml_relink_image", new JsonObject { ["path"] = path, ["linkId"] = linkId, ["newUri"] = Path.Combine(Path.GetTempPath(), "una.jpg"), ["outPath"] = first });
        client.CallText("idml_relink_image", new JsonObject { ["path"] = path, ["linkSelf"] = linkId, ["newUri"] = Path.Combine(Path.GetTempPath(), "otra.jpg"), ["outPath"] = second });

        using var one = IdmlDocument.Open(first);
        using var two = IdmlDocument.Open(second);
        Assert.EndsWith("una.jpg", one.Links.Single(link => link.Id == linkId).Uri, StringComparison.Ordinal);
        Assert.EndsWith("otra.jpg", two.Links.Single(link => link.Id == linkId).Uri, StringComparison.Ordinal);
        Assert.Contains("linkId", client.CallError("idml_relink_image", new JsonObject { ["path"] = path, ["newUri"] = Path.Combine(Path.GetTempPath(), "x.jpg"), ["inPlace"] = true }));
        Assert.Contains("No link", client.CallError("idml_relink_image", new JsonObject { ["path"] = path, ["linkId"] = "nope", ["newUri"] = Path.Combine(Path.GetTempPath(), "x.jpg"), ["inPlace"] = true }));
    }

    [Fact]
    public void Tool_errors_come_back_in_band()
    {
        var client = new McpTestClient();
        var nowhere = TestFiles.Nowhere("such.idml");
        var error = client.CallError("idml_set_text", new JsonObject { ["path"] = nowhere, ["storyId"] = "u1", ["runIndex"] = 0, ["text"] = "x", ["inPlace"] = true });

        Assert.Contains(nowhere, error);
        Assert.Contains("not found", error);
        Assert.Contains("must be an integer", client.CallError("idml_set_text", new JsonObject { ["path"] = CopyOf(TestFiles.Article), ["storyId"] = HeadlineStory(TestFiles.Article), ["runIndex"] = "uno", ["text"] = "x", ["inPlace"] = true }));
        Assert.Equal(-32601, client.Send("nope").GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, client.Send("tools/call", new JsonObject { ["name"] = "idml_nope" }).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32700, McpTestClient.Parse(client.Server.Handle("{not json")[0]).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Structured_content_matches_the_text_result()
    {
        var client = new McpTestClient();
        var result = client.Call("idml_validate", new JsonObject { ["path"] = TestFiles.Article });

        Assert.Equal(McpTestClient.Text(result), result.GetProperty("structuredContent").GetRawText());
        var old = new McpTestClient(protocolVersion: "2025-03-26").Call("idml_validate", new JsonObject { ["path"] = TestFiles.Article });
        Assert.False(old.TryGetProperty("structuredContent", out _));
    }

    [Fact]
    public void Prompts_are_listed_and_rendered()
    {
        var client = new McpTestClient();
        var prompts = client.Request("prompts/list").GetProperty("prompts").EnumerateArray().Select(prompt => prompt.GetProperty("name").GetString()).ToList();
        var review = client.Request("prompts/get", new JsonObject { ["name"] = "review_document", ["arguments"] = new JsonObject { ["path"] = "/docs/ficha.idml" } });

        Assert.Contains("fill_template", prompts);
        Assert.Contains("review_document", prompts);
        var text = review.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.Contains("/docs/ficha.idml", text);
        Assert.Contains("idml_check_fit", text);
        Assert.Equal(-32602, client.Send("prompts/get", new JsonObject { ["name"] = "review_document" }).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Create_fill_merge_and_diff_through_tools()
    {
        var created = TestFiles.TempPath(".idml");
        var filled = TestFiles.TempPath(".idml");
        var merged = TestFiles.TempPath(".idml");
        var client = new McpTestClient();
        client.CallText("idml_create", new JsonObject
        {
            ["spec"] = JsonNode.Parse("""{"pages":[{"items":[{"type":"text","name":"nombre","x":36,"y":36,"width":300,"height":30,"text":"{{nombre}}"}]}]}"""),
            ["outPath"] = created,
        });
        var fill = client.CallJson("idml_fill", new JsonObject { ["path"] = created, ["text"] = new JsonObject { ["nombre"] = "Ana" }, ["outPath"] = filled });
        var merge = client.CallJson("idml_merge", new JsonObject { ["path"] = filled, ["sources"] = new JsonArray(TestFiles.Article, new JsonObject { ["path"] = TestFiles.Editorial, ["pages"] = new JsonArray(1) }), ["outPath"] = merged });
        var diff = client.CallJson("idml_diff", new JsonObject { ["before"] = filled, ["after"] = merged });

        Assert.Equal(1, fill.GetProperty("filled").GetProperty("nombre").GetInt32());
        Assert.Equal(2, merge.GetProperty("pagesAdded").GetInt32());
        Assert.Equal(3, merge.GetProperty("pageCount").GetInt32());
        Assert.Contains(diff.GetProperty("changes").EnumerateArray(), change => change.GetProperty("kind").GetString() == "page-count");
    }

    [Fact]
    public void Apply_adds_styles_colours_and_pages()
    {
        var output = TestFiles.TempPath(".idml");
        var result = new McpTestClient().CallJson("idml_apply", new JsonObject
        {
            ["path"] = TestFiles.Article,
            ["spec"] = JsonNode.Parse("""{"colors":[{"name":"Marca","cmyk":[75,45,0,10]}],"paragraphStyles":[{"name":"Nota","size":8,"color":"Marca"}],"pages":[{"items":[{"type":"text","x":36,"y":36,"width":200,"height":40,"text":"Añadida","style":"Nota"}]}]}"""),
            ["outPath"] = output,
        });

        Assert.Equal(2, result.GetProperty("pageCount").GetInt32());
        using var document = IdmlDocument.Open(output);
        Assert.NotNull(document.Swatches.Find("Marca"));
        Assert.NotNull(document.Styles.FindParagraphStyle("Nota"));
        Assert.Contains(document.Stories, story => story.Text == "Añadida");
    }

    [Fact]
    public void Import_xml_takes_a_file_and_the_c_api_options()
    {
        var path = TestFiles.TempPath(".idml");
        File.Copy(TestFiles.ImportXmlTemplate, path);
        var xmlPath = TestFiles.TempPath(".xml");
        File.WriteAllText(xmlPath, TestFiles.Xml("article-1photo_import-xml.xml"));
        var output = TestFiles.TempPath(".idml");

        var report = new McpTestClient().CallJson("idml_import_xml", new JsonObject
        {
            ["path"] = path,
            ["xmlPath"] = xmlPath,
            ["relinkOnly"] = true,
            ["characterStyles"] = new JsonObject { ["b"] = CharacterStyleOf(path) },
            ["outPath"] = output,
        });

        Assert.True(report.GetProperty("updated").GetInt32() > 0);
    }

    [Fact]
    public void To_pdf_and_from_pdf_through_tools()
    {
        var pdf = TestFiles.TempPath(".pdf");
        var rebuilt = TestFiles.TempPath(".idml");
        var client = new McpTestClient();

        var export = client.CallJson("idml_to_pdf", new JsonObject { ["path"] = TestFiles.Editorial, ["outPath"] = pdf, ["pages"] = new JsonArray(0), ["fonts"] = new JsonArray(TestFiles.Fonts) });
        var import = client.CallJson("idml_from_pdf", new JsonObject { ["path"] = pdf, ["outPath"] = rebuilt });

        Assert.Equal(pdf, export.GetProperty("outPath").GetString());
        Assert.Equal(1, export.GetProperty("pageCount").GetInt32());
        Assert.True(export.TryGetProperty("missingFonts", out _));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4), StringComparison.Ordinal);
        Assert.Equal(rebuilt, import.GetProperty("outPath").GetString());
        Assert.True(import.GetProperty("textFrames").GetInt32() > 0);
        using var document = IdmlDocument.Open(rebuilt);
        Assert.Single(document.Pages);
        Assert.Contains("folder not found", client.CallError("idml_to_pdf", new JsonObject { ["path"] = TestFiles.Editorial, ["outPath"] = pdf, ["fonts"] = new JsonArray(TestFiles.Nowhere("fonts")) }));
    }

    // ---- command line --------------------------------------------------------------------------------

    [Fact]
    public void Cli_writes_utf8_json_whatever_the_text()
    {
        var path = DocumentWithText("43°16'38″N 2°56'02″W «Ñandú» “citas” — 5 m²");

        var (exitCode, output, error, bytes) = CliRunner.Run("summary", path);

        Assert.True(exitCode == 0, error);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "no BOM");
        Assert.Contains("43°16'38″N 2°56'02″W «Ñandú» “citas” — 5 m²", output);
        using var json = JsonDocument.Parse(output);
        Assert.Contains("43°16'38″N", json.RootElement.GetProperty("stories")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Cli_process_output_is_utf8_when_redirected()
    {
        var path = DocumentWithText("43°16'38″N");
        var cli = Path.Combine(AppContext.BaseDirectory, "CoreIdml.Cli.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("summary");
        start.ArgumentList.Add(path);
        // A non-UTF-8 locale must not change what is written.
        start.Environment["LANG"] = "C";
        start.Environment["LC_ALL"] = "C";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        using var json = JsonDocument.Parse(output);
        Assert.Contains("43°16'38″N", json.RootElement.GetProperty("stories")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Cli_real_sample_summary_parses_as_json()
    {
        var sample = TestFiles.RealSample;
        if (sample is null)
        {
            return; // real_samples_fichas is not in git; nothing to check without it.
        }

        var (exitCode, output, error, _) = CliRunner.Run("summary", sample);

        Assert.True(exitCode == 0, error);
        using var json = JsonDocument.Parse(output);
        Assert.Contains("43°16’38” ″N", output);
    }

    [Fact]
    public void A_file_that_is_not_an_idml_package_is_invalid_input()
    {
        var path = TestFiles.TempPath(".idml");
        File.WriteAllText(path, "not a zip package");

        var result = CliRunner.Run("summary", path);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Not a valid .idml", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_edits_need_out_or_in_place_and_use_exit_codes()
    {
        var path = CopyOf(TestFiles.Article);
        var storyId = HeadlineStory(path);
        var output = TestFiles.TempPath(".idml");

        var refused = CliRunner.Run("set-text", path, storyId, "0", "Hola");
        var written = CliRunner.Run("set-text", path, storyId, "0", "Hola", "-o", output);
        var dryRun = CliRunner.Run("set-text", path, storyId, "0", "Adiós", "--dry-run", "--pretty");
        var inPlace = CliRunner.Run("set-text", path, storyId, "0", "Aquí", "--in-place");

        Assert.Equal(2, refused.ExitCode);
        Assert.Contains("--in-place", Cli.Cli.Usage());
        Assert.Equal(0, written.ExitCode);
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Contains("\n  \"dryRun\": true", dryRun.Out.ReplaceLineEndings("\n"));
        Assert.Equal(0, inPlace.ExitCode);
        using (var document = IdmlDocument.Open(output))
        {
            Assert.Equal("Hola", document.RequireStory(storyId).Text);
        }
        using (var document = IdmlDocument.Open(path))
        {
            Assert.Equal("Aquí", document.RequireStory(storyId).Text);
        }
        Assert.Equal(3, CliRunner.Run("summary", TestFiles.Nowhere("such.idml")).ExitCode);
        Assert.Equal(3, CliRunner.Run("story", path, "nope").ExitCode);
        Assert.Equal(2, CliRunner.Run("summary").ExitCode);
        Assert.Equal(2, CliRunner.Run("summary", path, "--bogus").ExitCode);
        Assert.Equal(2, CliRunner.Run("frobnicate").ExitCode);
        Assert.Equal(2, CliRunner.Run().ExitCode);
    }

    [Fact]
    public void Cli_reads_stdin_and_writes_stdout_with_dash()
    {
        var input = File.ReadAllBytes(TestFiles.Article);
        var storyId = HeadlineStory(TestFiles.Article);

        var summary = CliRunner.RunWithInput(input, "summary", "-");
        var edited = CliRunner.RunWithInput(input, "set-text", "-", storyId, "0", "Por tubería", "-o", "-");
        var spec = CliRunner.RunWithInput("""{"pages":[{"items":[{"type":"text","x":10,"y":10,"width":100,"height":20,"text":"Desde stdin"}]}]}""", "create", "-", "-o", "-");

        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(1, JsonElement.Parse(summary.Out).GetProperty("pageCount").GetInt32());
        Assert.Equal(0, edited.ExitCode);
        Assert.Contains("\"ok\":true", edited.Error);
        using (var document = IdmlDocument.Open(edited.OutBytes))
        {
            Assert.Equal("Por tubería", document.RequireStory(storyId).Text);
        }
        Assert.Equal(0, spec.ExitCode);
        using (var document = IdmlDocument.Open(spec.OutBytes))
        {
            Assert.Contains(document.Stories, story => story.Text == "Desde stdin");
        }
    }

    [Fact]
    public void Cli_call_runs_any_tool_and_merge_takes_page_selections()
    {
        var output = TestFiles.TempPath(".idml");
        var validate = CliRunner.Run("call", "idml_validate", JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = TestFiles.Article }));
        var merge = CliRunner.Run("merge", TestFiles.Article, TestFiles.Editorial + "@1", TestFiles.Article, "-o", output);
        var tools = CliRunner.Run("tools");

        Assert.Equal(0, validate.ExitCode);
        Assert.True(JsonElement.Parse(validate.Out).GetProperty("isValid").GetBoolean());
        Assert.True(merge.ExitCode == 0, merge.Error);
        Assert.Equal(2, JsonElement.Parse(merge.Out).GetProperty("pagesAdded").GetInt32());
        Assert.Contains(JsonElement.Parse(tools.Out).EnumerateArray(), tool => tool.GetProperty("name").GetString() == "idml_merge");
        Assert.Equal(3, CliRunner.Run("call", "idml_nope").ExitCode);
    }

    [Fact]
    public void Cli_to_pdf_takes_repeated_font_folders()
    {
        var pdf = TestFiles.TempPath(".pdf");
        var emptyFolder = Path.Combine(Path.GetTempPath(), "idml-tests", "no-fonts");
        Directory.CreateDirectory(emptyFolder);

        var (exitCode, output, error, _) = CliRunner.Run("to-pdf", TestFiles.Article, "-o", pdf, "--fonts", TestFiles.Fonts, "--fonts", emptyFolder);

        Assert.True(exitCode == 0, error);
        Assert.Equal(pdf, JsonElement.Parse(output).GetProperty("outPath").GetString());
        Assert.True(new FileInfo(pdf).Length > 1000);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    internal static string CopyOf(string source)
    {
        var path = TestFiles.TempPath(".idml");
        File.Copy(source, path);
        return path;
    }

    internal static string HeadlineStory(string path)
    {
        using var document = IdmlDocument.Open(path);
        return document.Tags.AllNodes.First(node => node.Tag == "headline").BoundStory!.Id;
    }

    private static string CharacterStyleOf(string path)
    {
        using var document = IdmlDocument.Open(path);
        return document.Styles.CharacterStyles.First(style => !style.Name.StartsWith('$')).DisplayName;
    }

    internal static string DocumentWithText(string text)
    {
        var path = TestFiles.TempPath(".idml");
        using var document = IdmlDocument.Create();
        document.Pages[0].AddText(new Rect(36, 36, 400, 60), text);
        document.Save(path);
        return path;
    }
}
