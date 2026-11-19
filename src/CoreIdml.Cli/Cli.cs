using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIdml.Cli.Tools;

namespace CoreIdml.Cli;

/// <summary>How a command-line value becomes a tool argument.</summary>
internal enum CliType
{
    String,

    /// <summary>A file path; <c>-</c> means stdin (input) or stdout (output) where the command allows it.</summary>
    Path,
    Int,
    Double,

    /// <summary>An option without a value.</summary>
    Flag,

    /// <summary>Comma-separated integers ("0,2,5").</summary>
    IntList,

    /// <summary>Comma-separated strings ("pages,stories").</summary>
    StringList,

    /// <summary>A JSON file (or <c>-</c> for stdin) whose value becomes the argument.</summary>
    JsonFile,

    /// <summary>A JSON object file (or <c>-</c>) whose properties become arguments themselves.</summary>
    JsonArguments,

    /// <summary>A text file (or <c>-</c>) whose content becomes the argument.</summary>
    TextFile,

    /// <summary><c>key=value</c>, collected into an object.</summary>
    Pair,
}

/// <summary>A positional argument of a command.</summary>
internal sealed record CliValue(string Property, string Label, CliType Type = CliType.String, bool Optional = false, bool Rest = false);

/// <summary>An option of a command (<c>--flag</c> or <c>--name value</c>); repeatable options build lists.</summary>
internal sealed record CliOption(string Flag, string Property, CliType Type = CliType.String, bool Repeatable = false, string? Help = null);

/// <summary>A command: which tool it runs, and how its arguments map onto the tool's.</summary>
internal sealed record CliCommand(string Name, string Tool, string Help, IReadOnlyList<CliValue> Values, IReadOnlyList<CliOption> Options)
{
    public string Synopsis =>
        string.Join(' ', [Name, .. Values.Select(value => value.Optional ? $"[{value.Label}]" : value.Rest ? $"<{value.Label}>..." : $"<{value.Label}>"),
            .. Options.Select(option => option.Type == CliType.Flag ? $"[{option.Flag}]" : $"[{option.Flag} {option.Property}]")]);
}

/// <summary>
/// The command line: every command maps onto an MCP tool (same operations, same JSON), and <c>call</c> reaches any
/// tool directly. Exit codes: 0 success, 1 failed, 2 invalid input or usage, 3 not found.
/// </summary>
internal static class Cli
{
    private static readonly CliOption[] EditOptions =
    [
        new("-o", "outPath", CliType.Path),
        new("--in-place", "inPlace", CliType.Flag),
        new("--dry-run", "dryRun", CliType.Flag),
    ];

    private static readonly CliValue File = new("path", "file.idml", CliType.Path);

    public static IReadOnlyList<CliCommand> Commands { get; } =
    [
        new("summary", "idml_summarize", "pages, frames, stories, links, styles", [File],
            [new("--runs", "includeRuns", CliType.Flag), new("--max-text", "maxTextLength", CliType.Int), new("--pages", "pages", CliType.IntList),
                new("--sections", "sections", CliType.StringList, Help: "pages,offPage,stories,links,resources")]),
        new("story", "idml_get_story", "one story with indexed content runs", [File, new("storyId", "storyId")], [new("--paragraphs", "includeParagraphs", CliType.Flag)]),
        new("find", "idml_find", "frames and stories by name, label, tag, kind or text", [File],
            [new("--text", "text"), new("--name", "name"), new("--label", "label"), new("--tag", "tag"), new("--kind", "kind"), new("--page", "pageIndex", CliType.Int), new("--max", "maxResults", CliType.Int)]),
        new("validate", "idml_validate", "structural validation", [File], []),
        new("structure", "idml_xml_structure", "XML structure tree", [File], []),
        new("export-xml", "idml_export_xml", "the XML structure as XML", [File], [new("--at", "at")]),
        new("diff", "idml_diff", "what changed between two files", [new("before", "before", CliType.Path), new("after", "after", CliType.Path)], []),
        new("set-text", "idml_set_text", "replace one content run's text", [File, new("storyId", "storyId"), new("runIndex", "run", CliType.Int), new("text", "text")],
            [new("--force", "force", CliType.Flag), .. EditOptions]),
        new("relink", "idml_relink_image", "point a link at another file", [File, new("linkId", "linkId"), new("newUri", "uriOrPath")],
            [new("--refit", "refit", CliType.Flag), .. EditOptions]),
        new("replace", "idml_replace_text", "find and replace in every story", [File, new("find", "find"), new("replacement", "replacement")], EditOptions),
        new("fill", "idml_fill", "fill a template from {\"text\":{...},\"images\":{...}}", [File, new("data", "data.json", CliType.JsonArguments)], EditOptions),
        new("import-xml", "idml_import_xml", "import XML onto the structure", [File, new("xml", "data.xml", CliType.TextFile)],
            [new("--at", "at"), new("--fit", "fit"), new("--char-style", "characterStyles", CliType.Pair, Repeatable: true, Help: "tag=Style"),
                new("--relink-only", "relinkOnly", CliType.Flag), .. EditOptions]),
        new("merge", "idml_merge", "append pages of other files (other.idml@0,2 takes pages 0 and 2)", [File, new("sources", "other.idml", CliType.String, Rest: true)], EditOptions),
        new("apply", "idml_apply", "add a JSON spec's colours, styles and pages", [File, new("spec", "spec.json", CliType.JsonFile)], EditOptions),
        new("to-pdf", "idml_to_pdf", "render to PDF; reports missing fonts, links and overset text", [File],
            [new("-o", "outPath", CliType.Path), new("--fonts", "fonts", CliType.Path, Repeatable: true), new("--links", "linkDirectories", CliType.Path, Repeatable: true),
                new("--pages", "pages", CliType.IntList), new("--max-dpi", "maxImageDpi", CliType.Double), new("--jpeg-quality", "jpegQuality", CliType.Int),
                new("--options", "options", CliType.JsonFile)]),
        new("from-pdf", "idml_from_pdf", "rebuild an IDML from a PDF (images to <out>-links/)", [new("path", "file.pdf", CliType.Path)],
            [new("-o", "outPath", CliType.Path), new("--images", "imageDirectory", CliType.Path), new("--pages", "pages", CliType.IntList),
                new("--fonts", "fonts", CliType.Path, Repeatable: true), new("--preserve-line-breaks", "preserveLineBreaks", CliType.Flag), new("--options", "options", CliType.JsonFile)]),
        new("edit-item", "idml_edit_item", "move, resize, restyle, relayer or rename an item (page coordinates and sizes in points)", [File, new("itemId", "itemId")],
            [new("--x", "x", CliType.Double), new("--y", "y", CliType.Double), new("--width", "width", CliType.Double), new("--height", "height", CliType.Double),
                new("--page", "pageIndex", CliType.Int), new("--layer", "layer"), new("--create-layer", "createLayer", CliType.Flag), new("--name", "newName"),
                new("--fill", "fill"), new("--fill-tint", "fillTint", CliType.Double), new("--stroke", "stroke"), new("--stroke-weight", "strokeWeight", CliType.Double),
                new("--stroke-tint", "strokeTint", CliType.Double), new("--stroke-type", "strokeType"), new("--end-cap", "endCap"),
                new("--corner-radius", "cornerRadius", CliType.Double), new("--opacity", "opacity", CliType.Double), new("--rotate-by", "rotateBy", CliType.Double),
                new("--columns", "columns", CliType.Int), new("--column-gutter", "columnGutter", CliType.Double), new("--vertical-align", "verticalAlign"), .. EditOptions]),
        new("delete-item", "idml_delete_item", "remove an item", [File, new("itemId", "itemId")], EditOptions),
        new("duplicate-item", "idml_duplicate_item", "copy an item", [File, new("itemId", "itemId")],
            [new("--page", "pageIndex", CliType.Int), new("--dx", "offsetX", CliType.Double), new("--dy", "offsetY", CliType.Double), .. EditOptions]),
        new("add-page", "idml_add_page", "insert an empty page", [File], [new("--at", "at", CliType.Int), .. EditOptions]),
        new("delete-page", "idml_delete_page", "delete a page and its items", [File, new("pageIndex", "page", CliType.Int)], EditOptions),
        new("move-page", "idml_move_page", "move a page to another position", [File, new("from", "from", CliType.Int), new("to", "to", CliType.Int)], EditOptions),
        new("apply-style", "idml_apply_style", "paragraph style to a paragraph, or character style to a run", [File, new("storyId", "storyId")],
            [new("--paragraph", "paragraphIndex", CliType.Int), new("--paragraph-style", "paragraphStyle"), new("--run", "runIndex", CliType.Int), new("--character-style", "characterStyle"), .. EditOptions]),
        new("render", "idml_render_page", "page preview as PNG (needs -o)", [File],
            [new("--page", "pageIndex", CliType.Int), new("--dpi", "dpi", CliType.Double), new("--fonts", "fonts", CliType.Path, Repeatable: true),
                new("--links", "linkDirectories", CliType.Path, Repeatable: true), new("-o", "outPath", CliType.Path)]),
        new("check-fit", "idml_check_fit", "exact overset per story (lines and points that overflow)", [File],
            [new("--story", "storyId"), new("--all", "all", CliType.Flag), new("--fonts", "fonts", CliType.Path, Repeatable: true)]),
        new("batch", "idml_batch", "several edits at once, all or nothing ([{\"tool\":...,\"arguments\":{...}}])", [File, new("operations", "operations.json", CliType.JsonFile)], EditOptions),
        new("fonts", "idml_fonts", "fonts used, missing or substituted, with stories and pages", [File], [new("--fonts", "fonts", CliType.Path, Repeatable: true)]),
        new("list-fonts", "idml_list_fonts", "font families exports can use, with their style names", [],
            [new("--query", "query"), new("--max", "max", CliType.Int), new("--fonts", "fonts", CliType.Path, Repeatable: true)]),
        new("relink-folder", "idml_relink_folder", "relink missing links to files of the same name", [File, new("folders", "folder", CliType.String, Rest: true)],
            [new("--all", "all", CliType.Flag), new("--refit", "refit", CliType.Flag), .. EditOptions]),
        new("package", "idml_package", "zip the .idml with its links (like InDesign's Package)", [File],
            [new("-o", "outPath", CliType.Path), new("--links", "linkDirectories", CliType.Path, Repeatable: true), new("--fonts", "fontDirectories", CliType.Path, Repeatable: true)]),
        new("create", "idml_create", "new document from a JSON spec; warns about spec fields it ignores", [new("spec", "spec.json", CliType.JsonFile)],
            [new("-o", "outPath", CliType.Path), new("--check-fit", "checkFit", CliType.Flag), new("--fonts", "fonts", CliType.Path, Repeatable: true)]),
    ];

    /// <summary>Runs the command line; returns the exit code.</summary>
    public static int Run(string[] args, CliConsole console)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            console.Out.WriteLine(Usage());
            console.Out.Flush();
            return args.Length == 0 ? 2 : 0;
        }
        var temporaryFiles = new List<string>();
        try
        {
            var pretty = args.Contains("--pretty");
            var rest = args.Skip(1).Where(arg => arg != "--pretty").ToArray();
            var (toolName, arguments, binaryOutput) = args[0] switch
            {
                "call" => Call(rest, console),
                "tools" => ("tools", null, null),
                var name => Map(Commands.FirstOrDefault(command => command.Name == name)
                    ?? throw ToolException.Invalid($"Unknown command '{name}'.\n\n{Usage()}"), rest, console, temporaryFiles),
            };
            if (toolName == "tools")
            {
                Print(console, ToolList(), pretty);
                return 0;
            }
            var paths = new PathResolver(Environment.CurrentDirectory);
            using var parsed = JsonDocument.Parse(JsonText.Write(arguments));
            var result = ToolCatalog.Run(toolName, parsed.RootElement, paths);
            if (result.Png is { } png)
            {
                // Images are not part of the JSON: they go to -o (a file, or '-' for stdout).
                var imagePath = binaryOutput ?? (arguments!["outPath"]?.GetValue<string>() is { } target ? Path.GetFullPath(target)
                    : throw ToolException.Invalid($"'{args[0]}' writes an image: pass -o <file.png> (or -o - for stdout)."));
                System.IO.File.WriteAllBytes(imagePath, png);
            }
            // A dry run (--dry-run with -o -) saves nothing: its report is the output, on stdout as usual.
            if (binaryOutput is not null && System.IO.File.Exists(binaryOutput))
            {
                // The document (or image) went to stdout; the report goes to stderr.
                using (var stream = System.IO.File.OpenRead(binaryOutput))
                {
                    console.Out.Flush();
                    stream.CopyTo(console.OutStream);
                    console.OutStream.Flush();
                }
                console.Error.WriteLine(pretty ? JsonText.Indent(result.Json) : result.Json);
                console.Error.Flush();
                return 0;
            }
            if (toolName == "idml_export_xml")
            {
                console.Out.WriteLine(JsonElement.Parse(result.Json).GetString());
            }
            else
            {
                Print(console, result.Json, pretty);
            }
            console.Out.Flush();
            return 0;
        }
#pragma warning disable CA1031 // A CLI reports every failure as a message and an exit code.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            console.Out.Flush();
            console.Error.WriteLine("error: " + ToolException.Describe(exception));
            console.Error.Flush();
            return ToolException.ExitCode(ToolException.KindOf(exception));
        }
        finally
        {
            foreach (var file in temporaryFiles.Where(System.IO.File.Exists))
            {
                System.IO.File.Delete(file);
            }
        }
    }

    public static string Usage()
    {
        var builder = new StringBuilder();
        builder.AppendLine("usage: coreidml <command> [arguments] [--pretty]");
        builder.AppendLine();
        foreach (var command in Commands)
        {
            builder.Append("  ").Append(command.Synopsis).AppendLine();
            builder.Append("      ").Append(command.Help).Append(" (").Append(command.Tool).AppendLine(")");
        }
        builder.AppendLine("  call <tool> [<args.json> | - | '{...}']");
        builder.AppendLine("      run any MCP tool with JSON arguments (see 'coreidml tools')");
        builder.AppendLine("  tools");
        builder.AppendLine("      list the tools");
        builder.AppendLine("  mcp [--root <dir>]");
        builder.AppendLine("      MCP server on stdin/stdout; relative paths resolve against --root, else the client's roots");
        builder.AppendLine();
        builder.AppendLine("Edits need -o <out.idml> (a new file) or --in-place; --dry-run shows the change without saving.");
        builder.AppendLine("'-' reads a file from stdin or writes the output file to stdout (the JSON report then goes to stderr).");
        builder.AppendLine("Paths are relative to the current directory. Exit codes: 0 ok, 1 failed, 2 invalid input, 3 not found.");
        return builder.ToString();
    }

    private static (string Tool, JsonObject Arguments, string? BinaryOutput) Call(string[] rest, CliConsole console)
    {
        if (rest.Length == 0)
        {
            throw ToolException.Invalid("call needs a tool name: coreidml call <tool> [<args.json> | - | '{...}'].");
        }
        var tool = ToolCatalog.Require(rest[0]).Name;
        var text = rest.Length < 2 ? "{}"
            : rest[1] == "-" ? console.In.ReadToEnd()
            : rest[1].TrimStart().StartsWith('{') ? rest[1]
            : System.IO.File.ReadAllText(rest[1]);
        return (tool, JsonNode.Parse(text) as JsonObject ?? throw ToolException.Invalid("Tool arguments must be a JSON object."), null);
    }

    /// <summary>Turns a command's values and options into the tool's JSON arguments.</summary>
    private static (string Tool, JsonObject Arguments, string? BinaryOutput) Map(CliCommand command, string[] args, CliConsole console, List<string> temporaryFiles)
    {
        var arguments = new JsonObject();
        string? binaryOutput = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var option = command.Options.FirstOrDefault(candidate => candidate.Flag == arg || (candidate.Flag == "-o" && arg == "--out"));
            if (option is null)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal) || (arg.Length == 2 && arg[0] == '-' && arg != "-"))
                {
                    throw ToolException.Invalid($"Unknown option '{arg}' for '{command.Name}'.\n  {command.Synopsis}");
                }
                positional.Add(arg);
                continue;
            }
            if (option.Type == CliType.Flag)
            {
                arguments[option.Property] = true;
                continue;
            }
            if (++i >= args.Length)
            {
                throw ToolException.Invalid($"Option '{arg}' needs a value.\n  {command.Synopsis}");
            }
            var value = args[i];
            if (option.Property == "outPath" && value == "-")
            {
                binaryOutput = TemporaryFile(temporaryFiles, command.Tool == "idml_render_page" ? ".png" : ".out");
                value = binaryOutput;
            }
            var node = Convert(option.Property, option.Type, value, console, temporaryFiles);
            if (option.Type == CliType.Pair)
            {
                var map = arguments[option.Property] as JsonObject ?? new JsonObject();
                foreach (var (key, item) in (JsonObject)node!)
                {
                    map[key] = item?.DeepClone();
                }
                arguments[option.Property] = map;
            }
            else if (option.Repeatable)
            {
                var list = arguments[option.Property] as JsonArray ?? [];
                list.Add(node);
                arguments[option.Property] = list;
            }
            else
            {
                arguments[option.Property] = node;
            }
        }

        var index = 0;
        foreach (var value in command.Values)
        {
            if (value.Rest)
            {
                arguments[value.Property] = new JsonArray([.. positional.Skip(index).Select(item => (JsonNode)Source(item))]);
                index = positional.Count;
                break;
            }
            if (index >= positional.Count)
            {
                if (value.Optional)
                {
                    continue;
                }
                throw ToolException.Invalid($"Missing argument <{value.Label}>.\n  {command.Synopsis}");
            }
            var node = Convert(value.Property, value.Type, positional[index++], console, temporaryFiles);
            if (value.Type == CliType.JsonArguments)
            {
                foreach (var (key, item) in node as JsonObject ?? throw ToolException.Invalid($"<{value.Label}> must hold a JSON object."))
                {
                    arguments[key] = item?.DeepClone();
                }
            }
            else
            {
                arguments[value.Property] = node;
            }
        }
        if (index < positional.Count)
        {
            throw ToolException.Invalid($"Unexpected argument '{positional[index]}'.\n  {command.Synopsis}");
        }
        return (command.Tool, arguments, binaryOutput);
    }

    /// <summary>A merge source: "file.idml" or "file.idml@0,2" (only those 0-based pages).</summary>
    private static JsonNode Source(string value)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0 || System.IO.File.Exists(value) || Directory.Exists(value))
        {
            return value;
        }
        return new JsonObject
        {
            ["path"] = value[..at],
            ["pages"] = JsonText.Array(ParseInts("pages", value[(at + 1)..])),
        };
    }

    private static JsonNode? Convert(string property, CliType type, string value, CliConsole console, List<string> temporaryFiles) => type switch
    {
        CliType.Int => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw ToolException.Invalid($"'{value}' is not an integer ({property})."),
        CliType.Double => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : throw ToolException.Invalid($"'{value}' is not a number ({property})."),
        CliType.IntList => JsonText.Array(ParseInts(property, value)),
        CliType.StringList => JsonText.Array(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        CliType.Path when value == "-" && property == "path" => StdinToFile(console, temporaryFiles),
        CliType.JsonFile or CliType.JsonArguments => JsonNode.Parse(ReadText(value, console)),
        CliType.TextFile => ReadText(value, console),
        CliType.Pair => value.IndexOf('=', StringComparison.Ordinal) is var equals and > 0
            ? new JsonObject { [value[..equals]] = value[(equals + 1)..] }
            : throw ToolException.Invalid($"'{value}' must be key=value ({property})."),
        _ => value,
    };

    private static List<int> ParseInts(string property, string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw ToolException.Invalid($"'{value}' is not a list of integers ({property})."))
            .ToList();

    private static string ReadText(string value, CliConsole console) =>
        value == "-" ? console.In.ReadToEnd()
        : System.IO.File.Exists(value) ? System.IO.File.ReadAllText(value)
        : throw ToolException.NotFound($"File not found: {Path.GetFullPath(value)}");

    /// <summary>A document piped in on stdin, saved to a temporary file for the tool.</summary>
    private static string StdinToFile(CliConsole console, List<string> temporaryFiles)
    {
        var path = TemporaryFile(temporaryFiles, ".idml");
        using var file = System.IO.File.Create(path);
        console.InStream.CopyTo(file);
        return path;
    }

    private static string TemporaryFile(List<string> temporaryFiles, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "coreidml-" + Guid.NewGuid().ToString("N") + extension);
        temporaryFiles.Add(path);
        return path;
    }

    private static void Print(CliConsole console, string json, bool pretty)
    {
        console.Out.WriteLine(pretty ? JsonText.Indent(json) : json);
        console.Out.Flush();
    }

    private static string ToolList() => JsonText.Write(writer =>
    {
        writer.WriteStartArray();
        foreach (var tool in ToolCatalog.All)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("title", tool.Title);
            writer.WriteString("access", tool.Access.ToString().ToLowerInvariant());
            writer.WriteString("description", tool.Description);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
}

/// <summary>The streams the CLI uses: UTF-8 text writers over stdout/stderr, plus the raw streams for binary data.</summary>
internal sealed class CliConsole
{
    public CliConsole(Stream inStream, Stream outStream, Stream errorStream)
    {
        InStream = inStream;
        OutStream = outStream;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        In = new StreamReader(inStream, utf8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        Out = new StreamWriter(outStream, utf8, leaveOpen: true) { AutoFlush = false };
        Error = new StreamWriter(errorStream, utf8, leaveOpen: true) { AutoFlush = true };
    }

    public Stream InStream { get; }

    public Stream OutStream { get; }

    public TextReader In { get; }

    public TextWriter Out { get; }

    public TextWriter Error { get; }
}
