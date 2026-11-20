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

/// <summary>A corner of a paragraph's border or shading: <c>None</c>, <c>RoundedCorner</c>, <c>InverseRoundedCorner</c>, ...</summary>
internal readonly record struct BoxCorner(string Option, double Radius)
{
    public bool IsSquare => Option == "None" || Radius <= 0;
}

/// <summary>The four corners of a paragraph's border or shading, clockwise from the top left.</summary>
internal readonly record struct BoxCorners(BoxCorner TopLeft, BoxCorner TopRight, BoxCorner BottomRight, BoxCorner BottomLeft)
{
    public bool AllSquare => TopLeft.IsSquare && TopRight.IsSquare && BottomRight.IsSquare && BottomLeft.IsSquare;
}

/// <summary>
/// Where a paragraph's border or shading box goes (08-para-borders-shading): from the first line's ascent
/// (<paramref name="TopOrigin"/> <c>AscentTopOrigin</c>; <c>BaselineTopOrigin</c> and <c>LeadingTopOrigin</c> from
/// memory) to the last line's descent (<paramref name="BottomOrigin"/> <c>DescentBottomOrigin</c>, or
/// <c>BaselineBottomOrigin</c>), across the column or, with <paramref name="TextWidth"/>, the widest line's text,
/// grown by <paramref name="Offsets"/>; with <paramref name="Corners"/>.
/// </summary>
internal sealed record BoxPlacement(Spacing Offsets, BoxCorners Corners, bool TextWidth, string TopOrigin, string BottomOrigin);

/// <summary>
/// Paragraph shading (<c>ParagraphShadingOn</c>): the box filled in <paramref name="Color"/> (tint applied) behind the
/// paragraph's text, clipped to the frame with <paramref name="ClipToFrame"/>.
/// </summary>
internal sealed record ParagraphShading(ResolvedColor Color, BoxPlacement Box, bool ClipToFrame);

/// <summary>
/// A paragraph border (<c>ParagraphBorderOn</c>): the box stroked centred on its edges with the weight of each side
/// (<paramref name="Weights"/>; a side of 0 is not drawn), in <paramref name="Color"/> (tint applied) and stroke style
/// <paramref name="Type"/> over <paramref name="Gap"/>. <paramref name="DisplayIfSplits"/> draws the bottom and top
/// edges where the paragraph breaks across columns (<c>ParagraphBorderDisplayIfSplits</c>); <paramref name="Merge"/>
/// (<c>MergeConsecutiveParaBorders</c>) draws consecutive paragraphs with the same border as one box.
/// <paramref name="Cap"/> and <paramref name="Join"/> are the PDF line cap and join.
/// </summary>
internal sealed record ParagraphBorder(
    Spacing Weights, ResolvedColor Color, string Type, ResolvedColor? Gap, BoxPlacement Box, bool DisplayIfSplits, bool Merge, int Cap, int Join)
{
    /// <summary>Whether <paramref name="other"/> is drawn exactly like this border (colours compared by value).</summary>
    public bool SameAs(ParagraphBorder other) =>
        this with { Color = other.Color, Gap = other.Gap } == other && ParagraphDecorations.SameColor(Color, other.Color) && ParagraphDecorations.SameColor(Gap, other.Gap);
}

/// <summary>
/// The shading of a paragraph or of consecutive merged paragraphs, in one column: <paramref name="Box"/> filled with
/// <paramref name="Color"/>, clipped to the frame's bounds with <paramref name="ClipToFrame"/>.
/// </summary>
internal sealed record ParagraphShadingOp(Rect Box, BoxCorners Corners, ResolvedColor Color, bool ClipToFrame) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { Box = Box.Offset(dx, dy) };
}

/// <summary>
/// The border of a paragraph or of consecutive merged paragraphs, in one column: <paramref name="Box"/> stroked as
/// <paramref name="Border"/> says, without its top (<paramref name="OpenTop"/>) or bottom (<paramref name="OpenBottom"/>)
/// edge where the paragraph goes on from or to another column.
/// </summary>
internal sealed record ParagraphBorderOp(Rect Box, ParagraphBorder Border, bool OpenTop, bool OpenBottom) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { Box = Box.Offset(dx, dy) };
}

/// <summary>Reads paragraph rules, borders and shading from a paragraph's attributes (its own, then its style chain's).</summary>
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

    /// <summary>
    /// Paragraph shading; null when it is off, has no colour, or is not printed (<c>ParagraphShadingSuppressPrinting</c>,
    /// Do Not Print or Export: from memory, no sample sets it). The tint defaults to 20%, [No paragraph style]'s.
    /// </summary>
    public static ParagraphShading? Shading(Func<string, string?> get, SwatchCatalog swatches)
    {
        if (get("ParagraphShadingOn") != "true" || get("ParagraphShadingSuppressPrinting") == "true"
            || Tinted(swatches.ResolveColor(get("ParagraphShadingColor") ?? "Color/Black"), get("ParagraphShadingTint") ?? "20") is not { } color)
        {
            return null;
        }
        return new ParagraphShading(color, Placement(get, "ParagraphShading"), get("ParagraphShadingClipToFrame") == "true");
    }

    /// <summary>A paragraph border; null when it is off, has no colour or no side with a weight.</summary>
    public static ParagraphBorder? Border(Func<string, string?> get, SwatchCatalog swatches)
    {
        const string Prefix = "ParagraphBorder";
        if (get(Prefix + "On") != "true" || Tinted(swatches.ResolveColor(get(Prefix + "Color") ?? "Color/Black"), get(Prefix + "Tint")) is not { } color)
        {
            return null;
        }
        double Weight(string side) => Math.Max(0, Values.Number(get(Prefix + side + "LineWeight"), 1));
        var weights = new Spacing(Weight("Top"), Weight("Right"), Weight("Bottom"), Weight("Left"));
        if (weights is { Top: 0, Right: 0, Bottom: 0, Left: 0 })
        {
            return null;
        }
        return new ParagraphBorder(
            weights,
            color,
            get(Prefix + "Type") ?? "StrokeStyle/$ID/Solid",
            Tinted(swatches.ResolveColor(get(Prefix + "GapColor")), get(Prefix + "GapTint")),
            Placement(get, Prefix),
            get(Prefix + "DisplayIfSplits") == "true",
            get("MergeConsecutiveParaBorders") != "false",
            get(Prefix + "StrokeEndCap") switch { "RoundEndCap" => 1, "ProjectingEndCap" => 2, _ => 0 },
            get(Prefix + "StrokeEndJoin") switch { "RoundEndJoin" => 1, "BevelEndJoin" => 2, _ => 0 });
    }

    /// <summary>Whether two colours are the same (by value), or both absent.</summary>
    public static bool SameColor(ResolvedColor? a, ResolvedColor? b) =>
        a is null ? b is null : b is not null && a.Space == b.Space && a.Components.SequenceEqual(b.Components);

    private static BoxPlacement Placement(Func<string, string?> get, string prefix)
    {
        double Number(string name) => Values.Number(get(prefix + name), 0);
        BoxCorner Corner(string name) => new(get(prefix + name + "CornerOption") ?? "None", Values.Number(get(prefix + name + "CornerRadius"), 12));
        return new BoxPlacement(
            new Spacing(Number("TopOffset"), Number("RightOffset"), Number("BottomOffset"), Number("LeftOffset")),
            new BoxCorners(Corner("TopLeft"), Corner("TopRight"), Corner("BottomRight"), Corner("BottomLeft")),
            get(prefix + "Width") == "TextWidth",
            get(prefix + "TopOrigin") ?? "AscentTopOrigin",
            get(prefix + "BottomOrigin") ?? "DescentBottomOrigin");
    }

    private static ResolvedColor? Tinted(ResolvedColor? color, string? tint) =>
        color is not null && Values.Number(tint, -1) is var percent and >= 0 ? color.WithTint(percent) : color;
}
