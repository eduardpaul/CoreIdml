using CoreIdml.Dom;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// A paragraph rule (Paragraph &gt; Paragraph Rules, <c>RuleAbove*</c> or <c>RuleBelow*</c>): a stroke of
/// <paramref name="Weight"/> points whose bottom edge is <paramref name="Offset"/> above the first line's baseline
/// (rule above) or whose top edge is <paramref name="Offset"/> below the last line's baseline (rule below), as
/// 08-para-rules p1 and p2 show (weights 0.5 to 6 pt, offsets 0, 8 and 16). It spans the column, or with
/// <paramref name="TextWidth"/> the text of that line, less <paramref name="LeftIndent"/> and
/// <paramref name="RightIndent"/> (p3). <paramref name="TextColor"/> is <c>Text Color</c>, the default: the colour of
/// the text; otherwise <paramref name="Color"/> (null for [None]). <paramref name="Tint"/> applies to either (−1:
/// none); <paramref name="Type"/> and <paramref name="Gap"/> are the stroke style and the gap colour, drawn as
/// underlines draw them (p4). <paramref name="KeepInFrame"/> is <c>KeepRuleAboveInFrame</c> (rule above only).
/// </summary>
internal sealed record ParagraphRule(
    double Weight, double Offset, bool TextColor, ResolvedColor? Color, double Tint, string Type, ResolvedColor? Gap,
    bool TextWidth, double LeftIndent, double RightIndent, bool KeepInFrame)
{
    /// <summary>The rule's colour for text filled with <paramref name="fill"/>; null draws nothing.</summary>
    public ResolvedColor? ColorFor(ResolvedColor? fill)
    {
        var color = TextColor ? fill : Color;
        return color is not null && Tint >= 0 ? color.WithTint(Tint) : color;
    }

    /// <summary>
    /// How far above the first line's baseline a rule above kept in its frame reaches (its offset and weight): at the top
    /// of a column the line moves down until the rule's top is at the column's top (08-para-rules p5: a 3 pt rule 20 pt
    /// up puts the first baseline 23 pt below the frame's inset, not at its 9.7 pt ascent). 0 when it is not kept.
    /// </summary>
    public double KeptRoom => KeepInFrame ? Offset + Weight : 0;
}

/// <summary>Reads paragraph rules from a paragraph's attributes (its own, then its style chain's).</summary>
internal static class ParagraphDecorations
{
    private const string TextColorName = "Text Color";

    /// <summary>
    /// The rule above (<paramref name="prefix"/> <c>RuleAbove</c>) or below (<c>RuleBelow</c>); null when it is off.
    /// Defaults are [No paragraph style]'s: 1 pt, offset 0, column width, Text Color, solid.
    /// </summary>
    public static ParagraphRule? Rule(Func<string, string?> get, string prefix, SwatchCatalog swatches)
    {
        if (get(prefix) != "true")
        {
            return null;
        }
        var colorReference = get(prefix + "Color") ?? TextColorName;
        var textColor = colorReference == TextColorName;
        return new ParagraphRule(
            Math.Max(0, Values.Number(get(prefix + "LineWeight"), 1)),
            Values.Number(get(prefix + "Offset"), 0),
            textColor,
            textColor ? null : swatches.ResolveColor(colorReference),
            Values.Number(get(prefix + "Tint"), -1),
            get(prefix + "Type") ?? "StrokeStyle/$ID/Solid",
            Tinted(swatches.ResolveColor(get(prefix + "GapColor")), get(prefix + "GapTint")),
            get(prefix + "Width") == "TextWidth",
            Values.Number(get(prefix + "LeftIndent"), 0),
            Values.Number(get(prefix + "RightIndent"), 0),
            prefix == "RuleAbove" && get("KeepRuleAboveInFrame") == "true");
    }

    private static ResolvedColor? Tinted(ResolvedColor? color, string? tint) =>
        color is not null && Values.Number(tint, -1) is var percent and >= 0 ? color.WithTint(percent) : color;
}
