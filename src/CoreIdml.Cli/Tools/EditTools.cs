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
            Description = "Replace the text of one content run of a story, keeping its formatting and everything else in the file untouched.",
            InputSchema = Schema.Edit(
                """ "storyId":{"type":"string"},"runIndex":{"type":"integer","minimum":0},"text":{"type":"string"},""" +
                """ "force":{"type":"boolean","description":"Allow overwriting runs holding special characters such as page numbers"}""",
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
            Description = "Point an image link at another file (URI or local path), keeping the frame geometry; with refit, re-measure a local JPEG/PNG and fit it to the frame.",
            InputSchema = Schema.Edit(
                """ "linkId":{"type":"string","description":"Link id from idml_summarize links[].id"},""" +
                """ "linkSelf":{"type":"string","description":"Same as linkId (idmlkit's name; linkId is preferred)"},""" +
                """ "newUri":{"type":"string","description":"New file: absolute local path or URI"},"refit":{"type":"boolean"}""",
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
            Description = "Fill a template: text keys match frame names, script labels, XML tags, then {{key}} placeholders; image keys map to local JPEG/PNG paths. Reports filled and missing keys.",
            InputSchema = Schema.Edit(
                """ "text":{"type":"object","additionalProperties":{"type":"string"}},"images":{"type":"object","additionalProperties":{"type":"string"},"description":"Key to absolute image path"},""" +
                """ "fit":{"type":"string","enum":["fill","fit","stretch","center","original"]},"placeholderFormat":{"type":"string","description":"Placeholder pattern, default {{{{{0}}}}} (i.e. {{key}})"}"""),
            OutputSchema = """{"type":"object","properties":{"filled":{"type":"object","additionalProperties":{"type":"integer"}},"missing":{"type":"array","items":{"type":"string"}}},"required":["filled","missing"]}""",
            Access = ToolAccess.Edit,
            Apply = edit => IdmlJson.Fill(edit.Document, FillJson(edit.Args)),
        },
        new()
        {
            Name = "idml_replace_text",
            Title = "Replace text",
            Description = "Find and replace text in every story (matches may span formatting runs, not paragraphs). Returns the number of replacements.",
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
            Description = "Import XML data onto the XML structure (SimpleIDML rules: simpleidml-setcontent=false|delete|clear|remove-previous-br, simpleidml-ignorecontent, simpleidml-forcecontent, href for images). Pass the XML inline (xml) or as a file (xmlPath).",
            InputSchema = Schema.Edit(
                """ "xml":{"type":"string","description":"The XML to import"},"xmlPath":{"type":"string","description":"Absolute path of an XML file to import instead of xml"},""" +
                """ "at":{"type":"string","description":"Target structure path, default the root"},""" +
                """ "fit":{"type":"string","enum":["fill","fit","stretch","center","original"],"description":"How href images are fitted, default fill"},""" +
                """ "characterStyles":{"type":"object","additionalProperties":{"type":"string"},"description":"Inline tag to character style name, e.g. {\"b\":\"Bold\"}"},""" +
                """ "relinkOnly":{"type":"boolean","description":"Only relink href images; do not re-measure or refit them"}"""),
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
            Description = "Append the pages of other .idml files to a document (ids remapped, styles and swatches merged), e.g. one page per record. Each source is a path, or {path, pages:[0-based page indices]} to take only some pages.",
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
            Description = "Add the colours, paragraph/character styles (created, or updated when the name exists) and pages of a JSON document spec to an existing document. Pages without 'index' are appended; {\"index\": N, \"items\": [...]} adds items to existing page N (0-based).",
            InputSchema = Schema.Edit(""" "spec":{"type":"object","description":"Document spec (see idml_create)"}""", "spec"),
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                IdmlJson.Apply(edit.Document, SpecJson(edit.Args));
                return $"{{\"ok\":true,\"pageCount\":{edit.Document.Pages.Count}}}";
            },
        },
        new()
        {
            Name = "idml_create",
            Title = "Create document",
            Description = "Create a new .idml file from a JSON document spec (page setup, colors, paragraph/character styles, pages with text, image, rectangle, ellipse, line and table items). Returns the validation report.",
            InputSchema = Schema.Object(
                """ "spec":{"type":"object","description":"Document spec, see the Idml README"},"outPath":{"type":"string","description":"Absolute path of the new .idml"}""",
                ["spec", "outPath"]),
            Access = ToolAccess.Write,
            Run = args =>
            {
                using var document = IdmlJson.CreateDocument(SpecJson(args));
                EditSession.Save(document, args.Path("outPath"));
                return new(IdmlJson.ValidateToJson(document));
            },
        },
    ];

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
