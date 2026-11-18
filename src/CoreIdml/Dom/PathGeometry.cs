using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>Builds and reads the <c>PathGeometry</c> property of page items.</summary>
internal static class PathGeometry
{
    // Bezier handle length that approximates a quarter circle.
    private const double Kappa = 0.5522847498307936;

    public static XElement Rectangle(double width, double height)
    {
        var w = width / 2;
        var h = height / 2;
        return Build(open: false, [Corner(-w, -h), Corner(-w, h), Corner(w, h), Corner(w, -h)]);
    }

    public static XElement Ellipse(double width, double height)
    {
        var rx = width / 2;
        var ry = height / 2;
        var kx = rx * Kappa;
        var ky = ry * Kappa;
        return Build(open: false,
        [
            (new Point(-rx, 0), new Point(-rx, ky), new Point(-rx, -ky)),
            (new Point(0, -ry), new Point(-kx, -ry), new Point(kx, -ry)),
            (new Point(rx, 0), new Point(rx, -ky), new Point(rx, ky)),
            (new Point(0, ry), new Point(kx, ry), new Point(-kx, ry)),
        ]);
    }

    public static XElement Line(Point from, Point to) => Build(open: true, [Corner(from.X, from.Y), Corner(to.X, to.Y)]);

    /// <summary>All anchor points of the item's paths, in the item's inner coordinates.</summary>
    public static IEnumerable<Point> Anchors(XElement item)
    {
        var geometry = item.Element(IdmlXml.Properties)?.Element("PathGeometry");
        if (geometry is null)
        {
            yield break;
        }
        foreach (var point in geometry.Descendants("PathPointType"))
        {
            var anchor = IdmlXml.ParseList(point.Attribute("Anchor")?.Value);
            if (anchor.Length == 2)
            {
                yield return new Point(anchor[0], anchor[1]);
            }
        }
    }

    /// <summary>Every sub-path of the item with its Bézier handles, in the item's inner coordinates.</summary>
    public static IReadOnlyList<PathContour> Contours(XElement item)
    {
        var geometry = item.Element(IdmlXml.Properties)?.Element("PathGeometry");
        if (geometry is null)
        {
            return [];
        }
        var contours = new List<PathContour>();
        foreach (var path in geometry.Elements("GeometryPathType"))
        {
            var points = new List<PathPoint>();
            foreach (var point in path.Descendants("PathPointType"))
            {
                var anchor = IdmlXml.ParseList(point.Attribute("Anchor")?.Value);
                if (anchor.Length != 2)
                {
                    continue;
                }
                var left = IdmlXml.ParseList(point.Attribute("LeftDirection")?.Value);
                var right = IdmlXml.ParseList(point.Attribute("RightDirection")?.Value);
                var a = new Point(anchor[0], anchor[1]);
                points.Add(new PathPoint(a, left.Length == 2 ? new Point(left[0], left[1]) : a, right.Length == 2 ? new Point(right[0], right[1]) : a));
            }
            if (points.Count > 0)
            {
                contours.Add(new PathContour(path.Attribute("PathOpen")?.Value == "true", points));
            }
        }
        return contours;
    }

    private static (Point Anchor, Point Left, Point Right) Corner(double x, double y) => (new(x, y), new(x, y), new(x, y));

    private static XElement Build(bool open, (Point Anchor, Point Left, Point Right)[] points) =>
        new("PathGeometry",
            new XElement("GeometryPathType",
                new XAttribute("PathOpen", open ? "true" : "false"),
                new XElement("PathPointArray",
                    points.Select(point => new XElement("PathPointType",
                        new XAttribute("Anchor", IdmlXml.FormatList(point.Anchor.X, point.Anchor.Y)),
                        new XAttribute("LeftDirection", IdmlXml.FormatList(point.Left.X, point.Left.Y)),
                        new XAttribute("RightDirection", IdmlXml.FormatList(point.Right.X, point.Right.Y)))))));
}
