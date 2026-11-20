using System.Text;
using CoreIdml.Dom;
using static CoreIdml.Pdf.Writing.PdfObjectWriter;

namespace CoreIdml.Pdf.Writing;

/// <summary>
/// A gradient swatch as a PDF shading: axial (type 2, <c>/Coords [0 0 1 0]</c>) or radial (type 3, <c>/Coords
/// [0 0 0 0 0 1]</c>) in a unit gradient space that the content stream (or a pattern matrix) maps onto the item, with
/// <c>/Extend [true true]</c>, coloured by a stitching function (type 3) of one exponential function (type 2) per pair
/// of neighbouring stops. This is how InDesign 20 writes gradients (03-color-gradients reference.pdf):
/// <list type="bullet">
/// <item>the function's bounds are the inner stop locations (4 stops at 0, 33, 66 and 100% give <c>/Bounds [0.33
/// 0.66]</c>);</item>
/// <item>a midpoint m (the colour is half and half at m of the span) is the exponent N = ln 0.5 / ln m for m ≥ 50%;
/// below 50% the segment runs backwards, from the later colour, with <c>/Encode [1 0]</c> and N = ln 0.5 / ln (1 − m)
/// (black to cyan with its midpoint at 20%: <c>/C0</c> cyan, <c>/C1</c> black, <c>/Encode [1 0]</c>, <c>/N
/// 3.10628</c>; at 80%: forwards, the same N).</item>
/// </list>
/// InDesign writes the stops' inks as a DeviceN space. Here process stops are DeviceCMYK, which renders the same; a
/// gradient with an RGB or Lab stop is DeviceRGB. Spot stops are their process (alternate) values, like other swatches.
/// Between two different inks (two spots, or a spot and process inks) DeviceN mixes the inks' alternates
/// multiplicatively (1 − (1 − a)(1 − b) per CMYK component, the tint transform InDesign writes for "G Spot"), which is
/// not linear in CMYK: such a span is split into <see cref="MixedInkPieces"/> linear pieces sampled from that mix.
/// </summary>
internal sealed class GradientShading
{
    /// <summary>Linear pieces approximating a span between two different inks (error below 0.1% of full ink).</summary>
    internal const int MixedInkPieces = 16;

    private GradientShading(bool radial, ColorSpaceKind space, IReadOnlyList<double> bounds, IReadOnlyList<Segment> segments)
    {
        Radial = radial;
        Space = space;
        Bounds = bounds;
        Segments = segments;
    }

    public bool Radial { get; }

    /// <summary>The colour space of the stops (DeviceGray, DeviceRGB or DeviceCMYK).</summary>
    public ColorSpaceKind Space { get; }

    /// <summary>Where one segment ends and the next starts, increasing, inside 0..1 (one fewer than the segments).</summary>
    public IReadOnlyList<double> Bounds { get; }

    public IReadOnlyList<Segment> Segments { get; }

    /// <summary>
    /// One exponential function: <c>C0 + s^N (C1 − C0)</c>, s running 0 → 1 across the segment, or 1 → 0 when
    /// <see cref="Reversed"/>.
    /// </summary>
    internal sealed record Segment(IReadOnlyList<double> C0, IReadOnlyList<double> C1, double Exponent, bool Reversed);

    public static GradientShading From(ResolvedGradient gradient)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        var stops = gradient.Stops;
        var spaces = stops.Select(stop => stop.Color.Space).Distinct().ToList();
        var space = spaces.Count == 1 ? spaces[0] : ColorSpaceKind.Rgb;
        IReadOnlyList<double> Components(ResolvedGradientStop stop)
        {
            if (stop.Color.Space == space)
            {
                return stop.Color.Components;
            }
            var (r, g, b) = stop.Color.ToRgb();
            return [r, g, b];
        }

        var bounds = new List<double>();
        var segments = new List<Segment>();
        void Add(double end, Segment segment)
        {
            segments.Add(segment);
            bounds.Add(end);
        }
        var first = Components(stops[0]);
        if (stops[0].Location > 0)
        {
            Add(stops[0].Location, Constant(first));
        }
        for (var i = 1; i < stops.Count; i++)
        {
            double from = stops[i - 1].Location, to = stops[i].Location;
            if (to <= from)
            {
                continue; // Two stops at one location: a hard edge, drawn by the segments either side.
            }
            var a = Components(stops[i - 1]);
            var b = Components(stops[i]);
            var midpoint = Math.Clamp(stops[i].Midpoint, 0.01, 0.99);
            if (space == ColorSpaceKind.Cmyk && stops[i - 1].SpotInk != stops[i].SpotInk)
            {
                for (var piece = 1; piece <= MixedInkPieces; piece++)
                {
                    var t0 = (double)(piece - 1) / MixedInkPieces;
                    var t1 = (double)piece / MixedInkPieces;
                    Add(from + ((to - from) * t1), new Segment(MixInks(a, b, Curve(t0, midpoint)), MixInks(a, b, Curve(t1, midpoint)), 1, false));
                }
                continue;
            }
            Add(to, midpoint >= 0.5
                ? new Segment(a, b, Exponent(midpoint), false)
                : new Segment(b, a, Exponent(1 - midpoint), true));
        }
        var last = Components(stops[^1]);
        if (segments.Count == 0 || stops[^1].Location < 1)
        {
            Add(1, Constant(last));
        }
        // The last segment ends at the domain's end, which is not a bound.
        bounds.RemoveAt(bounds.Count - 1);
        return new GradientShading(gradient.Type == GradientType.Radial, space, bounds, segments);

        static Segment Constant(IReadOnlyList<double> color) => new(color, color, 1, false);
    }

    /// <summary>The exponent that puts half of the change at <paramref name="midpoint"/> (0.5 ≤ m &lt; 1).</summary>
    private static double Exponent(double midpoint) => Math.Log(0.5) / Math.Log(midpoint);

    /// <summary>
    /// How far (0..1) the colour has moved from one stop to the next at <paramref name="t"/> of the span, for a midpoint
    /// <paramref name="midpoint"/>: the curve InDesign's functions trace (see the class remarks).
    /// </summary>
    internal static double Curve(double t, double midpoint) => midpoint >= 0.5
        ? Math.Pow(t, Exponent(midpoint))
        : 1 - Math.Pow(1 - t, Exponent(1 - midpoint));

    /// <summary>
    /// Two different inks at weights 1 − s and s, mixed as DeviceN prints them through their CMYK alternates: each CMYK
    /// component is 1 − (1 − (1 − s) a)(1 − s b).
    /// </summary>
    internal static double[] MixInks(IReadOnlyList<double> a, IReadOnlyList<double> b, double s) =>
        a.Select((value, k) => 1 - ((1 - ((1 - s) * value)) * (1 - (s * b[k])))).ToArray();

    /// <summary>The shading dictionary, functions inline.</summary>
    public string ToPdf()
    {
        var builder = new StringBuilder();
        var colorSpace = Space switch
        {
            ColorSpaceKind.Gray => "/DeviceGray",
            ColorSpaceKind.Rgb => "/DeviceRGB",
            _ => "/DeviceCMYK",
        };
        builder.Append(Radial ? "<</ShadingType 3 /Coords [0 0 0 0 0 1]" : "<</ShadingType 2 /Coords [0 0 1 0]");
        builder.Append(" /ColorSpace ").Append(colorSpace).Append(" /Domain [0 1] /Extend [true true] /Function <</FunctionType 3 /Domain [0 1] /Functions [");
        foreach (var segment in Segments)
        {
            builder.Append("<</FunctionType 2 /Domain [0 1] /C0 ").Append(Array(segment.C0)).Append(" /C1 ").Append(Array(segment.C1))
                .Append(" /N ").Append(Number(segment.Exponent)).Append(">>");
        }
        builder.Append("] /Bounds ").Append(Array(Bounds)).Append(" /Encode [")
            .AppendJoin(' ', Segments.Select(segment => segment.Reversed ? "1 0" : "0 1")).Append("]>>>>");
        return builder.ToString();
    }

    private static string Array(IEnumerable<double> values) => "[" + string.Join(' ', values.Select(Number)) + "]";
}
