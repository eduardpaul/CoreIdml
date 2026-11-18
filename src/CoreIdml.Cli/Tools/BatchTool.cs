using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoreIdml.Cli.Tools;

/// <summary>idml_batch: several edits in one open/save, all or nothing.</summary>
internal static class BatchTool
{
    private static readonly string[] SessionArguments = ["path", "outPath", "inPlace", "dryRun"];

    public static ToolDefinition Definition { get; } = new()
    {
        Name = "idml_batch",
        Title = "Batch edits",
        Description = "Run several edit tools on one document in a single open and save, in order. Atomic: if any operation fails, nothing is saved and the error names the operation. Each operation is {tool, arguments} with the tool's own arguments minus path/outPath/inPlace/dryRun, which belong to the batch. With dryRun: true, returns every result and the diff without saving.",
        InputSchema = Schema.Edit(
            """ "operations":{"type":"array","minItems":1,"items":{"type":"object","properties":{"tool":{"type":"string","description":"An edit tool, e.g. idml_set_text, idml_edit_item, idml_apply_style"},"arguments":{"type":"object"}},"required":["tool"]}}""",
            "operations"),
        OutputSchema = """{"type":"object","properties":{"operations":{"type":"integer"},"results":{"type":"array","items":{"type":"object","properties":{"tool":{"type":"string"},"result":{}}}}},"required":["operations","results"]}""",
        Access = ToolAccess.Edit,
        Apply = Apply,
    };

    private static string Apply(EditContext edit)
    {
        var operations = edit.Args.Array("operations");
        if (operations.Count == 0)
        {
            throw ToolException.Invalid("Argument 'operations' is empty.");
        }
        var results = new JsonArray();
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = edit.Args.With(operations[index]);
            var name = operation.String("tool");
            var tool = ToolCatalog.Find(name);
            if (tool?.Apply is not { } apply || tool.Name == Definition.Name)
            {
                throw ToolException.Invalid($"Operation {index}: '{name}' is not an edit tool that can run in a batch (edit tools: {string.Join(", ", ToolCatalog.All.Where(candidate => candidate.Apply is not null && candidate.Name != Definition.Name).Select(candidate => candidate.Name))}).");
            }
            var arguments = operation.Get("arguments") ?? default;
            if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            {
                throw ToolException.Invalid($"Operation {index} ({name}): 'arguments' must be an object.");
            }
            if (arguments.ValueKind == JsonValueKind.Object && SessionArguments.FirstOrDefault(key => arguments.TryGetProperty(key, out _)) is { } misplaced)
            {
                throw ToolException.Invalid($"Operation {index} ({name}): '{misplaced}' belongs to the batch itself, not to its operations.");
            }
            string result;
            try
            {
                result = apply(edit with { Args = edit.Args.With(arguments) });
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new ToolException(ToolException.KindOf(exception), $"Operation {index} ({name}) failed, nothing was saved: {ToolException.Describe(exception)}");
            }
            results.Add((JsonNode)new JsonObject { ["tool"] = name, ["result"] = JsonNode.Parse(result) });
        }
        return JsonText.Write(new JsonObject { ["operations"] = operations.Count, ["results"] = results });
    }
}
