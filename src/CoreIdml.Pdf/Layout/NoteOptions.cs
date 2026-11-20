using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// The rule InDesign draws above a column's footnotes (Footnote Options &gt; Layout &gt; Rule Above): on or off, weight,
/// colour (with its tint), stroke type and gap colour, left indent and width from the column's left edge, and how far
/// above the top of the footnote area its centre sits (<see cref="Offset"/>).
/// </summary>
internal sealed record NoteRule(bool On, double Weight, ResolvedColor? Color, string Type, ResolvedColor? Gap, double LeftIndent, double Width, double Offset)
{
    /// <summary>How far the rule reaches above the top of the footnote area (none when it is off).</summary>
    public double Extent => On && Color is not null ? Math.Max(0, Offset + (Weight / 2)) : 0;
}

/// <summary>
/// The document's footnote options (<c>FootnoteOption</c> in Resources/Preferences.xml), as 11-long-footnotes-endnotes
/// stores them:
/// <code>
/// &lt;FootnoteOption StartAt="1" Prefix="" Suffix="" FootnoteTextStyle="ParagraphStyle/Footnote Text"
///   FootnoteMarkerStyle="CharacterStyle/$ID/[No character style]" SeparatorText="&amp;#x9;" SpaceBetween="4" Spacer="0"
///   FootnoteFirstBaselineOffset="LeadingOffset" FootnoteMinimumFirstBaselineOffset="0" NoSplitting="false" RuleOn="true"
///   RuleLineWeight="0.75" RuleTint="100" RuleLeftIndent="0" RuleWidth="120" RuleOffset="4" ContinuingRuleOn="true"
///   ContinuingRuleLineWeight="1" ContinuingRuleWidth="288" ContinuingRuleOffset="0" ...&gt;
///   &lt;Properties&gt; &lt;FootnoteNumberingStyle&gt;Arabic&lt;/...&gt; &lt;RestartNumbering&gt;PageRestart&lt;/...&gt;
///     &lt;ShowPrefixSuffix&gt;NoPrefixSuffix&lt;/...&gt; &lt;MarkerPositioning&gt;SuperscriptMarker&lt;/...&gt;
///     &lt;RuleType&gt;StrokeStyle/$ID/Solid&lt;/...&gt; &lt;RuleColor&gt;Color/Red&lt;/...&gt; &lt;RuleGapColor&gt;Swatch/None&lt;/...&gt; ...
/// </code>
/// The separator after the number (<c>SeparatorText</c>) and the footnote paragraph style are stored in the footnote's
/// own text, so the composer needs neither. A text frame can override the spacing (<c>TextFramePreference</c>
/// <c>FootnotesEnableOverrides</c>, <c>FootnotesMinimumSpacing</c>, <c>FootnotesSpaceBetween</c>,
/// <see cref="FrameArea.FootnoteSpacing"/>).
/// </summary>
internal sealed record FootnoteOptions(
    int StartAt, string NumberingStyle, string Restart, string Prefix, string Suffix, string ShowPrefixSuffix, string? MarkerStyle,
    string MarkerPositioning, double SpaceBefore, double SpaceBetween, string FirstBaselineOffset, double MinimumFirstBaselineOffset,
    bool AllowSplitting, NoteRule Rule, NoteRule ContinuingRule)
{
    public static FootnoteOptions Read(IdmlDocument document)
    {
        var option = document.Package.TryGetXml("Resources/Preferences.xml")?.Root?.Element("FootnoteOption") ?? new XElement("FootnoteOption");
        string? Get(string name) => NoteOptionValues.Get(option, name);
        double Number(string name, double fallback) => Values.Number(Get(name), fallback);
        NoteRule RuleOf(string prefix, double weight, string color, double width) => new(
            Get(prefix + "On") != "false",
            Number(prefix + "LineWeight", weight),
            NoteOptionValues.Color(document, Get(prefix + "Color") ?? color, Number(prefix + "Tint", 100)),
            Get(prefix + "Type") ?? "StrokeStyle/$ID/Solid",
            NoteOptionValues.Color(document, Get(prefix + "GapColor"), Number(prefix + "GapTint", 100)),
            Number(prefix + "LeftIndent", 0),
            Number(prefix + "Width", width),
            Number(prefix + "Offset", 0));
        return new FootnoteOptions(
            (int)Number("StartAt", 1),
            Get("FootnoteNumberingStyle") ?? "Arabic",
            Get("RestartNumbering") ?? "DontRestart",
            Get("Prefix") ?? string.Empty,
            Get("Suffix") ?? string.Empty,
            Get("ShowPrefixSuffix") ?? "NoPrefixSuffix",
            NoteOptionValues.Named(Get("FootnoteMarkerStyle")),
            Get("MarkerPositioning") ?? "SuperscriptMarker",
            Math.Max(0, Number("Spacer", 0)),
            Math.Max(0, Number("SpaceBetween", 0)),
            Get("FootnoteFirstBaselineOffset") ?? "AscentOffset",
            Number("FootnoteMinimumFirstBaselineOffset", 0),
            Get("NoSplitting") != "true",
            RuleOf("Rule", 1, "Color/Black", 72),
            RuleOf("ContinuingRule", 1, "Color/Black", 288));
    }

    /// <summary>A footnote's number as the reference in the text prints it, or (<paramref name="reference"/> false) as its footnote does.</summary>
    public string Format(int number, bool reference) => NoteOptionValues.Format(number, NumberingStyle, Prefix, Suffix, ShowPrefixSuffix, reference);
}

/// <summary>
/// The document's endnote options (<c>EndnoteOption</c> in designmap.xml, InDesign 2018 and later), as
/// 11-long-footnotes-endnotes stores them: <c>StartEndnoteNumberAt</c>, <c>EndnoteMarkerStyle</c>,
/// <c>EndnotePrefix</c>, <c>EndnoteSuffix</c> and, under Properties, <c>EndnoteNumberingStyle</c>,
/// <c>EndnoteMarkerPositioning</c> and <c>ShowEndnotePrefixSuffix</c>. The endnote text is an ordinary story
/// (<c>Story@IsEndnoteStory="true"</c>) in an <c>EndnoteTextFrame</c>; each endnote is an <c>EndnoteRange</c> of it
/// (<c>SourceEndnote</c>), in the order of the references.
/// </summary>
internal sealed record EndnoteOptions(int StartAt, string NumberingStyle, string Prefix, string Suffix, string ShowPrefixSuffix, string? MarkerStyle, string MarkerPositioning)
{
    public static EndnoteOptions Read(IdmlDocument document)
    {
        var option = document.DesignMap.Element("EndnoteOption") ?? new XElement("EndnoteOption");
        string? Get(string name) => NoteOptionValues.Get(option, name);
        return new EndnoteOptions(
            (int)Values.Number(Get("StartEndnoteNumberAt"), 1),
            Get("EndnoteNumberingStyle") ?? "Arabic",
            Get("EndnotePrefix") ?? string.Empty,
            Get("EndnoteSuffix") ?? string.Empty,
            Get("ShowEndnotePrefixSuffix") ?? "NoPrefixSuffix",
            NoteOptionValues.Named(Get("EndnoteMarkerStyle")),
            Get("EndnoteMarkerPositioning") ?? "SuperscriptMarker");
    }

    /// <summary>An endnote's number as the reference in the text prints it, or (<paramref name="reference"/> false) in the endnote.</summary>
    public string Format(int number, bool reference) => NoteOptionValues.Format(number, NumberingStyle, Prefix, Suffix, ShowPrefixSuffix, reference);
}

internal static class NoteOptionValues
{
    /// <summary>An option's attribute, or its element under Properties (where IDML puts enumerations and objects).</summary>
    public static string? Get(XElement option, string name) =>
        option.Attribute(name)?.Value ?? option.Element("Properties")?.Element(name)?.Value;

    /// <summary>A style reference, null for none ("[No character style]").</summary>
    public static string? Named(string? style) =>
        style is null or "" or "n" || style.EndsWith("[No character style]", StringComparison.Ordinal) ? null : style;

    public static ResolvedColor? Color(IdmlDocument document, string? reference, double tint) =>
        reference is null or "Swatch/None" ? null : document.Swatches.ResolveColor(reference)?.WithTint(tint);

    /// <summary>
    /// A note number in its numbering style, with the prefix and suffix where <paramref name="show"/> puts them: the
    /// reference, the note's own text, or both. Arabic, prefix and suffix off is what the sample has; the other names
    /// (<c>UpperRoman</c>, <c>LowerRoman</c>, <c>UpperLetters</c>, <c>LowerLetters</c>, <c>SingleLeadingZeros</c>,
    /// <c>DoubleLeadingZeros</c>, <c>Asterisks</c>, <c>Symbols</c>; <c>PrefixSuffixReference</c>, <c>PrefixSuffixText</c>,
    /// <c>PrefixSuffixBoth</c>) and InDesign's symbol sequence are from memory.
    /// </summary>
    public static string Format(int number, string style, string prefix, string suffix, string show, bool reference)
    {
        var text = style switch
        {
            "UpperRoman" => ListMarkers.Format(number, "I"),
            "LowerRoman" => ListMarkers.Format(number, "i"),
            "UpperLetters" => ListMarkers.Format(number, "A"),
            "LowerLetters" => ListMarkers.Format(number, "a"),
            "SingleLeadingZeros" => ListMarkers.Format(number, "01"),
            "DoubleLeadingZeros" => ListMarkers.Format(number, "001"),
            "Asterisks" when number > 0 => new string('*', number),
            "Symbols" when number > 0 => Symbol(number),
            _ => ListMarkers.Format(number, "1"),
        };
        var shown = show.Contains("Both", StringComparison.Ordinal)
            || (reference ? show.Contains("Reference", StringComparison.Ordinal) : show.EndsWith("Text", StringComparison.Ordinal));
        return shown ? prefix + text + suffix : text;
    }

    // *, †, ‡, §, ¶, #, then each doubled, tripled...
    private static string Symbol(int number)
    {
        ReadOnlySpan<char> symbols = ['*', '†', '‡', '§', '¶', '#'];
        var symbol = symbols[(number - 1) % symbols.Length];
        return new StringBuilder().Append(symbol, ((number - 1) / symbols.Length) + 1).ToString();
    }
}
