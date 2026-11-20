using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Writing;

namespace CoreIdml.Pdf.Rendering;

internal sealed partial class IdmlRenderer
{
    /// <summary>
    /// Fills a paragraph's shading box, as InDesign writes it (08-para-borders-shading): a rectangle (<c>re f</c>), or
    /// the box's path with its corners; clipped to the frame's bounds with Clip to Frame (from memory: no sample sets it).
    /// </summary>
    private static void DrawParagraphShading(ParagraphShadingOp shading, ContentBuilder content, Rect frameBounds)
    {
        if (shading.Box.Width <= 0 || shading.Box.Height <= 0)
        {
            return;
        }
        content.Save();
        if (shading.ClipToFrame)
        {
            content.Rectangle(frameBounds.X, frameBounds.Y, frameBounds.Width, frameBounds.Height).Clip();
        }
        AppendBox(shading.Box, shading.Corners, content);
        content.FillColor(shading.Color).Fill();
        content.Restore();
    }

    /// <summary>
    /// Strokes a paragraph's border centred on the box's edges (08-para-borders-shading p2: a 3 pt border 4 pt out
    /// from the text is a 251.75 × 32 pt rectangle stroked at 3 pt). With the same weight on every side it is the box's
    /// path, with its corners (p3: rounded, inverse rounded and bevel corners of 10 pt). Otherwise each side with a
    /// weight is a line of its own (p2: top and bottom only, two 2 pt lines across the column), the top and bottom
    /// ones running on over half the sides' weights so that the corners close; so is a box open at a column break.
    /// Stroke styles other than solid (dashed and dotted as on frames, over the gap colour) are from memory.
    /// </summary>
    private static void DrawParagraphBorder(ParagraphBorderOp op, ContentBuilder content)
    {
        var (box, border) = (op.Box, op.Border);
        var weights = border.Weights;
        if (box.Width < 0 || box.Height < 0)
        {
            return;
        }
        var uniform = weights.Top == weights.Right && weights.Top == weights.Bottom && weights.Top == weights.Left;
        if (uniform && !op.OpenTop && !op.OpenBottom)
        {
            Stroke(weights.Top, () => AppendBox(box, border.Box.Corners, content));
            return;
        }
        var (left, top, right, bottom) = (box.Left, box.Top, box.Right, box.Bottom);
        if (!op.OpenTop)
        {
            Stroke(weights.Top, () => content.MoveTo(left - (weights.Left / 2), top).LineTo(right + (weights.Right / 2), top));
        }
        if (!op.OpenBottom)
        {
            Stroke(weights.Bottom, () => content.MoveTo(left - (weights.Left / 2), bottom).LineTo(right + (weights.Right / 2), bottom));
        }
        Stroke(weights.Left, () => content.MoveTo(left, top).LineTo(left, bottom));
        Stroke(weights.Right, () => content.MoveTo(right, top).LineTo(right, bottom));

        void Stroke(double weight, Action path)
        {
            if (weight <= 0)
            {
                return;
            }
            var name = border.Type.StartsWith("StrokeStyle/$ID/", StringComparison.Ordinal) ? border.Type["StrokeStyle/$ID/".Length..] : border.Type;
            content.Save().LineWidth(weight).LineCap(border.Cap).LineJoin(border.Join).MiterLimit(10);
            if (border.Gap is { } gap && name != "Solid")
            {
                path();
                content.StrokeColor(gap).Stroke();
            }
            content.StrokeColor(border.Color);
            var canned = CannedDash().Match(name);
            if (canned.Success)
            {
                content.Dash([weight * Values.Number(canned.Groups[1].Value, 3), weight * Values.Number(canned.Groups[2].Value, 2)]);
            }
            else if (name.Contains("Dashed", StringComparison.Ordinal))
            {
                content.Dash([weight * 3, weight * 2]);
            }
            else if (name.Contains("Dotted", StringComparison.Ordinal))
            {
                content.LineCap(1).Dash([0, weight * 2]);
            }
            path();
            content.Stroke();
            content.Restore();
        }
    }

    /// <summary>
    /// Appends a paragraph box's path: a rectangle when its corners are square, else its outline with each corner as
    /// InDesign draws it (08-para-borders-shading p3): rounded as a quarter circle, inverse rounded as a curve into
    /// the box (one control point at the corner's inner point), bevel as a straight cut; inset as a square notch and
    /// fancy as rounded (from memory). A radius larger than
    /// half the box's shorter side is reduced to it.
    /// </summary>
    private static void AppendBox(Rect box, BoxCorners corners, ContentBuilder content)
    {
        if (corners.AllSquare)
        {
            content.Rectangle(box.X, box.Y, box.Width, box.Height);
            return;
        }
        const double Kappa = 0.5522847498307936;
        var max = Math.Min(box.Width, box.Height) / 2;
        double R(BoxCorner corner) => corner.IsSquare ? 0 : Math.Min(corner.Radius, max);
        var (tl, tr, br, bl) = (R(corners.TopLeft), R(corners.TopRight), R(corners.BottomRight), R(corners.BottomLeft));
        var (left, top, right, bottom) = (box.Left, box.Top, box.Right, box.Bottom);
        content.MoveTo(left + tl, top);
        content.LineTo(right - tr, top);
        Corner(corners.TopRight, tr, new Point(right - tr, top), new Point(right, top), new Point(right, top + tr));
        content.LineTo(right, bottom - br);
        Corner(corners.BottomRight, br, new Point(right, bottom - br), new Point(right, bottom), new Point(right - br, bottom));
        content.LineTo(left + bl, bottom);
        Corner(corners.BottomLeft, bl, new Point(left + bl, bottom), new Point(left, bottom), new Point(left, bottom - bl));
        content.LineTo(left, top + tl);
        Corner(corners.TopLeft, tl, new Point(left, top + tl), new Point(left, top), new Point(left + tl, top));
        content.ClosePath();

        // From `from` (where one edge ends) round `corner` to `to` (where the next starts).
        void Corner(BoxCorner kind, double radius, Point from, Point corner, Point to)
        {
            if (radius <= 0)
            {
                return;
            }
            var inner = new Point(from.X + to.X - corner.X, from.Y + to.Y - corner.Y);
            switch (kind.Option)
            {
                case "BevelCorner":
                    content.LineTo(to.X, to.Y);
                    break;
                case "InsetCorner":
                    content.LineTo(inner.X, inner.Y).LineTo(to.X, to.Y);
                    break;
                case "InverseRoundedCorner":
                    // InDesign goes round the box the other way, each curve leaving its start along the edge (a
                    // `v` curve): this way round the same curve arrives along the edge.
                    content.CurveTo(inner.X, inner.Y, to.X, to.Y, to.X, to.Y);
                    break;
                default:
                    content.CurveTo(from.X + ((corner.X - from.X) * Kappa), from.Y + ((corner.Y - from.Y) * Kappa),
                        to.X + ((corner.X - to.X) * Kappa), to.Y + ((corner.Y - to.Y) * Kappa), to.X, to.Y);
                    break;
            }
        }
    }
}
