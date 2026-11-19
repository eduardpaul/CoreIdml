using System.Text.RegularExpressions;
using CoreIdml.TestSupport;

namespace CoreIdml.Abi.Tests;

/// <summary>
/// The JSON shapes of <c>@coreidml/node</c> and <c>@coreidml/wasm</c> are written twice (bindings/*/src/types.ts),
/// with documentation for each platform: paths on disk in Node, paths of the in-memory file system in WebAssembly.
/// The declarations themselves must be the same, so that the two packages accept and return the same JSON.
/// </summary>
public partial class TypeScriptTypesTests
{
    /// <summary>Declared in only one of the files, on purpose: Node declares PdfExport in its index.ts.</summary>
    private static readonly string[] OnlyInWasm = ["PdfExport"];

    [Fact]
    public void Node_and_wasm_declare_the_same_json_shapes()
    {
        var node = Declarations(Path.Combine(TestFiles.RepositoryRoot, "bindings", "node", "src", "types.ts"));
        var wasm = Declarations(Path.Combine(TestFiles.RepositoryRoot, "bindings", "wasm", "src", "types.ts"));

        Assert.Equal(node.Keys.Order(StringComparer.Ordinal), wasm.Keys.Except(OnlyInWasm).Order(StringComparer.Ordinal));
        var different = node.Where(pair => wasm[pair.Key] != pair.Value).Select(pair => pair.Key).ToList();
        Assert.True(different.Count == 0, $"Declared differently in bindings/node/src/types.ts and bindings/wasm/src/types.ts: {string.Join(", ", different)}");
        Assert.True(node.Count > 20, $"Only {node.Count} declarations were read; the parser no longer matches the files.");
    }

    /// <summary>Each exported declaration by name, with comments removed and white space collapsed.</summary>
    private static Dictionary<string, string> Declarations(string path)
    {
        var text = BlockComment().Replace(File.ReadAllText(path), " ");
        text = LineComment().Replace(text, string.Empty);
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        var starts = Declaration().Matches(text).ToList();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            var body = Whitespace().Replace(text[starts[i].Index..end], " ").Trim();
            Assert.True(declarations.TryAdd(starts[i].Groups["name"].Value, body), $"{starts[i].Groups["name"].Value} is declared twice in {path}.");
        }
        return declarations;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^export\s+(?:interface|type)\s+(?<name>\w+)", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
