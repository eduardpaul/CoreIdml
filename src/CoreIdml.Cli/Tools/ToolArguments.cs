using System.Globalization;
using System.Text.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>
/// Turns path arguments into absolute paths. Absolute paths are used as they are. Relative paths resolve against
/// <see cref="Root"/> (the CLI's working directory, or <c>coreidml mcp --root</c>), else against the first root the
/// MCP client announced; with neither they are refused, because an MCP server's working directory is whatever the
/// client happened to start it in.
/// </summary>
internal sealed class PathResolver
{
    public PathResolver(string? root = null)
    {
        Root = root is null ? null : Path.GetFullPath(root);
    }

    /// <summary>Base directory for relative paths, or null to require absolute paths (unless the client has roots).</summary>
    public string? Root { get; }

    /// <summary>Directories the MCP client exposed through <c>roots/list</c>, in its order.</summary>
    public IReadOnlyList<string> ClientRoots { get; set; } = [];

    /// <summary>The absolute path for argument <paramref name="argument"/>.</summary>
    public string Resolve(string argument, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ToolException.Invalid($"Argument '{argument}' is an empty path.");
        }
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.LocalPath;
        }
        if (Path.IsPathFullyQualified(value))
        {
            return Path.GetFullPath(value);
        }
        var baseDirectory = Root ?? (ClientRoots.Count > 0 ? ClientRoots[0] : null);
        if (baseDirectory is null)
        {
            throw ToolException.Invalid(
                $"Argument '{argument}' is a relative path ('{value}'). Pass an absolute path: relative paths would resolve against " +
                $"the server's working directory ({Path.GetFullPath(value)}), which the MCP client chooses. Or start the server " +
                "with 'coreidml mcp --root <dir>'.");
        }
        return Path.GetFullPath(Path.Combine(baseDirectory, value));
    }

    /// <summary>Like <see cref="Resolve"/>, for a file that must exist; the error names the resolved path.</summary>
    public string ExistingFile(string argument, string value)
    {
        var path = Resolve(argument, value);
        if (!File.Exists(path))
        {
            throw ToolException.NotFound(Directory.Exists(path)
                ? $"Argument '{argument}': {path} is a folder, not a file."
                : $"Argument '{argument}': file not found: {path}" + (value == path ? string.Empty : $" (given as '{value}')"));
        }
        return path;
    }

    /// <summary>Like <see cref="Resolve"/>, for a folder that must exist.</summary>
    public string ExistingDirectory(string argument, string value)
    {
        var path = Resolve(argument, value);
        if (!Directory.Exists(path))
        {
            throw ToolException.NotFound($"Argument '{argument}': folder not found: {path}" + (value == path ? string.Empty : $" (given as '{value}')"));
        }
        return path;
    }
}

/// <summary>The arguments of one tool call: typed accessors whose errors name the argument and what it should be.</summary>
internal sealed class ToolArguments
{
    public ToolArguments(JsonElement json, PathResolver paths)
    {
        Json = json;
        Paths = paths;
        if (json.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw ToolException.Invalid("Tool arguments must be a JSON object.");
        }
    }

    public JsonElement Json { get; }

    public PathResolver Paths { get; }

    /// <summary>The argument's value, or null when it is absent or JSON null.</summary>
    public JsonElement? Get(string name) =>
        Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public bool Has(string name) => Get(name) is not null;

    public string? OptionalString(string name) => Get(name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw Expected(name, "a string"),
    };

    public string String(string name) => OptionalString(name) ?? throw Missing(name);

    public int? OptionalInt(string name) => Get(name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt32(out var number) => number,
        { ValueKind: JsonValueKind.String } value when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => throw Expected(name, "an integer"),
    };

    public int Int(string name) => OptionalInt(name) ?? throw Missing(name);

    public double? OptionalDouble(string name) => Get(name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Number } value => value.GetDouble(),
        { ValueKind: JsonValueKind.String } value when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => throw Expected(name, "a number"),
    };

    public double Double(string name) => OptionalDouble(name) ?? throw Missing(name);

    public bool? OptionalBool(string name) => Get(name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => throw Expected(name, "true or false"),
    };

    public bool Bool(string name) => OptionalBool(name) ?? false;

    /// <summary>A list of strings; a single string counts as a one-item list.</summary>
    public IReadOnlyList<string> Strings(string name) => Get(name) switch
    {
        null => [],
        { ValueKind: JsonValueKind.String } value => [value.GetString()!],
        { ValueKind: JsonValueKind.Array } value => value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Expected(name, "a list of strings"))
            .ToList(),
        _ => throw Expected(name, "a list of strings"),
    };

    /// <summary>A list of integers; a single integer counts as a one-item list.</summary>
    public IReadOnlyList<int> Ints(string name) => Get(name) switch
    {
        null => [],
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt32(out var number) => [number],
        { ValueKind: JsonValueKind.Array } value => value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) ? number : throw Expected(name, "a list of integers"))
            .ToList(),
        _ => throw Expected(name, "a list of integers"),
    };

    /// <summary>An object argument (or null).</summary>
    public ToolArguments? OptionalObject(string name) => Get(name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Object } value => new ToolArguments(value, Paths),
        _ => throw Expected(name, "an object"),
    };

    public ToolArguments Object(string name) => OptionalObject(name) ?? throw Missing(name);

    /// <summary>The items of an array argument.</summary>
    public IReadOnlyList<JsonElement> Array(string name) => Get(name) switch
    {
        null => [],
        { ValueKind: JsonValueKind.Array } value => value.EnumerateArray().ToList(),
        _ => throw Expected(name, "a list"),
    };

    /// <summary>A string-to-string map argument (e.g. fill data).</summary>
    public Dictionary<string, string> Map(string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Get(name) is not { } value)
        {
            return result;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Expected(name, "an object of strings");
        }
        foreach (var property in value.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : throw Expected(name + "." + property.Name, "a string");
        }
        return result;
    }

    /// <summary>A path argument, made absolute.</summary>
    public string Path(string name) => Paths.Resolve(name, String(name));

    public string? OptionalPath(string name) => OptionalString(name) is { } value ? Paths.Resolve(name, value) : null;

    /// <summary>A path argument naming a file that must exist.</summary>
    public string ExistingFile(string name) => Paths.ExistingFile(name, String(name));

    /// <summary>Path arguments naming folders that must exist (one or a list).</summary>
    public IReadOnlyList<string> ExistingDirectories(string name) => Strings(name).Select(value => Paths.ExistingDirectory(name, value)).ToList();

    public ToolArguments With(JsonElement json) => new(json, Paths);

    private static ToolException Missing(string name) => ToolException.Invalid($"Missing required argument '{name}'.");

    private static ToolException Expected(string name, string what) => ToolException.Invalid($"Argument '{name}' must be {what}.");
}
