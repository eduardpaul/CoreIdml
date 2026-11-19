using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIdml.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>What a tool does to files, which decides its MCP annotations and how it saves.</summary>
internal enum ToolAccess
{
    /// <summary>Only reads (readOnlyHint): safe to auto-approve.</summary>
    Read,

    /// <summary>Writes a new output file (a PDF, a package, a new .idml) and never changes its inputs.</summary>
    Write,

    /// <summary>Changes a document: saved to outPath, or over the input only with inPlace (destructiveHint).</summary>
    Edit,
}

/// <summary>A tool's result: JSON text, plus an optional PNG image (page previews).</summary>
internal sealed record ToolResult(string Json, byte[]? Png = null)
{
    /// <summary>False for dry runs, whose report does not have the tool's usual result shape.</summary>
    public bool MatchesOutputSchema { get; init; } = true;

    /// <summary>Results that are JSON objects can also be sent as MCP structured content.</summary>
    public bool IsObject => Json.AsSpan().TrimStart().StartsWith("{");
}

/// <summary>An edit tool's view of the open document.</summary>
internal sealed record EditContext(IdmlDocument Document, string DocumentPath, ToolArguments Args);

/// <summary>One tool: its MCP description and schema, and what runs it.</summary>
internal sealed class ToolDefinition
{
    public required string Name { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON schema of the arguments.</summary>
    public required string InputSchema { get; init; }

    /// <summary>JSON schema of the result, for tools whose result is a JSON object (MCP structuredContent).</summary>
    public string? OutputSchema { get; init; }

    public ToolAccess Access { get; init; }

    /// <summary>Calling it twice with the same arguments has no further effect.</summary>
    public bool Idempotent { get; init; }

    /// <summary>Runs a read or write tool.</summary>
    public Func<ToolArguments, ToolResult>? Run { get; init; }

    /// <summary>Applies an edit tool to an open document and returns its JSON result; saving is done by the caller.</summary>
    public Func<EditContext, string>? Apply { get; init; }
}

/// <summary>Every tool the CLI and the MCP server offer, and how a call runs.</summary>
internal static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        .. DocumentTools.Definitions,
        .. EditTools.Definitions,
        .. LayoutTools.Definitions,
        .. PdfTools.Definitions,
        .. FileTools.Definitions,
        BatchTool.Definition,
    ];

    private static readonly Dictionary<string, ToolDefinition> ByName = All.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public static ToolDefinition? Find(string name) => ByName.GetValueOrDefault(name);

    public static ToolDefinition Require(string name) =>
        Find(name) ?? throw ToolException.NotFound($"Unknown tool '{name}'. Known tools: {string.Join(", ", All.Select(tool => tool.Name))}.");

    /// <summary>Runs a tool. Edit tools write to outPath, or over path with inPlace, or nothing with dryRun.</summary>
    public static ToolResult Run(string name, JsonElement arguments, PathResolver paths)
    {
        var tool = Require(name);
        var args = new ToolArguments(arguments, paths);
        return tool.Apply is { } apply
            ? new ToolResult(EditSession.Run(tool, apply, args)) { MatchesOutputSchema = !args.Bool("dryRun") }
            : tool.Run!(args);
    }
}

/// <summary>Open, edit, then save (atomically) or report what would change.</summary>
internal static class EditSession
{
    public static string Run(ToolDefinition tool, Func<EditContext, string> apply, ToolArguments args)
    {
        var path = args.ExistingFile("path");
        var dryRun = args.Bool("dryRun");
        var inPlace = args.Bool("inPlace");
        var outPath = args.OptionalPath("outPath");
        if (outPath is not null && inPlace)
        {
            throw ToolException.Invalid("Pass either outPath or inPlace: true, not both.");
        }
        if (!dryRun && outPath is null && !inPlace)
        {
            throw ToolException.Invalid(
                $"{tool.Name} changes the document: pass outPath to write the result to a new file, or inPlace: true to " +
                $"overwrite {path}. Use dryRun: true to see the change without saving.");
        }
        var original = File.ReadAllBytes(path);
        using var document = IdmlDocument.Open(original);
        var result = apply(new EditContext(document, path, args));
        if (dryRun)
        {
            return DryRunReport(original, document, result);
        }
        Save(document, outPath ?? path);
        return result;
    }

    /// <summary>Saves through a temporary file next to the target, so a failure never leaves a half-written file.</summary>
    public static void Save(IdmlDocument document, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            throw ToolException.NotFound($"Output folder not found: {directory}");
        }
        var temporary = path + ".tmp";
        try
        {
            document.Save(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// The edit's own result, the package parts that would be rewritten, and the diff (stories, links, page count, page
    /// items). Changes the diff does not describe (styles, colours, layers, preferences) are named in a note.
    /// </summary>
    public static string DryRunReport(byte[] original, IdmlDocument edited, string result)
    {
        using var before = IdmlDocument.Open(original);
        var beforeParts = before.Package.PartPaths.ToHashSet(StringComparer.Ordinal);
        var changed = edited.Package.PartPaths.Where(part => edited.Package.IsModified(part) && beforeParts.Contains(part)).ToList();
        var added = edited.Package.PartPaths.Where(part => !beforeParts.Contains(part)).ToList();
        var removed = beforeParts.Where(part => !edited.Package.Contains(part)).Order(StringComparer.Ordinal).ToList();
        var report = new JsonObject
        {
            ["dryRun"] = true,
            ["result"] = JsonNode.Parse(result),
            ["changedParts"] = JsonText.Array(changed),
            ["addedParts"] = JsonText.Array(added),
            ["removedParts"] = JsonText.Array(removed),
            ["diff"] = JsonNode.Parse(IdmlJson.DiffJson(before, edited)),
        };
        var undescribed = changed.Concat(added).Concat(removed)
            .Where(part => !part.StartsWith("Stories/", StringComparison.Ordinal) && !part.StartsWith("Spreads/", StringComparison.Ordinal) && !part.StartsWith("MasterSpreads/", StringComparison.Ordinal))
            .ToList();
        if (undescribed.Count > 0)
        {
            report["note"] = $"The diff compares stories, links, page count and page items. Changes in {string.Join(", ", undescribed)} (styles, colours, layers, preferences) are not described there; they are listed only in the parts above.";
        }
        return JsonText.Write(report);
    }
}

/// <summary>JSON helpers shared by the tools (relaxed escaping: accents and quotes stay readable).</summary>
internal static class JsonText
{
    public static string Write(JsonNode? node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, IdmlJson.WriterOptions))
        {
            if (node is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                node.WriteTo(writer);
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, IdmlJson.WriterOptions))
        {
            write(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The same JSON, indented (for <c>--pretty</c>).</summary>
    public static string Indent(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, IdmlJson.WriterOptions with { Indented = true }))
        {
            document.RootElement.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string String(string value) => Write(writer => writer.WriteStringValue(value));

    public static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)value)]);

    public static JsonArray Array(IEnumerable<int> values) => new([.. values.Select(value => (JsonNode)value)]);

    public static JsonNode? Parse(string json) => JsonNode.Parse(json);

    public const string Ok = """{"ok":true}""";
}

/// <summary>Builds the JSON schemas of the tools from property fragments, adding the arguments every tool shares.</summary>
internal static class Schema
{
    public const string PathProperty = """ "path":{"type":"string","description":"Absolute .idml path"}""";

    internal const string EditProperties =
        """ "outPath":{"type":"string","description":"New file for the result; else inPlace or dryRun"},""" +
        """ "inPlace":{"type":"boolean","description":"Overwrite path"},""" +
        """ "dryRun":{"type":"boolean","description":"Preview: save nothing"}""";

    /// <summary>Schema of a tool reading one document (path plus <paramref name="properties"/>).</summary>
    public static string Read(string properties = "", params string[] required) => Object(Join(PathProperty, properties), ["path", .. required]);

    /// <summary>Schema of an edit tool: path, its own properties, outPath, inPlace and dryRun.</summary>
    public static string Edit(string properties = "", params string[] required) => Object(Join(PathProperty, properties, EditProperties), ["path", .. required]);

    public static string Object(string properties, IEnumerable<string> required) =>
        "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + string.Join(",", required.Select(name => "\"" + name + "\"")) + "]}";

    private static string Join(params string[] fragments) => string.Join(",", fragments.Where(fragment => fragment.Length > 0));
}
