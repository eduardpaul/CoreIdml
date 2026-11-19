using System.Text.Json.Nodes;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>Text, link, template and XML edits; merging pages; creating documents.</summary>
internal static class EditTools
{
    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new()
        {
            Name = "idml_set_text",
            Title = "Set run text",
            Description = "Replace one content run's text, keeping its formatting.",
            InputSchema = Schema.Edit(
                """ "storyId":{"type":"string"},"runIndex":{"type":"integer","minimum":0},"text":{"type":"string"},""" +
                """ "force":{"type":"boolean","description":"Allow overwriting special characters (page numbers...)"}""",
                "storyId", "runIndex", "text"),
            Access = ToolAccess.Edit,
            Idempotent = true,
            Apply = edit =>
            {
                var story = DocumentTools.RequireStory(edit.Document, edit.Args.String("storyId"));
                story.SetRunText(edit.Args.Int("runIndex"), edit.Args.String("text"), edit.Args.Bool("force"));
                return JsonText.Ok;
            },
        },
        new()
        {
            Name = "idml_relink_image",
            Title = "Relink image",
            Description = "Point an image link at another file (absolute path or URI), keeping the frame; refit re-measures a local JPEG/PNG and fits it to the frame.",
            InputSchema = Schema.Edit(
                """ "linkId":{"type":"string","description":"idml_summarize links[].id"},""" +
                """ "linkSelf":{"type":"string","description":"Alias of linkId"},""" +
                """ "newUri":{"type":"string"},"refit":{"type":"boolean"}""",
                "newUri"),
            Access = ToolAccess.Edit,
            Idempotent = true,
            Apply = edit =>
            {
                var linkId = LinkId(edit.Args);
                var link = edit.Document.Links.FirstOrDefault(candidate => candidate.Id == linkId)
                    ?? throw ToolException.NotFound($"No link '{linkId}' in this document (link ids are listed by idml_summarize links[].id).");
                link.Relink(ResolveImage(edit.Args, "newUri", edit.Args.String("newUri")), edit.Args.Bool("refit") ? ImageFit.Fill : null);
                return JsonText.Ok;
            },
        },
        new()
        {
            Name = "idml_fill",
            Title = "Fill template",
            Description = "Fill a template: text keys match frame names, script labels, XML tags, then {{key}} placeholders; image keys map to absolute JPEG/PNG paths. Returns filled and missing keys.",
            InputSchema = Schema.Edit(
                """ "text":{"type":"object","additionalProperties":{"type":"string"}},"images":{"type":"object","additionalProperties":{"type":"string"}},""" +
                """ "fit":{"type":"string","enum":["fill","fit","stretch","center","original"]},"placeholderFormat":{"type":"string","description":"Default {{{{{0}}}}}, i.e. {{key}}"}"""),
            OutputSchema = """{"type":"object","properties":{"filled":{"type":"object","additionalProperties":{"type":"integer"}},"missing":{"type":"array","items":{"type":"string"}}},"required":["filled","missing"]}""",
            Access = ToolAccess.Edit,
            Apply = edit => IdmlJson.Fill(edit.Document, FillJson(edit.Args)),
        },
        new()
        {
            Name = "idml_replace_text",
            Title = "Replace text",
            Description = "Find and replace text in every story (matches may span runs, not paragraphs). Returns the count.",
            InputSchema = Schema.Edit(""" "find":{"type":"string"},"replacement":{"type":"string"}""", "find", "replacement"),
            OutputSchema = """{"type":"object","properties":{"replacements":{"type":"integer"}},"required":["replacements"]}""",
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var find = edit.Args.String("find");
                if (find.Length == 0)
                {
                    throw ToolException.Invalid("Argument 'find' is empty.");
                }
                return $"{{\"replacements\":{edit.Document.ReplaceText(find, edit.Args.String("replacement"))}}}";
            },
        },
        new()
        {
            Name = "idml_import_xml",
            Title = "Import XML",
            Description = "Import XML data onto the XML structure (SimpleIDML rules: simpleidml-setcontent=false|delete|clear|remove-previous-br, simpleidml-ignorecontent, simpleidml-forcecontent, href for images). Pass xml or xmlPath.",
            InputSchema = Schema.Edit(
                """ "xml":{"type":"string"},"xmlPath":{"type":"string","description":"Absolute XML file path, instead of xml"},""" +
                """ "at":{"type":"string","description":"Structure path; default root"},""" +
                """ "fit":{"type":"string","enum":["fill","fit","stretch","center","original"],"description":"href images; default fill"},""" +
                """ "characterStyles":{"type":"object","additionalProperties":{"type":"string"},"description":"Inline tag to character style, e.g. {\"b\":\"Bold\"}"},""" +
                """ "relinkOnly":{"type":"boolean","description":"Relink href images without refitting"}"""),
            OutputSchema = """{"type":"object","properties":{"updated":{"type":"integer"},"added":{"type":"integer"},"deleted":{"type":"integer"},"unmatched":{"type":"array","items":{"type":"string"}},"warnings":{"type":"array","items":{"type":"string"}}},"required":["updated","added","deleted"]}""",
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var xml = edit.Args.OptionalString("xml")
                    ?? (edit.Args.Has("xmlPath") ? File.ReadAllText(edit.Args.ExistingFile("xmlPath")) : throw ToolException.Invalid("Pass the XML to import as 'xml' or 'xmlPath'."));
                var options = new JsonObject();
                if (edit.Args.OptionalString("fit") is { } fit)
                {
                    options["fit"] = fit;
                }
                if (edit.Args.Has("characterStyles"))
                {
                    options["characterStyles"] = new JsonObject([.. edit.Args.Map("characterStyles").Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]);
                }
                if (edit.Args.Bool("relinkOnly"))
                {
                    options["relinkOnly"] = true;
                }
                return IdmlJson.ImportXml(edit.Document, xml, edit.Args.OptionalString("at"), JsonText.Write(options));
            },
        },
        new()
        {
            Name = "idml_merge",
            Title = "Merge documents",
            Description = "Append the pages of other .idml files (ids remapped, styles and swatches merged). Each source is a path, or {path, pages:[0-based indices]}.",
            InputSchema = Schema.Edit(
                """ "sources":{"type":"array","items":{"anyOf":[{"type":"string"},{"type":"object","properties":{"path":{"type":"string"},"pages":{"type":"array","items":{"type":"integer","minimum":0}}},"required":["path"]}]}}""",
                "sources"),
            OutputSchema = """{"type":"object","properties":{"pagesAdded":{"type":"integer"},"pageCount":{"type":"integer"}},"required":["pagesAdded","pageCount"]}""",
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var added = 0;
                foreach (var source in edit.Args.Array("sources"))
                {
                    var (sourcePath, pages) = source.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (edit.Args.Paths.ExistingFile("sources", source.GetString()!), (IReadOnlyList<int>)[])
                        : (edit.Args.With(source).ExistingFile("path"), edit.Args.With(source).Ints("pages"));
                    using var other = IdmlDocument.Open(sourcePath);
                    added += edit.Document.ImportPages(other, [.. pages]).Count;
                }
                return $"{{\"pagesAdded\":{added},\"pageCount\":{edit.Document.Pages.Count}}}";
            },
        },
        new()
        {
            Name = "idml_apply",
            Title = "Apply JSON spec",
            Description = "Add a document spec's colours, styles (updated when the name exists) and pages to an existing document. Pages without index are appended; {\"index\":N,\"items\":[...]} draws on existing page N (0-based). Returns the added items (id, name, type, story), warnings for ignored spec fields, and with checkFit the text that does not fit.",
            // The full schema is idml_create's (repeating it here would double its cost in every tool listing).
            InputSchema = Schema.Edit(
                """ "spec":{"type":"object","description":"Fields as in idml_create's spec schema, without page: colors, characterStyles, paragraphStyles, pages."},""" +
                CheckFitProperty + "," + PdfTools.FontProperties,
                "spec"),
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var json = SpecJson(edit.Args);
                var warnings = IdmlJson.CheckSpec(json);
                var before = edit.Document.AllPageItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                IdmlJson.Apply(edit.Document, json);
                var result = new JsonObject
                {
                    ["ok"] = true,
                    ["pageCount"] = edit.Document.Pages.Count,
                    ["added"] = new JsonArray([.. edit.Document.AllPageItems.Where(item => !before.Contains(item.Id)).Select(ItemEntry)]),
                    ["warnings"] = JsonText.Array(warnings),
                };
                if (edit.Args.Bool("checkFit"))
                {
                    result["fit"] = PdfTools.FitReport(edit.Document, PdfTools.ExportOptions(edit.Args, edit.DocumentPath).Fonts, all: false);
                }
                return JsonText.Write(result);
            },
        },
        new()
        {
            Name = "idml_create",
            Title = "Create document",
            Description = "Create a new .idml from a document spec (page setup, colours, styles, pages of text, table, image, rectangle, ellipse and line items). Returns the validation report, pages with the created items (id, name, type, story), warnings for ignored spec fields, and with checkFit the text that does not fit. Then idml_render_page each page.",
            InputSchema = SpecSchema.Object(
                SpecSchema.Property + "," +
                """ "outPath":{"type":"string","description":"Absolute path of the new .idml"},""" + CheckFitProperty + "," + PdfTools.FontProperties,
                "spec", "outPath"),
            Access = ToolAccess.Write,
            Run = args =>
            {
                var json = SpecJson(args);
                var warnings = IdmlJson.CheckSpec(json);
                var outPath = args.Path("outPath");
                using var document = IdmlJson.CreateDocument(json);
                EditSession.Save(document, outPath);
                var result = JsonNode.Parse(IdmlJson.ValidateToJson(document))!.AsObject();
                result["pages"] = new JsonArray([.. document.Pages.Select(page => (JsonNode)new JsonObject
                {
                    ["index"] = page.Index,
                    ["items"] = new JsonArray([.. page.PageItems.Select(ItemEntry)]),
                })]);
                result["warnings"] = JsonText.Array(warnings);
                if (args.Bool("checkFit"))
                {
                    result["fit"] = PdfTools.FitReport(document, PdfTools.ExportOptions(args, outPath).Fonts, all: false);
                }
                return new(JsonText.Write(result));
            },
        },
    ];

    private const string CheckFitProperty =
        """ "checkFit":{"type":"boolean","description":"Also return overflowing stories by frame name (as idml_check_fit)"}""";

    /// <summary>An item as create and apply report it: what later calls need to address it.</summary>
    private static JsonNode ItemEntry(PageItem item)
    {
        var entry = new JsonObject
        {
            ["id"] = item.Id,
            ["type"] = item.Kind,
        };
        if (item.Name is { Length: > 0 } name)
        {
            entry["name"] = name;
        }
        if (item.Page is { } page)
        {
            entry["page"] = page.Index;
        }
        if (item is TextFrame frame)
        {
            entry["story"] = frame.StoryId;
        }
        return entry;
    }

    private static string LinkId(ToolArguments args) =>
        args.OptionalString("linkId") ?? args.OptionalString("linkSelf") ?? throw ToolException.Invalid("Missing required argument 'linkId' (or its alias 'linkSelf').");

    /// <summary>A spec given as an object, or as a JSON string.</summary>
    internal static string SpecJson(ToolArguments args) => args.Get("spec") switch
    {
        { ValueKind: System.Text.Json.JsonValueKind.String } text => text.GetString()!,
        { ValueKind: System.Text.Json.JsonValueKind.Object } spec => spec.GetRawText(),
        null => throw ToolException.Invalid("Missing required argument 'spec'."),
        _ => throw ToolException.Invalid("Argument 'spec' must be a JSON object."),
    };

    /// <summary>Local image paths resolve like every other path; URIs (file:, http:) are kept as they are.</summary>
    internal static string ResolveImage(ToolArguments args, string argument, string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1 ? value : args.Paths.Resolve(argument, value);

    private static string FillJson(ToolArguments args)
    {
        var fill = new JsonObject
        {
            ["text"] = new JsonObject([.. args.Map("text").Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]),
            ["images"] = new JsonObject([.. args.Map("images").Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)ResolveImage(args, "images." + pair.Key, pair.Value)))]),
        };
        if (args.OptionalString("fit") is { } fit)
        {
            fill["fit"] = fit;
        }
        if (args.OptionalString("placeholderFormat") is { } format)
        {
            fill["placeholderFormat"] = format;
        }
        return JsonText.Write(fill);
    }
}
