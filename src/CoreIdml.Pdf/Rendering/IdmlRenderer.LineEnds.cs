using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Writing;

namespace CoreIdml.Pdf.Rendering;

internal sealed partial class IdmlRenderer
{
    private const double Kappa = 0.5522847498307936;

    /// <summary>
    /// The arrowheads of a line or open path (Stroke panel Start and End: <c>LeftLineEnd</c> at the path's first
    /// point, <c>RightLineEnd</c> at its last), on each open contour. As in InDesign their size follows the stroke
    /// weight, times <c>LeftArrowHeadScale</c> / <c>RightArrowHeadScale</c> (percent, default 100), and they are
    /// solid on a dashed stroke. With the default alignment the head extends beyond the end of the path;
    /// <c>ArrowHeadAlignment="InsideArrowHeadAlignment"</c> puts its tip on the end, and the stroke stops inside the
    /// head (a straight last segment is shortened; a curved one is left as it is).
    /// The shapes are drawn to InDesign's proportions by eye, not measured: a triangle 5 weights long and 4 wide, a
    /// simple arrow's arms 4.6 weights long at 30 degrees, circles and squares 4 weights across.
    /// </summary>
    private sealed record LineEnd(string Shape, Point At, Point Direction, double Unit);

    /// <summary>The ends to draw on <paramref name="contours"/>, and the contours the stroke is drawn with (shortened for inside alignment).</summary>
    private (List<LineEnd> Ends, IReadOnlyList<PathContour> Contours) LineEnds(PageItem item, IReadOnlyList<PathContour> contours, double weight)
    {
        var element = item.Element;
        var start = resolver.Item(element, "LeftLineEnd") is { } left && left != "None" ? left : null;
        var end = resolver.Item(element, "RightLineEnd") is { } right && right != "None" ? right : null;
        var ends = new List<LineEnd>();
        if (start is null && end is null || !contours.Any(contour => contour.IsOpen && contour.Points.Count >= 2))
        {
            return (ends, contours);
        }
        var inside = resolver.Item(element, "ArrowHeadAlignment") == "InsideArrowHeadAlignment";
        var shortened = new List<PathContour>(contours.Count);
        foreach (var contour in contours)
        {
            if (!contour.IsOpen || contour.Points.Count < 2)
            {
                shortened.Add(contour);
                continue;
            }
            var points = contour.Points.ToList();
            foreach (var (shape, scaleName, first) in (ReadOnlySpan<(string?, string, bool)>)[(start, "LeftArrowHeadScale", true), (end, "RightArrowHeadScale", false)])
            {
                if (shape is null || ArrowTip(shape) is not { } tipUnits)
                {
                    continue;
                }
                var unit = weight * Values.Number(resolver.Item(element, scaleName), 100) / 100;
                var (index, neighbour) = first ? (0, 1) : (points.Count - 1, points.Count - 2);
                var anchor = points[index];
                var toward = first ? anchor.RightDirection : anchor.LeftDirection;
                var other = points[neighbour];
                var from = toward != anchor.Anchor ? toward
                    : (first ? other.LeftDirection : other.RightDirection) is var handle && handle != other.Anchor ? handle
                    : other.Anchor;
                var length = Distance(from, anchor.Anchor);
                if (length < 1e-9 || unit <= 0)
                {
                    continue;
                }
                var direction = new Point((anchor.Anchor.X - from.X) / length, (anchor.Anchor.Y - from.Y) / length);
                var tip = tipUnits * unit;
                var at = anchor.Anchor;
                if (inside)
                {
                    at = new Point(at.X - (direction.X * tip), at.Y - (direction.Y * tip));
                    var straight = toward == anchor.Anchor && (first ? other.LeftDirection : other.RightDirection) == other.Anchor;
                    if (straight)
                    {
                        // The stroke stops where the head is attached, short of the other end.
                        var back = Math.Min(tip, Distance(other.Anchor, anchor.Anchor) * 0.999);
                        var moved = new Point(anchor.Anchor.X - (direction.X * back), anchor.Anchor.Y - (direction.Y * back));
                        points[index] = new PathPoint(moved, moved, moved);
                    }
                }
                ends.Add(new LineEnd(shape, at, direction, unit));
            }
            shortened.Add(new PathContour(true, points));
        }
        return (ends, shortened);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>How far ahead of where it is attached an arrowhead's tip is, in stroke weights; null for an unknown one (not drawn).</summary>
    private static double? ArrowTip(string shape) => shape switch
    {
        "SimpleArrowHead" or "SimpleWideArrowHead" => 1,
        "TriangleArrowHead" or "TriangleWideArrowHead" or "BarbedArrowHead" or "CurvedArrowHead" => 4.5,
        "CircleArrowHead" or "SquareArrowHead" => 4.5,
        "CircleSolidArrowHead" or "SquareSolidArrowHead" => 2,
        "BarArrowHead" => 0.5,
        _ => null,
    };

    /// <summary>
    /// Draws the arrowheads in the stroke's colour, already selected for stroking; <paramref name="fillPattern"/> is
    /// the stroke's gradient pattern, if it has one, to fill the solid heads with.
    /// </summary>
    private static void DrawLineEnds(List<LineEnd> ends, ContentBuilder content, ResolvedColor? color, string? fillPattern)
    {
        if (ends.Count == 0)
        {
            return;
        }
        if (fillPattern is not null)
        {
            content.PatternColor(fillPattern, stroke: false);
        }
        else if (color is not null)
        {
            content.FillColor(color);
        }
        // Solid, whatever the stroke's dashes; sharp corners.
        content.Dash([]).LineCap(0).LineJoin(0).MiterLimit(10);
        foreach (var end in ends)
        {
            var (at, d, k) = (end.At, end.Direction, end.Unit);
            // u along the line, beyond its end; v across it.
            Point P(double u, double v) => new(at.X + (((d.X * u) - (d.Y * v)) * k), at.Y + (((d.Y * u) + (d.X * v)) * k));
            void Move(double u, double v)
            {
                var p = P(u, v);
                content.MoveTo(p.X, p.Y);
            }
            void Line(double u, double v)
            {
                var p = P(u, v);
                content.LineTo(p.X, p.Y);
            }
            void Curve(double u1, double v1, double u2, double v2, double u3, double v3)
            {
                var (a, b, c) = (P(u1, v1), P(u2, v2), P(u3, v3));
                content.CurveTo(a.X, a.Y, b.X, b.Y, c.X, c.Y);
            }
            void Circle(double cu, double r)
            {
                var o = r * Kappa;
                Move(cu + r, 0);
                Curve(cu + r, o, cu + o, r, cu, r);
                Curve(cu - o, r, cu - r, o, cu - r, 0);
                Curve(cu - r, -o, cu - o, -r, cu, -r);
                Curve(cu + o, -r, cu + r, -o, cu + r, 0);
                content.ClosePath();
            }
            void Polygon(params ReadOnlySpan<(double U, double V)> corners)
            {
                Move(corners[0].U, corners[0].V);
                foreach (var (u, v) in corners[1..])
                {
                    Line(u, v);
                }
                content.ClosePath();
            }
            switch (end.Shape)
            {
                case "SimpleArrowHead" or "SimpleWideArrowHead":
                {
                    var (back, half) = end.Shape == "SimpleArrowHead" ? (-4.0, 2.3) : (-2.5, 3.0);
                    content.LineWidth(k);
                    Move(back, half);
                    Line(0, 0);
                    Line(back, -half);
                    content.Stroke();
                    break;
                }
                case "TriangleArrowHead":
                    Polygon((4.5, 0), (-0.5, 2), (-0.5, -2));
                    content.Fill();
                    break;
                case "TriangleWideArrowHead":
                    Polygon((4.5, 0), (-0.5, 3), (-0.5, -3));
                    content.Fill();
                    break;
                case "BarbedArrowHead":
                    Polygon((4.5, 0), (-1, 2.2), (0, 0), (-1, -2.2));
                    content.Fill();
                    break;
                case "CurvedArrowHead":
                    Move(4.5, 0);
                    Curve(3, 0.5, 0.5, 1.5, -1, 2.4);
                    Curve(-0.5, 1.3, 0, 0.5, 0, 0);
                    Curve(0, -0.5, -0.5, -1.3, -1, -2.4);
                    Curve(0.5, -1.5, 3, -0.5, 4.5, 0);
                    content.ClosePath();
                    content.Fill();
                    break;
                case "CircleArrowHead":
                    content.LineWidth(k);
                    Circle(2, 2);
                    content.Stroke();
                    break;
                case "CircleSolidArrowHead":
                    Circle(0, 2);
                    content.Fill();
                    break;
                case "SquareArrowHead":
                    content.LineWidth(k);
                    Polygon((0, 2), (4, 2), (4, -2), (0, -2));
                    content.Stroke();
                    break;
                case "SquareSolidArrowHead":
                    Polygon((-2, 2), (2, 2), (2, -2), (-2, -2));
                    content.Fill();
                    break;
                case "BarArrowHead":
                    Polygon((-0.5, 2.5), (0.5, 2.5), (0.5, -2.5), (-0.5, -2.5));
                    content.Fill();
                    break;
            }
        }
    }
}
