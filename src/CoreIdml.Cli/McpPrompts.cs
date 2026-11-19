using System.Text.Json;

namespace CoreIdml.Cli;

/// <summary>An MCP prompt: a reusable task description with arguments.</summary>
internal sealed record McpPrompt(string Name, string Title, string Description, IReadOnlyList<McpPromptArgument> Arguments, Func<IReadOnlyDictionary<string, string>, string> Render);

internal sealed record McpPromptArgument(string Name, string Description, bool Required = true);

/// <summary>The server instructions and the prompts (fill a template, review a document, change a layout).</summary>
internal static class McpPrompts
{
    /// <summary>Sent in the initialize result: how the tools fit together.</summary>
    public const string Instructions = """
        Tools for Adobe InDesign .idml files (no InDesign needed). Paths absolute unless the server runs with --root.
        Workflow:
        1. idml_summarize (big documents: pass pages and sections) or idml_find (name, script label, XML tag, text) to locate frames, stories, links, styles.
        2. idml_get_story for run (and paragraph) indices before editing text.
        3. Edit: idml_set_text, idml_replace_text, idml_fill, idml_relink_image, idml_import_xml, layout tools (idml_edit_item, idml_add_page, idml_apply_style, idml_edit_table, ...); several at once with idml_batch (atomic). Edits never overwrite silently: pass outPath (new file) or inPlace: true; dryRun: true previews.
        4. idml_check_fit: which frames overflow; fix and recheck.
        5. idml_validate before handing over.
        6. idml_render_page to look at a page; idml_to_pdf for a PDF with missing-font/link/overset report.
        New document: idml_list_fonts for real font/style names; idml_create with checkFit: true (read its warnings); idml_render_page each page; fix with idml_edit_item calls in one idml_batch, not by recreating.
        Coordinates: points, page-relative, top-left origin, y down. Page indices 0-based.
        """;

    public static IReadOnlyList<McpPrompt> All { get; } =
    [
        new("fill_template", "Fill a template",
            "Fill an InDesign template with data, check the fit, save the result.",
            [
                new("path", "Absolute path of the template .idml"),
                new("data", "Text per field, image paths"),
                new("outPath", "Absolute path for the result", Required: false),
            ],
            arguments => $"""
                Fill the InDesign template {arguments["path"]} with this data:

                {arguments["data"]}

                1. idml_summarize (sections ["pages","stories"], maxTextLength 80) for the fields: frame names, script labels, XML tags, {Placeholder} placeholders.
                2. Map the data onto those keys; idml_fill with text and images, writing to {Out(arguments)}. Report keys that matched nothing.
                3. idml_check_fit on the result. Per overflowing frame, shorten the text (idml_set_text) or enlarge the frame (idml_edit_item); recheck.
                4. idml_validate, then idml_render_page on page 0 and look at it.
                """),
        new("review_document", "Review a document",
            "Check an .idml for overset text, missing fonts and links, structural problems.",
            [new("path", "Absolute path of the .idml"), new("fonts", "Absolute path of a font folder", Required: false)],
            arguments => $"""
                Review the InDesign document {arguments["path"]} and report its problems, most serious first:

                1. idml_validate: structural errors and warnings.
                2. idml_check_fit: stories that overflow, by lines and points.
                3. idml_fonts{(arguments.TryGetValue("fonts", out var fonts) ? $" with fonts [{Tools.JsonText.String(fonts)}]" : string.Empty)}: missing or substituted fonts and the pages affected.
                4. idml_summarize (sections ["links"]): missing link files; suggest idml_relink_folder if a folder holds them.
                5. idml_render_page on pages with problems, to confirm visually.
                Do not change the file; give concrete fixes per problem.
                """),
        new("adjust_layout", "Adjust a layout",
            "Change a layout as described, verifying each step.",
            [new("path", "Absolute path of the .idml"), new("change", "What should change"), new("outPath", "Absolute path for the result", Required: false)],
            arguments => $"""
                In the InDesign document {arguments["path"]}, make this change: {arguments["change"]}

                1. Locate the items with idml_find or idml_summarize (pages filter).
                2. Put the edits in one idml_batch: dryRun: true first, then for real, writing to {Out(arguments)}.
                3. Verify with idml_check_fit, idml_validate and idml_render_page on the changed pages.
                """),
    ];

    private const string Placeholder = "{{key}}";

    public static McpPrompt? Find(string name) => All.FirstOrDefault(prompt => prompt.Name == name);

    public static void WriteList(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("prompts");
        foreach (var prompt in All)
        {
            writer.WriteStartObject();
            writer.WriteString("name", prompt.Name);
            writer.WriteString("title", prompt.Title);
            writer.WriteString("description", prompt.Description);
            writer.WriteStartArray("arguments");
            foreach (var argument in prompt.Arguments)
            {
                writer.WriteStartObject();
                writer.WriteString("name", argument.Name);
                writer.WriteString("description", argument.Description);
                writer.WriteBoolean("required", argument.Required);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string Out(IReadOnlyDictionary<string, string> arguments) =>
        arguments.TryGetValue("outPath", out var outPath) ? outPath : "a new file next to the original (never overwrite it)";
}
