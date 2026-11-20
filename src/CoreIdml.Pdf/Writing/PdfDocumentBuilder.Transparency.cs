using System.Globalization;
using System.Text;
using static CoreIdml.Pdf.Writing.PdfObjectWriter;

namespace CoreIdml.Pdf.Writing;

/// <summary>
/// Transparency as InDesign 20 writes it (04-fx-opacity-blend, 04-fx-shadows-glows, 04-fx-feathers-other reference
/// PDFs):
/// <list type="bullet">
/// <item>an item with opacity or a blend mode is a transparency group (a Form XObject with <c>/Group &lt;&lt;/S
/// /Transparency /CS /DeviceCMYK /I false /K false&gt;&gt;</c>) painted under an ExtGState that holds <c>/ca</c>,
/// <c>/CA</c> and <c>/BM</c>, so its fill does not show through its own stroke;</item>
/// <item>soft effects (shadows, glows, feathers) are luminosity soft masks (<c>/SMask &lt;&lt;/S /Luminosity /G
/// form&gt;&gt;</c>) whose form draws an 8-bit DeviceGray image of the effect's alpha; a gradient feather's form
/// paints a DeviceGray shading instead. Shadows and glows fill a rectangle in the effect colour through such a
/// mask.</item>
/// </list>
/// The forms use the document's shared resource dictionary, like the pages.
/// </summary>
internal sealed partial class PdfDocumentBuilder
{
    private readonly List<(string Name, int Number)> effectXObjects = [];
    private readonly Dictionary<string, string> maskImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> softMasks = new(StringComparer.Ordinal);
    private int transparencyGroups;

    /// <summary>
    /// The colour space transparency groups blend in: <c>/DeviceCMYK</c> (InDesign's Document CMYK blending space,
    /// <c>TransparencyPreference@BlendingSpace</c>, used by every sample) or <c>/DeviceRGB</c>.
    /// </summary>
    public string BlendSpace { get; set; } = "/DeviceCMYK";

    /// <summary>
    /// The resource name of an ExtGState: fill and stroke alpha, blend mode (a PDF blend mode name, which InDesign's
    /// <c>BlendMode</c> values are) and an optional soft mask (the object number of a mask dictionary).
    /// </summary>
    public string GraphicsState(double fill, double stroke, string blendMode = "Normal", int? softMask = null)
    {
        var key = Number(fill) + "/" + Number(stroke) + "/" + blendMode + "/" + softMask?.ToString(CultureInfo.InvariantCulture);
        if (!graphicStates.TryGetValue(key, out var name))
        {
            name = "GS" + (graphicStates.Count + 1).ToString(CultureInfo.InvariantCulture);
            var number = writer.Reserve();
            var blend = blendMode == "Normal" ? string.Empty : " /BM " + Name(blendMode);
            var mask = softMask is { } maskNumber ? " /SMask " + Ref(maskNumber) : string.Empty;
            writer.WriteObject(number, $"<</Type /ExtGState /ca {Number(fill)} /CA {Number(stroke)}{blend}{mask}>>");
            graphicStates[key] = name;
            graphicStateObjects[name] = number;
        }
        return name;
    }

    /// <summary>
    /// Writes <paramref name="content"/> as a transparency group (a Form XObject in <see cref="BlendSpace"/>) clipped
    /// to <paramref name="box"/>, both in the user space it is painted in, and returns its resource name.
    /// </summary>
    public string TransparencyGroup(ContentBuilder content, CoreIdml.Rect box, bool isolated, bool knockout)
    {
        var group = $"<</Type /Group /S /Transparency /CS {BlendSpace} /I {(isolated ? "true" : "false")} /K {(knockout ? "true" : "false")}>>";
        var name = "Tg" + (++transparencyGroups).ToString(CultureInfo.InvariantCulture);
        effectXObjects.Add((name, Form(content, Box(box, CoreIdml.Matrix.Identity), group)));
        return name;
    }

    /// <summary>
    /// The resource name of an 8-bit DeviceGray image (row 0 at the top) for a soft mask; identical images are written
    /// once (keyed by an FNV-1a hash of the samples, see <see cref="SubsetTag"/> for why not a cryptographic one).
    /// </summary>
    public string MaskImage(byte[] samples, int width, int height)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}:{Fnv(samples):X16}");
        if (!maskImages.TryGetValue(key, out var name))
        {
            name = "Mk" + (maskImages.Count + 1).ToString(CultureInfo.InvariantCulture);
            var number = writer.Reserve();
            writer.WriteStream(number, $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8", samples, compress: true);
            effectXObjects.Add((name, number));
            maskImages[key] = name;
        }
        return name;
    }

    /// <summary>
    /// A luminosity soft mask (its dictionary's object number) that shows the gray image <paramref name="image"/>
    /// placed by <paramref name="placement"/> (the image's unit square, first row at v = 1, to the space where the
    /// ExtGState is set) and hides everything outside it.
    /// </summary>
    public int ImageSoftMask(string image, CoreIdml.Matrix placement)
    {
        var key = image + "@" + Matrix(placement);
        if (!softMasks.TryGetValue(key, out var number))
        {
            var content = new ContentBuilder();
            content.Save().Transform(placement).XObject(image).Restore();
            number = LuminosityMask(content, Box(new CoreIdml.Rect(0, 0, 1, 1), placement));
            softMasks[key] = number;
        }
        return number;
    }

    /// <summary>
    /// A luminosity soft mask painting a DeviceGray <paramref name="shading"/> through <paramref name="space"/> (unit
    /// gradient space to the space where the ExtGState is set), limited to <paramref name="area"/> (the box of the
    /// rectangle <paramref name="area"/> through <paramref name="toMask"/>): a gradient feather.
    /// </summary>
    public int ShadingSoftMask(GradientShading shading, CoreIdml.Matrix space, CoreIdml.Rect area, CoreIdml.Matrix toMask)
    {
        var content = new ContentBuilder();
        content.Save().Transform(space).PaintShading(Shading(shading)).Restore();
        return LuminosityMask(content, Box(area, toMask));
    }

    /// <summary>The box of <paramref name="rect"/> through <paramref name="matrix"/>, as a PDF rectangle.</summary>
    private static string Box(CoreIdml.Rect rect, CoreIdml.Matrix matrix)
    {
        var corners = new[] { new CoreIdml.Point(rect.Left, rect.Top), new CoreIdml.Point(rect.Right, rect.Top), new CoreIdml.Point(rect.Left, rect.Bottom), new CoreIdml.Point(rect.Right, rect.Bottom) }
            .Select(matrix.Apply).ToList();
        return $"[{Number(corners.Min(p => p.X))} {Number(corners.Min(p => p.Y))} {Number(corners.Max(p => p.X))} {Number(corners.Max(p => p.Y))}]";
    }

    private static string Matrix(CoreIdml.Matrix m) => $"[{Number(m.A)} {Number(m.B)} {Number(m.C)} {Number(m.D)} {Number(m.Tx)} {Number(m.Ty)}]";

    /// <summary>A mask dictionary whose group (a DeviceGray form) is <paramref name="content"/>; black outside it.</summary>
    private int LuminosityMask(ContentBuilder content, string box)
    {
        var form = Form(content, box, "<</Type /Group /S /Transparency /CS /DeviceGray>>");
        var number = writer.Reserve();
        writer.WriteObject(number, $"<</Type /Mask /S /Luminosity /G {Ref(form)} /BC [0]>>");
        return number;
    }

    /// <summary>Writes a Form XObject with a group attribute dictionary and returns its object number.</summary>
    private int Form(ContentBuilder content, string box, string group)
    {
        var number = writer.Reserve();
        writer.WriteStream(number, $"/Type /XObject /Subtype /Form /BBox {box} /Group {group} /Resources {Ref(resourcesObject)}", content.ToBytes(), compress);
        return number;
    }

    private static ulong Fnv(byte[] data)
    {
        const ulong prime = 1099511628211;
        var hash = 14695981039346656037;
        foreach (var value in data)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }
}
