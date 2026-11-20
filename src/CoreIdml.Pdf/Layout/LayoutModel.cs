using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;

namespace CoreIdml.Pdf.Layout;

/// <summary>Resolved character formatting of a run.</summary>
internal sealed record RunStyle
{
    public required FontFace Font { get; init; }

    /// <summary>
    /// <see cref="Font"/> stands in for a family that is not installed. Only then do characters it lacks come from
    /// fallback fonts; InDesign prints nothing for characters missing from the document's own font.
    /// </summary>
    public bool FontIsSubstitute { get; init; }

    public double Size { get; init; } = 12;

    public ResolvedColor? Fill { get; init; } = new(ColorSpaceKind.Cmyk, [0, 0, 0, 1]);

    /// <summary>The gradient the text is filled with (then <see cref="Fill"/> is null).</summary>
    public TextGradient? FillGradient { get; init; }

    /// <summary>Tracking in 1/1000 em.</summary>
    public double Tracking { get; init; }

    /// <summary>Horizontal scale, 1 = 100%.</summary>
    public double HorizontalScale { get; init; } = 1;

    /// <summary>Vertical scale, 1 = 100%: glyphs are stretched up from the baseline; line spacing is unchanged.</summary>
    public double VerticalScale { get; init; } = 1;

    public double BaselineShift { get; init; }

    public string Capitalization { get; init; } = "Normal";

    public string Position { get; init; } = "Normal";

    /// <summary>The underline (<c>Underline="true"</c>) and its options; null for none.</summary>
    public TextRule? Underline { get; init; }

    /// <summary>The strikethrough (<c>StrikeThru="true"</c>) and its options; null for none.</summary>
    public TextRule? StrikeThrough { get; init; }

    /// <summary>The stroke on the glyph outlines (<c>StrokeColor</c>, <c>StrokeWeight</c>, ...); null for none.</summary>
    public TextStroke? Stroke { get; init; }

    /// <summary>Skew (false italic) in degrees, sheared about the baseline: positive leans the glyphs right.</summary>
    public double Skew { get; init; }

    public bool Kerning { get; init; } = true;

    /// <summary>Standard ligatures (fi, fl, ...) from the font, InDesign's default.</summary>
    public bool Ligatures { get; init; } = true;

    /// <summary>Fixed leading in points; null means Auto.</summary>
    public double? Leading { get; init; }

    /// <summary>Auto leading as a fraction of the point size (InDesign default 120% = 1.2).</summary>
    public double AutoLeading { get; init; } = 1.2;

    public double EffectiveLeading => Leading ?? (AutoLeading * Size);

    /// <summary>InDesign language name ("$ID/Spanish: Castilian"), used for hyphenation.</summary>
    public string? Language { get; init; }
}

/// <summary>A tab stop: position from the column's left edge.</summary>
/// <summary>
/// A tab stop: position from the column's left edge; alignment (LeftAlign, CenterAlign, RightAlign, CharacterAlign); the
/// character a CharacterAlign stop aligns on; and the leader drawn across the tab's space (empty for none).
/// </summary>
internal readonly record struct ResolvedTab(double Position, string Alignment, string AlignmentCharacter = ".", string Leader = "");

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

    public IReadOnlyList<ResolvedTab> Tabs { get; init; } = [];

    public HyphenationSettings Hyphenation { get; init; } = new(false, 2, 2, 5, true);

    /// <summary>
    /// <c>Composer</c>: the Adobe Paragraph Composer (<c>HL Composer</c>, InDesign's default) chooses the breaks of the
    /// whole paragraph; the Single-line Composer (<c>HL Single</c>) breaks one line at a time.
    /// </summary>
    public bool ParagraphComposer { get; init; } = true;

    /// <summary>Word spacing, letter spacing, glyph scaling and single-word justification.</summary>
    public JustificationSettings Spacing { get; init; } = JustificationSettings.Default;

    /// <summary>Align to Baseline Grid (<c>GridAlignment="AlignBaseline"</c>): the paragraph's baselines snap to the frame's grid.</summary>
    public bool AlignToBaselineGrid { get; init; }

    /// <summary><c>GridAlignFirstLineOnly</c>: only the first line snaps; the others keep their leading from it.</summary>
    public bool GridAlignFirstLineOnly { get; init; }

    public KeepOptions Keeps { get; init; } = KeepOptions.None;

    /// <summary>The paragraph's drop cap (<c>DropCapLines</c>, <c>DropCapCharacters</c>, <c>DropcapDetail</c>); null for none.</summary>
    public DropCapFormat? DropCap { get; init; }

    /// <summary>The paragraph spans or splits the frame's columns (<c>SpanColumnType</c>); null for a single column.</summary>
    public ColumnSpanFormat? ColumnSpan { get; init; }

    /// <summary>The rule above the paragraph (<c>RuleAbove="true"</c>); null for none.</summary>
    public ParagraphRule? RuleAbove { get; init; }

    /// <summary>The rule below the paragraph (<c>RuleBelow="true"</c>); null for none.</summary>
    public ParagraphRule? RuleBelow { get; init; }

    /// <summary>The paragraph's border (<c>ParagraphBorderOn="true"</c>); null for none.</summary>
    public ParagraphBorder? Border { get; init; }

    /// <summary>The paragraph's shading (<c>ParagraphShadingOn="true"</c>); null for none.</summary>
    public ParagraphShading? Shading { get; init; }
}

/// <summary>
/// Span Columns (Paragraph &gt; Span Columns, <c>SpanColumnType</c>): a paragraph that spans <paramref name="Count"/>
/// columns of its frame from the first (0: all, <c>SpanSplitColumnCount="All"</c>), or, with
/// <paramref name="Split"/>, is split into <paramref name="Count"/> sub-columns of its own column, with
/// <paramref name="InsideGutter"/> between them and <paramref name="OutsideGutter"/> at either side
/// (<c>SplitColumnInsideGutter</c>, <c>SplitColumnOutsideGutter</c>). <paramref name="SpaceBefore"/> and
/// <paramref name="SpaceAfter"/> are the least space a span keeps from the text above and below it
/// (<c>SpanColumnMinSpaceBefore</c>, <c>SpanColumnMinSpaceAfter</c>).
/// </summary>
internal sealed record ColumnSpanFormat(bool Split, int Count, double InsideGutter, double OutsideGutter, double SpaceBefore, double SpaceAfter);

/// <summary>
/// A drop cap: the first <paramref name="Characters"/> characters of the paragraph, <paramref name="Lines"/> lines
/// tall. <paramref name="AlignLeftEdge"/> is bit 0 of <c>DropcapDetail</c> (08-para-drop-caps p4: with "1", the
/// default in that document, the outline of the "g" starts at the text's left edge; with "0" its origin does).
/// </summary>
internal sealed record DropCapFormat(int Lines, int Characters, bool AlignLeftEdge);

/// <summary>
/// A paragraph's keep options (Paragraph &gt; Keep Options): keep its last line with the first
/// <paramref name="WithNext"/> lines of the next paragraph; keep its lines together (all of them, or at least
/// <paramref name="FirstLines"/> at the start and <paramref name="LastLines"/> at the end of each part; InDesign's
/// defaults of 2 and 2 only apply while <paramref name="LinesTogether"/> is on); and where it starts
/// (<paramref name="Start"/>: Anywhere, NextColumn, NextFrame, NextPage, NextOddPage, NextEvenPage).
/// </summary>
internal sealed record KeepOptions(int WithNext, bool LinesTogether, bool AllLines, int FirstLines, int LastLines, string Start)
{
    public static readonly KeepOptions None = new(0, false, false, 2, 2, "Anywhere");
}

/// <summary>An element of a paragraph's content.</summary>
internal abstract record InlineItem(RunStyle Style)
{
    /// <summary>Generated by InDesign (a list's bullet or number), not in the story: never hyphenated, not overset text.</summary>
    public bool Generated { get; init; }
}

internal sealed record TextItem(string Text, RunStyle Style) : InlineItem(Style)
{
    /// <summary>The run's <c>AppliedCharacterStyle</c>, which running-header variables match.</summary>
    public string? CharacterStyle { get; init; }
}

internal sealed record TabItem(RunStyle Style, bool RightIndentTab = false) : InlineItem(Style);

internal sealed record LineBreakItem(RunStyle Style) : InlineItem(Style);

/// <summary>Which page marker a <c>&lt;?ACE 18?&gt;</c> or <c>&lt;?ACE 19?&gt;</c> is.</summary>
internal enum PageMarker
{
    /// <summary>The current page's number (<c>&lt;?ACE 18?&gt;</c>).</summary>
    Current,

    /// <summary>
    /// <c>&lt;?ACE 18?&gt;</c> in a run with <c>PageNumberType="NextPageNumber"</c>: the page of the next frame of the
    /// story thread (a jump line, "Continued on page 4").
    /// </summary>
    Next,

    /// <summary><c>&lt;?ACE 18?&gt;</c> in a run with <c>PageNumberType="PreviousPageNumber"</c>: the previous frame's page.</summary>
    Previous,

    /// <summary>The section's <c>Marker</c> text (<c>&lt;?ACE 19?&gt;</c>).</summary>
    Section,
}

internal sealed record PageNumberItem(RunStyle Style, PageMarker Marker = PageMarker.Current) : InlineItem(Style);

/// <summary>
/// A text variable (<c>TextVariableInstance</c>): its text is computed per page where it depends on the page
/// (<see cref="FrameMarkers.Variable"/>), else it is the <c>ResultText</c> InDesign stored.
/// </summary>
internal sealed record TextVariableItem(XElement Instance, RunStyle Style) : InlineItem(Style)
{
    public string ResultText => Instance.Attribute("ResultText")?.Value ?? string.Empty;
}

internal sealed record TableItem(XElement Table, RunStyle Style) : InlineItem(Style);

/// <summary>
/// A footnote's reference in the text (a <c>Footnote</c> element): its number, numbered where the line goes, in
/// <see cref="InlineItem.Style"/>; the footnote's text goes to the foot of that column (see <c>TextComposer.Footnotes</c>).
/// </summary>
internal sealed record FootnoteItem(XElement Footnote, RunStyle Style) : InlineItem(Style);

/// <summary>The footnote's number at the start of its own text (<c>&lt;?ACE 4?&gt;</c>): <see cref="FrameArea.FootnoteNumber"/>.</summary>
internal sealed record FootnoteNumberItem(RunStyle Style) : InlineItem(Style);

/// <summary>
/// A page item anchored in the text (a shape, frame or group): its visible bounds in its own parent coordinates (where
/// its ItemTransform puts it; the composer moves it to the line). Inline (<see cref="Anchored"/> null) it flows like a
/// character, its bottom <see cref="YOffset"/> above the baseline; otherwise <see cref="Anchored"/> says where it goes.
/// </summary>
internal sealed record InlineObjectItem(XElement Element, Rect Bounds, double YOffset, RunStyle Style) : InlineItem(Style)
{
    /// <summary>The above-line or custom position (<c>AnchoredObjectSetting</c>); null for an inline object.</summary>
    public AnchorSettings? Anchored { get; init; }
}

/// <summary>
/// Where an above-line (<paramref name="AboveLine"/>) or custom (<c>AnchoredPosition="Anchored"</c>) anchored object
/// goes, as <c>AnchoredObjectSetting</c> stores it: the point of the object (<paramref name="AnchorPoint"/>, such as
/// <c>BottomRightAnchor</c>) placed on the reference point (<paramref name="HorizontalReferencePoint"/> with
/// <paramref name="HorizontalAlignment"/>, <paramref name="VerticalReferencePoint"/> with
/// <paramref name="VerticalAlignment"/>) moved by the offsets; <paramref name="Pinned"/> is Keep Within Top/Bottom
/// Column Boundaries (<c>PinPosition</c>), <paramref name="SpineRelative"/> Relative to Spine. Above the line,
/// <paramref name="HorizontalAlignment"/> aligns it in the column, <paramref name="SpaceAbove"/> is Space Before
/// (<c>AnchorSpaceAbove</c>) and <paramref name="YOffset"/> Space After.
/// </summary>
internal sealed record AnchorSettings(
    bool AboveLine, string AnchorPoint, string HorizontalAlignment, string HorizontalReferencePoint, string VerticalAlignment,
    string VerticalReferencePoint, double XOffset, double YOffset, double SpaceAbove, bool Pinned, bool SpineRelative)
{
    /// <summary>
    /// A custom-positioned object's text wrap, around its visible bounds (as <see cref="InlineObjectItem.Bounds"/>, before
    /// it is placed); null for none.
    /// </summary>
    public WrapRegion? Wrap { get; init; }
}

/// <summary>A paragraph: its formatting, content, and the style of its paragraph mark (height of an empty line).</summary>
internal sealed record ParagraphModel(ParagraphFormatting Format, IReadOnlyList<InlineItem> Items, RunStyle MarkStyle)
{
    /// <summary>The paragraph's <c>AppliedParagraphStyle</c>, which running-header variables match.</summary>
    public string? ParagraphStyle { get; init; }

    /// <summary>
    /// The paragraph's nested line styles, when it has any: <see cref="Items"/> are styled for its first line, and the
    /// composer restyles the rest of the paragraph at each new line.
    /// </summary>
    public NestedStyleText? LineStyles { get; init; }
}

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

    /// <summary>The gradient the glyphs are filled with, instead of <see cref="Color"/>.</summary>
    public TextGradient? Gradient { get; init; }

    /// <summary>The stroke on the glyph outlines; null for none.</summary>
    public TextStroke? Stroke { get; init; }

    /// <summary>Skew (false italic) in degrees: positive leans the glyphs right.</summary>
    public double Skew { get; init; }

    public double Width => Glyphs.Sum(glyph => glyph.Advance);

    public override DrawOp Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Glyphs painted as <paramref name="style"/> says: fill (colour or gradient), stroke, vertical scale, skew.</summary>
    public static GlyphRunOp Of(RunStyle style, FontFace font, double size, double horizontalScale, double x, double y, IReadOnlyList<PlacedGlyph> glyphs) =>
        new(font, size, horizontalScale, style.Fill, x, y, glyphs)
        {
            VerticalScale = style.VerticalScale,
            Gradient = style.FillGradient,
            Stroke = style.Stroke,
            Skew = style.Skew,
        };
}

/// <summary>
/// A gradient text fill or stroke: the swatch and the run's <c>GradientFillStart</c>, <c>GradientFillLength</c> and
/// <c>GradientFillAngle</c> (or the <c>GradientStroke</c> ones). Text keeps InDesign's default length of −1, which
/// spans the text frame (see <c>IdmlRenderer.GradientSpace</c>).
/// </summary>
internal sealed record TextGradient(ResolvedGradient Gradient, Point Start, double Length, double Angle);

/// <summary>
/// A stroke on the glyph outlines (Character panel stroke): a colour or a gradient (<paramref name="Gradient"/>, then
/// <paramref name="Color"/> is null), the weight in points, and <paramref name="Outside"/> for
/// <c>StrokeAlignment="OutsideAlignment"</c>, the default of [No paragraph style]. <paramref name="Join"/> is the PDF
/// line join (0 miter, 1 round, 2 bevel, from <c>EndJoin</c>).
/// </summary>
internal sealed record TextStroke(ResolvedColor? Color, TextGradient? Gradient, double Weight, bool Outside, int Join, double MiterLimit);

/// <summary>
/// An underline or a strikethrough and its options (07-char-underline-strike). <paramref name="Weight"/> and
/// <paramref name="Offset"/> are in points, null for automatic (IDML writes −9999): the composer then takes them from
/// the font. <paramref name="TextColor"/>: the colour is the text's fill (<c>Text Color</c>, the default); otherwise
/// <paramref name="Color"/>, null for [None]. <paramref name="Tint"/> applies to either (−1: none).
/// <paramref name="Type"/> is the stroke style (<c>StrokeStyle/$ID/Solid</c>, <c>.../Dashed</c>, ...) and
/// <paramref name="Gap"/> the colour painted between its dashes, null for none.
/// </summary>
internal sealed record TextRule(double? Weight, double? Offset, bool TextColor, ResolvedColor? Color, double Tint, string Type, ResolvedColor? Gap)
{
    /// <summary>The colour for text filled with <paramref name="fill"/>; null draws nothing.</summary>
    public ResolvedColor? ColorFor(ResolvedColor? fill)
    {
        var color = TextColor ? fill : Color;
        return color is not null && Tint >= 0 ? color.WithTint(Tint) : color;
    }
}

/// <summary>
/// An underline or strikethrough of a run: centred on the horizontal line from <paramref name="From"/>,
/// <paramref name="Length"/> long, drawn in the rule's <paramref name="Type"/> over its gap colour (see
/// <c>IdmlRenderer.DrawTextRule</c>).
/// </summary>
internal sealed record TextRuleOp(Point From, double Length, double Weight, ResolvedColor Color, string Type, ResolvedColor? Gap) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { From = new Point(From.X + dx, From.Y + dy) };
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
    /// <summary>
    /// A custom-positioned object: InDesign draws it after the text of its frame (02-item-anchored p3: the four boxes
    /// follow the story's text in the content stream), not where its anchor is in the line.
    /// </summary>
    public bool AfterText { get; init; }

    /// <summary>Placed from the frame, a column or the page rather than the line: vertical justification leaves it.</summary>
    public bool FixedInFrame { get; init; }

    public override DrawOp Offset(double dx, double dy) => this with { Placement = Placement.Then(Matrix.Translation(dx, dy)) };
}

/// <summary>
/// Where a story is laid out: one entry per threaded frame, columns in the frame's inner coordinates.
/// <paramref name="BaselineGrid"/> is the grid that grid-aligned paragraphs snap to (null: none, as in table cells).
/// </summary>
internal sealed record FrameArea(string FrameId, IReadOnlyList<Rect> Columns, string VerticalJustification, string FirstBaselineOffset, double MinimumFirstBaselineOffset, string PageName, BaselineGrid? BaselineGrid = null)
{
    /// <summary>What the frame's next/previous page markers, section markers and text variables print.</summary>
    public FrameMarkers Markers { get; init; } = FrameMarkers.None;

    /// <summary>The document page the frame is on (0-based; -1 off the pages or on a master): where "start on next page" goes.</summary>
    public int PageIndex { get; init; } = -1;

    /// <summary>The frame's page has an odd page number (for "start on next odd page" and "next even page").</summary>
    public bool OddPage { get; init; } = true;

    /// <summary>The frame's geometric bounds in its inner coordinates (the Text Frame reference of anchored objects).</summary>
    public Rect Bounds { get; init; }

    /// <summary>The page the frame is on, in the frame's inner coordinates; null off the pages (and in a table cell).</summary>
    public PageArea? Page { get; init; }

    /// <summary>Where text wrap and the frame's own shape let lines go in the columns; null: all of them.</summary>
    public TextArea? TextArea { get; init; }

    /// <summary>
    /// Balance Columns (<c>VerticalBalanceColumns</c>): when the story ends in this frame, its columns end at about the
    /// same height.
    /// </summary>
    public bool BalanceColumns { get; init; }

    /// <summary>
    /// The frame's own footnote spacing (<c>FootnotesEnableOverrides</c>: <c>FootnotesMinimumSpacing</c>,
    /// <c>FootnotesSpaceBetween</c>); null when it uses the document's footnote options.
    /// </summary>
    public (double SpaceBefore, double SpaceBetween)? FootnoteSpacing { get; init; }

    /// <summary>The number a footnote's text starts with (<see cref="FootnoteNumberItem"/>), when this area holds one.</summary>
    public string FootnoteNumber { get; init; } = string.Empty;
}

/// <summary>
/// A page in a frame's inner coordinates: its edges, its margins and whether it is the left page of a facing spread
/// (where anchored objects positioned relative to the spine mirror).
/// </summary>
internal sealed record PageArea(Rect Bounds, Rect Margins, bool LeftPage);

/// <summary>
/// A baseline grid in a frame's inner coordinates: lines at <see cref="Start"/>, then every <see cref="Increment"/>
/// below it (no lines above the start, as InDesign draws it). <see cref="Increment"/> is positive.
/// </summary>
internal readonly record struct BaselineGrid(double Start, double Increment)
{
    /// <summary>
    /// The first grid line at or below <paramref name="baseline"/>: InDesign moves a grid-aligned line down to the
    /// next grid line, never up (06-frame-baseline-grid: 12 pt leading on a 14 pt grid gives lines 14 pt apart).
    /// </summary>
    public double Snap(double baseline) =>
        baseline <= Start
            ? Start
            // A baseline within 0.01 pt of a line is on it (rounding in the inputs must not push it a line down).
            : Start + (Math.Ceiling((baseline - Start - 0.01) / Increment) * Increment);
}

/// <summary>
/// The page-dependent text of a frame: the page names its next and previous page markers print (null: the current
/// page), its section marker, and the text of its variables (<see cref="Variable"/> returns null for a variable that
/// prints its stored <c>ResultText</c>).
/// </summary>
internal sealed record FrameMarkers(string? NextPage, string? PreviousPage, string SectionMarker, Func<XElement, string?>? Variable)
{
    public static readonly FrameMarkers None = new(null, null, string.Empty, null);
}

/// <summary>
/// A laid-out line (for tests and reports). <see cref="Text"/> includes a list marker at its start;
/// <see cref="MarkerLength"/> is how many of its characters that marker takes.
/// </summary>
internal sealed record LineInfo(int FrameIndex, int Column, double Baseline, double Left, double Width, string Text)
{
    public int MarkerLength { get; init; }

    /// <summary>The index of the line's paragraph in <see cref="StoryLayout.Paragraphs"/>.</summary>
    public int Paragraph { get; init; }

    /// <summary>Where the line's text starts, once aligned (<see cref="Width"/> long from there).</summary>
    public double TextLeft { get; init; }

    /// <summary>The left edge of the column the line was set in (the columns spanned, or a split paragraph's sub-column).</summary>
    public double ColumnLeft { get; init; }

    /// <summary>The width of the column the line was set in.</summary>
    public double ColumnWidth { get; init; }

    /// <summary>How far the line's glyphs reach above the baseline (the first-baseline Ascent).</summary>
    public double Ascent { get; init; }

    /// <summary>How far the line's glyphs reach below the baseline.</summary>
    public double Descent { get; init; }

    /// <summary>The line's leading.</summary>
    public double Leading { get; init; }
}

/// <summary>The result of composing a story into its frames.</summary>
internal sealed class StoryLayout
{
    public Dictionary<string, List<DrawOp>> OpsByFrame { get; } = new(StringComparer.Ordinal);

    public List<LineInfo> Lines { get; } = [];

    /// <summary>The paragraphs composed, in story order.</summary>
    public IReadOnlyList<ParagraphModel> Paragraphs { get; set; } = [];

    /// <summary>For each of <see cref="Paragraphs"/>, the index of the frame its first line went in; -1 when it did not fit.</summary>
    public List<int> ParagraphFrames { get; } = [];

    /// <summary>Text did not fit in the frames (InDesign would show the red overset marker).</summary>
    public bool Overset { get; set; }

    public int OversetCharacters { get; set; }

    /// <summary>Descent of the last placed line (how far its glyphs hang below the baseline).</summary>
    public double LastDescent { get; set; }

    /// <summary>
    /// For each column (frame index, column index) holding footnotes, how far below its last line of text the column
    /// must reach for them: the space before the footnotes, their rule and their lines.
    /// </summary>
    public Dictionary<(int Frame, int Column), double> FootnoteDepths { get; } = [];

    /// <summary>
    /// Where the column of <paramref name="line"/> must end to hold it: its baseline, or below it the footnotes of its
    /// column when it is the column's last line (<see cref="FootnoteDepths"/>, counted from any line of the column).
    /// </summary>
    public double FootnoteBottom(LineInfo line) =>
        line.Baseline + (FootnoteDepths.TryGetValue((line.FrameIndex, line.Column), out var depth) ? depth : 0);
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
