using System.Text;
using System.Text.Json;
using CoreIdml.Cli.Tools;

namespace CoreIdml.Cli;

/// <summary>Options of <c>coreidml mcp</c>.</summary>
internal sealed record McpServerOptions
{
    /// <summary>Base directory for relative paths (<c>--root</c>); null means absolute paths or the client's roots.</summary>
    public string? Root { get; init; }
}

/// <summary>
/// A Model Context Protocol server over stdio (JSON-RPC 2.0, one message per line): initialize (with version
/// negotiation), ping, tools/list and tools/call, prompts/list and prompts/get, and the client's roots (used to
/// resolve relative paths when <c>--root</c> is not given). Tool failures come back in-band as <c>isError</c> results,
/// as MCP expects, so an agent sees the message and can correct itself.
/// </summary>
internal sealed class McpServer
{
    /// <summary>Protocol versions this server speaks, newest first.</summary>
    public static readonly IReadOnlyList<string> SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private const string RootsRequestPrefix = "idml-roots-";

    private readonly PathResolver paths;
    private string protocolVersion = SupportedVersions[0];
    private bool clientHasRoots;
    private int rootsRequests;

    public McpServer(McpServerOptions? options = null)
    {
        paths = new PathResolver(options?.Root);
    }

    /// <summary>The paths the tools resolve (for tests).</summary>
    internal PathResolver Paths => paths;

    /// <summary>The negotiated protocol version.</summary>
    public string ProtocolVersion => protocolVersion;

    public async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            foreach (var message in Handle(line))
            {
                await output.WriteLineAsync(message).ConfigureAwait(false);
            }
            await output.FlushAsync().ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>Handles one incoming line; returns the messages to send (a response, requests to the client, or nothing).</summary>
    public IReadOnlyList<string> Handle(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }
        JsonDocument request;
        try
        {
            request = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            return [Error(null, -32700, "Parse error: " + exception.Message)];
        }
        using (request)
        {
            var root = request.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [Error(null, -32600, "Invalid request: expected a JSON-RPC object.")];
            }
            var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null ? idElement.Clone() : (JsonElement?)null;
            var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String ? methodElement.GetString() : null;
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;
            if (method is null)
            {
                // A response to one of our requests (roots/list).
                if (id is { ValueKind: JsonValueKind.String } responseId && responseId.GetString()!.StartsWith(RootsRequestPrefix, StringComparison.Ordinal)
                    && root.TryGetProperty("result", out var result))
                {
                    ReadRoots(result);
                }
                return [];
            }
            if (id is null)
            {
                return method switch
                {
                    "notifications/initialized" or "notifications/roots/list_changed" when clientHasRoots && paths.Root is null => [RootsRequest()],
                    _ => [],
                };
            }
            return method switch
            {
                "initialize" => [Initialize(id, parameters)],
                "ping" => [Result(id, _ => { })],
                "tools/list" => [Result(id, WriteTools)],
                "tools/call" => [CallTool(id, parameters)],
                "prompts/list" => [Result(id, McpPrompts.WriteList)],
                "prompts/get" => [GetPrompt(id, parameters)],
                _ => [Error(id, -32601, $"Method not found: {method}")],
            };
        }
    }

    /// <summary>The requested version when supported, else the newest one this server supports.</summary>
    public static string Negotiate(string? requested) =>
        requested is not null && SupportedVersions.Contains(requested, StringComparer.Ordinal) ? requested : SupportedVersions[0];

    private bool AtLeast(string version) => string.CompareOrdinal(protocolVersion, version) >= 0;

    private string Initialize(JsonElement? id, JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        protocolVersion = Negotiate(requested);
        clientHasRoots = parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("roots", out _);
        return Result(id, writer =>
        {
            writer.WriteString("protocolVersion", protocolVersion);
            writer.WriteStartObject("capabilities");
            writer.WriteStartObject("tools");
            writer.WriteBoolean("listChanged", false);
            writer.WriteEndObject();
            writer.WriteStartObject("prompts");
            writer.WriteBoolean("listChanged", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject("serverInfo");
            writer.WriteString("name", "coreidml");
            if (AtLeast("2025-06-18"))
            {
                writer.WriteString("title", "InDesign IDML tools");
            }
            writer.WriteString("version", IdmlDocument.LibraryVersion);
            writer.WriteEndObject();
            if (AtLeast("2025-03-26"))
            {
                writer.WriteString("instructions", McpPrompts.Instructions);
            }
        });
    }

    private void WriteTools(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("tools");
        foreach (var tool in ToolCatalog.All)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            if (AtLeast("2025-06-18"))
            {
                writer.WriteString("title", tool.Title);
            }
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            writer.WriteRawValue(tool.InputSchema);
            if (tool.OutputSchema is { } outputSchema && AtLeast("2025-06-18"))
            {
                writer.WritePropertyName("outputSchema");
                writer.WriteRawValue(outputSchema);
            }
            if (AtLeast("2025-03-26"))
            {
                writer.WriteStartObject("annotations");
                writer.WriteString("title", tool.Title);
                writer.WriteBoolean("readOnlyHint", tool.Access == ToolAccess.Read);
                writer.WriteBoolean("destructiveHint", tool.Access == ToolAccess.Edit);
                writer.WriteBoolean("idempotentHint", tool.Idempotent);
                writer.WriteBoolean("openWorldHint", false);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private string CallTool(JsonElement? id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return Error(id, -32602, "tools/call needs params.name.");
        }
        var name = nameElement.GetString()!;
        var tool = ToolCatalog.Find(name);
        if (tool is null)
        {
            return Error(id, -32602, $"Unknown tool: {name}");
        }
        var arguments = parameters.TryGetProperty("arguments", out var argumentsElement) ? argumentsElement : default;
        ToolResult? result = null;
        string? error = null;
        try
        {
            result = ToolCatalog.Run(name, arguments, paths);
        }
#pragma warning disable CA1031 // Every failure is reported to the agent in-band.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            error = ToolException.Describe(exception);
        }
        return Result(id, writer =>
        {
            writer.WriteStartArray("content");
            if (result?.Png is { } png)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteBase64String("data", png);
                writer.WriteString("mimeType", "image/png");
                writer.WriteEndObject();
            }
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", result?.Json ?? error);
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (result is { MatchesOutputSchema: true, IsObject: true } && tool.OutputSchema is not null && AtLeast("2025-06-18"))
            {
                writer.WritePropertyName("structuredContent");
                writer.WriteRawValue(result.Json);
            }
            if (error is not null)
            {
                writer.WriteBoolean("isError", true);
            }
        });
    }

    private static string GetPrompt(JsonElement? id, JsonElement parameters)
    {
        var name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()!
            : null;
        var arguments = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object
            ? argumentsElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.ToString(), StringComparer.Ordinal)
            : [];
        var prompt = name is null ? null : McpPrompts.Find(name);
        if (prompt is null)
        {
            return Error(id, -32602, $"Unknown prompt: {name}");
        }
        var missing = prompt.Arguments.Where(argument => argument.Required && !arguments.ContainsKey(argument.Name)).Select(argument => argument.Name).ToList();
        if (missing.Count > 0)
        {
            return Error(id, -32602, $"Prompt '{name}' needs: {string.Join(", ", missing)}.");
        }
        return Result(id, writer =>
        {
            writer.WriteString("description", prompt.Description);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartObject("content");
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt.Render(arguments));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        });
    }

    private string RootsRequest() => Write(writer =>
    {
        writer.WriteString("id", RootsRequestPrefix + (++rootsRequests).ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("method", "roots/list");
    });

    private void ReadRoots(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        var directories = new List<string>();
        foreach (var root in roots.EnumerateArray())
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("uri", out var uriElement) && uriElement.ValueKind == JsonValueKind.String
                && Uri.TryCreate(uriElement.GetString(), UriKind.Absolute, out var uri) && uri.IsFile)
            {
                directories.Add(Path.GetFullPath(uri.LocalPath));
            }
        }
        paths.ClientRoots = directories;
    }

    private static string Result(JsonElement? id, Action<Utf8JsonWriter> writeResult) => Write(id, writer =>
    {
        writer.WriteStartObject("result");
        writeResult(writer);
        writer.WriteEndObject();
    });

    private static string Error(JsonElement? id, int code, string message) => Write(id, writer =>
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    });

    private static string Write(JsonElement? id, Action<Utf8JsonWriter> body) => Write(writer =>
    {
        writer.WritePropertyName("id");
        if (id is { } value)
        {
            value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
        body(writer);
    });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, CoreIdml.Json.IdmlJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
