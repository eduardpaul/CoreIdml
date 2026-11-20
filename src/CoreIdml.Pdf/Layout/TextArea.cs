using System.Xml.Linq;
using CoreIdml.Dom;

namespace CoreIdml.Pdf.Layout;

/// <summary>What a wrap region does to the lines that meet it.</summary>
internal enum WrapMode
{
    /// <summary>Text goes around the region (Wrap around Bounding Box, Wrap around Object Shape).</summary>
    Around,

    /// <summary>A line that meets the region goes below it (Jump Object): nothing beside it.</summary>
    Jump,

    /// <summary>A line that meets the region goes to the next column (Jump to Next Column).</summary>
    NextColumn,

    /// <summary>Text stays inside the region: an inverted wrap, or a text frame's own shape.</summary>
    Inside,
}

/// <summary>Which side of a region text goes on (<c>TextWrapSide</c>; the spine sides are resolved to left or right).</summary>
internal enum WrapSide
{
    Both,
    Left,
    Right,
    Largest,
}

/// <summary>Whether a line's band meets text wrap, and how.</summary>
internal enum BandWrap
{
    /// <summary>Nothing limits the column there.</summary>
    Clear,

    /// <summary>The free stretches are listed.</summary>
    Wrapped,

    /// <summary>A Jump to Next Column region: the line goes on in the next column.</summary>
    NextColumn,
}

/// <summary>A path flattened to straight edges, in a text frame's inner coordinates.</summary>
internal sealed record Outline(Point[] Points, bool Closed);

/// <summary>
/// An item's text wrap (or a text frame's own shape), in the inner coordinates of the frame whose text it limits.
/// A region without <see cref="Outlines"/> is the box <see cref="Box"/>, grown by <see cref="Offset"/> on each side
/// (shrunk for <see cref="WrapMode.Inside"/>); a region with outlines is their shape grown (or shrunk) by
/// <see cref="Offset"/>.Top in every direction, round at the corners, the way a stroke would be.
/// </summary>
internal sealed record WrapRegion(WrapMode Mode, Rect Box, Spacing Offset, WrapSide Side = WrapSide.Both, IReadOnlyList<Outline>? Outlines = null)
{
    /// <summary>The vertical extent in which the region affects lines.</summary>
    public (double Top, double Bottom) Extent => Mode == WrapMode.Inside
        ? (Box.Top + Offset.Top, Box.Bottom - (Outlines is null ? Offset.Bottom : Offset.Top))
        : (Box.Top - Offset.Top, Box.Bottom + (Outlines is null ? Offset.Bottom : Offset.Top));

    /// <summary>The horizontal extent in which the region affects lines.</summary>
    public (double Left, double Right) Across => Mode == WrapMode.Inside
        ? (Box.Left, Box.Right)
        : (Box.Left - (Outlines is null ? Offset.Left : Offset.Top), Box.Right + (Outlines is null ? Offset.Right : Offset.Top));

    /// <summary>
    /// An item's <c>TextWrapPreference</c> (<paramref name="objectChain"/>: the item, then its object styles), or its
    /// object style's when the item has none and the style controls text wrap (<c>EnableTextWrapAndOthers</c>).
    /// </summary>
    public static XElement? PreferenceOf(IReadOnlyList<XElement> objectChain) =>
        objectChain[0].Element("TextWrapPreference")
        ?? (AttributeResolver.StylesControl(objectChain, "EnableTextWrapAndOthers")
            ? objectChain.Skip(1).Select(style => style.Element("TextWrapPreference")).FirstOrDefault(found => found is not null)
            : null);

    /// <summary>
    /// What a <c>TextWrapPreference</c> asks for: its mode (an inverted wrap keeps text inside), side (towards and away
    /// from the spine resolved by <paramref name="leftPage"/>: the right side of a left page is towards it, from memory)
    /// and offsets, around an empty box; null for no wrap (<c>TextWrapMode="None"</c>).
    /// </summary>
    public static WrapRegion? Read(XElement? preference, bool leftPage)
    {
        var mode = preference?.Attribute("TextWrapMode")?.Value ?? "None";
        if (preference is null || mode == "None")
        {
            return null;
        }
        var offset = preference.Element("Properties")?.Element("TextWrapOffset");
        double Offset(string side) => Math.Max(0, Values.Number(offset?.Attribute(side)?.Value, 0));
        var side = preference.Attribute("TextWrapSide")?.Value switch
        {
            "LeftSide" => WrapSide.Left,
            "RightSide" => WrapSide.Right,
            "LargestArea" => WrapSide.Largest,
            "SideTowardsSpine" => leftPage ? WrapSide.Right : WrapSide.Left,
            "SideAwayFromSpine" => leftPage ? WrapSide.Left : WrapSide.Right,
            _ => WrapSide.Both,
        };
        var wrapMode = preference.Attribute("Inverse")?.Value == "true" ? WrapMode.Inside : mode switch
        {
            "JumpObjectTextWrap" => WrapMode.Jump,
            "NextColumnTextWrap" => WrapMode.NextColumn,
            _ => WrapMode.Around,
        };
        return new WrapRegion(wrapMode, default, new Spacing(Offset("Top"), Offset("Right"), Offset("Bottom"), Offset("Left")), side);
    }
}

/// <summary>
/// Where the lines of a text frame may go when that is not all of its columns: the regions of the items that wrap
/// text around them, and the frame's own shape when it is not a rectangle. Measured in 02-item-text-wrap and
/// 06-frame-shapes:
/// <list type="bullet">
/// <item>A line is tested over the band from the baseline before it (the baseline less its leading) down to its own
/// baseline; descenders do not count. A column's first line is tested from the top of the column (the baseline less
/// its first-baseline offset). 02-item-text-wrap p4: the stretch in the star's notch starts at 263, where the notch's
/// edge is at the baseline before (from the line's ascent it would start at 255), and the line above the star's tip
/// ends at 286.8, which the tip would block at the line's descent; p9: the inverted box takes the lines whose bands
/// lie inside it, and leaves the band that crosses its top edge empty.</item>
/// <item>Bounding box wraps are exact: the item's box with its stroke, grown by each side's offset (02-item-text-wrap
/// p2 starts the lines right of the oval's box at 390 + 0.5 + 24 = 414.5; p7, p9). The edges of shapes (object-shape
/// wraps, shaped frames) are truncated to whole points from the column's left edge: p4's star leaves 263.89 free,
/// where the line starts at 263; 06-frame-shapes p1's rounded corners give 345.2, 333.96 and 328.56 where the lines
/// start at 344.5, 333.5 and 328.5 (the column starts at 326.5), the hexagon of p2 152.47 where the line starts at
/// 152.25 (from 53.25).</item>
/// <item>An object-shape wrap is the shape grown by its offset and half its stroke, round at the corners (p4: the
/// star's tip 8.5 pt above it, not mitred); text goes in every gap the shape leaves, also between its points (p4:
/// "tion to" below the star's notch). A shaped frame keeps its text inside its path less the inset and half the
/// stroke.</item>
/// </list>
/// </summary>
internal sealed class TextArea(IReadOnlyList<WrapRegion> regions)
{
    // Lines are not wrapped by a region they only touch.
    private const double Touch = 0.01;

    // A shape no nearer than its offset to a band, or touching the band only at its edge, leaves the band alone
    // (02-item-corners p4: the first line's band starts exactly the inset below the frame's top edge).
    private const double Graze = 1e-6;

    public IReadOnlyList<WrapRegion> Regions => regions;

    /// <summary>
    /// The stretches of <paramref name="column"/> free for a line whose band runs from <paramref name="top"/> to
    /// <paramref name="bottom"/>, left to right in <paramref name="free"/> (cleared first).
    /// </summary>
    public BandWrap Pieces(Rect column, double top, double bottom, List<(double Left, double Right)> free)
    {
        free.Clear();
        free.Add((column.Left, column.Right));
        var wrapped = false;
        List<(double Left, double Right)>? scratch = null;
        foreach (var region in regions)
        {
            var (from, to) = region.Extent;
            var (left, right) = region.Across;
            if (bottom <= from + Touch || top >= to - Touch || right <= column.Left || left >= column.Right)
            {
                continue;
            }
            wrapped = true;
            switch (region.Mode)
            {
                case WrapMode.NextColumn:
                    free.Clear();
                    return BandWrap.NextColumn;
                case WrapMode.Jump:
                    free.Clear();
                    break;
                case WrapMode.Around:
                    scratch ??= [];
                    Around(region, column, top, bottom, free, scratch);
                    break;
                case WrapMode.Inside:
                    scratch ??= [];
                    Inside(region, column, top, bottom, free, scratch);
                    break;
            }
        }
        if (!wrapped)
        {
            return BandWrap.Clear;
        }
        free.RemoveAll(piece => piece.Right - piece.Left <= Touch);
        return BandWrap.Wrapped;
    }

    private static void Around(WrapRegion region, Rect column, double top, double bottom, List<(double Left, double Right)> free, List<(double Left, double Right)> blocked)
    {
        blocked.Clear();
        if (region.Outlines is null)
        {
            blocked.Add((region.Box.Left - region.Offset.Left, region.Box.Right + region.Offset.Right));
        }
        else
        {
            Covered(region.Outlines, region.Offset.Top, top + Graze, bottom - Graze, blocked);
            for (var i = 0; i < blocked.Count; i++)
            {
                blocked[i] = (Truncate(blocked[i].Left, column), Truncate(blocked[i].Right, column));
            }
        }
        if (blocked.Count == 0)
        {
            return;
        }
        var low = blocked.Min(range => range.Left);
        var high = blocked.Max(range => range.Right);
        var side = region.Side;
        if (side == WrapSide.Largest)
        {
            // The wider of the two sides, the left one on a tie (02-item-text-wrap p7: 35.5 pt either side, text left).
            side = low - column.Left >= column.Right - high ? WrapSide.Left : WrapSide.Right;
        }
        switch (side)
        {
            case WrapSide.Left:
                Subtract(free, low, double.PositiveInfinity);
                break;
            case WrapSide.Right:
                Subtract(free, double.NegativeInfinity, high);
                break;
            default:
                foreach (var (left, right) in blocked)
                {
                    Subtract(free, left, right);
                }
                break;
        }
    }

    private static void Inside(WrapRegion region, Rect column, double top, double bottom, List<(double Left, double Right)> free, List<(double Left, double Right)> kept)
    {
        kept.Clear();
        var offset = region.Offset;
        var box = region.Box;
        if (region.Outlines is null)
        {
            // Only a band wholly inside the box keeps text (02-item-text-wrap p9).
            if (top >= box.Top + offset.Top - Touch && bottom <= box.Bottom - offset.Bottom + Touch)
            {
                kept.Add((box.Left + offset.Left, box.Right - offset.Right));
            }
        }
        else
        {
            // Inside the shape at the band's top, less every stretch that comes within the inset of its edges.
            CrossSection(region.Outlines, top + Graze, kept);
            var edges = new List<(double Left, double Right)>();
            Covered(region.Outlines, offset.Top, top + Graze, bottom - Graze, edges, crossSection: false);
            foreach (var (left, right) in edges)
            {
                Subtract(kept, left, right);
            }
            for (var i = 0; i < kept.Count; i++)
            {
                kept[i] = (Truncate(kept[i].Left, column), Truncate(kept[i].Right, column));
            }
        }
        Intersect(free, kept);
    }

    // To whole points from the column's left edge (towards the left), as InDesign sets the edges of shapes.
    private static double Truncate(double x, Rect column) => column.Left + Math.Floor(x - column.Left + 1e-6);

    /// <summary>
    /// The x ranges (in <paramref name="output"/>, unmerged) of the band from <paramref name="top"/> to
    /// <paramref name="bottom"/> that come within <paramref name="distance"/> of the outlines' edges, and with
    /// <paramref name="crossSection"/> the ranges inside the closed outlines.
    /// </summary>
    internal static void Covered(IReadOnlyList<Outline> outlines, double distance, double top, double bottom, List<(double Left, double Right)> output, bool crossSection = true)
    {
        foreach (var outline in outlines)
        {
            var points = outline.Points;
            var count = outline.Closed ? points.Length : points.Length - 1;
            for (var i = 0; i < count; i++)
            {
                if (EdgeReach(points[i], points[(i + 1) % points.Length], distance, top, bottom) is { } reach)
                {
                    output.Add(reach);
                }
            }
            if (points.Length == 1 && EdgeReach(points[0], points[0], distance, top, bottom) is { } dot)
            {
                output.Add(dot);
            }
        }
        if (crossSection)
        {
            CrossSection(outlines, top, output);
        }
    }

    /// <summary>
    /// The x range of the points within <paramref name="distance"/> of the edge from <paramref name="a"/> to
    /// <paramref name="b"/> whose y is between <paramref name="top"/> and <paramref name="bottom"/>; null when there are
    /// none. Exact: the extremes are at the ends of the edge's stretch inside the band, at the ends of the stretches
    /// above and below it within reach, or where the circle round a point of the edge touches the band's edge at its
    /// leftmost or rightmost.
    /// </summary>
    internal static (double Left, double Right)? EdgeReach(Point a, Point b, double distance, double top, double bottom)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        Consider(0);
        Consider(1);
        if (dy != 0)
        {
            foreach (var y in (ReadOnlySpan<double>)[top, bottom, top - distance, bottom + distance])
            {
                Consider((y - a.Y) / dy);
            }
            if (distance > 0)
            {
                // Above the band, x = α + βu over u = top - y; the circle's extreme is where its slope matches.
                var slope = Math.Abs(dx / dy);
                var u = slope * distance / Math.Sqrt(1 + (slope * slope));
                Consider((top - u - a.Y) / dy);
                Consider((bottom + u - a.Y) / dy);
            }
        }
        return low <= high ? (low, high) : null;

        void Consider(double t)
        {
            if (t is < 0 or > 1 || double.IsNaN(t))
            {
                return;
            }
            var y = a.Y + (t * dy);
            var away = y < top ? top - y : y > bottom ? y - bottom : 0;
            if (away > distance + 1e-9)
            {
                return;
            }
            var half = Math.Sqrt(Math.Max(0, (distance * distance) - (away * away)));
            var x = a.X + (t * dx);
            low = Math.Min(low, x - half);
            high = Math.Max(high, x + half);
        }
    }

    /// <summary>The x ranges inside the closed outlines at height <paramref name="y"/> (even-odd), added to <paramref name="output"/>.</summary>
    internal static void CrossSection(IReadOnlyList<Outline> outlines, double y, List<(double Left, double Right)> output)
    {
        var crossings = new List<double>();
        foreach (var outline in outlines)
        {
            if (!outline.Closed)
            {
                continue;
            }
            var points = outline.Points;
            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                if ((a.Y <= y && y < b.Y) || (b.Y <= y && y < a.Y))
                {
                    crossings.Add(a.X + ((y - a.Y) / (b.Y - a.Y) * (b.X - a.X)));
                }
            }
        }
        crossings.Sort();
        for (var i = 0; i + 1 < crossings.Count; i += 2)
        {
            output.Add((crossings[i], crossings[i + 1]));
        }
    }

    /// <summary>Takes <paramref name="left"/> to <paramref name="right"/> out of the sorted, disjoint ranges in <paramref name="ranges"/>.</summary>
    internal static void Subtract(List<(double Left, double Right)> ranges, double left, double right)
    {
        if (right <= left)
        {
            return;
        }
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var range = ranges[i];
            if (right <= range.Left || left >= range.Right)
            {
                continue;
            }
            ranges.RemoveAt(i);
            if (right < range.Right)
            {
                ranges.Insert(i, (right, range.Right));
            }
            if (left > range.Left)
            {
                ranges.Insert(i, (range.Left, left));
            }
        }
    }

    // Keeps in `ranges` only what is also in one of `kept`.
    private static void Intersect(List<(double Left, double Right)> ranges, List<(double Left, double Right)> kept)
    {
        var result = new List<(double Left, double Right)>();
        foreach (var range in ranges)
        {
            foreach (var other in kept)
            {
                var left = Math.Max(range.Left, other.Left);
                var right = Math.Min(range.Right, other.Right);
                if (right > left)
                {
                    result.Add((left, right));
                }
            }
        }
        result.Sort((x, y) => x.Left.CompareTo(y.Left));
        ranges.Clear();
        ranges.AddRange(result);
    }

    /// <summary>
    /// Flattens <paramref name="contours"/> (Bézier paths in an item's inner coordinates) to straight edges mapped by
    /// <paramref name="transform"/>, close enough that no edge strays more than 0.02 pt from the curve.
    /// </summary>
    internal static List<Outline> Flatten(IReadOnlyList<PathContour> contours, Matrix transform)
    {
        const double Tolerance = 0.02;
        var outlines = new List<Outline>(contours.Count);
        foreach (var contour in contours)
        {
            var source = contour.Points;
            if (source.Count == 0)
            {
                continue;
            }
            var points = new List<Point> { transform.Apply(source[0].Anchor) };
            var segments = contour.IsOpen ? source.Count - 1 : source.Count;
            for (var i = 0; i < segments; i++)
            {
                var from = source[i];
                var to = source[(i + 1) % source.Count];
                var (p0, p1, p2, p3) = (from.Anchor, from.RightDirection, to.LeftDirection, to.Anchor);
                if (p1 != p0 || p2 != p3)
                {
                    // A cubic strays at most 3/4 of its largest second difference over n² from n chords.
                    var bend = Math.Max(Math.Max(Math.Abs(p0.X - (2 * p1.X) + p2.X), Math.Abs(p0.Y - (2 * p1.Y) + p2.Y)),
                        Math.Max(Math.Abs(p1.X - (2 * p2.X) + p3.X), Math.Abs(p1.Y - (2 * p2.Y) + p3.Y)));
                    var steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(0.75 * bend / Tolerance)), 1, 256);
                    for (var step = 1; step < steps; step++)
                    {
                        var t = (double)step / steps;
                        var s = 1 - t;
                        var x = (s * s * s * p0.X) + (3 * s * s * t * p1.X) + (3 * s * t * t * p2.X) + (t * t * t * p3.X);
                        var y = (s * s * s * p0.Y) + (3 * s * s * t * p1.Y) + (3 * s * t * t * p2.Y) + (t * t * t * p3.Y);
                        points.Add(transform.Apply(new Point(x, y)));
                    }
                }
                if (!contour.IsOpen && i == segments - 1)
                {
                    break;
                }
                points.Add(transform.Apply(p3));
            }
            outlines.Add(new Outline([.. points], !contour.IsOpen));
        }
        return outlines;
    }
}
