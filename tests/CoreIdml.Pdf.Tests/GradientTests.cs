using CoreIdml.Dom;
using CoreIdml.Pdf.Rendering;
using CoreIdml.Pdf.Writing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Gradient swatches as PDF shadings, written the way InDesign writes them (03-color-gradients reference.pdf): the
/// stitching function and midpoint exponents, the gradient's placement from start, length and angle, gradient strokes
/// as shading patterns and gradient text.
/// </summary>
public sealed class GradientTests
{
    private static readonly ResolvedColor Red = new(ColorSpaceKind.Cmyk, [0, 1, 1, 0]);
    private static readonly ResolvedColor Blue = new(ColorSpaceKind.Cmyk, [1, 1, 0, 0]);
    private static readonly ResolvedColor Black = new(ColorSpaceKind.Cmyk, [0, 0, 0, 1]);
    private static readonly ResolvedColor Cyan = new(ColorSpaceKind.Cmyk, [1, 0, 0, 0]);

    [Fact]
    public void Two_stops_are_one_linear_function()
    {
        var shading = GradientShading.From(new ResolvedGradient(GradientType.Linear, [new(Red, 0, 0.5, null), new(Blue, 1, 0.5, null)]));

        Assert.Equal(
            "<</ShadingType 2 /Coords [0 0 1 0] /ColorSpace /DeviceCMYK /Domain [0 1] /Extend [true true] /Function <</FunctionType 3 /Domain [0 1] /Functions [<</FunctionType 2 /Domain [0 1] /C0 [0 1 1 0] /C1 [1 1 0 0] /N 1>>] /Bounds [] /Encode [0 1]>>>>",
            shading.ToPdf());
    }

    [Fact]
    public void Four_stops_stitch_three_functions_at_the_inner_locations()
    {
        var yellow = new ResolvedColor(ColorSpaceKind.Cmyk, [0, 0, 1, 0]);
        var green = new ResolvedColor(ColorSpaceKind.Cmyk, [1, 0, 1, 0]);
        var shading = GradientShading.From(new ResolvedGradient(GradientType.Linear,
            [new(Red, 0, 0.5, null), new(yellow, 0.33, 0.5, null), new(green, 0.66, 0.5, null), new(Blue, 1, 0.5, null)]));

        Assert.Equal([0.33, 0.66], shading.Bounds);
        Assert.Equal(3, shading.Segments.Count);
        Assert.Equal([0, 0, 1, 0], shading.Segments[1].C0);
        Assert.Contains("/Bounds [0.33 0.66] /Encode [0 1 0 1 0 1]", shading.ToPdf(), StringComparison.Ordinal);
    }

    [Fact]
    public void Midpoints_are_exponents_and_below_half_the_segment_runs_backwards()
    {
        // InDesign: black to cyan at 20% is /C0 cyan /C1 black /Encode [1 0] /N 3.10628; at 80% forwards, same N.
        var at20 = GradientShading.From(new ResolvedGradient(GradientType.Linear, [new(Black, 0, 0.5, null), new(Cyan, 1, 0.2, null)]));
        var at80 = GradientShading.From(new ResolvedGradient(GradientType.Linear, [new(Black, 0, 0.5, null), new(Cyan, 1, 0.8, null)]));

        Assert.Equal(new GradientShading.Segment(Cyan.Components, Black.Components, at20.Segments[0].Exponent, Reversed: true), at20.Segments[0], SegmentComparer.Instance);
        Assert.Equal(3.10628, at20.Segments[0].Exponent, 4);
        Assert.Contains("/N 3.1063>>] /Bounds [] /Encode [1 0]", at20.ToPdf(), StringComparison.Ordinal);
        Assert.False(at80.Segments[0].Reversed);
        Assert.Equal(Black.Components, at80.Segments[0].C0);
        Assert.Equal(3.10628, at80.Segments[0].Exponent, 4);
        // Half and half at the midpoint.
        Assert.Equal(0.5, GradientShading.Curve(0.2, 0.2), 6);
        Assert.Equal(0.5, GradientShading.Curve(0.8, 0.8), 6);
    }

    [Fact]
    public void Stops_inside_the_range_hold_their_colour_to_the_ends()
    {
        var shading = GradientShading.From(new ResolvedGradient(GradientType.Radial, [new(Red, 0.25, 0.5, null), new(Blue, 0.75, 0.5, null)]));

        Assert.True(shading.Radial);
        Assert.Equal([0.25, 0.75], shading.Bounds);
        Assert.Equal(shading.Segments[0].C0, shading.Segments[0].C1);
        Assert.Equal(Blue.Components, shading.Segments[2].C0);
        Assert.StartsWith("<</ShadingType 3 /Coords [0 0 0 0 0 1]", shading.ToPdf(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_spot_inks_mix_like_their_alternates()
    {
        // 03-color-gradients p5: Spot Orange (0 70 100 0) to Spot Teal (90 0 40 0), a DeviceN of both inks in the reference.
        var orange = new ResolvedColor(ColorSpaceKind.Cmyk, [0, 0.7, 1, 0]);
        var teal = new ResolvedColor(ColorSpaceKind.Cmyk, [0.9, 0, 0.4, 0]);
        var shading = GradientShading.From(new ResolvedGradient(GradientType.Linear, [new(orange, 0, 0.5, "Spot Orange"), new(teal, 1, 0.5, "Spot Teal")]));

        Assert.Equal(GradientShading.MixedInkPieces, shading.Segments.Count);
        // Halfway, both inks at 50%: yellow is 1 - (1 - 0.5)(1 - 0.2) = 0.6, not the 0.7 of a CMYK blend.
        var middle = shading.Segments[(GradientShading.MixedInkPieces / 2) - 1].C1;
        Assert.Equal(0.45, middle[0], 6);
        Assert.Equal(0.35, middle[1], 6);
        Assert.Equal(0.6, middle[2], 6);
        Assert.Equal(orange.Components, shading.Segments[0].C0);
        Assert.Equal(teal.Components, shading.Segments[^1].C1);
    }

    [Fact]
    public void Mixed_colour_spaces_blend_in_rgb()
    {
        var rgb = new ResolvedColor(ColorSpaceKind.Rgb, [0, 1, 0]);
        var shading = GradientShading.From(new ResolvedGradient(GradientType.Linear, [new(Black, 0, 0.5, null), new(rgb, 1, 0.5, null)]));

        Assert.Equal(ColorSpaceKind.Rgb, shading.Space);
        Assert.Equal([0, 0, 0], shading.Segments[0].C0);
        Assert.Contains("/ColorSpace /DeviceRGB", shading.ToPdf(), StringComparison.Ordinal);
    }

    [Theory]
    // 03-color-gradients p1: start (217.67, 201), length 166.67; InDesign's matrix in y-up page space is
    // [144.34 83.33 83.33 -144.34 ...] at 30 degrees, i.e. [144.34 -83.33 83.33 144.34] in y-down inner coordinates.
    [InlineData(30, 144.3376, -83.3333, 83.3333, 144.3376)]
    [InlineData(0, 166.6667, 0, 0, 166.6667)]
    [InlineData(90, 0, -166.6667, 166.6667, 0)]
    public void Start_length_and_angle_place_the_gradient_in_inner_coordinates(double angle, double a, double b, double c, double d)
    {
        var m = IdmlRenderer.GradientSpace(GradientType.Linear, new Point(217.6667, 201), 166.6667, angle, new Rect(217.6667, 65, 166.6667, 136));

        Assert.Equal(a, m.A, 3);
        Assert.Equal(b, m.B, 3);
        Assert.Equal(c, m.C, 3);
        Assert.Equal(d, m.D, 3);
        Assert.Equal(217.6667, m.Tx, 3);
        Assert.Equal(201, m.Ty, 3);
    }

    [Fact]
    public void Without_a_length_the_gradient_spans_the_bounds()
    {
        var bounds = new Rect(10, 20, 200, 100);

        var linear = IdmlRenderer.GradientSpace(GradientType.Linear, default, -1, 0, bounds);
        Assert.Equal(new CoreIdml.Matrix(200, 0, 0, 200, 10, 70), linear);
        // At 90 degrees it runs from the bottom up.
        var vertical = IdmlRenderer.GradientSpace(GradientType.Linear, default, 0, 90, bounds);
        Assert.Equal(100, -vertical.B, 6);
        Assert.Equal(120, vertical.Ty, 6);
        var radial = IdmlRenderer.GradientSpace(GradientType.Radial, default, -1, 0, bounds);
        Assert.Equal(new CoreIdml.Matrix(100, 0, 0, 100, 110, 70), radial);
    }

    [Fact]
    public void Gradient_fills_strokes_and_text_are_written_as_shadings_and_patterns()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var red = document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
        var blue = document.Swatches.AddCmyk("Blue", 100, 100, 0, 0);
        var gradient = document.Swatches.AddGradient("Red to blue", GradientType.Linear, new GradientStop(red, 0), new GradientStop(blue, 100));
        document.Pages[0].AddRectangle(new Rect(100, 100, 200, 100), new ShapeStyle { Fill = gradient, Stroke = Swatch.None });
        document.Pages[0].AddRectangle(new Rect(100, 300, 200, 100), new ShapeStyle { Fill = Swatch.None, Stroke = gradient, StrokeWeight = 10 });
        var text = document.Pages[0].AddText(new Rect(100, 500, 300, 60), "Gradient");
        text.Story.Element.Descendants("CharacterStyleRange").Single().SetAttributeValue("FillColor", gradient.Reference);
        Assert.Equal(GradientType.Linear, document.Swatches.ResolveGradient(gradient.Reference)?.Type);

        var bytes = TestSupport.Export(document, new PdfExportOptions { Fonts = TestSupport.Fonts(), CompressContent = false });

        using var pdf = PdfDocument.Open(bytes);
        var page = pdf.GetPage(1);
        var resources = (DictionaryToken)Resolve(pdf, page.Dictionary.Data["Resources"]);
        var shadings = (DictionaryToken)Resolve(pdf, resources.Data["Shading"]);
        var shading = (DictionaryToken)Resolve(pdf, shadings.Data.Values.Single());
        Assert.Equal(2, ((NumericToken)shading.Data["ShadingType"]).Int);
        var function = (DictionaryToken)shading.Data["Function"];
        Assert.Equal(3, ((NumericToken)function.Data["FunctionType"]).Int);
        var patterns = (DictionaryToken)Resolve(pdf, resources.Data["Pattern"]);
        Assert.Equal(2, patterns.Data.Count);
        // The stroke's pattern, in default (y-up) space: the rectangle's left edge, across its 200 pt width.
        var matrices = patterns.Data.Values.Select(value => ((ArrayToken)((DictionaryToken)Resolve(pdf, value)).Data["Matrix"]).Data.Select(n => ((NumericToken)n).Double).ToArray()).ToList();
        var height = PageSizes.A4.Height;
        Assert.Contains(matrices, m => Math.Abs(m[0] - 200) < 1e-3 && Math.Abs(m[3] + 200) < 1e-3 && Math.Abs(m[4] - 100) < 1e-3 && Math.Abs(m[5] - (height - 350)) < 1e-3);

        var content = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains(" sh", content, StringComparison.Ordinal);
        Assert.Contains("/Pattern CS", content, StringComparison.Ordinal);
        Assert.Contains("/Pattern cs", content, StringComparison.Ordinal);
        Assert.Equal("Gradient", page.GetWords().Select(word => word.Text).Single());
    }

    [Fact]
    public void A_gradient_fill_shades_from_the_first_to_the_last_colour()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var red = document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
        var blue = document.Swatches.AddCmyk("Blue", 100, 100, 0, 0);
        var gradient = document.Swatches.AddGradient("Red to blue", GradientType.Linear, new GradientStop(red, 0), new GradientStop(blue, 100));
        document.Pages[0].AddRectangle(new Rect(100, 100, 200, 100), new ShapeStyle { Fill = gradient, Stroke = Swatch.None });

        // 72 dpi: one pixel per point.
        var image = Rasterizer.Render(TestSupport.Export(document), 0, 72);
        var (leftRed, _, leftBlue) = Pixel(image, 103, 150);
        var (rightRed, _, rightBlue) = Pixel(image, 297, 150);
        var (outsideRed, outsideGreen, outsideBlue) = Pixel(image, 320, 150);
        Assert.True(leftRed > 180 && leftBlue < 100, $"left {leftRed},{leftBlue}");
        Assert.True(rightBlue > 100 && rightRed < 100, $"right {rightRed},{rightBlue}");
        // Clipped to the rectangle.
        Assert.True(outsideRed > 240 && outsideGreen > 240 && outsideBlue > 240);
    }

    private static (int R, int G, int B) Pixel(Imaging.RasterImage image, int x, int y)
    {
        var i = ((y * image.Width) + x) * image.Channels;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    private static IToken Resolve(PdfDocument pdf, IToken token) =>
        token is IndirectReferenceToken reference ? pdf.Structure.GetObject(reference.Data).Data : token;

    private sealed class SegmentComparer : IEqualityComparer<GradientShading.Segment>
    {
        public static readonly SegmentComparer Instance = new();

        public bool Equals(GradientShading.Segment? x, GradientShading.Segment? y) =>
            x is not null && y is not null && x.C0.SequenceEqual(y.C0) && x.C1.SequenceEqual(y.C1) && x.Exponent == y.Exponent && x.Reversed == y.Reversed;

        public int GetHashCode(GradientShading.Segment obj) => 0;
    }
}
