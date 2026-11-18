namespace CoreIdml.AbiGen;

/// <summary>
/// The C types the ABI uses. The list is closed on purpose: a new kind of parameter has to be taught to every
/// emitter (and to the ABI tests) before a header that uses it is accepted.
/// </summary>
public enum AbiType
{
    /// <summary><c>void</c> (return only).</summary>
    Nothing,

    /// <summary><c>int32_t</c>: a status code or a small number.</summary>
    Number,

    /// <summary><c>size_t</c>: a length in bytes.</summary>
    Length,

    /// <summary><c>const char*</c>: UTF-8 text in, or text owned by the library when returned.</summary>
    Text,

    /// <summary><c>char**</c>: receives UTF-8 text the caller frees.</summary>
    TextOut,

    /// <summary><c>const uint8_t*</c>: bytes in.</summary>
    Data,

    /// <summary><c>uint8_t**</c>: receives a buffer the caller frees.</summary>
    DataOut,

    /// <summary><c>size_t*</c>: receives a length.</summary>
    LengthOut,

    /// <summary><c>void*</c>: memory handed back to the library.</summary>
    Memory,

    /// <summary><c>coreidml_document</c>: a document handle.</summary>
    Document,

    /// <summary><c>coreidml_document*</c>: receives a document handle.</summary>
    DocumentOut,

    /// <summary><c>const int32_t*</c>: numbers in, with their count in a separate <c>size_t</c>.</summary>
    Numbers,

    /// <summary><c>int32_t*</c>: receives a number.</summary>
    NumberOut,

    /// <summary><c>coreidml_progress_callback</c>: a function the library calls back; may be NULL.</summary>
    Progress,
}

/// <summary>A <c>#define COREIDML_OK</c> / <c>COREIDML_ERR_*</c> line.</summary>
/// <param name="CName">The macro, e.g. <c>COREIDML_ERR_INVALID_HANDLE</c>.</param>
/// <param name="Value">Its value.</param>
public sealed record AbiStatus(string CName, int Value)
{
    /// <summary>The words of the name without the prefix: <c>invalid</c>, <c>handle</c>.</summary>
    public IReadOnlyList<string> Words { get; } =
        CName == "COREIDML_OK" ? ["ok"] : [.. CName["COREIDML_ERR_".Length..].ToLowerInvariant().Split('_')];
}

/// <summary>One parameter of a function.</summary>
public sealed record AbiParameter(string Name, AbiType Type)
{
    /// <summary>The words of the snake_case name.</summary>
    public IReadOnlyList<string> Words { get; } = Name.Split('_');
}

/// <summary>One <c>COREIDML_API</c> declaration.</summary>
/// <param name="CName">The exported symbol, e.g. <c>coreidml_document_open</c>.</param>
/// <param name="ReturnType">What it returns.</param>
/// <param name="Parameters">Its parameters, in order.</param>
/// <param name="Documentation">The comment above it, one entry per line.</param>
public sealed record AbiFunction(string CName, AbiType ReturnType, IReadOnlyList<AbiParameter> Parameters, IReadOnlyList<string> Documentation)
{
    /// <summary>The words of the name without the <c>coreidml_</c> prefix.</summary>
    public IReadOnlyList<string> Words { get; } = CName["coreidml_".Length..].Split('_');
}

/// <summary>Everything the header declares.</summary>
public sealed record AbiDefinition(int Version, IReadOnlyList<AbiStatus> Statuses, IReadOnlyList<AbiFunction> Functions);

/// <summary>Naming conventions of the target languages.</summary>
public static class Naming
{
    /// <summary><c>documentOpen</c>.</summary>
    public static string Camel(IReadOnlyList<string> words) => words[0] + string.Concat(words.Skip(1).Select(Capitalize));

    /// <summary><c>DocumentOpen</c>.</summary>
    public static string Pascal(IReadOnlyList<string> words) => string.Concat(words.Select(Capitalize));

    /// <summary><c>DOCUMENT_OPEN</c>.</summary>
    public static string UpperSnake(IReadOnlyList<string> words) => string.Join('_', words).ToUpperInvariant();

    private static string Capitalize(string word) => char.ToUpperInvariant(word[0]) + word[1..];
}
