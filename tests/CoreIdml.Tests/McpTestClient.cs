using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIdml.Cli;

namespace CoreIdml.Tests;

/// <summary>Drives the MCP server in-process with JSON-RPC lines, like a client would.</summary>
internal sealed class McpTestClient
{
    private int nextId;

    public McpTestClient(string? root = null, string protocolVersion = "2025-06-18", bool initialize = true)
    {
        Server = new McpServer(new McpServerOptions { Root = root });
        if (initialize)
        {
            Initialize = Request("initialize", new JsonObject { ["protocolVersion"] = protocolVersion, ["capabilities"] = new JsonObject() });
            Assert.Empty(Server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        }
    }

    public McpServer Server { get; }

    public JsonElement Initialize { get; }

    /// <summary>Sends a request and returns its result (fails the test on a JSON-RPC error).</summary>
    public JsonElement Request(string method, JsonObject? parameters = null)
    {
        var response = Send(method, parameters);
        Assert.False(response.TryGetProperty("error", out var error), error.ToString());
        return response.GetProperty("result");
    }

    /// <summary>Sends a request and returns the whole response.</summary>
    public JsonElement Send(string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++nextId, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }
        var responses = Server.Handle(message.ToJsonString());
        Assert.Single(responses);
        // Over stdio every message is one line: a line break inside one (a multi-line schema, say) breaks the client.
        Assert.DoesNotContain('\n', responses[0]);
        return Parse(responses[0]);
    }

    /// <summary>Calls a tool and returns its result object (content, isError, structuredContent).</summary>
    public JsonElement Call(string tool, JsonObject arguments) =>
        Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });

    /// <summary>Calls a tool that must succeed; returns its text content parsed as JSON.</summary>
    public JsonElement CallJson(string tool, JsonObject arguments) => Parse(CallText(tool, arguments));

    public string CallText(string tool, JsonObject arguments)
    {
        var result = Call(tool, arguments);
        var text = Text(result);
        Assert.False(result.TryGetProperty("isError", out _), text);
        return text;
    }

    /// <summary>Calls a tool that must fail; returns the error message.</summary>
    public string CallError(string tool, JsonObject arguments)
    {
        var result = Call(tool, arguments);
        Assert.True(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), "expected an error: " + Text(result));
        return Text(result);
    }

    public static string Text(JsonElement result) =>
        result.GetProperty("content").EnumerateArray().First(content => content.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Runs the command line in-process with in-memory streams.</summary>
internal static class CliRunner
{
    public static (int ExitCode, string Out, string Error, byte[] OutBytes) RunWithInput(string stdin, params string[] args) => RunWithInput(Encoding.UTF8.GetBytes(stdin), args);

    public static (int ExitCode, string Out, string Error, byte[] OutBytes) RunWithInput(byte[] stdin, params string[] args)
    {
        using var input = new MemoryStream(stdin);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var exitCode = Cli.Cli.Run(args, new CliConsole(input, output, error));
        var bytes = output.ToArray();
        return (exitCode, Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(error.ToArray()), bytes);
    }

    public static (int ExitCode, string Out, string Error, byte[] OutBytes) Run(params string[] args) => RunWithInput([], args);
}
