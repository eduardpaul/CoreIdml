using CoreIdml.Xml;

namespace CoreIdml;

/// <summary>Unit conversion to points, IDML's native unit (1 pt = 1/72 in).</summary>
public static class Units
{
    public const double PointsPerInch = 72.0;

    public static double Points(double value) => value;

    public static double Millimeters(double value) => value * PointsPerInch / 25.4;

    public static double Centimeters(double value) => value * PointsPerInch / 2.54;

    public static double Inches(double value) => value * PointsPerInch;

    public static double ToMillimeters(double points) => points * 25.4 / PointsPerInch;

    /// <summary>Converts a value in the given unit name (<c>pt</c>, <c>mm</c>, <c>cm</c>, <c>in</c>) to points.</summary>
    public static double FromUnit(double value, string? unit) => (unit ?? "pt").ToLowerInvariant() switch
    {
        "pt" or "points" => value,
        "mm" or "millimeters" => Millimeters(value),
        "cm" or "centimeters" => Centimeters(value),
        "in" or "inches" => Inches(value),
        _ => throw new ArgumentException($"Unknown unit '{unit}'. Use pt, mm, cm or in.", nameof(unit)),
    };
}

/// <summary>Standard page sizes in points.</summary>
public static class PageSizes
{
    public static readonly Size A3 = new(Units.Millimeters(297), Units.Millimeters(420));
    public static readonly Size A4 = new(Units.Millimeters(210), Units.Millimeters(297));
    public static readonly Size A5 = new(Units.Millimeters(148), Units.Millimeters(210));
    public static readonly Size Letter = new(612, 792);
    public static readonly Size Legal = new(612, 1008);
}

public readonly record struct Size(double Width, double Height);

public readonly record struct Point(double X, double Y);

/// <summary>An axis-aligned rectangle: top-left corner plus size, y growing downwards (InDesign convention).</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public Point Center => new(X + (Width / 2), Y + (Height / 2));

    public static Rect FromEdges(double left, double top, double right, double bottom) => new(left, top, right - left, bottom - top);

    public bool Contains(Point point) => point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public Rect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public Rect Inflate(double amount) => new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));
}

/// <summary>Uniform or per-side spacing (margins, insets).</summary>
public readonly record struct Spacing(double Top, double Right, double Bottom, double Left)
{
    public static Spacing All(double value) => new(value, value, value, value);
}

/// <summary>The 2D affine transform IDML stores in <c>ItemTransform</c> ("a b c d tx ty").</summary>
public readonly record struct Matrix(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public static Matrix Translation(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    /// <summary>Rotation by <paramref name="degrees"/>; positive values rotate counter-clockwise on the page, like InDesign.</summary>
    public static Matrix Rotation(double degrees)
    {
        var radians = -degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new(cos, sin, -sin, cos, 0, 0);
    }

    public static Matrix Parse(string? value)
    {
        var parts = IdmlXml.ParseList(value);
        return parts.Length == 6 ? new(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]) : Identity;
    }

    /// <summary>Applies this transform, then <paramref name="next"/>.</summary>
    public Matrix Then(Matrix next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (Tx * next.A) + (Ty * next.C) + next.Tx,
        (Tx * next.B) + (Ty * next.D) + next.Ty);

    public Point Apply(Point point) => new((point.X * A) + (point.Y * C) + Tx, (point.X * B) + (point.Y * D) + Ty);

    public override string ToString() => IdmlXml.FormatList(A, B, C, D, Tx, Ty);
}
