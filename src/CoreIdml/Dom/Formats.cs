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
    }

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

    public bool? KeepLinesTogether { get; init; }

    /// <summary>Keep with the next N lines.</summary>
    public int? KeepWithNext { get; init; }

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
        Set(element, "KeepWithNext", KeepWithNext?.ToString(CultureInfo.InvariantCulture));
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
