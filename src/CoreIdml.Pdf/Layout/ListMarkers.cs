using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// The bullets and numbers InDesign generates in front of list paragraphs (<c>BulletsAndNumberingListType</c>).
/// They are not stored in the story: the bullet character comes from <c>BulletChar</c>, the number from the list's
/// counters, and the text after them from <c>BulletsTextAfter</c> or <c>NumberingExpression</c>, whose metacharacters
/// (<c>^t</c> tab, <c>^#</c> the number, <c>^m</c> em space, ...) are expanded here.
/// One instance numbers one story (or one table cell): counters run per list (<c>AppliedNumberingList</c>) and level,
/// continue across paragraphs that are not in the list, and restart where a higher level intervened
/// (<c>NumberingRestartPolicies</c>) or where a paragraph says so (<c>NumberingContinue="false"</c>). A list whose
/// <c>NumberingList</c> continues across stories starts from the counters <see cref="StoryReader"/> carries in from the
/// stories before (see <see cref="Carried"/>).
/// </summary>
/// <remarks>
/// Checked against InDesign 20 (sample 08-para-bullets-numbering). Not done: centre and right marker alignment
/// (<c>BulletsAlignment</c>, <c>NumberingAlignment</c>) are drawn left (in the sample's hanging indents InDesign draws
/// right-aligned numbers at the same place as left-aligned ones); lists continued across documents; bullets given as a
/// glyph id (<c>GlyphWithFont</c>) are drawn as U+2022.
/// </remarks>
internal sealed class ListMarkers
{
    private const int Levels = 9;

    private readonly StoryReader reader;
    private readonly Dictionary<string, ListState> lists = new(StringComparer.Ordinal);

    /// <param name="reader">Resolves the paragraphs' list attributes and the markers' formatting.</param>
    /// <param name="carried">Counters of the lists that continue from earlier stories, copied (never changed).</param>
    public ListMarkers(StoryReader reader, IReadOnlyDictionary<string, ListState>? carried = null)
    {
        this.reader = reader;
        foreach (var (list, state) in carried ?? new Dictionary<string, ListState>())
        {
            lists[list] = state.Clone();
        }
    }

    /// <summary>The counters of one list: per level its value, when it was last numbered, and its format.</summary>
    internal sealed class ListState
    {
        public readonly int[] Value = new int[Levels + 1];
        public readonly long[] Sequence = new long[Levels + 1];
        public readonly string?[] Format = new string?[Levels + 1];

        // Orders the numbering of the list's levels (restart policies compare it); per list, so it carries with it.
        public long Clock;

        public ListState Clone()
        {
            var copy = new ListState { Clock = Clock };
            Value.CopyTo(copy.Value, 0);
            Sequence.CopyTo(copy.Sequence, 0);
            Format.CopyTo(copy.Format, 0);
            return copy;
        }
    }

    /// <summary>Copies of the counters of the lists in <paramref name="names"/> as they stand now.</summary>
    public Dictionary<string, ListState> Carried(IReadOnlySet<string> names) =>
        lists.Where(pair => names.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

    /// <summary>Advances the counters for paragraph <paramref name="range"/> without making its marker.</summary>
    public void Count(XElement range)
    {
        if (reader.Resolver.Paragraph(range, "BulletsAndNumberingListType") == "NumberedList")
        {
            Advance(range);
        }
    }

    /// <summary>Numbers the paragraph: its list's counters, its level and its number format.</summary>
    private (ListState State, int Level, string Format) Advance(XElement range)
    {
        string? Get(string name) => reader.Resolver.Paragraph(range, name);
        var level = Math.Clamp((int)Values.Number(Get("NumberingLevel"), 1), 1, Levels);
        var list = Get("AppliedNumberingList") ?? DefaultList;
        if (!lists.TryGetValue(list, out var state))
        {
            lists[list] = state = new ListState();
        }
        var format = Get("NumberingFormat") ?? "1, 2, 3, 4...";
        var startAt = (int)Values.Number(Get("NumberingStartAt"), 1);
        var restart = state.Sequence[level] == 0 || Get("NumberingContinue") == "false";
        if (!restart && Get("NumberingApplyRestartPolicy") != "false")
        {
            restart = RestartedByHigherLevel(range, state, level);
        }
        state.Value[level] = restart ? startAt : state.Value[level] + 1;
        state.Sequence[level] = ++state.Clock;
        state.Format[level] = format;
        return (state, level, format);
    }

    /// <summary>The list paragraphs belong to when they name none.</summary>
    public const string DefaultList = "NumberingList/$ID/[Default]";

    /// <summary>
    /// The generated marker of the paragraph <paramref name="range"/> (bullet or number and the text after it), or
    /// null when it is not a list paragraph. <paramref name="firstText"/> is where the paragraph's text starts: the
    /// marker takes that character's formatting, under the list's character style.
    /// </summary>
    public IReadOnlyList<InlineItem>? Marker(XElement range, XElement firstText)
    {
        var resolver = reader.Resolver;
        string? Get(string name) => resolver.Paragraph(range, name);
        switch (Get("BulletsAndNumberingListType"))
        {
            case "BulletList":
            {
                var bulletChar = reader.ParagraphProperty(range, "BulletChar");
                var type = bulletChar?.Attribute("BulletCharacterType")?.Value ?? "UnicodeOnly";
                var code = (int)Values.Number(bulletChar?.Attribute("BulletCharacterValue")?.Value, 0x2022);
                if (type == "GlyphWithFont" || code <= 0 || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
                {
                    code = 0x2022;
                }
                var (family, fontStyle) = type == "UnicodeOnly" ? (null, null) : (Named(Get("BulletsFont")), Named(Get("BulletsFontStyle")));
                var style = reader.MarkerStyle(firstText, Get("BulletsCharacterStyle"), family, fontStyle);
                var items = new List<InlineItem> { new TextItem(char.ConvertFromUtf32(code), style) { Generated = true } };
                Expand(Get("BulletsTextAfter") ?? "^t", style, items, number: null);
                return items;
            }
            case "NumberedList":
            {
                var (state, level, format) = Advance(range);
                var style = reader.MarkerStyle(firstText, Get("NumberingCharacterStyle"), null, null);
                var items = new List<InlineItem>();
                string Number(int wanted) => wanted == 0
                    ? Format(state.Value[level], format)
                    : state.Sequence[wanted] == 0 ? string.Empty : Format(state.Value[wanted], state.Format[wanted] ?? format);
                Expand(Get("NumberingExpression") ?? "^#.^t", style, items, Number);
                return items;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a paragraph of a higher level (a lower level number) of the list came after the last number at
    /// <paramref name="level"/>, among the levels the restart policy names.
    /// </summary>
    private bool RestartedByHigherLevel(XElement range, ListState state, int level)
    {
        var policy = reader.ParagraphProperty(range, "NumberingRestartPolicies");
        var kind = policy?.Attribute("RestartPolicy")?.Value ?? "AnyPreviousLevel";
        var lower = (int)Values.Number(policy?.Attribute("LowerLevel")?.Value, 0);
        var upper = (int)Values.Number(policy?.Attribute("UpperLevel")?.Value, 0);
        var (from, to) = kind switch
        {
            "SpecificPreviousLevel" => (lower, lower),
            "RangeOfPreviousLevels" => (lower, upper),
            _ => (1, level - 1),
        };
        from = Math.Max(1, from);
        to = Math.Min(level - 1, to);
        for (var k = from; k <= to; k++)
        {
            if (state.Sequence[k] > state.Sequence[level])
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A font family or style name, or null for InDesign's "use the text's" values.</summary>
    private static string? Named(string? value) => value is null or "" or "$ID/" or "Nothing" ? null : value;

    /// <summary>
    /// Appends <paramref name="expression"/> as inline items: literal text, tabs (<c>^t</c>, <c>^y</c>), a forced line
    /// break (<c>^n</c>), special spaces and dashes, and with <paramref name="number"/> the number placeholders
    /// (<c>^#</c> the paragraph's own level, <c>^1</c> to <c>^9</c> that level's current number).
    /// </summary>
    internal static void Expand(string expression, RunStyle style, List<InlineItem> items, Func<int, string>? number)
    {
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                items.Add(new TextItem(text.ToString(), style) { Generated = true });
                text.Clear();
            }
        }
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '\t')
            {
                Flush();
                items.Add(new TabItem(style) { Generated = true });
                continue;
            }
            if (c != '^' || i + 1 >= expression.Length)
            {
                text.Append(c);
                continue;
            }
            var code = expression[++i];
            if (number is not null && (code == '#' || code is >= '1' and <= '9'))
            {
                text.Append(number(code == '#' ? 0 : code - '0'));
                continue;
            }
            switch (code)
            {
                case 't':
                    Flush();
                    items.Add(new TabItem(style) { Generated = true });
                    break;
                case 'y':
                    Flush();
                    items.Add(new TabItem(style, RightIndentTab: true) { Generated = true });
                    break;
                case 'n':
                    Flush();
                    items.Add(new LineBreakItem(style) { Generated = true });
                    break;
                default:
                    if (Special(code) is { } special)
                    {
                        text.Append(special);
                    }
                    else
                    {
                        // Not a metacharacter InDesign knows here: shown as typed.
                        text.Append('^').Append(code);
                    }
                    break;
            }
        }
        Flush();
    }

    private static string? Special(char code) => code switch
    {
        '^' => "^",
        'm' or 'f' => " ",   // em space; flush space drawn as one
        '>' => " ",          // en space
        '3' => " ",          // third space (outside numbering expressions)
        '4' => " ",          // quarter space
        '%' => " ",          // sixth space
        '<' => " ",          // thin space
        '|' => " ",          // hair space
        '/' => " ",          // figure space
        '.' => " ",          // punctuation space
        's' => " ",          // nonbreaking space
        'S' => " ",          // fixed-width nonbreaking space
        '_' => "—",          // em dash
        '=' => "–",          // en dash
        '~' => "‑",          // nonbreaking hyphen
        '-' => "­",          // discretionary hyphen
        '8' => "•",          // bullet
        'e' => "…",          // ellipsis
        'r' => "®",          // registered
        '6' => "§",          // section
        '{' => "“",
        '}' => "”",
        '[' => "‘",
        ']' => "’",
        _ => null,
    };

    /// <summary>A number in an InDesign numbering format ("1, 2, 3, 4...", "01, 02, 03...", "I, II, III, IV...", "a, b, c, d...").</summary>
    internal static string Format(int value, string format)
    {
        var digits = format.TrimStart();
        if (digits.StartsWith("0001", StringComparison.Ordinal))
        {
            return Padded(value, 4);
        }
        if (digits.StartsWith("001", StringComparison.Ordinal))
        {
            return Padded(value, 3);
        }
        if (digits.StartsWith("01", StringComparison.Ordinal))
        {
            return Padded(value, 2);
        }
        return digits.Length == 0 ? Arabic(value) : digits[0] switch
        {
            'I' => Roman(value, upper: true),
            'i' => Roman(value, upper: false),
            'A' => Alphabetic(value, upper: true),
            'a' => Alphabetic(value, upper: false),
            _ => Arabic(value),
        };
    }

    private static string Arabic(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Padded(int value, int width) =>
        value < 0 ? Arabic(value) : value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    private static string Roman(int value, bool upper)
    {
        if (value is <= 0 or >= 4000)
        {
            return Arabic(value);
        }
        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var result = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (value >= values[i])
            {
                result.Append(symbols[i]);
                value -= values[i];
            }
        }
        return upper ? result.ToString() : result.ToString().ToLowerInvariant();
    }

    /// <summary>a ... z, then aa, ab, ... (bijective base 26).</summary>
    private static string Alphabetic(int value, bool upper)
    {
        if (value <= 0)
        {
            return Arabic(value);
        }
        var result = new StringBuilder();
        for (; value > 0; value = (value - 1) / 26)
        {
            result.Insert(0, (char)((upper ? 'A' : 'a') + ((value - 1) % 26)));
        }
        return result.ToString();
    }
}
