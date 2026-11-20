using System.Text.Json;

namespace CoreIdml.Json;

public static partial class IdmlJson
{
    private static readonly string[] TopFields = ["unit", "page", "colors", "paragraphStyles", "characterStyles", "pages"];
    private static readonly string[] PageSetupFields = ["size", "width", "height", "landscape", "margins", "facingPages", "columns", "columnGutter", "bleed", "baselineGrid"];
    private static readonly string[] BaselineGridFields = ["start", "increment", "relativeTo"];
    private static readonly string[] SpacingFields = ["all", "top", "right", "bottom", "left"];
    private static readonly string[] ColorFields = ["name", "cmyk", "rgb", "hex"];
    private static readonly string[] CharacterFormatFields =
    [
        "font", "fontStyle", "size", "color", "tint", "underline", "strikeThrough", "tracking", "caps", "position", "stroke", "strokeWeight",
        "strokeTint", "skew", "underlineWeight", "underlineOffset", "underlineColor", "underlineTint", "underlineType", "strikeThroughWeight",
        "strikeThroughOffset", "strikeThroughColor", "strikeThroughTint", "strikeThroughType",
    ];
    private static readonly string[] ParagraphOnlyFields =
    [
        "leading", "align", "spaceBefore", "spaceAfter", "firstLineIndent", "leftIndent", "rightIndent",
        "list", "bullet", "bulletAfter", "numberFormat", "numberText", "listLevel", "startAt", "tabs",
        "keepWithNext", "keepTogether", "keepFirstLines", "keepLastLines", "startParagraph", "dropCapLines", "dropCapCharacters",
        "dropCapStyle", "nestedStyles", "grepStyles", "alignToBaselineGrid", "gridFirstLineOnly", "spanColumns", "splitColumns", "splitGutter",
    ];
    private static readonly string[] PageFields = ["index", "margins", "items"];
    private static readonly string[] CommonItemFields =
    [
        "type", "name", "label", "tag", "layer", "x", "y", "width", "height", "fill", "fillTint", "stroke", "strokeWeight",
        "strokeTint", "strokeType", "endCap", "opacity", "cornerRadius", "rotation", "wrap", "wrapOffset", "wrapSide", "wrapInverse",
    ];
    private static readonly string[] TextItemFields =
    [
        "text", "style", "paragraphs", "columns", "columnGutter", "inset", "verticalAlign", "continueFrom", "autoSize", "autoSizeFrom",
        "autoSizeMinHeight", "balanceColumns", "firstBaseline", "firstBaselineMin", "ignoreWrap",
    ];
    private static readonly string[] TableItemFields = ["rows", "columnWidths", "headerRows", "rowHeight", "cellStyle", "headerStyle", "headerFill", "alternateFill", "cellInset"];
    private static readonly string[] ImageItemFields = ["path", "fit"];
    private static readonly string[] LineItemFields = ["x2", "y2"];

    // Names other formats use for a field of the spec (CSS, other layout APIs), so the warning can say which one is meant.
    private static readonly Dictionary<string, string> KnownAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fontSize"] = "size", ["pointSize"] = "size", ["fontFamily"] = "font", ["family"] = "font", ["fontWeight"] = "fontStyle",
        ["weight"] = "fontStyle", ["lineHeight"] = "leading", ["textAlign"] = "align", ["alignment"] = "align",
        ["justification"] = "align", ["letterSpacing"] = "tracking", ["textTransform"] = "caps", ["fontColor"] = "color",
        ["textColor"] = "color", ["fillColor"] = "fill", ["background"] = "fill", ["backgroundColor"] = "fill",
        ["strokeColor"] = "stroke", ["border"] = "stroke", ["borderColor"] = "stroke", ["outline"] = "stroke",
        ["strokeWidth"] = "strokeWeight", ["borderWidth"] = "strokeWeight", ["lineWidth"] = "strokeWeight",
        ["radius"] = "cornerRadius", ["borderRadius"] = "cornerRadius", ["alpha"] = "opacity", ["transparency"] = "opacity",
        ["lineCap"] = "endCap", ["cap"] = "endCap", ["dash"] = "strokeType", ["strokeStyle"] = "strokeType",
        ["src"] = "path", ["file"] = "path", ["image"] = "path", ["url"] = "path", ["kind"] = "type", ["id"] = "name",
        ["left"] = "x", ["top"] = "y", ["w"] = "width", ["h"] = "height", ["children"] = "items", ["elements"] = "items",
        ["objects"] = "items", ["content"] = "text", ["paragraphStyle"] = "style", ["characterStyle"] = "style",
        ["styles"] = "paragraphStyles", ["swatches"] = "colors", ["colours"] = "colors", ["pageSize"] = "size",
        ["orientation"] = "landscape", ["verticalAlignment"] = "verticalAlign", ["gutter"] = "columnGutter",
        ["padding"] = "inset", ["header"] = "headerRows", ["cells"] = "rows", ["data"] = "rows", ["parent"] = "basedOn",
        ["inherits"] = "basedOn", ["based_on"] = "basedOn", ["bold"] = "fontStyle", ["italic"] = "fontStyle",
        ["listType"] = "list", ["listStyleType"] = "list", ["bullets"] = "list", ["numbering"] = "list", ["numbered"] = "list",
        ["bulletChar"] = "bullet", ["bulletCharacter"] = "bullet", ["marker"] = "bullet", ["bulletText"] = "bulletAfter",
        ["numberingFormat"] = "numberFormat", ["numberStyle"] = "numberFormat", ["numberingExpression"] = "numberText",
        ["numberExpression"] = "numberText", ["level"] = "listLevel", ["numberingLevel"] = "listLevel", ["start"] = "startAt",
        ["numberingStartAt"] = "startAt", ["tabStops"] = "tabs", ["tabList"] = "tabs", ["stops"] = "tabs", ["leaderChar"] = "leader",
        ["fill"] = "leader", ["alignmentCharacter"] = "alignOn", ["decimalChar"] = "alignOn", ["pos"] = "position", ["x"] = "position",
        ["keepWithNextLines"] = "keepWithNext", ["keepLinesTogether"] = "keepTogether", ["keepAllLinesTogether"] = "keepTogether",
        ["orphans"] = "keepFirstLines", ["widows"] = "keepLastLines", ["startOn"] = "startParagraph",
        ["dropCap"] = "dropCapLines", ["dropCaps"] = "dropCapLines", ["dropCapCharacterStyle"] = "dropCapStyle", ["nested"] = "nestedStyles",
        ["grep"] = "expression", ["regex"] = "expression", ["pattern"] = "expression", ["grepStyle"] = "grepStyles", ["nestedStyle"] = "nestedStyles",
        ["alignToGrid"] = "alignToBaselineGrid", ["baselineGridAlign"] = "alignToBaselineGrid", ["gridAlignment"] = "alignToBaselineGrid",
        ["snapToBaselineGrid"] = "alignToBaselineGrid", ["firstLineOnly"] = "gridFirstLineOnly", ["gridAlignFirstLineOnly"] = "gridFirstLineOnly",
        ["span"] = "spanColumns", ["spanAcrossColumns"] = "spanColumns", ["columnSpan"] = "spanColumns", ["split"] = "splitColumns",
        ["splitColumnGutter"] = "splitGutter", ["delimiter"] = "through", ["until"] = "upTo", ["repetition"] = "count",
        ["times"] = "count", ["baselineStart"] = "start", ["offset"] = "start", ["baselineDivision"] = "increment", ["division"] = "increment",
        ["gridIncrement"] = "increment", ["relativeOption"] = "relativeTo", ["baseline"] = "baselineGrid",
        ["textStroke"] = "stroke", ["slant"] = "skew", ["oblique"] = "skew", ["italicAngle"] = "skew", ["underlineThickness"] = "underlineWeight",
        ["underlinePosition"] = "underlineOffset", ["underlineStyle"] = "underlineType", ["strikeWeight"] = "strikeThroughWeight",
        ["strikeColor"] = "strikeThroughColor", ["strikeThroughStyle"] = "strikeThroughType", ["strikeThruWeight"] = "strikeThroughWeight",
        ["textWrap"] = "wrap", ["wrapMode"] = "wrap", ["textWrapMode"] = "wrap", ["wrapOffsets"] = "wrapOffset", ["textWrapOffset"] = "wrapOffset",
        ["wrapGap"] = "wrapOffset", ["textWrapSide"] = "wrapSide", ["inverse"] = "wrapInverse", ["invert"] = "wrapInverse", ["autoSizing"] = "autoSize",
        ["autoSizingType"] = "autoSize", ["autoFit"] = "autoSize", ["autoGrow"] = "autoSize", ["autoSizingReferencePoint"] = "autoSizeFrom",
        ["referencePoint"] = "autoSizeFrom", ["anchor"] = "autoSizeFrom", ["minHeight"] = "autoSizeMinHeight", ["minimumHeight"] = "autoSizeMinHeight",
        ["balance"] = "balanceColumns", ["verticalBalanceColumns"] = "balanceColumns", ["firstBaselineOffset"] = "firstBaseline",
        ["minimumFirstBaselineOffset"] = "firstBaselineMin", ["ignoreTextWrap"] = "ignoreWrap",
    };

    /// <summary>
    /// Checks a document spec (as for <see cref="CreateDocument(string?)"/> and <see cref="Apply(IdmlDocument, string)"/>)
    /// for what building it would silently ignore: fields the spec does not have (with the field probably meant), fields
    /// that do not apply where they are (a table's rows on a text frame, a paragraph's alignment on a run), and values of
    /// the wrong JSON type. Unknown names of styles and colours, and unknown values, are errors when the document is built.
    /// Returns one message per problem, with its place in the spec ("pages[0].items[2]: ..."); empty when there is none.
    /// </summary>
    public static IReadOnlyList<string> CheckSpec(string? json)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return warnings;
        }
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = parsed.RootElement;
        if (!ExpectObject(root, "spec", warnings))
        {
            return warnings;
        }
        CheckFields(root, "spec", TopFields, warnings);
        if (Property(root, "page") is { } page && ExpectObject(page, "page", warnings))
        {
            CheckFields(page, "page", PageSetupFields, warnings);
            CheckSpacing(page, "page", warnings);
            if (Property(page, "baselineGrid") is { } grid && ExpectObject(grid, "page.baselineGrid", warnings))
            {
                CheckFields(grid, "page.baselineGrid", BaselineGridFields, warnings);
            }
        }
        ForEach(root, "colors", warnings, (color, path) => CheckFields(color, path, ColorFields, warnings));
        ForEach(root, "characterStyles", warnings, (style, path) =>
            CheckFields(style, path, ["name", "basedOn", .. CharacterFormatFields], warnings, ParagraphOnlyFields, "a character style"));
        ForEach(root, "paragraphStyles", warnings, (style, path) =>
        {
            CheckFields(style, path, ["name", "basedOn", .. CharacterFormatFields, .. ParagraphOnlyFields], warnings);
            CheckTabs(style, path, warnings);
            CheckCharacterStyleLists(style, path, warnings);
        });
        ForEach(root, "pages", warnings, (pageSpec, pagePath) =>
        {
            CheckFields(pageSpec, pagePath, PageFields, warnings);
            CheckSpacing(pageSpec, pagePath, warnings);
            ForEach(pageSpec, "items", warnings, (item, itemPath) => CheckItem(item, itemPath, warnings), pagePath);
        });
        return warnings;
    }

    private static void CheckItem(JsonElement item, string path, List<string> warnings)
    {
        var type = Property(item, "type") is { ValueKind: JsonValueKind.String } typeValue ? typeValue.GetString()!.ToLowerInvariant() : "text";
        string[] own = type switch
        {
            "text" => TextItemFields,
            "table" => TableItemFields,
            "image" => ImageItemFields,
            "line" => LineItemFields,
            _ => [],
        };
        string[] others = [.. TextItemFields, .. TableItemFields, .. ImageItemFields, .. LineItemFields];
        CheckFields(item, path, [.. CommonItemFields, .. own], warnings, [.. others.Except(own)], $"a {type} item");
        CheckSpacing(item, path, warnings, "inset");
        CheckSpacing(item, path, warnings, "wrapOffset");
        if (type == "text" && Property(item, "continueFrom") is not null && (Property(item, "text") ?? Property(item, "paragraphs")) is not null)
        {
            warnings.Add($"{path}: a frame with continueFrom shows the text of the frame it continues; its own text and paragraphs are ignored.");
        }
        ForEach(item, "paragraphs", warnings, (paragraph, paragraphPath) =>
        {
            CheckFields(paragraph, paragraphPath, ["text", "style", "runs", .. CharacterFormatFields, .. ParagraphOnlyFields], warnings);
            CheckTabs(paragraph, paragraphPath, warnings);
            CheckCharacterStyleLists(paragraph, paragraphPath, warnings);
            ForEach(paragraph, "runs", warnings, (run, runPath) =>
                CheckFields(run, runPath, ["text", "style", .. CharacterFormatFields], warnings, ParagraphOnlyFields, "a run (set it on the paragraph)"),
                paragraphPath);
        }, path);
    }

    private static readonly string[] TabFields = ["position", "align", "leader", "alignOn"];

    /// <summary>The fields of each stop in a paragraph's or paragraph style's <c>tabs</c>.</summary>
    private static void CheckTabs(JsonElement owner, string path, List<string> warnings) =>
        ForEach(owner, "tabs", warnings, (tab, tabPath) =>
        {
            CheckFields(tab, tabPath, TabFields, warnings);
            if (Property(tab, "position") is null)
            {
                warnings.Add($"{tabPath}: a tab stop needs a position; 0 is used.");
            }
        }, path);

    private static readonly string[] NestedStyleFields = ["style", "through", "upTo", "count"];
    private static readonly string[] GrepStyleFields = ["style", "expression"];

    /// <summary>The fields of each entry of a paragraph's or paragraph style's <c>nestedStyles</c> and <c>grepStyles</c>.</summary>
    private static void CheckCharacterStyleLists(JsonElement owner, string path, List<string> warnings)
    {
        ForEach(owner, "nestedStyles", warnings, (nested, nestedPath) => CheckFields(nested, nestedPath, NestedStyleFields, warnings), path);
        ForEach(owner, "grepStyles", warnings, (grep, grepPath) => CheckFields(grep, grepPath, GrepStyleFields, warnings), path);
    }

    private static void CheckSpacing(JsonElement owner, string path, List<string> warnings, string field = "margins")
    {
        if (Property(owner, field) is { } spacing)
        {
            if (spacing.ValueKind is JsonValueKind.Number)
            {
                warnings.Add($"{path}.{field}: give an object such as {{\"all\": {spacing.GetRawText()}}}, not a number; the number is ignored.");
            }
            else if (ExpectObject(spacing, $"{path}.{field}", warnings))
            {
                CheckFields(spacing, $"{path}.{field}", SpacingFields, warnings);
            }
        }
    }

    /// <summary>Warns about each property of <paramref name="element"/> that is not one of <paramref name="known"/>.</summary>
    private static void CheckFields(JsonElement element, string path, string[] known, List<string> warnings, string[]? misplaced = null, string? where = null)
    {
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            if (known.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (misplaced is not null && misplaced.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add($"{path}: '{name}' does not apply to {where}; it is ignored.");
                continue;
            }
            var hint = SuggestField(name, known);
            warnings.Add($"{path}: unknown field '{name}', ignored{(hint is null ? "" : $" (did you mean '{hint}'?)")}.");
        }
    }

    private static string? SuggestField(string name, string[] known)
    {
        if (KnownAliases.TryGetValue(name, out var alias) && known.Contains(alias, StringComparer.Ordinal))
        {
            return alias;
        }
        var lower = name.ToLowerInvariant();
        return known
            .Select(candidate => (candidate, distance: Distance(lower, candidate.ToLowerInvariant())))
            .Where(pair => pair.distance <= Math.Max(1, Math.Min(3, pair.candidate.Length / 3)))
            .OrderBy(pair => pair.distance)
            .Select(pair => pair.candidate)
            .FirstOrDefault();
    }

    /// <summary>Levenshtein distance.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        return null;
    }

    private static bool ExpectObject(JsonElement element, string path, List<string> warnings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        warnings.Add($"{path}: expected an object, found {Describe(element)}; it is ignored.");
        return false;
    }

    /// <summary>Calls <paramref name="check"/> on each object of the array <paramref name="name"/>, with its full path.</summary>
    private static void ForEach(JsonElement owner, string name, List<string> warnings, Action<JsonElement, string> check, string? ownerPath = null)
    {
        if (Property(owner, name) is not { } array)
        {
            return;
        }
        var arrayPath = ownerPath is null ? name : $"{ownerPath}.{name}";
        if (array.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"{arrayPath}: expected an array, found {Describe(array)}; it is ignored.");
            return;
        }
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"{arrayPath}[{index++}]";
            if (ExpectObject(element, path, warnings))
            {
                check(element, path);
            }
        }
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "an object",
    };
}
