namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Above-line and custom-positioned anchored objects (<c>AnchoredObjectSetting@AnchoredPosition</c> <c>AboveLine</c>
/// and <c>Anchored</c>). Positions are those of the object's visible bounds (its stroke included), as for inline
/// objects; measured against 02-item-anchored p2 to p5 and 12-pdf-accessibility p4.
/// </summary>
internal sealed partial class TextComposer
{
    /// <summary>
    /// How far above its baseline the line below an above-line object starts, in em of the line's largest size: the
    /// object's bottom sits there (with no Space After). Measured with Liberation Serif: 8.274 pt at 10 pt on 12.5 pt
    /// leading (02-item-anchored p2) and 8.688 pt at 10.5 pt on 14 pt leading (12-pdf-accessibility p4); it follows
    /// the size, not the leading. No metric of that font gives 0.8274 (ascent 0.693, cap height 0.655, hhea ascender
    /// 0.891), so other fonts are not measured.
    /// </summary>
    private const double LineTopBelowObject = 0.8274;

    /// <summary>
    /// The room an above-line object takes in the text: Space Before (<c>AnchorSpaceAbove</c>, at least minus the
    /// object's height), its height and Space After (<c>AnchorYoffset</c>; from memory, 0 in every sample). The line
    /// holding the anchor keeps its leading below that, also at the top of a column: 02-item-anchored p2 puts the
    /// first baseline 6 + 31 + 12.5 pt below the frame top and the next paragraph's 12 + 31 + 12.5 pt below the
    /// previous baseline, whatever the first-baseline offset.
    /// </summary>
    private static double AboveLineRoom(InlineObjectItem item)
    {
        var height = item.Bounds.Height;
        return Math.Max(-height, item.Anchored!.SpaceAbove) + height + item.Anchored.YOffset;
    }

    /// <summary>
    /// Places the line's above-line objects in the room <see cref="Flow.Place"/> made above its baseline: the last
    /// one's bottom <see cref="LineTopBelowObject"/> above the baseline, the others stacked above it, each aligned
    /// left, centre or right between the paragraph's indents (02-item-anchored p2: 90 pt wide boxes at the left
    /// edge, centred and at the right edge of a 300 pt column), or as the paragraph is aligned (<c>TextAlign</c>).
    /// Relative to the spine, left and right swap on a left page. Drawn before the line's text, as InDesign writes them.
    /// </summary>
    private static void EmitAboveLine(List<Shaped> line, Flow flow, ParagraphFormatting format, double baseline, LineMetrics metrics)
    {
        var objects = line.Select(glyph => glyph.Object).OfType<InlineObjectItem>().Where(item => item.Anchored is { AboveLine: true }).ToList();
        if (objects.Count == 0)
        {
            return;
        }
        var column = flow.Column;
        var left = column.X + format.LeftIndent;
        var right = column.Right - format.RightIndent;
        var tops = new double[objects.Count];
        var bottom = baseline - (LineTopBelowObject * metrics.MaxSize);
        for (var i = objects.Count - 1; i >= 0; i--)
        {
            var height = objects[i].Bounds.Height;
            bottom -= objects[i].Anchored!.YOffset;
            tops[i] = bottom - height;
            bottom = tops[i] - Math.Max(-height, objects[i].Anchored!.SpaceAbove);
        }
        for (var i = 0; i < objects.Count; i++)
        {
            var (item, settings, bounds) = (objects[i], objects[i].Anchored!, objects[i].Bounds);
            var alignment = settings.HorizontalAlignment == "TextAlign"
                ? format.Justification switch
                {
                    "CenterAlign" or "CenterJustified" => "CenterAlign",
                    "RightAlign" or "RightJustified" or "AwayFromBindingSide" => "RightAlign",
                    _ => "LeftAlign",
                }
                : settings.HorizontalAlignment;
            var x = Side(alignment, Mirrored(settings, flow.Frame)) switch
            {
                0 => (left + right - bounds.Width) / 2,
                > 0 => right - bounds.Width,
                _ => left,
            };
            flow.Add(new PageItemOp(item.Element, Matrix.Translation(x - bounds.Left, tops[i] - bounds.Top)));
        }
    }

    /// <summary>
    /// A custom-positioned object: its <c>AnchorPoint</c> goes on the reference point, moved by the offsets. The
    /// reference is horizontally the anchor marker, or the left edge, centre or right edge of the column, the text
    /// frame, the page margins or the page; vertically a point of the anchor's line (the baseline, ...) or the top,
    /// centre or bottom of those areas. A positive Y offset moves the object down; a positive X offset moves it right,
    /// except from a left edge, where it moves it left, out of the area (02-item-anchored p3: the right edges of four
    /// boxes 10 pt right of the column, 4 pt inside the frame, the margin and the page, their bottoms 8 pt above the
    /// baseline; p4: a box aligned to the column's left edge with X offset -90 ends 90 pt inside it). Kept within the
    /// column's top and bottom when <c>PinPosition</c> is on and the reference is the line (p4: moved down to the
    /// column top). Relative to the spine, left and right swap on a left page. It takes no room in the text (text wrap
    /// is not done) and is drawn after the frame's text.
    /// </summary>
    private static PageItemOp PlaceCustom(InlineObjectItem item, AnchorSettings settings, Shaped anchor, Flow flow, double x, double baseline, LineMetrics metrics)
    {
        var frame = flow.Frame;
        var column = flow.Column;
        var mirrored = Mirrored(settings, frame);
        Rect? Area(string reference) => reference switch
        {
            "ColumnEdge" => column,
            "TextFrame" => frame.Bounds,
            "PageMargins" => frame.Page?.Margins ?? frame.Bounds,
            "PageEdge" => frame.Page?.Bounds ?? frame.Bounds,
            _ => null,
        };

        var side = Side(settings.HorizontalAlignment, mirrored);
        var xOffset = mirrored ? -settings.XOffset : settings.XOffset;
        var referenceX = Area(settings.HorizontalReferencePoint) is { } horizontal
            ? Edge(horizontal.Left, horizontal.Right, side) + (side < 0 ? -xOffset : xOffset)
            : x + xOffset;

        // Line references other than the baseline from memory (no sample uses them).
        var font = anchor.Font;
        double? lineY = settings.VerticalReferencePoint switch
        {
            "LineXheight" => baseline - (font.XHeight * anchor.Size),
            "CapHeight" => baseline - (font.CapHeight * anchor.Size),
            "LineAscent" => baseline - (font.Ascent * anchor.Size),
            "TopOfLeading" => baseline - metrics.Leading,
            "ColumnEdge" or "TextFrame" or "PageMargins" or "PageEdge" => null,
            _ => baseline,
        };
        var vertical = Area(settings.VerticalReferencePoint) ?? column;
        var referenceY = settings.YOffset + (lineY ?? Edge(vertical.Top, vertical.Bottom, settings.VerticalAlignment switch
        {
            "CenterAlign" => 0,
            "BottomAlign" => 1,
            _ => -1,
        }));

        var bounds = item.Bounds;
        var point = settings.AnchorPoint;
        var objectSide = point.Contains("Left", StringComparison.Ordinal) ? -1 : point.Contains("Right", StringComparison.Ordinal) ? 1 : 0;
        var left = referenceX - ((mirrored ? -objectSide : objectSide) + 1) / 2.0 * bounds.Width;
        var top = referenceY - (point.StartsWith("Top", StringComparison.Ordinal) ? 0 : point.StartsWith("Bottom", StringComparison.Ordinal) ? 1 : 0.5) * bounds.Height;
        if (lineY is not null && settings.Pinned)
        {
            top = Math.Max(column.Top, Math.Min(top, column.Bottom - bounds.Height));
        }
        return new PageItemOp(item.Element, Matrix.Translation(left - bounds.Left, top - bounds.Top)) { AfterText = true, FixedInFrame = lineY is null };
    }

    /// <summary>Relative to Spine (<c>SpineRelative</c>) on a left page: left and right swap.</summary>
    private static bool Mirrored(AnchorSettings settings, FrameArea frame) => settings.SpineRelative && frame.Page?.LeftPage == true;

    /// <summary>-1 for a left alignment, 0 centred, 1 right, swapped when <paramref name="mirrored"/>.</summary>
    private static int Side(string alignment, bool mirrored)
    {
        var side = alignment switch
        {
            "CenterAlign" => 0,
            "RightAlign" => 1,
            _ => -1,
        };
        return mirrored ? -side : side;
    }

    private static double Edge(double low, double high, int side) => side switch
    {
        < 0 => low,
        0 => (low + high) / 2,
        _ => high,
    };
}
