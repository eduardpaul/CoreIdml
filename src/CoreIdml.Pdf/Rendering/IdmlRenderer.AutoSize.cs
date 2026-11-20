using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;

namespace CoreIdml.Pdf.Rendering;

/// <summary>Auto-sizing text frames (<c>TextFramePreference@AutoSizingType</c>).</summary>
internal sealed partial class IdmlRenderer
{
    // A size within this of the stored one is the stored one: InDesign saved the size its text gave the frame.
    private const double AutoSizeTolerance = 0.05;

    // A proportionally auto-sizing frame its text fits at this scale or more keeps its size (see AutoSize).
    private const double ProportionalTolerance = 0.99;

    // The inner bounds auto-sizing frames take for their text, by frame id (by frame id and page for master frames).
    private readonly Dictionary<string, Rect> autoSized = new(StringComparer.Ordinal);

    /// <summary>
    /// Composes <paramref name="story"/> into <paramref name="areas"/> (one per frame of <paramref name="frames"/>,
    /// in thread order), first sizing the last frame to the text when it auto-sizes (see <see cref="AutoSize"/>);
    /// returns the layout and the areas it was composed in. <paramref name="key"/> is where the frame's new bounds
    /// are kept for drawing (<see cref="FrameBounds"/>).
    /// </summary>
    internal (StoryLayout Layout, IReadOnlyList<FrameArea> Areas) ComposeFitted(Story story, IReadOnlyList<TextFrame> frames, IReadOnlyList<FrameArea> areas, string? key = null)
    {
        if (frames.Count > 0 && frames.Count == areas.Count)
        {
            var last = frames[^1];
            key ??= last.Id;
            if (AutoSize(story, last, areas) is { } bounds)
            {
                autoSized[key] = bounds;
                areas = [.. areas.Take(areas.Count - 1), FrameArea(last, areas[^1].PageName, bounds) with { Markers = areas[^1].Markers }];
            }
            else
            {
                autoSized.Remove(key);
            }
        }
        return (composer.Compose(story.Element, areas), areas);
    }

    /// <summary>The inner bounds a frame is drawn with: the size its text gave it when it auto-sizes, else its own.</summary>
    private Rect FrameBounds(PageItem item, string? key = null) =>
        item is TextFrame && autoSized.TryGetValue(key ?? item.Id, out var bounds) ? bounds : item.InnerBounds;

    /// <summary>The path a frame is drawn with: a rectangle of its auto-sized bounds, else its own contours.</summary>
    private IReadOnlyList<PathContour> FrameContours(PageItem item, string? key = null)
    {
        if (item is not TextFrame || !autoSized.TryGetValue(key ?? item.Id, out var r))
        {
            return item.Contours;
        }
        PathPoint Corner(double x, double y) => new(new Point(x, y), new Point(x, y), new Point(x, y));
        return [new PathContour(false, [Corner(r.Left, r.Top), Corner(r.Left, r.Bottom), Corner(r.Right, r.Bottom), Corner(r.Right, r.Top)])];
    }

    /// <summary>
    /// The inner bounds InDesign gives the last frame of a story that auto-sizes, or null when it keeps its stored
    /// size (it does not auto-size, is not a plain rectangle, or the text needs the size it has: InDesign stored the
    /// size it computed, so an unedited document keeps every frame as it is). Measured in 06-frame-autosize:
    /// <list type="bullet">
    /// <item>height (<c>HeightOnly</c>, <c>HeightAndWidth</c>): the frame ends its bottom inset below the last
    /// baseline (descenders hang out, as in a fixed frame), its columns balanced (p6: 14 lines in two columns of
    /// 7), and no lower than <c>MinimumHeightForAutoSizing</c> with <c>UseMinimumHeightForAutoSizing</c>, counted
    /// with the stroke (p6: 90 pt with a 1 pt stroke is 89 pt from path to path);</item>
    /// <item>width (<c>WidthOnly</c>, or <c>HeightAndWidth</c> with <c>UseNoLineBreaksForAutoSizing</c>): as wide as
    /// the longest paragraph set on one line (p3, p4);</item>
    /// <item><c>HeightAndWidthProportionally</c>: both scaled alike until the text just fits (p5);</item>
    /// <item><c>AutoSizingReferencePoint</c> stays put: the top, centre or bottom of the frame as it grows (p2),
    /// its left, centre or right as it widens (p3).</item>
    /// </list>
    /// From memory, not in the samples: a minimum width (<c>UseMinimumWidthForAutoSizing</c>) counts like the
    /// minimum height; frames threaded on to another frame keep their size; non-rectangular frames do not auto-size.
    /// </summary>
    private Rect? AutoSize(Story story, TextFrame frame, IReadOnlyList<FrameArea> areas)
    {
        var chain = resolver.ObjectChain(frame.Element);
        string? Preference(string name) => AttributeResolver.FramePreference(chain, name);
        var type = Preference("AutoSizingType") ?? "Off";
        if (type is not ("HeightOnly" or "WidthOnly" or "HeightAndWidth" or "HeightAndWidthProportionally") || !IsRectangle(frame.Contours))
        {
            return null;
        }
        const double Unlimited = 100_000;
        var stored = frame.InnerBounds;
        var inset = TextInset(chain);
        var last = areas[^1];
        var lastIndex = areas.Count - 1;
        StoryLayout Lay(double width, double height, bool balance = false) => composer.Compose(story.Element,
            [.. areas.Take(lastIndex), FrameArea(frame, last.PageName, stored with { Width = width, Height = height }) with
            {
                Markers = last.Markers,
                VerticalJustification = "TopAlign",
                BalanceColumns = balance,
            }]);
        var stroke = StrokeAllowance(frame.Element);
        var (width, height) = (stored.Width, stored.Height);
        if (type == "HeightAndWidthProportionally")
        {
            // InDesign leaves a little room when it scales (p5: 0.13 and 0.04 pt of height our text would not need):
            // a frame its text fits within 1% of its size keeps it.
            var scale = Bisect(1, s => !Lay(stored.Width * s, stored.Height * s).Overset);
            scale = scale is > ProportionalTolerance and <= 1 ? 1 : scale;
            (width, height) = (stored.Width * scale, stored.Height * scale);
        }
        else
        {
            if (type == "WidthOnly" || (type == "HeightAndWidth" && Preference("UseNoLineBreaksForAutoSizing") == "true"))
            {
                // As narrow as the frame can be with no paragraph broken into more lines than at any width.
                var lines = Lay(Unlimited, Unlimited).Lines.Count;
                var scale = Bisect(1, s => Lay(stored.Width * s, Unlimited) is { Overset: false } layout && layout.Lines.Count <= lines);
                width = Math.Max(stored.Width * scale, Minimum("Width"));
            }
            if (type is "HeightOnly" or "HeightAndWidth")
            {
                var layout = Lay(width, Unlimited, balance: last.Columns.Count > 1);
                var laid = layout.Lines.Where(line => line.FrameIndex == lastIndex).ToList();
                if (laid.Count > 0)
                {
                    // Footnotes go below the last baseline (from memory: no sample has an auto-sizing frame with any).
                    height = Math.Max(laid.Max(layout.FootnoteBottom) - stored.Y + inset.Bottom, Minimum("Height"));
                }
            }
        }
        if (Math.Abs(width - stored.Width) <= AutoSizeTolerance && Math.Abs(height - stored.Height) <= AutoSizeTolerance)
        {
            return null;
        }
        var point = Preference("AutoSizingReferencePoint") ?? "CenterPoint";
        var across = point.Contains("Left", StringComparison.Ordinal) ? 0 : point.Contains("Right", StringComparison.Ordinal) ? 1 : 0.5;
        var down = point.StartsWith("Top", StringComparison.Ordinal) ? 0 : point.StartsWith("Bottom", StringComparison.Ordinal) ? 1 : 0.5;
        return new Rect(stored.X + ((stored.Width - width) * across), stored.Y + ((stored.Height - height) * down), width, height);

        // The minimum width or height from path to path: the minimum is measured with the stroke.
        double Minimum(string dimension) => Preference($"UseMinimum{dimension}ForAutoSizing") == "true"
            ? Values.Number(Preference($"Minimum{dimension}ForAutoSizing"), 0) - stroke
            : 0;
    }

    /// <summary>
    /// The smallest scale (to 1/10 000) at which <paramref name="fits"/>, starting from <paramref name="start"/>
    /// (doubled until it fits, at most 64 times the stored size).
    /// </summary>
    private static double Bisect(double start, Func<double, bool> fits)
    {
        const double Precision = 1e-4;
        var high = start;
        while (!fits(high))
        {
            if (high >= 64)
            {
                return start;
            }
            high *= 2;
        }
        var low = 0.0;
        while (high - low > Precision * high)
        {
            var middle = (low + high) / 2;
            (low, high) = fits(middle) ? (low, middle) : (middle, high);
        }
        return high;
    }

    // How much a visible stroke adds to a frame's size outside its path (its whole weight, half on each side).
    private double StrokeAllowance(System.Xml.Linq.XElement element)
    {
        var weight = Values.Number(resolver.Item(element, "StrokeWeight"), 0);
        return weight <= 0 || resolver.Item(element, "StrokeColor") is null or "Swatch/None" ? 0 : weight;
    }

    // A path InDesign can auto-size: one closed contour of four corner points, with straight sides along the axes.
    private static bool IsRectangle(IReadOnlyList<PathContour> contours)
    {
        if (contours.Count != 1 || contours[0].IsOpen || contours[0].Points.Count != 4)
        {
            return false;
        }
        var points = contours[0].Points;
        for (var i = 0; i < 4; i++)
        {
            var (from, to) = (points[i], points[(i + 1) % 4]);
            if (from.LeftDirection != from.Anchor || from.RightDirection != from.Anchor
                || (Math.Abs(from.Anchor.X - to.Anchor.X) > 0.001 && Math.Abs(from.Anchor.Y - to.Anchor.Y) > 0.001))
            {
                return false;
            }
        }
        return true;
    }
}
