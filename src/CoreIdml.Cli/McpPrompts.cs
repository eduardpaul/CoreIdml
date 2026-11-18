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
        Tools for Adobe InDesign .idml files (no InDesign needed). Paths must be absolute unless the server was started with --root.
        Usual workflow:
        1. idml_summarize to see pages, frames, stories, links and styles. On big documents pass pages and sections to keep it small, or use idml_find to locate frames and stories by name, script label, XML tag or text.
        2. idml_get_story for a story's indexed runs (and paragraphs) before editing text.
        3. Edit: idml_set_text, idml_replace_text, idml_fill, idml_relink_image, idml_import_xml, layout tools (idml_edit_item, idml_add_page, idml_apply_style, idml_edit_table, ...), or several at once with idml_batch (atomic).
           Edits never overwrite silently: pass outPath for a new file or inPlace: true; dryRun: true shows the change without saving.
        4. idml_check_fit to see which frames overflow (lines and points, from the PDF composer) and fix them.
        5. idml_validate before handing the file over.
        6. idml_render_page to look at a page, idml_to_pdf for a PDF with the missing-font/link/overset report.
        Coordinates are points, page-relative, origin top-left, y down. Page indices are 0-based.
        """;

    public static IReadOnlyList<McpPrompt> All { get; } =
    [
        new("fill_template", "Fill a template",
            "Fill an InDesign template with data, check that everything fits, and save the result.",
            [
                new("path", "Absolute path of the template .idml"),
                new("data", "The data to put in: text per field, image paths"),
                new("outPath", "Absolute path of the filled .idml", Required: false),
            ],
            arguments => $"""
                Fill the InDesign template {arguments["path"]} with this data:

                {arguments["data"]}

                1. Call idml_summarize (sections ["pages","stories"], maxTextLength 80) to learn the template's fields: frame names, script labels, XML tags and {Placeholder} placeholders.
                2. Map the data onto those keys and call idml_fill with text and images, writing to {Out(arguments)}. Report keys that matched nothing.
                3. Call idml_check_fit on the result. For each overflowing frame, shorten the text (idml_set_text) or enlarge the frame (idml_edit_item) and check again.
                4. Call idml_validate, then idml_render_page on page 0 and look at the result.
                """),
        new("review_document", "Review a document",
            "Check an .idml file for overset text, missing fonts, missing links and structural problems.",
            [new("path", "Absolute path of the .idml"), new("fonts", "Absolute path of a font folder to check against", Required: false)],
            arguments => $"""
                Review the InDesign document {arguments["path"]} and report its problems, most serious first:

                1. idml_validate: structural errors and warnings.
                2. idml_check_fit: every frame and story whose text does not fit, with how many lines and points overflow.
                3. idml_fonts{(arguments.TryGetValue("fonts", out var fonts) ? $" with fontDirectories [{Tools.JsonText.String(fonts)}]" : string.Empty)}: fonts that are missing or substituted, and the pages they affect.
                4. idml_summarize (sections ["links"]): links whose files are missing; suggest idml_relink_folder if a folder holds them.
                5. idml_render_page on the pages with problems, to confirm them visually.
                Do not change the file; list concrete fixes for each problem.
                """),
        new("adjust_layout", "Adjust a layout",
            "Change the layout of a document as described, verifying each step.",
            [new("path", "Absolute path of the .idml"), new("change", "What should change"), new("outPath", "Absolute path for the result", Required: false)],
            arguments => $"""
                In the InDesign document {arguments["path"]}, make this change: {arguments["change"]}

                1. Locate the items with idml_find or idml_summarize (pages filter).
                2. Plan the edits; prefer one idml_batch with dryRun: true first, then run it for real writing to {Out(arguments)}.
                3. Verify with idml_check_fit, idml_validate and idml_render_page on the pages you changed.
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
