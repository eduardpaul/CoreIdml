using CoreIdml.Dom;

namespace CoreIdml.Json;

public static partial class IdmlJson
{
    private sealed record SpecError(string Path, string Message, IdmlErrorKind Kind);

    /// <summary>
    /// Every error building <paramref name="spec"/> into <paramref name="document"/> would stop at, found before anything is
    /// built so that they come back together: colours without values, unknown colours, styles and values, image items
    /// without a path, pages and frames that do not exist. Declarations count in the order the build makes them
    /// (colours, then character styles, then paragraph styles, then pages in order).
    /// </summary>
    private static List<SpecError> FindSpecErrors(IdmlDocument document, DocumentSpec spec, bool reuseExistingPages)
    {
        var errors = new List<SpecError>();
        void Add(string path, string message, IdmlErrorKind kind = IdmlErrorKind.InvalidOperation) => errors.Add(new(path, message, kind));
        void Choice<TEnum>(string? value, string field, string path)
            where TEnum : struct, Enum
        {
            if (value is not null && ChoiceError<TEnum>(value, field) is { } message)
            {
                Add(path, message);
            }
        }

        // A colour with bad values is reported once, where it is declared, and still counts as declared.
        var swatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool KnownSwatch(string name) => swatches.Contains(name) || document.Swatches.Find(name) is not null;
        void Swatch(string? name, string path)
        {
            if (name is not null && !KnownSwatch(name))
            {
                Add(path, SwatchMissing(name), IdmlErrorKind.NotFound);
            }
        }
        var colors = spec.Colors ?? [];
        for (var i = 0; i < colors.Count; i++)
        {
            swatches.Add(colors[i].Name);
            if (ColorError(colors[i]) is { } message)
            {
                Add($"colors[{i}]", message);
            }
        }

        var characterStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paragraphStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void CharacterStyle(string? name, string path)
        {
            if (name is not null && !characterStyles.Contains(name) && document.Styles.FindCharacterStyle(name) is null)
            {
                Add(path, CharacterStyleMissing(name), IdmlErrorKind.NotFound);
            }
        }
        void ParagraphStyle(string? name, string path)
        {
            if (name is not null && !paragraphStyles.Contains(name) && document.Styles.FindParagraphStyle(name) is null)
            {
                Add(path, ParagraphStyleMissing(name), IdmlErrorKind.NotFound);
            }
        }
        void Format(TextFormatSpec format, string path, bool paragraph)
        {
            Swatch(format.Color, path);
            if (paragraph && format.Align is { } align && TryJustification(align) is null)
            {
                Add(path, AlignError(align));
            }
            Choice<Capitalization>(format.Caps, "caps", path);
            Choice<TextPosition>(format.Position, "position", path);
            Swatch(format.Stroke, path);
            Swatch(format.UnderlineColor, path);
            Swatch(format.StrikeThroughColor, path);
            Choice<TextLineType>(format.UnderlineType, "underlineType", path);
            Choice<TextLineType>(format.StrikeThroughType, "strikeThroughType", path);
            if (CharacterValueError(format) is { } characterError)
            {
                Add(path, characterError);
            }
            if (paragraph && ParagraphValueError(format) is { } valueError)
            {
                Add(path, valueError);
            }
            if (paragraph)
            {
                CharacterStyle(format.DropCapStyle, path);
                foreach (var style in (format.NestedStyles ?? []).Select(nested => nested.Style).Concat((format.GrepStyles ?? []).Select(grep => grep.Style)))
                {
                    CharacterStyle(style, path);
                }
            }
        }

        var characterStyleSpecs = spec.CharacterStyles ?? [];
        for (var i = 0; i < characterStyleSpecs.Count; i++)
        {
            var path = $"characterStyles[{i}]";
            CharacterStyle(characterStyleSpecs[i].BasedOn, path);
            Format(characterStyleSpecs[i], path, paragraph: false);
            characterStyles.Add(characterStyleSpecs[i].Name);
        }
        var paragraphStyleSpecs = spec.ParagraphStyles ?? [];
        for (var i = 0; i < paragraphStyleSpecs.Count; i++)
        {
            var path = $"paragraphStyles[{i}]";
            ParagraphStyle(paragraphStyleSpecs[i].BasedOn, path);
            Format(paragraphStyleSpecs[i], path, paragraph: true);
            paragraphStyles.Add(paragraphStyleSpecs[i].Name);
        }

        var pageCount = document.Pages.Count;
        var nextExisting = 0;
        // Text frames the spec makes before a given item, by name and label (what continueFrom looks for).
        var frames = new HashSet<string>(StringComparer.Ordinal);
        var pages = spec.Pages ?? [];
        for (var p = 0; p < pages.Count; p++)
        {
            var pagePath = $"pages[{p}]";
            if (pages[p].Index is { } index)
            {
                if (index < 0 || index >= pageCount)
                {
                    Add(pagePath, $"Page index {index} does not exist.", IdmlErrorKind.NotFound);
                }
            }
            else if (reuseExistingPages && nextExisting < pageCount)
            {
                nextExisting++;
            }
            else
            {
                pageCount++;
            }
            var items = pages[p].Items ?? [];
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var path = $"{pagePath}.items[{i}]";
                var type = item.Type.ToLowerInvariant();
                if (type is not ("text" or "rectangle" or "ellipse" or "line" or "image" or "table"))
                {
                    Add(path, ItemTypeError(item.Type));
                    continue;
                }
                Swatch(item.Fill, path);
                Swatch(item.Stroke, path);
                Choice<StrokeKind>(item.StrokeType, "strokeType", path);
                Choice<LineCap>(item.EndCap, "endCap", path);
                switch (type)
                {
                    case "text":
                        Choice<VerticalJustification>(item.VerticalAlign, "verticalAlign", path);
                        if (item.ContinueFrom is { } previous)
                        {
                            if (!frames.Contains(previous) && document.FindPageItem(previous) is not TextFrame)
                            {
                                Add(path, $"continueFrom: no text frame named '{previous}'.", IdmlErrorKind.NotFound);
                            }
                        }
                        else
                        {
                            if (item.Text is not null)
                            {
                                ParagraphStyle(item.Style, path);
                            }
                            var paragraphs = item.Paragraphs ?? [];
                            for (var j = 0; j < paragraphs.Count; j++)
                            {
                                var paragraphPath = $"{path}.paragraphs[{j}]";
                                ParagraphStyle(paragraphs[j].Style, paragraphPath);
                                Format(paragraphs[j], paragraphPath, paragraph: true);
                                var runs = paragraphs[j].Runs ?? [];
                                for (var k = 0; k < runs.Count; k++)
                                {
                                    CharacterStyle(runs[k].Style, $"{paragraphPath}.runs[{k}]");
                                    Format(runs[k], $"{paragraphPath}.runs[{k}]", paragraph: false);
                                }
                            }
                        }
                        foreach (var key in (string?[])[item.Name, item.Label])
                        {
                            if (key is not null)
                            {
                                frames.Add(key);
                            }
                        }
                        break;
                    case "image":
                        if (item.Path is null)
                        {
                            Add(path, "Image items need a path.");
                        }
                        if (!string.IsNullOrEmpty(item.Fit))
                        {
                            Choice<ImageFit>(item.Fit, "fit", path);
                        }
                        break;
                    case "table":
                        ParagraphStyle(item.CellStyle, path);
                        ParagraphStyle(item.HeaderStyle, path);
                        Swatch(item.HeaderFill, path);
                        Swatch(item.AlternateFill, path);
                        break;
                }
            }
        }
        return errors;
    }

    /// <summary>
    /// Throws when there are errors: one error as its own message (as when the build stopped at it); several as one
    /// message that lists each with its place in the spec.
    /// </summary>
    private static void ThrowSpecErrors(List<SpecError> errors, bool creating)
    {
        if (errors.Count == 0)
        {
            return;
        }
        var kind = errors.TrueForAll(error => error.Kind == IdmlErrorKind.NotFound) ? IdmlErrorKind.NotFound : IdmlErrorKind.InvalidOperation;
        if (errors.Count == 1)
        {
            throw new IdmlException(kind, errors[0].Message);
        }
        throw new IdmlException(kind,
            $"The spec has {errors.Count} errors; nothing was {(creating ? "created" : "changed")}:" + string.Concat(errors.Select(error => $"\n- {error.Path}: {error.Message}")));
    }

    private static string? ColorError(ColorSpec color) =>
        color.Cmyk is { Length: 4 } || color.Rgb is { Length: 3 } ? null
        : color.Hex is { } hex ? (IsHexColor(hex) ? null : $"Colour '{color.Name}': '{hex}' is not a #RRGGBB colour.")
        : $"Colour '{color.Name}' needs cmyk, rgb or hex.";

    private static bool IsHexColor(string hex)
    {
        var value = hex.TrimStart('#');
        return value.Length == 6 && value.All(Uri.IsHexDigit);
    }

    /// <summary>What is wrong with a format's text stroke, skew, underline or strikethrough values, or null.</summary>
    private static string? CharacterValueError(TextFormatSpec format)
    {
        if (format.Skew is { } skew && skew is < -85 or > 85)
        {
            return $"skew {skew} is out of range. Use -85 to 85 degrees.";
        }
        foreach (var (name, weight) in (ReadOnlySpan<(string, double?)>)[("strokeWeight", format.StrokeWeight), ("underlineWeight", format.UnderlineWeight), ("strikeThroughWeight", format.StrikeThroughWeight)])
        {
            if (weight is < 0 or > 800)
            {
                return $"{name} {weight} is out of range. Use 0 to 800 pt.";
            }
        }
        foreach (var (name, tint) in (ReadOnlySpan<(string, double?)>)[("strokeTint", format.StrokeTint), ("underlineTint", format.UnderlineTint), ("strikeThroughTint", format.StrikeThroughTint)])
        {
            if (tint is < 0 or > 100)
            {
                return $"{name} {tint} is out of range. Use 0 to 100.";
            }
        }
        return null;
    }

    /// <summary>What is wrong with a format's bullets and numbering, tab stop or keep option values, or null.</summary>
    private static string? ParagraphValueError(TextFormatSpec format)
    {
        if (format.List is { } list && TryListType(list) is null)
        {
            return $"Unknown list '{list}'. Use bullet, numbered or none.";
        }
        if (format.NumberFormat is { } numberFormat && TryNumberingFormat(numberFormat) is null)
        {
            return $"Unknown numberFormat '{numberFormat}'. Use 1, 01, 001, 0001, a, A, i or I.";
        }
        if (format.ListLevel is { } level && level is < 1 or > 9)
        {
            return $"listLevel {level} is out of range. Use 1 to 9.";
        }
        if (format.Bullet is { } bullet && !IsOneCharacter(bullet))
        {
            return $"bullet '{bullet}' must be one character.";
        }
        foreach (var tab in format.Tabs ?? [])
        {
            if (tab.Align is { } align && TryTabAlignment(align) is null)
            {
                return $"Unknown tab align '{align}'. Use left, center, right or decimal.";
            }
            if (tab.Position < 0)
            {
                return $"Tab position {tab.Position} is negative.";
            }
            if (tab.AlignOn is { } alignOn && !IsOneCharacter(alignOn))
            {
                return $"Tab alignOn '{alignOn}' must be one character.";
            }
            if (tab.Leader is { Length: > 8 } leader)
            {
                return $"Tab leader '{leader}' is longer than InDesign's 8 characters.";
            }
        }
        if (format.KeepWithNext is { } keepWithNext && keepWithNext is < 0 or > 5)
        {
            return $"keepWithNext {keepWithNext} is out of range. Use 0 to 5 lines.";
        }
        if (format.KeepFirstLines is { } firstLines && firstLines is < 1 or > 50)
        {
            return $"keepFirstLines {firstLines} is out of range. Use 1 to 50.";
        }
        if (format.KeepLastLines is { } lastLines && lastLines is < 1 or > 50)
        {
            return $"keepLastLines {lastLines} is out of range. Use 1 to 50.";
        }
        if (format.StartParagraph is { } start && TryStartParagraph(start) is null)
        {
            return $"Unknown startParagraph '{start}'. Use anywhere, nextColumn, nextFrame, nextPage, nextOddPage or nextEvenPage.";
        }
        if (format.DropCapLines is { } dropLines && dropLines is < 0 or > 25)
        {
            return $"dropCapLines {dropLines} is out of range. Use 0 (none) to 25.";
        }
        if (format.DropCapCharacters is { } dropCharacters && dropCharacters is < 0 or > 150)
        {
            return $"dropCapCharacters {dropCharacters} is out of range. Use 0 to 150.";
        }
        foreach (var nested in format.NestedStyles ?? [])
        {
            if ((nested.Through is null) == (nested.UpTo is null))
            {
                return $"Nested style '{nested.Style}' needs through or upTo (one of them).";
            }
            if ((nested.Through ?? nested.UpTo) is { } delimiter && TryDelimiter(delimiter) is null)
            {
                return $"Unknown nested style delimiter '{delimiter}'. Use words, characters, sentences, letters, digits, tabs, lineBreak, endNestedStyle or one character such as \":\".";
            }
            if (nested.Count is < 1)
            {
                return $"Nested style count {nested.Count} is out of range. Use 1 or more.";
            }
        }
        foreach (var grep in format.GrepStyles ?? [])
        {
            if (string.IsNullOrEmpty(grep.Expression))
            {
                return $"GREP style '{grep.Style}' needs an expression.";
            }
        }
        if (format.SpanColumns is not null && format.SplitColumns is not null)
        {
            return "A paragraph either spans columns (spanColumns) or splits its column (splitColumns), not both.";
        }
        if (format.SpanColumns is { } span && span is < 0 or > 40)
        {
            return $"spanColumns {span} is out of range. Use 0 (all columns), 1 (a single column) or up to 40.";
        }
        if (format.SplitColumns is { } split && split is < 1 or > 40)
        {
            return $"splitColumns {split} is out of range. Use 1 (a single column) to 40.";
        }
        if (format.SplitGutter is < 0)
        {
            return $"splitGutter {format.SplitGutter} is negative.";
        }
        return null;
    }

    private static string? BaselineGridError(BaselineGridSpec grid) =>
        grid.Increment <= 0 ? $"baselineGrid increment {grid.Increment} must be positive: the body text's leading in points."
        : IsTopMarginOrigin(grid.RelativeTo) is null ? $"Unknown baselineGrid relativeTo '{grid.RelativeTo}'. Use page or topMargin."
        : null;

    private static bool IsOneCharacter(string text) =>
        text.Length == 1 && !char.IsSurrogate(text[0]) || text.Length == 2 && char.IsSurrogatePair(text[0], text[1]);

    private static string ItemTypeError(string type) => $"Unknown item type '{type}'. Use text, rectangle, ellipse, line, image or table.";

    private static string AlignError(string align) => $"Unknown align '{align}'. Use left, center, right, justify or justifyAll.";

    private static string SwatchMissing(string name) => $"Unknown swatch '{name}'. Declare it in colors first.";

    private static string ParagraphStyleMissing(string name) => $"Unknown paragraph style '{name}'.";

    private static string CharacterStyleMissing(string name) => $"Unknown character style '{name}'.";
}
