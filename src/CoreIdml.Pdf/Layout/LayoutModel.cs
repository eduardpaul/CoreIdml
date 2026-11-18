using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;

namespace CoreIdml.Pdf.Layout;

/// <summary>Resolved character formatting of a run.</summary>
internal sealed record RunStyle
{
    public required FontFace Font { get; init; }

    public double Size { get; init; } = 12;

    public ResolvedColor? Fill { get; init; } = new(ColorSpaceKind.Cmyk, [0, 0, 0, 1]);

    /// <summary>Tracking in 1/1000 em.</summary>
    public double Tracking { get; init; }

    /// <summary>Horizontal scale, 1 = 100%.</summary>
    public double HorizontalScale { get; init; } = 1;

    /// <summary>Vertical scale, 1 = 100%: glyphs are stretched up from the baseline; line spacing is unchanged.</summary>
    public double VerticalScale { get; init; } = 1;

    public double BaselineShift { get; init; }

    public string Capitalization { get; init; } = "Normal";

    public string Position { get; init; } = "Normal";

    public bool Underline { get; init; }

    public bool StrikeThrough { get; init; }

    public bool Kerning { get; init; } = true;

    /// <summary>Fixed leading in points; null means Auto.</summary>
    public double? Leading { get; init; }

    /// <summary>Auto leading as a fraction of the point size (InDesign default 120% = 1.2).</summary>
    public double AutoLeading { get; init; } = 1.2;

    public double EffectiveLeading => Leading ?? (AutoLeading * Size);

    /// <summary>InDesign language name ("$ID/Spanish: Castilian"), used for hyphenation.</summary>
    public string? Language { get; init; }
}

/// <summary>A tab stop: position from the column's left edge.</summary>
internal readonly record struct TabStop(double Position, string Alignment);

/// <summary>Resolved paragraph formatting.</summary>
internal sealed record ParagraphFormatting
{
    public string Justification { get; init; } = "LeftAlign";

    public double SpaceBefore { get; init; }

    public double SpaceAfter { get; init; }

    public double LeftIndent { get; init; }

    public double RightIndent { get; init; }

    public double FirstLineIndent { get; init; }

    public double LastLineIndent { get; init; }

    public IReadOnlyList<TabStop> Tabs { get; init; } = [];

    public HyphenationSettings Hyphenation { get; init; } = new(false, 2, 2, 5, true);
}

/// <summary>An element of a paragraph's content.</summary>
internal abstract record InlineItem(RunStyle Style);

internal sealed record TextItem(string Text, RunStyle Style) : InlineItem(Style);

internal sealed record TabItem(RunStyle Style, bool RightIndentTab = false) : InlineItem(Style);

internal sealed record LineBreakItem(RunStyle Style) : InlineItem(Style);

internal sealed record PageNumberItem(RunStyle Style) : InlineItem(Style);

internal sealed record TableItem(XElement Table, RunStyle Style) : InlineItem(Style);

/// <summary>
/// A page item anchored inline in the text (a shape, frame or group that flows like a character): its bounds in its
/// own parent coordinates (where its ItemTransform puts it; the composer moves it to the line) and how far above the
/// baseline its bottom sits.
/// </summary>
internal sealed record InlineObjectItem(XElement Element, Rect Bounds, double YOffset, RunStyle Style) : InlineItem(Style);

/// <summary>A paragraph: its formatting, content, and the style of its paragraph mark (height of an empty line).</summary>
internal sealed record ParagraphModel(ParagraphFormatting Format, IReadOnlyList<InlineItem> Items, RunStyle MarkStyle);

/// <summary>Something to paint, in the coordinates of the frame (or cell) it was laid out in.</summary>
internal abstract record DrawOp
{
    public abstract DrawOp Offset(double dx, double dy);
}

/// <summary>One glyph placed by the composer: the advance includes kerning, tracking and justification.</summary>
internal readonly record struct PlacedGlyph(ushort Glyph, string Text, double Advance, double NaturalAdvance);

internal sealed record GlyphRunOp(FontFace Font, double Size, double HorizontalScale, ResolvedColor? Color, double X, double Y, IReadOnlyList<PlacedGlyph> Glyphs) : DrawOp
{
    public string Text => string.Concat(Glyphs.Select(glyph => glyph.Text));

    /// <summary>Vertical glyph scale, 1 = 100%.</summary>
    public double VerticalScale { get; init; } = 1;

    public double Width => Glyphs.Sum(glyph => glyph.Advance);

    public override DrawOp Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

internal sealed record RectOp(Rect Rect, ResolvedColor Color) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { Rect = Rect.Offset(dx, dy) };
}

internal sealed record LineOp(Point From, Point To, double Width, ResolvedColor Color) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) =>
        this with { From = new Point(From.X + dx, From.Y + dy), To = new Point(To.X + dx, To.Y + dy) };
}

/// <summary>A page item anchored in text, drawn through <paramref name="Placement"/> (its parent coordinates to the frame's).</summary>
internal sealed record PageItemOp(XElement Element, Matrix Placement) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { Placement = Placement.Then(Matrix.Translation(dx, dy)) };
}

/// <summary>Where a story is laid out: one entry per threaded frame, columns in the frame's inner coordinates.</summary>
internal sealed record FrameArea(string FrameId, IReadOnlyList<Rect> Columns, string VerticalJustification, string FirstBaselineOffset, double MinimumFirstBaselineOffset, string PageName);

/// <summary>A laid-out line (for tests and reports).</summary>
internal sealed record LineInfo(int FrameIndex, int Column, double Baseline, double Left, double Width, string Text);

/// <summary>The result of composing a story into its frames.</summary>
internal sealed class StoryLayout
{
    public Dictionary<string, List<DrawOp>> OpsByFrame { get; } = new(StringComparer.Ordinal);

    public List<LineInfo> Lines { get; } = [];

    /// <summary>Text did not fit in the frames (InDesign would show the red overset marker).</summary>
    public bool Overset { get; set; }

    public int OversetCharacters { get; set; }

    /// <summary>Descent of the last placed line (how far its glyphs hang below the baseline).</summary>
    public double LastDescent { get; set; }
}

/// <summary>Number parsing helpers for IDML attribute values.</summary>
internal static class Values
{
    public static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    public static double Number(string? value, double fallback) => Number(value) ?? fallback;

    public static double[] List(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => Number(part) ?? 0).ToArray();
}
