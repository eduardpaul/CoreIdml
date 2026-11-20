using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

public enum Justification
{
    Left,
    Center,
    Right,
    LeftJustified,
    CenterJustified,
    RightJustified,
    FullyJustified,
    ToBindingSide,
    AwayFromBindingSide,
}

public enum Capitalization
{
    Normal,
    SmallCaps,
    AllCaps,
    CapToSmallCap,
}

public enum TextPosition
{
    Normal,
    Superscript,
    Subscript,
}

/// <summary>Bullets and Numbering list type (<c>BulletsAndNumberingListType</c>).</summary>
public enum ListType
{
    /// <summary>Not a list paragraph (<c>NoList</c>).</summary>
    None,
    Bullet,
    Numbered,
}

/// <summary>Where a paragraph starts (Keep Options &gt; Start Paragraph, <c>StartParagraph</c>).</summary>
public enum StartParagraph
{
    Anywhere,
    NextColumn,
    NextFrame,
    NextPage,
    NextOddPage,
    NextEvenPage,
}

/// <summary>The line of an underline or strikethrough (Underline Options &gt; Type).</summary>
public enum TextLineType
{
    Solid,
    Dashed,
    Dotted,
    Wavy,

    /// <summary>A thick line over a thin one.</summary>
    ThickThin,
}

/// <summary>Where a paragraph sits across its frame's columns (Paragraph &gt; Span Columns, <c>SpanColumnType</c>).</summary>
public enum ColumnSpan
{
    /// <summary>In its column, as usual.</summary>
    SingleColumn,

    /// <summary>Across several (or all) of the frame's columns: headlines over multi-column text.</summary>
    SpanColumns,

    /// <summary>In sub-columns of its own column.</summary>
    SplitColumns,
}

/// <summary>
/// What ends a nested style (<c>Delimiter</c>): one of InDesign's named delimiters (words, characters, ...) or a
/// character. With <see cref="NestedStyle.Inclusive"/> the style runs through the delimiter, else up to it.
/// </summary>
public sealed record NestedStyleDelimiter
{
    private NestedStyleDelimiter(string value, bool isEnumeration)
    {
        Value = value;
        IsEnumeration = isEnumeration;
    }

    /// <summary>The stored value: an enumeration name (<c>AnyWord</c>) or the character itself.</summary>
    public string Value { get; }

    /// <summary>True for a named delimiter (<c>type="enumeration"</c>), false for a character (<c>type="string"</c>).</summary>
    public bool IsEnumeration { get; }

    public static NestedStyleDelimiter Words { get; } = new("AnyWord", true);

    public static NestedStyleDelimiter Characters { get; } = new("AnyCharacter", true);

    public static NestedStyleDelimiter Sentences { get; } = new("Sentence", true);

    public static NestedStyleDelimiter Letters { get; } = new("Letters", true);

    public static NestedStyleDelimiter Digits { get; } = new("Digits", true);

    public static NestedStyleDelimiter Tabs { get; } = new("Tabs", true);

    public static NestedStyleDelimiter ForcedLineBreak { get; } = new("ForcedLineBreak", true);

    /// <summary>The End Nested Style Here character (Type &gt; Insert Special Character &gt; Other).</summary>
    public static NestedStyleDelimiter EndNestedStyle { get; } = new("EndNestedStyle", true);

    /// <summary>The paragraph's drop cap: "through 1 drop cap" is how InDesign stores a drop cap's character style.</summary>
    public static NestedStyleDelimiter DropCap { get; } = new("Dropcap", true);

    /// <summary>A delimiter character (":", "." or any other one character).</summary>
    public static NestedStyleDelimiter Character(string character)
    {
        ArgumentException.ThrowIfNullOrEmpty(character);
        if (!(character.Length == 1 && !char.IsSurrogate(character[0]) || character.Length == 2 && char.IsSurrogatePair(character[0], character[1])))
        {
            throw new ArgumentException($"A nested style delimiter is one character, not '{character}'.", nameof(character));
        }
        return new NestedStyleDelimiter(character, false);
    }
}

/// <summary>
/// A nested style (Paragraph Style Options &gt; Drop Caps and Nested Styles): <paramref name="CharacterStyle"/> from
/// where the previous one ended through (or up to, when not <paramref name="Inclusive"/>) the
/// <paramref name="Repetition"/>th <paramref name="Delimiter"/>, e.g. "Lead through 3 words".
/// </summary>
public sealed record NestedStyle(CharacterStyle CharacterStyle, NestedStyleDelimiter Delimiter, int Repetition = 1, bool Inclusive = true);

/// <summary>A GREP style: <paramref name="CharacterStyle"/> on the paragraph's text that <paramref name="Expression"/> (InDesign's GREP) matches.</summary>
public sealed record GrepStyle(CharacterStyle CharacterStyle, string Expression);

/// <summary>How text after a tab lines up with its stop.</summary>
public enum TabAlignment
{
    /// <summary>The text starts at the stop.</summary>
    Left,
    Center,
    /// <summary>The text ends at the stop (page numbers in a table of contents, prices).</summary>
    Right,
    /// <summary>
    /// <see cref="TabStop.AlignOn"/> (a decimal point by default) sits on the stop; text without it ends at the stop.
    /// </summary>
    Character,
}

/// <summary>
/// A tab stop: <paramref name="Position"/> in points from the frame's (column's) left edge, its alignment, the leader
/// repeated across the tab's space (".", "-", "_", "·" or any short text; empty for none) and, for
/// <see cref="TabAlignment.Character"/>, the character to align on.
/// </summary>
public sealed record TabStop(double Position, TabAlignment Alignment = TabAlignment.Left, string Leader = "", string AlignOn = ".");

/// <summary>InDesign's numbering formats (<c>NumberingFormat</c>), the values the Bullets and Numbering dialog offers.</summary>
public static class NumberingFormats
{
    public const string Arabic = "1, 2, 3, 4...";
    public const string ArabicTwoDigits = "01, 02, 03...";
    public const string ArabicThreeDigits = "001, 002, 003...";
    public const string ArabicFourDigits = "0001, 0002, 0003...";
    public const string UpperRoman = "I, II, III, IV...";
    public const string LowerRoman = "i, ii, iii, iv...";
    public const string UpperLetters = "A, B, C, D...";
    public const string LowerLetters = "a, b, c, d...";
}

/// <summary>
/// InDesign language names (<c>AppliedLanguage</c>).
/// </summary>
public static class Languages
{
    public const string Spanish = "$ID/Spanish: Castilian";
    public const string EnglishUk = "$ID/English: UK";
    public const string EnglishUs = "$ID/English: USA";
}

/// <summary>
/// Character attributes, used for character styles and local (direct) formatting of runs.
/// Null properties are left untouched (inherited from the style).
/// </summary>
public class CharacterFormat
{
    /// <summary>Font family name as InDesign shows it, e.g. "Minion Pro", "Arial".</summary>
    public string? FontFamily { get; init; }

    /// <summary>Font style within the family, e.g. "Regular", "Bold", "Italic", "Bold Italic".</summary>
    public string? FontStyle { get; init; }

    public double? PointSize { get; init; }

    public Swatch? FillColor { get; init; }

    public double? FillTint { get; init; }

    public bool? Underline { get; init; }

    public bool? StrikeThrough { get; init; }

    /// <summary>Tracking in thousandths of an em.</summary>
    public double? Tracking { get; init; }

    public Capitalization? Capitalization { get; init; }

    public TextPosition? Position { get; init; }

    public double? BaselineShift { get; init; }

    public double? HorizontalScale { get; init; }

    /// <summary>Language for hyphenation and spelling, as InDesign names it, e.g. <see cref="Languages.Spanish"/>.</summary>
    public string? Language { get; init; }

    /// <summary>Text stroke (outlined text) colour; <see cref="Swatch.None"/> removes it.</summary>
    public Swatch? StrokeColor { get; init; }

    /// <summary>Text stroke weight in points.</summary>
    public double? StrokeWeight { get; init; }

    /// <summary>Text stroke tint in percent.</summary>
    public double? StrokeTint { get; init; }

    /// <summary>Skew (false italic) in degrees, positive leaning right.</summary>
    public double? Skew { get; init; }

    /// <summary>Underline weight in points (InDesign's automatic weight when unset).</summary>
    public double? UnderlineWeight { get; init; }

    /// <summary>Underline offset in points below the baseline (automatic when unset).</summary>
    public double? UnderlineOffset { get; init; }

    /// <summary>Underline colour (the text colour when unset).</summary>
    public Swatch? UnderlineColor { get; init; }

    public double? UnderlineTint { get; init; }

    public TextLineType? UnderlineType { get; init; }

    /// <summary>Strikethrough weight in points (automatic when unset).</summary>
    public double? StrikeThroughWeight { get; init; }

    /// <summary>Strikethrough offset in points above the baseline (automatic when unset).</summary>
    public double? StrikeThroughOffset { get; init; }

    /// <summary>Strikethrough colour (the text colour when unset).</summary>
    public Swatch? StrikeThroughColor { get; init; }

    public double? StrikeThroughTint { get; init; }

    public TextLineType? StrikeThroughType { get; init; }

    internal virtual void ApplyTo(XElement element)
    {
        if (Language is not null)
        {
            element.SetAttributeValue("AppliedLanguage", Language);
        }
        if (FontFamily is not null)
        {
            IdmlXml.SetProperty(element, "AppliedFont", "string", FontFamily);
        }
        Set(element, "FontStyle", FontStyle);
        Set(element, "PointSize", PointSize);
        Set(element, "FillColor", FillColor?.Reference);
        Set(element, "FillTint", FillTint);
        Set(element, "Underline", Underline);
        Set(element, "StrikeThru", StrikeThrough);
        Set(element, "Tracking", Tracking);
        Set(element, "Capitalization", Capitalization?.ToString());
        Set(element, "Position", Position?.ToString());
        Set(element, "BaselineShift", BaselineShift);
        Set(element, "HorizontalScale", HorizontalScale);
        // As 07-char-outlined-text, 07-char-basics and 07-char-underline-strike: the stroke, skew, weights, offsets and
        // tints are attributes; the underline and strikethrough colours and types object references in Properties.
        Set(element, "StrokeColor", StrokeColor?.Reference);
        Set(element, "StrokeWeight", StrokeWeight);
        Set(element, "StrokeTint", StrokeTint);
        Set(element, "Skew", Skew);
        Set(element, "UnderlineWeight", UnderlineWeight);
        Set(element, "UnderlineOffset", UnderlineOffset);
        Set(element, "UnderlineTint", UnderlineTint);
        Set(element, "StrikeThroughWeight", StrikeThroughWeight);
        Set(element, "StrikeThroughOffset", StrikeThroughOffset);
        Set(element, "StrikeThroughTint", StrikeThroughTint);
        if (UnderlineColor is { } underlineColor)
        {
            IdmlXml.SetProperty(element, "UnderlineColor", "object", underlineColor.Reference);
        }
        if (UnderlineType is { } underlineType)
        {
            IdmlXml.SetProperty(element, "UnderlineType", "object", StrokeStyleReference(underlineType));
        }
        if (StrikeThroughColor is { } strikeThroughColor)
        {
            IdmlXml.SetProperty(element, "StrikeThroughColor", "object", strikeThroughColor.Reference);
        }
        if (StrikeThroughType is { } strikeThroughType)
        {
            IdmlXml.SetProperty(element, "StrikeThroughType", "object", StrokeStyleReference(strikeThroughType));
        }
    }

    /// <summary>The stroke style InDesign's Underline and Strikethrough Options pick for a line type.</summary>
    private static string StrokeStyleReference(TextLineType type) => type switch
    {
        TextLineType.Dashed => "StrokeStyle/$ID/Dashed",
        TextLineType.Dotted => "StrokeStyle/$ID/Canned Dotted",
        TextLineType.Wavy => "StrokeStyle/$ID/Wavy",
        TextLineType.ThickThin => "StrokeStyle/$ID/ThickThin",
        _ => "StrokeStyle/$ID/Solid",
    };

    private protected static void Set(XElement element, string name, string? value)
    {
        if (value is not null)
        {
            element.SetAttributeValue(name, value);
        }
    }

    private protected static void Set(XElement element, string name, double? value)
    {
        if (value is { } number)
        {
            element.SetAttributeValue(name, IdmlXml.Format(number));
        }
    }

    private protected static void Set(XElement element, string name, bool? value)
    {
        if (value is { } flag)
        {
            element.SetAttributeValue(name, flag ? "true" : "false");
        }
    }
}

/// <summary>Paragraph attributes (character attributes included, as in InDesign paragraph styles).</summary>
public sealed class ParagraphFormat : CharacterFormat
{
    /// <summary>Line spacing in points; null keeps the style's (InDesign default: Auto = 120% of size).</summary>
    public double? Leading { get; init; }

    public Justification? Justification { get; init; }

    public double? SpaceBefore { get; init; }

    public double? SpaceAfter { get; init; }

    public double? FirstLineIndent { get; init; }

    public double? LeftIndent { get; init; }

    public double? RightIndent { get; init; }

    public bool? Hyphenation { get; init; }

    /// <summary>
    /// Keep lines together: all of them (<see cref="KeepAllLinesTogether"/>), or at least
    /// <see cref="KeepFirstLines"/> and <see cref="KeepLastLines"/> on each side of a column break (orphans and widows;
    /// InDesign's defaults are 2 and 2).
    /// </summary>
    public bool? KeepLinesTogether { get; init; }

    /// <summary>With <see cref="KeepLinesTogether"/>: the paragraph never breaks across columns.</summary>
    public bool? KeepAllLinesTogether { get; init; }

    /// <summary>With <see cref="KeepLinesTogether"/>: lines a paragraph keeps before a column break (orphans).</summary>
    public int? KeepFirstLines { get; init; }

    /// <summary>With <see cref="KeepLinesTogether"/>: lines a paragraph keeps after a column break (widows).</summary>
    public int? KeepLastLines { get; init; }

    /// <summary>Keep the paragraph's last line with the next paragraph's first N lines (headings: 1 or 2).</summary>
    public int? KeepWithNext { get; init; }

    /// <summary>Start the paragraph in the next column, frame or page (or the next odd or even page).</summary>
    public StartParagraph? StartParagraph { get; init; }

    /// <summary>
    /// Bullet or numbered list. InDesign generates the bullet or number; it is not part of the text. For the usual
    /// hanging list, give a <see cref="LeftIndent"/> and the opposite <see cref="FirstLineIndent"/>: the tab after
    /// the marker then stops at the left indent.
    /// </summary>
    public ListType? ListType { get; init; }

    /// <summary>The bullet (one character; InDesign's default is U+2022 •).</summary>
    public string? BulletCharacter { get; init; }

    /// <summary>
    /// Text after the bullet, with InDesign's metacharacters: <c>^t</c> tab (the default), <c>^m</c> em space,
    /// <c>^&gt;</c> en space. A tab character is written as <c>^t</c>.
    /// </summary>
    public string? BulletTextAfter { get; init; }

    /// <summary>Number style, one of <see cref="NumberingFormats"/>.</summary>
    public string? NumberingFormat { get; init; }

    /// <summary>
    /// The number's text: <c>^#</c> is the number, <c>^1</c> to <c>^9</c> the current number of that level,
    /// <c>^t</c> a tab. InDesign's default is <c>^#.^t</c>. A tab character is written as <c>^t</c>.
    /// </summary>
    public string? NumberingExpression { get; init; }

    /// <summary>List level, 1 to 9: a level restarts after any paragraph of a higher level.</summary>
    public int? NumberingLevel { get; init; }

    /// <summary>The first number (used when the list starts or restarts).</summary>
    public int? NumberingStartAt { get; init; }

    /// <summary>False restarts the numbering at this paragraph (at <see cref="NumberingStartAt"/>); true continues it.</summary>
    public bool? NumberingContinue { get; init; }

    /// <summary>
    /// Tab stops, replacing the ones set before (an empty list clears them, so InDesign's default stops every 36 pt
    /// apply). Null keeps the style's.
    /// </summary>
    public IReadOnlyList<TabStop>? TabStops { get; init; }

    /// <summary>Lines the drop cap drops through (0 for none).</summary>
    public int? DropCapLines { get; init; }

    /// <summary>Characters the drop cap takes (InDesign's default 1 once <see cref="DropCapLines"/> is set).</summary>
    public int? DropCapCharacters { get; init; }

    /// <summary>
    /// The drop cap's character style, stored as InDesign does: a first nested style "through 1 drop cap". Other
    /// nested styles stay after it.
    /// </summary>
    public CharacterStyle? DropCapStyle { get; init; }

    /// <summary>
    /// Nested styles, in order, replacing the ones set before on the same paragraph or style (the drop cap's style, see
    /// <see cref="DropCapStyle"/>, stays first); an empty list turns them off. Null keeps the style's.
    /// </summary>
    public IReadOnlyList<NestedStyle>? NestedStyles { get; init; }

    /// <summary>GREP styles, in order (a later one wins what both set), replacing the ones set before; empty clears them.</summary>
    public IReadOnlyList<GrepStyle>? GrepStyles { get; init; }

    /// <summary>Align to Baseline Grid (<c>GridAlignment="AlignBaseline"</c>; false: <c>None</c>).</summary>
    public bool? AlignToBaselineGrid { get; init; }

    /// <summary>With <see cref="AlignToBaselineGrid"/>: only the first line of the paragraph snaps to the grid.</summary>
    public bool? GridAlignFirstLineOnly { get; init; }

    /// <summary>Span the frame's columns or split the paragraph's own column (Paragraph &gt; Span Columns).</summary>
    public ColumnSpan? SpanColumnType { get; init; }

    /// <summary>
    /// Columns spanned (<see cref="ColumnSpan.SpanColumns"/>; 0 = all, InDesign's default) or sub-columns
    /// (<see cref="ColumnSpan.SplitColumns"/>; default 2).
    /// </summary>
    public int? SpanSplitColumnCount { get; init; }

    /// <summary>The gutter between split sub-columns (InDesign's default 6 pt).</summary>
    public double? SplitColumnInsideGutter { get; init; }

    /// <summary>Space between split sub-columns and the column's edges (default 0).</summary>
    public double? SplitColumnOutsideGutter { get; init; }

    internal override void ApplyTo(XElement element)
    {
        base.ApplyTo(element);
        if (Leading is { } leading)
        {
            IdmlXml.SetProperty(element, "Leading", "unit", IdmlXml.Format(leading));
        }
        Set(element, "Justification", Justification is { } value ? ToIdml(value) : null);
        Set(element, "SpaceBefore", SpaceBefore);
        Set(element, "SpaceAfter", SpaceAfter);
        Set(element, "FirstLineIndent", FirstLineIndent);
        Set(element, "LeftIndent", LeftIndent);
        Set(element, "RightIndent", RightIndent);
        Set(element, "Hyphenation", Hyphenation);
        Set(element, "KeepLinesTogether", KeepLinesTogether);
        Set(element, "KeepAllLinesTogether", KeepAllLinesTogether);
        Set(element, "KeepFirstLines", KeepFirstLines?.ToString(CultureInfo.InvariantCulture));
        Set(element, "KeepLastLines", KeepLastLines?.ToString(CultureInfo.InvariantCulture));
        Set(element, "KeepWithNext", KeepWithNext?.ToString(CultureInfo.InvariantCulture));
        Set(element, "StartParagraph", StartParagraph switch
        {
            Dom.StartParagraph.Anywhere => "Anywhere",
            Dom.StartParagraph.NextColumn => "NextColumn",
            Dom.StartParagraph.NextFrame => "NextFrame",
            Dom.StartParagraph.NextPage => "NextPage",
            Dom.StartParagraph.NextOddPage => "NextOddPage",
            Dom.StartParagraph.NextEvenPage => "NextEvenPage",
            _ => null,
        });
        Set(element, "BulletsAndNumberingListType", ListType switch
        {
            Dom.ListType.None => "NoList",
            Dom.ListType.Bullet => "BulletList",
            Dom.ListType.Numbered => "NumberedList",
            _ => null,
        });
        if (BulletCharacter is { Length: > 0 } bullet)
        {
            var properties = IdmlXml.GetOrCreateProperties(element);
            properties.Element("BulletChar")?.Remove();
            properties.Add(new XElement("BulletChar",
                new XAttribute("BulletCharacterType", "UnicodeOnly"),
                new XAttribute("BulletCharacterValue", char.ConvertToUtf32(bullet, 0).ToString(CultureInfo.InvariantCulture))));
        }
        Set(element, "BulletsTextAfter", Metacharacters(BulletTextAfter));
        if (NumberingFormat is not null)
        {
            IdmlXml.SetProperty(element, "NumberingFormat", "string", NumberingFormat);
        }
        Set(element, "NumberingExpression", Metacharacters(NumberingExpression));
        Set(element, "NumberingLevel", NumberingLevel?.ToString(CultureInfo.InvariantCulture));
        Set(element, "NumberingStartAt", NumberingStartAt?.ToString(CultureInfo.InvariantCulture));
        Set(element, "NumberingContinue", NumberingContinue);
        if (TabStops is not null)
        {
            // As InDesign writes it: one record per stop, in position order.
            var properties = IdmlXml.GetOrCreateProperties(element);
            properties.Element("TabList")?.Remove();
            properties.Add(new XElement("TabList", new XAttribute("type", "list"),
                TabStops.OrderBy(stop => stop.Position).Select(stop => new XElement("ListItem", new XAttribute("type", "record"),
                    new XElement("Alignment", new XAttribute("type", "enumeration"), stop.Alignment switch
                    {
                        TabAlignment.Center => "CenterAlign",
                        TabAlignment.Right => "RightAlign",
                        TabAlignment.Character => "CharacterAlign",
                        _ => "LeftAlign",
                    }),
                    new XElement("AlignmentCharacter", new XAttribute("type", "string"), stop.AlignOn),
                    new XElement("Leader", new XAttribute("type", "string"), stop.Leader),
                    new XElement("Position", new XAttribute("type", "unit"), IdmlXml.Format(stop.Position))))));
        }
        Set(element, "DropCapCharacters", DropCapCharacters?.ToString(CultureInfo.InvariantCulture));
        Set(element, "DropCapLines", DropCapLines?.ToString(CultureInfo.InvariantCulture));
        if (DropCapStyle is not null || NestedStyles is not null)
        {
            WriteNestedStyles(element);
        }
        if (GrepStyles is not null)
        {
            // <AllGREPStyles type="list"><ListItem type="record"><AppliedCharacterStyle type="object">...</AppliedCharacterStyle>
            // <GrepExpression type="string">\d+</GrepExpression></ListItem></AllGREPStyles>, as 08-para-nested-grep-styles.
            WriteList(element, "AllGREPStyles", "EmptyGrepStyles", GrepStyles.Select(grep => new XElement("ListItem", new XAttribute("type", "record"),
                new XElement("AppliedCharacterStyle", new XAttribute("type", "object"), grep.CharacterStyle.Reference),
                new XElement("GrepExpression", new XAttribute("type", "string"), grep.Expression))).ToList());
        }
        Set(element, "GridAlignment", AlignToBaselineGrid switch
        {
            true => "AlignBaseline",
            false => "None",
            null => null,
        });
        Set(element, "GridAlignFirstLineOnly", GridAlignFirstLineOnly);
        if (SpanColumnType is { } span)
        {
            // SpanColumnType="SpanColumns" with <SpanSplitColumnCount type="enumeration">All</SpanSplitColumnCount> (or
            // type="short" for a count) in Properties, as 06-frame-span-balance.
            element.SetAttributeValue("SpanColumnType", span.ToString());
            var count = SpanSplitColumnCount ?? (span == ColumnSpan.SplitColumns ? 2 : 0);
            if (span != ColumnSpan.SingleColumn)
            {
                IdmlXml.SetProperty(element, "SpanSplitColumnCount", count > 0 ? "short" : "enumeration",
                    count > 0 ? count.ToString(CultureInfo.InvariantCulture) : "All");
            }
        }
        Set(element, "SplitColumnInsideGutter", SplitColumnInsideGutter);
        Set(element, "SplitColumnOutsideGutter", SplitColumnOutsideGutter);
    }

    /// <summary>
    /// <c>AllNestedStyles</c>: the drop cap's style first ("through 1 Dropcap", as 08-para-drop-caps stores it), then the
    /// nested styles; whichever of the two is not given keeps its entries.
    /// </summary>
    private void WriteNestedStyles(XElement element)
    {
        static bool IsDropCap(XElement item) =>
            item.Element("Delimiter") is { } delimiter && delimiter.Attribute("type")?.Value == "enumeration" && delimiter.Value == "Dropcap";
        var existing = element.Element(IdmlXml.Properties)?.Element("AllNestedStyles")?.Elements("ListItem").ToList() ?? [];
        var dropCap = DropCapStyle is { } style
            ? [NestedItem(new NestedStyle(style, NestedStyleDelimiter.DropCap))]
            : existing.Where(IsDropCap).Select(item => new XElement(item)).ToList();
        var others = NestedStyles is { } nested
            ? nested.Select(NestedItem).ToList()
            : existing.Where(item => !IsDropCap(item)).Select(item => new XElement(item)).ToList();
        WriteList(element, "AllNestedStyles", "EmptyNestedStyles", [.. dropCap, .. others]);
    }

    private static XElement NestedItem(NestedStyle style) => new("ListItem", new XAttribute("type", "record"),
        new XElement("AppliedCharacterStyle", new XAttribute("type", "object"), style.CharacterStyle.Reference),
        new XElement("Delimiter", new XAttribute("type", style.Delimiter.IsEnumeration ? "enumeration" : "string"), style.Delimiter.Value),
        new XElement("Repetition", new XAttribute("type", "long"), style.Repetition.ToString(CultureInfo.InvariantCulture)),
        new XElement("Inclusive", new XAttribute("type", "boolean"), style.Inclusive ? "true" : "false"));

    /// <summary>
    /// A list property with its <c>Empty...</c> flag as InDesign writes them: the list and <c>"false"</c>, or no list
    /// and <c>"true"</c> (which also stops a based-on style's list from applying).
    /// </summary>
    private static void WriteList(XElement element, string name, string emptyFlag, List<XElement> items)
    {
        element.Element(IdmlXml.Properties)?.Element(name)?.Remove();
        element.SetAttributeValue(emptyFlag, items.Count == 0 ? "true" : "false");
        if (items.Count > 0)
        {
            IdmlXml.GetOrCreateProperties(element).Add(new XElement(name, new XAttribute("type", "list"), items));
        }
    }

    /// <summary>Tab characters as InDesign's <c>^t</c> (the list texts are stored with metacharacters).</summary>
    private static string? Metacharacters(string? text) => text?.Replace("\t", "^t", StringComparison.Ordinal);

    private static string ToIdml(Justification value) => value switch
    {
        Dom.Justification.Left => "LeftAlign",
        Dom.Justification.Center => "CenterAlign",
        Dom.Justification.Right => "RightAlign",
        _ => value.ToString(),
    };
}
