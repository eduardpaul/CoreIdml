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

    /// <summary>Ends of lines: butt (default), round or projecting.</summary>
    public string? EndCap { get; set; }

    /// <summary>Opacity of the whole item in percent (100 = opaque).</summary>
    public double? Opacity { get; set; }

    public double? CornerRadius { get; set; }

    public double? Rotation { get; set; }

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
