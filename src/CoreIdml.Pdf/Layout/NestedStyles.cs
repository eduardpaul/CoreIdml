using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// The character styles a paragraph style applies to its own text: nested styles (<c>AllNestedStyles</c>), nested
/// line styles (<c>AllLineStyles</c>) and GREP styles (<c>AllGREPStyles</c>), as InDesign 20 draws them in
/// 08-para-nested-grep-styles.
/// <list type="bullet">
/// <item>Nested styles apply one after the other from the start of the paragraph: each covers the text up to
/// (<c>Inclusive="false"</c>) or through (<c>"true"</c>) the <c>Repetition</c>-th occurrence of its
/// <c>Delimiter</c>, and the next one starts where it ends. A delimiter that never comes styles the rest of the
/// paragraph (p1: "Strong through ':'" styles a paragraph without a colon entirely). <c>AnyWord</c> counts the space
/// that ends a word (p2: "3 words" ends before the space after the third word, which takes the next style).</item>
/// <item>Line styles style composed lines: <c>LineCount</c> lines each, in order (p3: line 1 Strong, line 2 Emph,
/// then plain with <c>RepeatLast="-1"</c>). The composer restyles the rest of the paragraph at each new line
/// (<see cref="NestedStyleText.ItemsFrom"/>), so a line's breaks use the widths of its own style (p3, all caps).</item>
/// <item>GREP styles style every match of their expression in the paragraph (<see cref="GrepExpression"/>), in order:
/// where two match, the later one's attributes win (p5: numbers inside a quotation).</item>
/// </list>
/// Layers, lowest first: the run's own character style, line style, nested style, GREP styles; the run's local
/// formatting stays on top. The sample only has runs without a character style or local formatting, so where an
/// applied character style or line styles meet nested styles the order follows InDesign's documented behaviour
/// (from memory, not measured): the automatic styles win over an applied character style only for the attributes
/// they define.
/// </summary>
internal sealed class NestedStyles
{
    private readonly StoryReader reader;
    private readonly Dictionary<XElement, Definitions?> definitions = [];
    private readonly Dictionary<string, Regex?> expressions = new(StringComparer.Ordinal);

    public NestedStyles(StoryReader reader) => this.reader = reader;

    private sealed record Nested(string CharacterStyle, string Delimiter, bool Enumerated, int Repetition, bool Inclusive);

    private sealed record Grep(string CharacterStyle, Regex Expression);

    private sealed record Definitions(IReadOnlyList<Nested> Nested, IReadOnlyList<NestedLineStyle> Lines, IReadOnlyList<Grep> Greps);

    /// <summary>
    /// The text of a paragraph under its nested, line and GREP styles: <paramref name="items"/> as read, the element
    /// each came from, and the zero-width special characters between them (end nested style, indent to
    /// here) that delimiters and expressions can see. Null when the paragraph has none of these styles.
    /// </summary>
    public NestedStyleText? Apply(XElement range, IReadOnlyList<InlineItem> items, IReadOnlyList<XElement> sources, IReadOnlyList<(int Before, char Code)> hidden)
    {
        if (items.Count == 0 || DefinitionsOf(range) is not { } definition)
        {
            return null;
        }
        var text = new StringBuilder();
        var starts = new int[items.Count];
        var nextHidden = 0;
        for (var i = 0; i < items.Count; i++)
        {
            for (; nextHidden < hidden.Count && hidden[nextHidden].Before <= i; nextHidden++)
            {
                text.Append(hidden[nextHidden].Code);
            }
            starts[i] = text.Length;
            switch (items[i])
            {
                case TextItem item:
                    text.Append(item.Text);
                    break;
                case TabItem tab:
                    text.Append(tab.RightIndentTab ? '\u0008' : '\t');
                    break;
                case LineBreakItem:
                    text.Append('\n');
                    break;
                case PageNumberItem number:
                    text.Append(number.Marker == PageMarker.Section ? '\u0013' : '\u0012');
                    break;
                case TableItem:
                    text.Append('\u0016');
                    break;
                default:
                    text.Append('\uFFFC');
                    break;
            }
        }
        var content = text.ToString();
        var nested = AssignNested(content, definition.Nested, DropCapEnd(content, reader.FormatOf(range).DropCap));
        var greps = new List<string>?[content.Length];
        foreach (var grep in definition.Greps)
        {
            try
            {
                for (var match = grep.Expression.Match(content); match.Success; match = match.NextMatch())
                {
                    for (var k = match.Index; k < match.Index + match.Length; k++)
                    {
                        (greps[k] ??= []).Add(grep.CharacterStyle);
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Too slow on this paragraph: the matches found so far stay, the rest of this style is not applied.
            }
        }
        return new NestedStyleText(reader, [.. items], [.. sources], starts, nested, greps, definition.Lines);
    }

    private Definitions? DefinitionsOf(XElement range)
    {
        if (definitions.TryGetValue(range, out var cached))
        {
            return cached;
        }
        var chain = new List<XElement> { range };
        chain.AddRange(reader.Resolver.StyleChain(range.Attribute("AppliedParagraphStyle")?.Value));
        var nested = List(chain, "AllNestedStyles", "EmptyNestedStyles")
            .Select(item => new Nested(
                item.Element("AppliedCharacterStyle")?.Value ?? string.Empty,
                item.Element("Delimiter")?.Value ?? string.Empty,
                item.Element("Delimiter")?.Attribute("type")?.Value == "enumeration",
                (int)Math.Clamp(Values.Number(item.Element("Repetition")?.Value, 1), 0, int.MaxValue),
                item.Element("Inclusive")?.Value != "false"))
            .ToList();
        var lines = List(chain, "AllLineStyles", "EmptyLineStyles")
            .Select(item => new NestedLineStyle(
                item.Element("AppliedCharacterStyle")?.Value ?? string.Empty,
                (int)Math.Clamp(Values.Number(item.Element("LineCount")?.Value, 1), 0, int.MaxValue),
                (int)Math.Clamp(Values.Number(item.Element("RepeatLast")?.Value, -1), -1, int.MaxValue)))
            .ToList();
        var greps = new List<Grep>();
        foreach (var item in List(chain, "AllGREPStyles", "EmptyGrepStyles"))
        {
            var expression = item.Element("GrepExpression")?.Value ?? string.Empty;
            if (!expressions.TryGetValue(expression, out var regex))
            {
                regex = expression.Length == 0 ? null : GrepExpression.Compile(expression);
                expressions[expression] = regex;
            }
            if (regex is not null)
            {
                greps.Add(new Grep(item.Element("AppliedCharacterStyle")?.Value ?? string.Empty, regex));
            }
        }
        var result = nested.Count + lines.Count + greps.Count == 0 ? null : new Definitions(nested, lines, greps);
        definitions[range] = result;
        return result;
    }

    /// <summary>
    /// The entries of a list property (<c>AllNestedStyles</c>, ...) along the paragraph and its style chain: the first
    /// element that has the list, or that says it is empty (<c>EmptyNestedStyles="true"</c>), decides.
    /// </summary>
    private static IEnumerable<XElement> List(IReadOnlyList<XElement> chain, string name, string emptyFlag)
    {
        foreach (var element in chain)
        {
            if (element.Element("Properties")?.Element(name) is { } list)
            {
                return list.Elements("ListItem");
            }
            if (element.Attribute(emptyFlag)?.Value == "true")
            {
                return [];
            }
        }
        return [];
    }

    /// <summary>The nested style of each character of <paramref name="text"/>, null where none applies.</summary>
    private static string?[] AssignNested(string text, IReadOnlyList<Nested> styles, int dropCapEnd)
    {
        var result = new string?[text.Length];
        var position = 0;
        var index = 0;
        var lastRepeat = -1;
        while (index < styles.Count && position < text.Length)
        {
            var style = styles[index];
            if (style.Enumerated && style.Delimiter == "Repeat")
            {
                // [Repeat]: loop over the Repetition styles before it, while they still take text.
                if (index == 0 || position == lastRepeat)
                {
                    break;
                }
                lastRepeat = position;
                index -= Math.Clamp(style.Repetition, 1, index);
                continue;
            }
            if (End(text, position, style, dropCapEnd) is not { } end)
            {
                // A delimiter CoreIdml does not know: nothing from here on is styled.
                break;
            }
            for (var k = position; k < end; k++)
            {
                result[k] = style.CharacterStyle;
            }
            position = end;
            index++;
        }
        return result;
    }

    /// <summary>Where a nested style starting at <paramref name="start"/> ends (exclusive); null for an unknown delimiter.</summary>
    private static int? End(string text, int start, Nested style, int dropCapEnd)
    {
        if (style.Enumerated && style.Delimiter == "Dropcap")
        {
            // "Through 1 drop cap" is how InDesign stores the drop cap's character style (08-para-drop-caps p3: red bold
            // Liberation Sans, blue italic Raleway): it covers the drop cap's characters, and "up to" the text before them.
            return style.Inclusive ? Math.Max(start, dropCapEnd) : start;
        }
        Func<string, int, bool>? isDelimiter = !style.Enumerated
            ? (style.Delimiter.Length == 0 ? null : (s, k) => style.Delimiter.Contains(s[k], StringComparison.Ordinal))
            : style.Delimiter switch
            {
                "AnyCharacter" => static (_, _) => true,
                "Letters" => static (s, k) => char.IsLetter(s[k]),
                "Digits" => static (s, k) => char.IsDigit(s[k]),
                "AnyWord" => static (s, k) => IsWordSpace(s[k]) && k > 0 && !IsWordSpace(s[k - 1]),
                "Sentence" => static (s, k) => s[k] is '.' or '!' or '?' && (k + 1 == s.Length || char.IsWhiteSpace(s[k + 1]) || s[k + 1] is '\u201D' or '\u2019' or '"' or '\''),
                "Tabs" => static (s, k) => s[k] is '\t' or '\u0008',
                "ForcedLineBreak" => static (s, k) => s[k] == '\n',
                "IndentHereTab" => static (s, k) => s[k] == '\u0007',
                "EmSpace" => static (s, k) => s[k] == '\u2003',
                "EnSpace" => static (s, k) => s[k] == '\u2002',
                "NonbreakingSpace" => static (s, k) => s[k] is '\u00A0' or '\u202F',
                "InlineGraphic" => static (s, k) => s[k] == '\uFFFC',
                "AutoPageNumber" => static (s, k) => s[k] == '\u0012',
                "SectionMarker" => static (s, k) => s[k] == '\u0013',
                "EndNestedStyle" => static (s, k) => s[k] == '\u0003',
                _ => null,
            };
        if (isDelimiter is null)
        {
            return null;
        }
        var count = 0;
        for (var k = start; k < text.Length; k++)
        {
            if (isDelimiter(text, k) && ++count >= Math.Max(1, style.Repetition))
            {
                return style.Inclusive ? k + 1 : k;
            }
        }
        return text.Length;
    }

    /// <summary>
    /// Where the drop cap's characters end in <paramref name="text"/> (0 without a drop cap): the first
    /// <see cref="DropCapFormat.Characters"/> characters of text, as the composer counts them, ending early at a tab,
    /// break or object. The zero-width end nested style and indent to here characters do not count.
    /// </summary>
    private static int DropCapEnd(string text, DropCapFormat? dropCap)
    {
        if (dropCap is null)
        {
            return 0;
        }
        var count = 0;
        for (var k = 0; k < text.Length; k++)
        {
            switch (text[k])
            {
                case '\u0003' or '\u0007':
                    continue;
                case '\t' or '\u0008' or '\n' or '\u0012' or '\u0013' or '\u0016' or '\uFFFC':
                    return k;
            }
            if (++count == dropCap.Characters)
            {
                return k + 1;
            }
        }
        return text.Length;
    }

    /// <summary>A space that separates words (no-break spaces join them).</summary>
    private static bool IsWordSpace(char c) => char.IsWhiteSpace(c) && c is not ('\u00A0' or '\u202F' or '\u2007');
}

/// <summary>
/// A nested line style: <c>LineCount</c> lines in a character style. An entry with <c>RepeatLast</c> &gt; 0 is read as
/// a repeat of that many entries before it, like a nested style's <c>[Repeat]</c> (inferred: the sample only has
/// <c>RepeatLast="-1"</c>, which repeats nothing).
/// </summary>
internal sealed record NestedLineStyle(string CharacterStyle, int LineCount, int RepeatLast);

/// <summary>
/// A paragraph's items under its nested and GREP styles, and its line styles for the composer: the items from any
/// point of the paragraph on, styled for the line that starts there.
/// </summary>
internal sealed class NestedStyleText
{
    private readonly StoryReader reader;
    private readonly IReadOnlyList<InlineItem> items;
    private readonly IReadOnlyList<XElement> sources;
    private readonly int[] starts;
    private readonly string?[] nested;
    private readonly List<string>?[] greps;
    private readonly IReadOnlyList<NestedLineStyle> lines;
    // Where each item handed out starts in the paragraph's text (items are records: compared by reference).
    private readonly Dictionary<InlineItem, int> offsets = new(ReferenceEqualityComparer.Instance);

    internal NestedStyleText(StoryReader reader, IReadOnlyList<InlineItem> items, IReadOnlyList<XElement> sources, int[] starts, string?[] nested, List<string>?[] greps,
        IReadOnlyList<NestedLineStyle> lines)
    {
        this.reader = reader;
        this.items = items;
        this.sources = sources;
        this.starts = starts;
        this.nested = nested;
        this.greps = greps;
        this.lines = lines;
    }

    /// <summary>The paragraph has line styles: the composer restyles it line by line.</summary>
    public bool HasLineStyles => lines.Count > 0;

    /// <summary>The line style of composed line <paramref name="line"/> (0 = first), null for none.</summary>
    public string? LineStyle(int line)
    {
        var first = 0;
        var index = 0;
        var lastRepeat = -1;
        while (index < lines.Count)
        {
            var (characterStyle, count, repeatLast) = lines[index];
            if (repeatLast > 0)
            {
                // A repeat entry: loop over the RepeatLast line styles before it, while they still take lines.
                if (index == 0 || first == lastRepeat)
                {
                    return null;
                }
                lastRepeat = first;
                index -= Math.Min(repeatLast, index);
                continue;
            }
            if (line < first + count)
            {
                return characterStyle;
            }
            first += count;
            index++;
        }
        return null;
    }

    /// <summary>
    /// The items from the glyph at <paramref name="index"/> of <paramref name="item"/> (one this text handed out) to
    /// the end of the paragraph, with <paramref name="lineStyle"/> under the nested and GREP styles.
    /// </summary>
    public IReadOnlyList<InlineItem> ItemsFrom(InlineItem item, int index, string? lineStyle) =>
        Items(offsets.TryGetValue(item, out var start) ? start + (item is TextItem ? index : 0) : 0, lineStyle);

    /// <summary>The items from <paramref name="from"/> (an offset in the paragraph's text) on, under <paramref name="lineStyle"/>.</summary>
    public IReadOnlyList<InlineItem> Items(int from, string? lineStyle)
    {
        var result = new List<InlineItem>();
        var layers = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var start = starts[i];
            var item = items[i];
            var length = item is TextItem text ? text.Text.Length : 1;
            if (start + length <= from)
            {
                continue;
            }
            if (item is not TextItem textItem)
            {
                Add(item with { Style = StyleAt(i, start, lineStyle, layers) }, start);
                continue;
            }
            var segment = Math.Max(start, from);
            while (segment < start + length)
            {
                var end = segment + 1;
                while (end < start + length && SameLayers(segment, end))
                {
                    end++;
                }
                Add(textItem with { Text = textItem.Text[(segment - start)..(end - start)], Style = StyleAt(i, segment, lineStyle, layers) }, segment);
                segment = end;
            }
        }
        return result;

        void Add(InlineItem item, int offset)
        {
            offsets[item] = offset;
            result.Add(item);
        }
    }

    private bool SameLayers(int a, int b) =>
        nested[a] == nested[b] && (greps[a] is null ? greps[b] is null : greps[b] is not null && greps[a]!.SequenceEqual(greps[b]!));

    private RunStyle StyleAt(int item, int offset, string? lineStyle, List<string> layers)
    {
        layers.Clear();
        if (lineStyle is not null)
        {
            layers.Add(lineStyle);
        }
        if (nested[offset] is { } style)
        {
            layers.Add(style);
        }
        if (greps[offset] is { } matched)
        {
            layers.AddRange(matched);
        }
        return reader.StyleOf(sources[item], layers);
    }
}
