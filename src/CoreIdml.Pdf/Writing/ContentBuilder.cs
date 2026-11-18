using System.Globalization;
using System.Text;
using CoreIdml.Dom;
using static CoreIdml.Pdf.Writing.PdfObjectWriter;

namespace CoreIdml.Pdf.Writing;

/// <summary>Builds a page content stream (PDF operators) with invariant number formatting.</summary>
internal sealed class ContentBuilder
{
    private readonly StringBuilder builder = new();

    public int Length => builder.Length;

    public byte[] ToBytes() => Encoding.Latin1.GetBytes(builder.ToString());

    public ContentBuilder Raw(string operators)
    {
        builder.Append(operators).Append('\n');
        return this;
    }

    public ContentBuilder Save() => Raw("q");

    public ContentBuilder Restore() => Raw("Q");

    public ContentBuilder Transform(CoreIdml.Matrix m) =>
        Raw($"{Number(m.A)} {Number(m.B)} {Number(m.C)} {Number(m.D)} {Number(m.Tx)} {Number(m.Ty)} cm");

    public ContentBuilder MoveTo(double x, double y) => Raw($"{Number(x)} {Number(y)} m");

    public ContentBuilder LineTo(double x, double y) => Raw($"{Number(x)} {Number(y)} l");

    public ContentBuilder CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        Raw($"{Number(x1)} {Number(y1)} {Number(x2)} {Number(y2)} {Number(x3)} {Number(y3)} c");

    public ContentBuilder ClosePath() => Raw("h");

    public ContentBuilder Rectangle(double x, double y, double width, double height) =>
        Raw($"{Number(x)} {Number(y)} {Number(width)} {Number(height)} re");

    public ContentBuilder LineWidth(double width) => Raw($"{Number(width)} w");

    public ContentBuilder LineCap(int cap) => Raw(cap.ToString(CultureInfo.InvariantCulture) + " J");

    public ContentBuilder LineJoin(int join) => Raw(join.ToString(CultureInfo.InvariantCulture) + " j");

    public ContentBuilder MiterLimit(double limit) => Raw($"{Number(limit)} M");

    public ContentBuilder Dash(IReadOnlyList<double> pattern, double phase = 0) =>
        Raw("[" + string.Join(' ', pattern.Select(Number)) + "] " + Number(phase) + " d");

    public ContentBuilder FillColor(ResolvedColor color) => Raw(ColorOperator(color, stroke: false));

    public ContentBuilder StrokeColor(ResolvedColor color) => Raw(ColorOperator(color, stroke: true));

    public ContentBuilder Fill(bool evenOdd = false) => Raw(evenOdd ? "f*" : "f");

    public ContentBuilder Stroke() => Raw("S");

    public ContentBuilder FillStroke(bool evenOdd = false) => Raw(evenOdd ? "B*" : "B");

    public ContentBuilder Clip(bool evenOdd = false) => Raw(evenOdd ? "W* n" : "W n");

    public ContentBuilder EndPath() => Raw("n");

    public ContentBuilder GraphicsState(string name) => Raw(Name(name) + " gs");

    public ContentBuilder XObject(string name) => Raw(Name(name) + " Do");

    public static string ColorOperator(ResolvedColor color, bool stroke)
    {
        var values = string.Join(' ', color.Components.Select(Number));
        var op = color.Space switch
        {
            ColorSpaceKind.Gray => "g",
            ColorSpaceKind.Rgb => "rg",
            _ => "k",
        };
        return values + " " + (stroke ? op.ToUpperInvariant() : op);
    }
}
