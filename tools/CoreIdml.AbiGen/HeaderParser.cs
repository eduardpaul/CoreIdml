using System.Globalization;
using System.Text.RegularExpressions;

namespace CoreIdml.AbiGen;

/// <summary>
/// Reads <c>include/coreidml.h</c>. It is not a C parser: the header is written in one regular shape (a comment,
/// then <c>COREIDML_API type name(parameters);</c>) and anything outside that shape is an error, not a guess.
/// </summary>
public static partial class HeaderParser
{
    private static readonly Dictionary<string, AbiType> Types = new(StringComparer.Ordinal)
    {
        ["void"] = AbiType.Nothing,
        ["int32_t"] = AbiType.Number,
        ["size_t"] = AbiType.Length,
        ["const char*"] = AbiType.Text,
        ["char**"] = AbiType.TextOut,
        ["const uint8_t*"] = AbiType.Data,
        ["uint8_t**"] = AbiType.DataOut,
        ["size_t*"] = AbiType.LengthOut,
        ["void*"] = AbiType.Memory,
        ["coreidml_document"] = AbiType.Document,
        ["coreidml_document*"] = AbiType.DocumentOut,
        ["const int32_t*"] = AbiType.Numbers,
        ["int32_t*"] = AbiType.NumberOut,
        ["coreidml_progress_callback"] = AbiType.Progress,
    };

    /// <summary>
    /// The one callback type, exactly as every emitter maps it. A header that changes it must change the emitters
    /// too, so a different declaration is refused.
    /// </summary>
    public const string ProgressTypedef = "typedef int32_t (*coreidml_progress_callback)(int32_t done, int32_t total, void* user_data);";

    /// <summary>Parses the text of the header.</summary>
    /// <exception cref="FormatException">The header uses something the generator does not know.</exception>
    public static AbiDefinition Parse(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        header = header.ReplaceLineEndings("\n");

        var version = VersionPattern().Match(header);
        if (!version.Success)
        {
            throw new FormatException("The header does not define COREIDML_ABI_VERSION.");
        }

        if (header.Contains("coreidml_progress_callback", StringComparison.Ordinal) && !header.Contains(ProgressTypedef, StringComparison.Ordinal))
        {
            throw new FormatException($"coreidml_progress_callback must be declared exactly as: {ProgressTypedef}");
        }

        List<AbiStatus> statuses = [];
        foreach (Match match in StatusPattern().Matches(header))
        {
            statuses.Add(new AbiStatus(match.Groups["name"].Value, int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)));
        }

        List<AbiFunction> functions = [];
        foreach (Match match in FunctionPattern().Matches(header))
        {
            var name = match.Groups["name"].Value;
            List<AbiParameter> parameters = [];
            var list = match.Groups["params"].Value.Trim();
            if (list is not ("void" or ""))
            {
                foreach (var parameter in list.Split(','))
                {
                    var text = parameter.Trim();
                    var split = ParameterNamePattern().Match(text);
                    if (!split.Success)
                    {
                        throw new FormatException($"{name}: cannot read the parameter '{text}'.");
                    }
                    parameters.Add(new AbiParameter(split.Groups["name"].Value, ParseType(split.Groups["type"].Value, $"{name}, parameter {split.Groups["name"].Value}")));
                }
            }
            functions.Add(new AbiFunction(name, ParseType(match.Groups["ret"].Value, $"{name}, return type"), parameters, CleanComment(match.Groups["doc"].Value)));
        }

        // Every COREIDML_API line must have been understood: a declaration the pattern skipped would silently
        // be missing from every binding.
        var declared = DeclarationPattern().Count(header);
        if (declared != functions.Count)
        {
            throw new FormatException($"The header has {declared} COREIDML_API declarations but only {functions.Count} could be read; keep each one in the form 'COREIDML_API type name(parameters);'.");
        }
        return new AbiDefinition(int.Parse(version.Groups["value"].Value, CultureInfo.InvariantCulture), statuses, functions);
    }

    private static AbiType ParseType(string text, string where)
    {
        // "const char *" and "const char*" are the same type.
        var normalized = WhitespacePattern().Replace(text.Trim(), " ").Replace(" *", "*", StringComparison.Ordinal);
        return Types.TryGetValue(normalized, out var type)
            ? type
            : throw new FormatException($"{where}: the type '{normalized}' is not one the bindings know. Known types: {string.Join(", ", Types.Keys)}. Add it to AbiType and to every emitter first.");
    }

    private static string[] CleanComment(string comment) =>
        [.. comment.Split('\n').Select(line => line.Trim()).SkipWhile(line => line.Length == 0).Reverse().SkipWhile(line => line.Length == 0).Reverse()];

    [GeneratedRegex(@"^#define\s+COREIDML_ABI_VERSION\s+(?<value>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^#define\s+(?<name>COREIDML_OK|COREIDML_ERR_\w+)\s+\(?(?<value>-?\d+)\)?\s*$", RegexOptions.Multiline)]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"(?:/\*(?<doc>(?:(?!\*/).)*)\*/[ \t]*\n)?^COREIDML_API\s+(?<ret>[^;()]+?)\s*\b(?<name>coreidml_\w+)\s*\((?<params>[^()]*)\)\s*;", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FunctionPattern();

    [GeneratedRegex(@"^COREIDML_API\b", RegexOptions.Multiline)]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"^(?<type>.+?)\s*\b(?<name>[a-z_][a-z0-9_]*)$")]
    private static partial Regex ParameterNamePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
