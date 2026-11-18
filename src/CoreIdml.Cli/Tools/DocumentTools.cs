using System.Text.Json.Nodes;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>Read-only tools: summary, stories, XML structure, validation, diff.</summary>
internal static class DocumentTools
{
    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new()
        {
            Name = "idml_summarize",
            Title = "Summarize document",
            Description = "Structured summary of an .idml file: pages with frames (page-relative position in points, name, tag, text, likely overset), stories (word count, run count; runs with includeRuns), image links with ids, styles, swatches, layers, tags, fonts.",
            InputSchema = Schema.Read(
                """ "includeRuns":{"type":"boolean","description":"Include each story's indexed content runs (needed for idml_set_text)"},""" +
                """ "maxTextLength":{"type":"integer","description":"Characters of text per frame/story; 0 none, -1 all. Default 200"},""" +
                """ "pages":{"type":"array","items":{"type":"integer","minimum":0},"description":"Only these 0-based pages, with the stories and links shown on them (pasteboard items left out)"},""" +
                """ "sections":{"type":"array","items":{"type":"string","enum":["pages","offPage","stories","links","resources"]},"description":"Only these parts; default all"}"""),
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args => new(WithDocument(args, document => IdmlJson.SummarizeToJson(document, new SummaryOptions
            {
                IncludeRuns = args.Bool("includeRuns"),
                MaxTextLength = args.OptionalInt("maxTextLength") ?? 200,
                Pages = args.Has("pages") ? [.. args.Ints("pages")] : null,
                Sections = args.Has("sections") ? [.. args.Strings("sections")] : null,
            }))),
        },
        new()
        {
            Name = "idml_get_story",
            Title = "Get story",
            Description = "One story in full: plain text and every content run with its index, text, paragraph style, character style and scope (body, table cell, footnote, note). With includeParagraphs, also its body paragraphs (index, text, style, first and last run), as idml_apply_style uses them.",
            InputSchema = Schema.Read(
                """ "storyId":{"type":"string","description":"Story id from idml_summarize (e.g. u123)"},"includeParagraphs":{"type":"boolean"}""", "storyId"),
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args => new(WithDocument(args, document =>
            {
                var story = RequireStory(document, args.String("storyId"));
                var json = IdmlJson.GetStoryJson(document, story.Id);
                if (!args.Bool("includeParagraphs"))
                {
                    return json;
                }
                var result = JsonNode.Parse(json)!.AsObject();
                result["paragraphs"] = new JsonArray([.. story.Paragraphs().Select(paragraph => (JsonNode)new JsonObject
                {
                    ["index"] = paragraph.Index,
                    ["text"] = paragraph.Text,
                    ["style"] = paragraph.ParagraphStyle,
                    ["firstRun"] = paragraph.FirstRun,
                    ["lastRun"] = paragraph.LastRun,
                })]);
                return JsonText.Write(result);
            })),
        },
        new()
        {
            Name = "idml_find",
            Title = "Find frames and stories",
            Description = "Find page items by name, script label, XML tag or kind, and stories by text, without reading the whole summary. Matches are case-insensitive substrings; every criterion given must match. Returns items (id, kind, name, label, tag, page, bounds, storyId) and stories (id, frames, pages, matching runs with their index).",
            InputSchema = Schema.Read(
                """ "text":{"type":"string","description":"Text to look for in stories"},"name":{"type":"string"},"label":{"type":"string"},"tag":{"type":"string"},""" +
                """ "kind":{"type":"string","description":"TextFrame, Rectangle, Oval, Polygon, GraphicLine, Group, ..."},""" +
                """ "pageIndex":{"type":"integer","minimum":0,"description":"Only on this page"},"maxResults":{"type":"integer","minimum":1,"description":"Default 50"}"""),
            OutputSchema = """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object"}},"stories":{"type":"array","items":{"type":"object"}},"truncated":{"type":"boolean"}},"required":["items","stories","truncated"]}""",
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args => new(WithDocument(args, document => Finder.Find(document, args))),
        },
        new()
        {
            Name = "idml_validate",
            Title = "Validate document",
            Description = "Structural validation: broken part references, duplicate ids, missing stories, styles, swatches, layers, dangling XML bindings, broken threads.",
            InputSchema = Schema.Read(),
            OutputSchema = """{"type":"object","properties":{"isValid":{"type":"boolean"},"issues":{"type":"array","items":{"type":"object","properties":{"severity":{"type":"string"},"code":{"type":"string"},"message":{"type":"string"},"part":{"type":"string"}}}}},"required":["isValid","issues"]}""",
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args => new(WithDocument(args, IdmlJson.ValidateToJson)),
        },
        new()
        {
            Name = "idml_xml_structure",
            Title = "XML structure",
            Description = "The document's XML structure as a tree: tag, path (usable as 'at'), what it is bound to, attributes, text.",
            InputSchema = Schema.Read(),
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args => new(WithDocument(args, document => IdmlJson.XmlStructureJson(document))),
        },
        new()
        {
            Name = "idml_export_xml",
            Title = "Export XML",
            Description = "Export the XML structure (or the subtree at an XML path such as /Root/article[2]) as XML text.",
            InputSchema = Schema.Read(""" "at":{"type":"string","description":"XML path of the subtree, default the root"}"""),
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args => new(WithDocument(args, document => JsonText.String(IdmlJson.ExportXml(document, args.OptionalString("at"))))),
        },
        new()
        {
            Name = "idml_diff",
            Title = "Compare documents",
            Description = "What changed between two .idml files: stories run by run, image links, page count.",
            InputSchema = Schema.Object(
                """ "before":{"type":"string","description":"Absolute path of the original .idml"},"after":{"type":"string","description":"Absolute path of the changed .idml"}""",
                ["before", "after"]),
            OutputSchema = """{"type":"object","properties":{"identical":{"type":"boolean"},"changes":{"type":"array","items":{"type":"object"}}},"required":["identical","changes"]}""",
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args =>
            {
                using var before = IdmlDocument.Open(args.ExistingFile("before"));
                using var after = IdmlDocument.Open(args.ExistingFile("after"));
                return new(IdmlJson.DiffJson(before, after));
            },
        },
    ];

    /// <summary>Opens the document named by the 'path' argument and runs <paramref name="action"/> on it.</summary>
    public static string WithDocument(ToolArguments args, Func<IdmlDocument, string> action)
    {
        using var document = IdmlDocument.Open(args.ExistingFile("path"));
        return action(document);
    }

    public static Story RequireStory(IdmlDocument document, string storyId) =>
        document.FindStory(storyId) ?? throw ToolException.NotFound($"No story '{storyId}' in this document (story ids are listed by idml_summarize).");
}
