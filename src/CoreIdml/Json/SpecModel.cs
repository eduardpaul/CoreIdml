namespace CoreIdml.Json;

// Declarative JSON model. It is the contract of the native (C / Dart) API, so it favours flat, forgiving
// shapes: every field optional, names camelCase, lengths in DocumentSpec.Unit (font sizes always in points).

/// <summary>A whole document (or additions to one) described as data.</summary>
public sealed class DocumentSpec
{
    /// <summary>Unit for geometry and margins: pt (default), mm, cm or in. Font sizes and leading are always pt.</summary>
    public string? Unit { get; set; }

    public PageSetupSpec? Page { get; set; }

    public List<ColorSpec>? Colors { get; set; }

    /// <summary>Gradient swatches, usable as fills, strokes and text colours like colours.</summary>
    public List<GradientSpec>? Gradients { get; set; }

    /// <summary>Object styles: the look of frames and shapes, applied with <see cref="ItemSpec.ObjectStyle"/>.</summary>
    public List<ObjectStyleSpec>? ObjectStyles { get; set; }

    public List<StyleSpec>? ParagraphStyles { get; set; }

    public List<StyleSpec>? CharacterStyles { get; set; }

    public List<PageSpec>? Pages { get; set; }
}

public sealed class PageSetupSpec
{
    /// <summary>Named size: A3, A4, A5, Letter, Legal. Ignored when Width and Height are given.</summary>
    public string? Size { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public bool? Landscape { get; set; }

    public SpacingSpec? Margins { get; set; }

    public bool? FacingPages { get; set; }

    public int? Columns { get; set; }

    public double? ColumnGutter { get; set; }

    public double? Bleed { get; set; }

    /// <summary>The document's baseline grid, for paragraphs with <see cref="TextFormatSpec.AlignToBaselineGrid"/>.</summary>
    public BaselineGridSpec? BaselineGrid { get; set; }
}

public sealed class BaselineGridSpec
{
    /// <summary>Where the first grid line is, in DocumentSpec.Unit from the top of the page (or the top margin). Default 36 pt.</summary>
    public double? Start { get; set; }

    /// <summary>Points between grid lines: the body text's leading.</summary>
    public double Increment { get; set; }

    /// <summary>page (default) or topMargin: what <see cref="Start"/> counts from.</summary>
    public string? RelativeTo { get; set; }
}

public sealed class SpacingSpec
{
    public double? All { get; set; }

    public double? Top { get; set; }

    public double? Right { get; set; }

    public double? Bottom { get; set; }

    public double? Left { get; set; }
}

public sealed class ColorSpec
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Four percentages: cyan, magenta, yellow, black.</summary>
    public double[]? Cmyk { get; set; }

    /// <summary>Three values 0-255.</summary>
    public double[]? Rgb { get; set; }

    /// <summary>"#RRGGBB".</summary>
    public string? Hex { get; set; }
}

public sealed class GradientSpec
{
    public string Name { get; set; } = string.Empty;

    /// <summary>linear (default) or radial.</summary>
    public string? Type { get; set; }

    /// <summary>At least two stops, in order.</summary>
    public List<GradientStopSpec>? Stops { get; set; }
}

public sealed class GradientStopSpec
{
    /// <summary>A colour swatch.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>Percent along the gradient, 0 to 100; default spaced evenly.</summary>
    public double? Location { get; set; }

    /// <summary>Where this stop and the one before mix half and half, percent of the span between them (13 to 87, default 50).</summary>
    public double? Midpoint { get; set; }
}

/// <summary>An object style: fill, stroke, corners, opacity and text frame options, in the units of items.</summary>
public sealed class ObjectStyleSpec
{
    public string Name { get; set; } = string.Empty;

    public string? BasedOn { get; set; }

    public string? Fill { get; set; }

    public double? FillTint { get; set; }

    public string? Stroke { get; set; }

    public double? StrokeWeight { get; set; }

    public double? StrokeTint { get; set; }

    /// <summary>solid, dashed or dotted.</summary>
    public string? StrokeType { get; set; }

    public double? CornerRadius { get; set; }

    public double? Opacity { get; set; }

    public int? Columns { get; set; }

    public double? ColumnGutter { get; set; }

    public SpacingSpec? Inset { get; set; }

    /// <summary>top, center, bottom, justify.</summary>
    public string? VerticalAlign { get; set; }
}

/// <summary>Text attributes shared by styles, paragraphs and runs.</summary>
public class TextFormatSpec
{
    public string? Font { get; set; }

    public string? FontStyle { get; set; }

    public double? Size { get; set; }

    public double? Leading { get; set; }

    /// <summary>Swatch name ("Black", or a colour declared in Colors).</summary>
    public string? Color { get; set; }

    public double? Tint { get; set; }

    /// <summary>left, center, right, justify (= left justified), justifyAll.</summary>
    public string? Align { get; set; }

    public double? SpaceBefore { get; set; }

    public double? SpaceAfter { get; set; }

    public double? FirstLineIndent { get; set; }

    public double? LeftIndent { get; set; }

    public double? RightIndent { get; set; }

    public bool? Underline { get; set; }

    public bool? StrikeThrough { get; set; }

    public double? Tracking { get; set; }

    /// <summary>normal, allCaps, smallCaps.</summary>
    public string? Caps { get; set; }

    /// <summary>normal, superscript, subscript.</summary>
    public string? Position { get; set; }

    /// <summary>Text stroke (outlined text): a swatch; "None" removes it.</summary>
    public string? Stroke { get; set; }

    /// <summary>Text stroke weight in points.</summary>
    public double? StrokeWeight { get; set; }

    public double? StrokeTint { get; set; }

    /// <summary>Skew (false italic) in degrees, -85 to 85; positive leans right.</summary>
    public double? Skew { get; set; }

    /// <summary>Underline weight in points (with underline: true).</summary>
    public double? UnderlineWeight { get; set; }

    /// <summary>Underline offset in points below the baseline.</summary>
    public double? UnderlineOffset { get; set; }

    /// <summary>Underline swatch (default the text colour).</summary>
    public string? UnderlineColor { get; set; }

    public double? UnderlineTint { get; set; }

    /// <summary>solid, dashed, dotted, wavy or thickThin.</summary>
    public string? UnderlineType { get; set; }

    /// <summary>Strikethrough weight in points (with strikeThrough: true).</summary>
    public double? StrikeThroughWeight { get; set; }

    /// <summary>Strikethrough offset in points above the baseline.</summary>
    public double? StrikeThroughOffset { get; set; }

    /// <summary>Strikethrough swatch (default the text colour).</summary>
    public string? StrikeThroughColor { get; set; }

    public double? StrikeThroughTint { get; set; }

    /// <summary>solid, dashed, dotted, wavy or thickThin.</summary>
    public string? StrikeThroughType { get; set; }

    // ---- bullets and numbering (paragraphs and paragraph styles) ----

    /// <summary>bullet, numbered or none. InDesign draws the bullet or number; it is not part of the text.</summary>
    public string? List { get; set; }

    /// <summary>The bullet character (default •).</summary>
    public string? Bullet { get; set; }

    /// <summary>Text after the bullet (default a tab). InDesign metacharacters work: ^t tab, ^m em space, ^&gt; en space.</summary>
    public string? BulletAfter { get; set; }

    /// <summary>1 (default), 01, 001, 0001, a, A, i, I, or InDesign's own names ("1, 2, 3, 4...").</summary>
    public string? NumberFormat { get; set; }

    /// <summary>The number's text: ^# is the number, ^1 to ^9 that level's number, a tab or ^t a tab. Default "^#.\t".</summary>
    public string? NumberText { get; set; }

    /// <summary>1 to 9: a level restarts after any paragraph of a higher level.</summary>
    public int? ListLevel { get; set; }

    /// <summary>On a paragraph: restart the numbering at this number. On a style: the number lists start at.</summary>
    public int? StartAt { get; set; }

    /// <summary>Tab stops (paragraphs and paragraph styles), replacing the style's; [] clears them.</summary>
    public List<TabSpec>? Tabs { get; set; }

    /// <summary>Keep the paragraph's last line with the next paragraph's first N lines (0 to 5; headings: 1 or 2).</summary>
    public int? KeepWithNext { get; set; }

    /// <summary>True: never break the paragraph across columns or frames.</summary>
    public bool? KeepTogether { get; set; }

    /// <summary>Lines the paragraph keeps before a column break (orphans; turns Keep Lines Together on).</summary>
    public int? KeepFirstLines { get; set; }

    /// <summary>Lines the paragraph keeps after a column break (widows; turns Keep Lines Together on).</summary>
    public int? KeepLastLines { get; set; }

    /// <summary>anywhere (default), nextColumn, nextFrame, nextPage, nextOddPage or nextEvenPage.</summary>
    public string? StartParagraph { get; set; }

    // ---- drop caps, nested and GREP styles, grid and columns (paragraphs and paragraph styles) ----

    /// <summary>Lines the drop cap drops through (0 removes it).</summary>
    public int? DropCapLines { get; set; }

    /// <summary>Characters in the drop cap (default 1).</summary>
    public int? DropCapCharacters { get; set; }

    /// <summary>Character style of the drop cap (a nested style "through 1 drop cap", as InDesign stores it).</summary>
    public string? DropCapStyle { get; set; }

    /// <summary>Character styles applied in sequence from the paragraph's start; [] clears them.</summary>
    public List<NestedStyleSpec>? NestedStyles { get; set; }

    /// <summary>Character styles applied to the text a GREP expression matches; [] clears them.</summary>
    public List<GrepStyleSpec>? GrepStyles { get; set; }

    // ---- paragraph rules, borders and shading (paragraphs and paragraph styles; points) ----

    /// <summary>A rule above the paragraph (false turns a style's off). The ruleAbove... fields turn it on too.</summary>
    public bool? RuleAbove { get; set; }

    public string? RuleAboveColor { get; set; }

    public double? RuleAboveWeight { get; set; }

    /// <summary>Points up from the first line's baseline.</summary>
    public double? RuleAboveOffset { get; set; }

    public double? RuleAboveTint { get; set; }

    /// <summary>solid, dashed, dotted, wavy or thickThin.</summary>
    public string? RuleAboveType { get; set; }

    /// <summary>column (default) or text: as wide as the line's text.</summary>
    public string? RuleAboveWidth { get; set; }

    public double? RuleAboveLeftIndent { get; set; }

    public double? RuleAboveRightIndent { get; set; }

    /// <summary>A rule below the paragraph (false turns a style's off). The ruleBelow... fields turn it on too.</summary>
    public bool? RuleBelow { get; set; }

    public string? RuleBelowColor { get; set; }

    public double? RuleBelowWeight { get; set; }

    /// <summary>Points down from the last line's baseline.</summary>
    public double? RuleBelowOffset { get; set; }

    public double? RuleBelowTint { get; set; }

    /// <summary>solid, dashed, dotted, wavy or thickThin.</summary>
    public string? RuleBelowType { get; set; }

    /// <summary>column (default) or text.</summary>
    public string? RuleBelowWidth { get; set; }

    public double? RuleBelowLeftIndent { get; set; }

    public double? RuleBelowRightIndent { get; set; }

    /// <summary>A border round the paragraph: a swatch; "None" removes it.</summary>
    public string? Border { get; set; }

    /// <summary>Border line weight in points, every side.</summary>
    public double? BorderWeight { get; set; }

    public double? BorderTint { get; set; }

    /// <summary>solid, dashed, dotted, wavy or thickThin.</summary>
    public string? BorderType { get; set; }

    /// <summary>Points between the text and the border on each side.</summary>
    public SpacingSpec? BorderOffset { get; set; }

    public double? BorderCornerRadius { get; set; }

    /// <summary>Shading behind the paragraph: a swatch; "None" removes it.</summary>
    public string? Shading { get; set; }

    public double? ShadingTint { get; set; }

    /// <summary>Points the shading reaches beyond the text on each side.</summary>
    public SpacingSpec? ShadingOffset { get; set; }

    public double? ShadingCornerRadius { get; set; }

    /// <summary>Put the baselines on the document's baseline grid.</summary>
    public bool? AlignToBaselineGrid { get; set; }

    /// <summary>With alignToBaselineGrid: only the first line snaps to the grid.</summary>
    public bool? GridFirstLineOnly { get; set; }

    /// <summary>Span this many of the frame's columns: 0 all of them, 1 back to a single column.</summary>
    public int? SpanColumns { get; set; }

    /// <summary>Split the paragraph's column into this many sub-columns (1: a single column).</summary>
    public int? SplitColumns { get; set; }

    /// <summary>Points between split sub-columns (default 6).</summary>
    public double? SplitGutter { get; set; }
}

/// <summary>A nested style: <see cref="Style"/> through (or up to) the <see cref="Count"/>th delimiter.</summary>
public sealed class NestedStyleSpec
{
    /// <summary>Character style.</summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>The style ends after this delimiter: words, characters, sentences, letters, digits, tabs, lineBreak, endNestedStyle or one character.</summary>
    public string? Through { get; set; }

    /// <summary>The style ends before this delimiter (same values as <see cref="Through"/>).</summary>
    public string? UpTo { get; set; }

    /// <summary>How many delimiters (default 1).</summary>
    public int? Count { get; set; }
}

/// <summary>A GREP style: <see cref="Style"/> on every match of <see cref="Expression"/>.</summary>
public sealed class GrepStyleSpec
{
    /// <summary>Character style.</summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>InDesign GREP, e.g. "\\d+" or "\\$[\\d.,]+".</summary>
    public string Expression { get; set; } = string.Empty;
}

/// <summary>A tab stop.</summary>
public sealed class TabSpec
{
    /// <summary>Points from the frame's left edge (inside its inset).</summary>
    public double Position { get; set; }

    /// <summary>left (default), center, right, or decimal (on <see cref="AlignOn"/>).</summary>
    public string? Align { get; set; }

    /// <summary>Repeated across the tab's space: ".", "-", "_", "·" or any short text.</summary>
    public string? Leader { get; set; }

    /// <summary>For decimal: the character to align on (default ".").</summary>
    public string? AlignOn { get; set; }
}

public sealed class StyleSpec : TextFormatSpec
{
    public string Name { get; set; } = string.Empty;

    public string? BasedOn { get; set; }
}

public sealed class PageSpec
{
    /// <summary>Draw on this existing page (0-based) instead of the next one.</summary>
    public int? Index { get; set; }

    public SpacingSpec? Margins { get; set; }

    public List<ItemSpec>? Items { get; set; }
}

/// <summary>One page item. <see cref="Type"/> decides which fields apply.</summary>
public sealed class ItemSpec
{
    /// <summary>text, rectangle, ellipse, line, image, table.</summary>
    public string Type { get; set; } = "text";

    /// <summary>Name in the Layers panel (also the key used by template filling).</summary>
    public string? Name { get; set; }

    public string? Label { get; set; }

    /// <summary>XML tag to bind the item to.</summary>
    public string? Tag { get; set; }

    public string? Layer { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>Line end point (type line); X/Y is the start.</summary>
    public double? X2 { get; set; }

    public double? Y2 { get; set; }

    public string? Fill { get; set; }

    public double? FillTint { get; set; }

    public string? Stroke { get; set; }

    public double? StrokeWeight { get; set; }

    /// <summary>Stroke tint in percent (100 = solid).</summary>
    public double? StrokeTint { get; set; }

    /// <summary>solid, dashed or dotted.</summary>
    public string? StrokeType { get; set; }

    /// <summary>
    /// A custom dash pattern in points (like strokeWeight): [dash, gap], [dash, gap, dash, gap] or six lengths; one
    /// length is a dash and a gap of that length. InDesign's "Dashed" stroke type with these lengths; wins over
    /// strokeType. [] is a solid stroke.
    /// </summary>
    public double[]? StrokeDash { get; set; }

    /// <summary>Arrowhead at a line's start (x, y): none, simple, simpleWide, triangle, triangleWide, barbed, curved, circle, circleSolid, square, squareSolid or bar.</summary>
    public string? StartArrow { get; set; }

    /// <summary>Arrowhead at a line's end (x2, y2), with the values of <see cref="StartArrow"/>.</summary>
    public string? EndArrow { get; set; }

    /// <summary>Ends of lines: butt (default), round or projecting.</summary>
    public string? EndCap { get; set; }

    /// <summary>Opacity of the whole item in percent (100 = opaque).</summary>
    public double? Opacity { get; set; }

    public double? CornerRadius { get; set; }

    public double? Rotation { get; set; }

    /// <summary>Object style; the item's own fields override it.</summary>
    public string? ObjectStyle { get; set; }

    /// <summary>Angle of a gradient fill or stroke, degrees counter-clockwise (0: left to right).</summary>
    public double? GradientAngle { get; set; }

    /// <summary>Text wrap around the item: none, boundingBox, objectShape, jumpObject or nextColumn.</summary>
    public string? Wrap { get; set; }

    /// <summary>Distance kept between the item and the wrapped text (an object-shape wrap uses top all round).</summary>
    public SpacingSpec? WrapOffset { get; set; }

    /// <summary>both (default), left, right, towardsSpine, awayFromSpine or largest: where the text may go.</summary>
    public string? WrapSide { get; set; }

    /// <summary>Keep the text inside the item instead.</summary>
    public bool? WrapInverse { get; set; }

    // ---- text ----

    /// <summary>Plain text; "\n" separates paragraphs.</summary>
    public string? Text { get; set; }

    /// <summary>Paragraph style for <see cref="Text"/>.</summary>
    public string? Style { get; set; }

    public List<ParagraphSpec>? Paragraphs { get; set; }

    public int? Columns { get; set; }

    public double? ColumnGutter { get; set; }

    public SpacingSpec? Inset { get; set; }

    /// <summary>top, center, bottom, justify.</summary>
    public string? VerticalAlign { get; set; }

    /// <summary>Name of a text frame whose overflow continues in this one.</summary>
    public string? ContinueFrom { get; set; }

    /// <summary>off, height, width, heightAndWidth or proportional: the frame grows or shrinks to its text.</summary>
    public string? AutoSize { get; set; }

    /// <summary>The point that stays put: topLeft, top, topRight, left, center, right, bottomLeft, bottom, bottomRight.</summary>
    public string? AutoSizeFrom { get; set; }

    /// <summary>The height an auto-sizing frame never shrinks below.</summary>
    public double? AutoSizeMinHeight { get; set; }

    /// <summary>Columns end at the same depth when the story ends in the frame.</summary>
    public bool? BalanceColumns { get; set; }

    /// <summary>ascent (default), capHeight, leading, xHeight, emBox or fixed: where the first baseline goes.</summary>
    public string? FirstBaseline { get; set; }

    /// <summary>The least distance from the frame's top (inside the inset) to the first baseline.</summary>
    public double? FirstBaselineMin { get; set; }

    /// <summary>The frame's text ignores other items' text wrap.</summary>
    public bool? IgnoreWrap { get; set; }

    // ---- image ----

    /// <summary>Local path of a JPEG or PNG file.</summary>
    public string? Path { get; set; }

    /// <summary>fill (default), fit, stretch, center, original.</summary>
    public string? Fit { get; set; }

    // ---- table ----

    public List<List<string>>? Rows { get; set; }

    public double[]? ColumnWidths { get; set; }

    public int? HeaderRows { get; set; }

    public double? RowHeight { get; set; }

    public string? CellStyle { get; set; }

    public string? HeaderStyle { get; set; }

    public string? HeaderFill { get; set; }

    public string? AlternateFill { get; set; }

    public double? CellInset { get; set; }
}

public sealed class ParagraphSpec : TextFormatSpec
{
    public string? Text { get; set; }

    public string? Style { get; set; }

    public List<RunSpec>? Runs { get; set; }
}

public sealed class RunSpec : TextFormatSpec
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Character style name.</summary>
    public string? Style { get; set; }
}

/// <summary>Data for filling a template (see <see cref="TemplateData"/>).</summary>
public sealed class FillSpec
{
    public Dictionary<string, string>? Text { get; set; }

    public Dictionary<string, string>? Images { get; set; }

    public string? Fit { get; set; }

    public string? PlaceholderFormat { get; set; }
}

public sealed class FillReport
{
    public Dictionary<string, int> Filled { get; set; } = [];

    public List<string> Missing { get; set; } = [];
}


public sealed class ValidationReport
{
    public bool IsValid { get; set; }

    public List<IssueReport> Issues { get; set; } = [];
}

public sealed class IssueReport
{
    public string Severity { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Part { get; set; }
}
