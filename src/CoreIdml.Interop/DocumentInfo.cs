using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreIdml.Interop;

/// <summary>What <see cref="IdmlInterop.DocumentInfo"/> returns, as JSON with camelCase names.</summary>
/// <param name="DomVersion">The InDesign DOM version the document was written with, if it says.</param>
/// <param name="Parts">Every part of the package, in package order.</param>
/// <param name="Spreads">Paths of the spread parts, in document order.</param>
/// <param name="MasterSpreads">Paths of the master spread parts.</param>
/// <param name="Stories">Paths of the story parts.</param>
public sealed record DocumentInfo(
    string? DomVersion,
    IReadOnlyList<PartInfo> Parts,
    IReadOnlyList<string> Spreads,
    IReadOnlyList<string> MasterSpreads,
    IReadOnlyList<string> Stories);

/// <summary>One part of the package.</summary>
/// <param name="Path">Path inside the package, e.g. <c>Stories/Story_u188.xml</c>.</param>
/// <param name="Length">Size in bytes as it would be written now.</param>
/// <param name="Modified">Whether the part was added, replaced or changed since the document was opened.</param>
public sealed record PartInfo(string Path, long Length, bool Modified);

/// <summary>
/// Source-generated serialization for every payload that crosses a language boundary: no reflection, so it works
/// trimmed, under Native AOT and in the browser.
/// </summary>
[JsonSerializable(typeof(DocumentInfo))]
internal sealed partial class InteropJson : JsonSerializerContext
{
    /// <summary>camelCase names; text is written as UTF-8 rather than \u escapes, which programs and agents read better.</summary>
    public static InteropJson Contract { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
