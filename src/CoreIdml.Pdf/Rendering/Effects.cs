using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;

namespace CoreIdml.Pdf.Rendering;

/// <summary>What an InDesign effect applies to: the whole object, or only its fill, stroke or content (text or graphic).</summary>
internal enum EffectTarget
{
    Object,
    Fill,
    Stroke,
    Content,
}

/// <summary>Blending of one target (<c>BlendingSetting</c>): blend mode, opacity (0..1), isolate blending, knockout group.</summary>
internal sealed record Blending(string Mode, double Opacity, bool Isolate, bool Knockout)
{
    public static readonly Blending Normal = new("Normal", 1, false, false);

    public bool IsNormal => this == Normal;
}

/// <summary>Which soft effect a <see cref="SoftEffect"/> is: drawn under the item (shadow, glow) or over it, inside its shape.</summary>
internal enum SoftEffectKind
{
    DropShadow,
    OuterGlow,
    InnerShadow,
    InnerGlow,
}

/// <summary>
/// A shadow or glow: its colour, blend mode and opacity (0..1), size (blur), spread (drop shadow, outer glow) or choke
/// (inner shadow, inner glow) as a fraction, and offset in points (y down). <see cref="CenterSourced"/> is an inner glow
/// from the centre; <see cref="KnockedOut"/> removes a drop shadow under the object.
/// </summary>
internal sealed record SoftEffect(
    SoftEffectKind Kind, ResolvedColor Color, string BlendMode, double Opacity, double Size, double Spread, double OffsetX, double OffsetY,
    bool CenterSourced = false, bool KnockedOut = false)
{
    public bool Under => Kind is SoftEffectKind.DropShadow or SoftEffectKind.OuterGlow;
}

/// <summary>Basic feather: the width in points and the corner type (Sharp, Rounded, Diffusion).</summary>
internal sealed record Feather(double Width, string CornerType);

/// <summary>Directional feather: a width in points for each side of the item's bounds.</summary>
internal sealed record DirectionalFeather(double Left, double Right, double Top, double Bottom);

/// <summary>
/// Gradient feather: an opacity gradient (stops with opacity 0..1, location and midpoint 0..1) placed like a gradient
/// fill (start, length and angle in the item's inner coordinates; a length of 0 spans the item).
/// </summary>
internal sealed record GradientFeather(GradientType Type, Point Start, double Length, double Angle, IReadOnlyList<(double Opacity, double Location, double Midpoint)> Stops);

/// <summary>The transparency and effects of one target of an item, resolved through its object style.</summary>
internal sealed record EffectSet(
    Blending Blending, IReadOnlyList<SoftEffect> SoftEffects, Feather? Feather, DirectionalFeather? DirectionalFeather,
    GradientFeather? GradientFeather, IReadOnlyList<string> NotDrawn)
{
    public static readonly EffectSet None = new(Blending.Normal, [], null, null, null, []);

    public bool IsNone => Blending.IsNormal && SoftEffects.Count == 0 && Feather is null && DirectionalFeather is null && GradientFeather is null;

    /// <summary>Whether an effect needs the alpha of the item's shape (a raster effect).</summary>
    public bool NeedsShape => SoftEffects.Count > 0 || Feather is not null || DirectionalFeather is not null;
}

/// <summary>
/// Reads an item's transparency settings: <c>TransparencySetting</c> (the object), <c>FillTransparencySetting</c>,
/// <c>StrokeTransparencySetting</c> and <c>ContentTransparencySetting</c>, each holding <c>BlendingSetting</c>,
/// <c>DropShadowSetting</c>, <c>FeatherSetting</c>, <c>InnerShadowSetting</c>, <c>OuterGlowSetting</c>,
/// <c>InnerGlowSetting</c>, <c>BevelAndEmbossSetting</c>, <c>SatinSetting</c>, <c>DirectionalFeatherSetting</c> and
/// <c>GradientFeatherSetting</c>. Each attribute comes from the item, else its object style chain when the style applies
/// that effect (<c>ObjectStyle&lt;Target&gt;EffectsCategorySettings@Enable&lt;Effect&gt;</c>, the most specific style
/// that says decides), else InDesign's default. The defaults are those every sample's
/// <c>TransparencyDefaultContainerObject</c> (<c>Resources/Preferences.xml</c>) lists: a drop shadow is Multiply 75%,
/// 7 pt right and down, size 5; an inner shadow Multiply 75%, 7 pt at 120°, size 7; glows Screen 75%, size 7; a feather
/// 9 pt with diffusion corners. A colour of "n" (no swatch) is black for shadows (04-fx-shadows-glows p1 draws the
/// default shadow in <c>0 0 0 1 k</c>) and white for glows (p3: the default centre-sourced inner glow is
/// <c>0 0 0 0 k</c>; the outer glow is assumed the same).
/// </summary>
internal sealed class EffectReader(IdmlDocument document, AttributeResolver resolver)
{
    private static readonly string[] Containers = ["TransparencySetting", "FillTransparencySetting", "StrokeTransparencySetting", "ContentTransparencySetting"];
    private static readonly ResolvedColor Black = new(ColorSpaceKind.Cmyk, [0, 0, 0, 1]);
    private static readonly ResolvedColor White = new(ColorSpaceKind.Cmyk, [0, 0, 0, 0]);

    private double? globalLightAngle;

    /// <summary>The effects of <paramref name="target"/> of an item (or of a placed graphic, which has only the object target).</summary>
    public EffectSet Read(XElement item, EffectTarget target)
    {
        var chain = resolver.ObjectChain(item);
        var container = Containers[(int)target];
        if (!chain.Any(element => element.Element(container) is not null))
        {
            return EffectSet.None;
        }
        var category = "ObjectStyle" + target + "EffectsCategorySettings";
        string? Get(string setting, string enable, string name)
        {
            var count = StylesApply(chain, category, enable) ? chain.Count : 1;
            for (var i = 0; i < count; i++)
            {
                if (chain[i].Element(container)?.Element(setting)?.Attribute(name)?.Value is { } value)
                {
                    return value;
                }
            }
            return null;
        }
        double Number(string setting, string enable, string name, double fallback) => Values.Number(Get(setting, enable, name), fallback);

        var blending = new Blending(
            Get("BlendingSetting", "EnableTransparency", "BlendMode") ?? "Normal",
            Math.Clamp(Number("BlendingSetting", "EnableTransparency", "Opacity", 100) / 100, 0, 1),
            Get("BlendingSetting", "EnableTransparency", "IsolateBlending") == "true",
            Get("BlendingSetting", "EnableTransparency", "KnockoutGroup") == "true");

        var soft = new List<SoftEffect>();
        if (Get("DropShadowSetting", "EnableDropShadow", "Mode") == "Drop")
        {
            string? Shadow(string name) => Get("DropShadowSetting", "EnableDropShadow", name);
            var (x, y) = (Values.Number(Shadow("XOffset"), 7), Values.Number(Shadow("YOffset"), 7));
            if (Shadow("UseGlobalLight") == "true")
            {
                (x, y) = LightOffset(GlobalLightAngle(), Math.Sqrt((x * x) + (y * y)));
            }
            soft.Add(new SoftEffect(SoftEffectKind.DropShadow, Color(Shadow("EffectColor"), Black), Shadow("BlendMode") ?? "Multiply",
                Values.Number(Shadow("Opacity"), 75) / 100, Values.Number(Shadow("Size"), 5), Values.Number(Shadow("Spread"), 0) / 100, x, y,
                KnockedOut: Shadow("KnockedOut") != "false"));
        }
        if (Get("OuterGlowSetting", "EnableOuterGlow", "Applied") == "true")
        {
            string? Glow(string name) => Get("OuterGlowSetting", "EnableOuterGlow", name);
            soft.Add(new SoftEffect(SoftEffectKind.OuterGlow, Color(Glow("EffectColor"), White), Glow("BlendMode") ?? "Screen",
                Values.Number(Glow("Opacity"), 75) / 100, Values.Number(Glow("Size"), 7), Values.Number(Glow("Spread"), 0) / 100, 0, 0));
        }
        if (Get("InnerShadowSetting", "EnableInnerShadow", "Applied") == "true")
        {
            string? Shadow(string name) => Get("InnerShadowSetting", "EnableInnerShadow", name);
            var angle = Shadow("UseGlobalLight") == "true" ? GlobalLightAngle() : Values.Number(Shadow("Angle"), 120);
            var (x, y) = LightOffset(angle, Values.Number(Shadow("Distance"), 7));
            soft.Add(new SoftEffect(SoftEffectKind.InnerShadow, Color(Shadow("EffectColor"), Black), Shadow("BlendMode") ?? "Multiply",
                Values.Number(Shadow("Opacity"), 75) / 100, Values.Number(Shadow("Size"), 7), Values.Number(Shadow("ChokeAmount"), 0) / 100, x, y));
        }
        if (Get("InnerGlowSetting", "EnableInnerGlow", "Applied") == "true")
        {
            string? Glow(string name) => Get("InnerGlowSetting", "EnableInnerGlow", name);
            soft.Add(new SoftEffect(SoftEffectKind.InnerGlow, Color(Glow("EffectColor"), White), Glow("BlendMode") ?? "Screen",
                Values.Number(Glow("Opacity"), 75) / 100, Values.Number(Glow("Size"), 7), Values.Number(Glow("Spread"), 0) / 100, 0, 0,
                CenterSourced: Glow("Source") == "CenterSourced"));
        }

        Feather? feather = null;
        if (Get("FeatherSetting", "EnableFeather", "Mode") == "Standard")
        {
            feather = new Feather(Number("FeatherSetting", "EnableFeather", "Width", 9), Get("FeatherSetting", "EnableFeather", "CornerType") ?? "Diffusion");
        }
        DirectionalFeather? directional = null;
        if (Get("DirectionalFeatherSetting", "EnableDirectionalFeather", "Applied") == "true")
        {
            double Width(string side) => Math.Max(0, Number("DirectionalFeatherSetting", "EnableDirectionalFeather", side + "Width", 0));
            directional = new DirectionalFeather(Width("Left"), Width("Right"), Width("Top"), Width("Bottom"));
            if (directional is { Left: 0, Right: 0, Top: 0, Bottom: 0 })
            {
                directional = null;
            }
        }
        GradientFeather? gradient = null;
        if (Get("GradientFeatherSetting", "EnableGradientFeather", "Applied") == "true")
        {
            string? Fade(string name) => Get("GradientFeatherSetting", "EnableGradientFeather", name);
            var start = Values.List(Fade("GradientStart"));
            gradient = new GradientFeather(
                Fade("Type") == "Radial" ? GradientType.Radial : GradientType.Linear,
                start.Length == 2 ? new Point(start[0], start[1]) : default,
                Values.Number(Fade("Length"), 0),
                Values.Number(Fade("Angle"), 0),
                FeatherStops(chain, container));
        }

        var notDrawn = new List<string>();
        if (Get("BevelAndEmbossSetting", "EnableBevelEmboss", "Applied") == "true")
        {
            notDrawn.Add("bevel and emboss");
        }
        if (Get("SatinSetting", "EnableSatin", "Applied") == "true")
        {
            notDrawn.Add("satin");
        }
        return new EffectSet(blending, soft, feather, directional, gradient, notDrawn);
    }

    /// <summary>
    /// The opacity stops of a gradient feather: <c>OpacityGradientStop</c> children (<c>Opacity</c>, <c>Location</c>,
    /// <c>Midpoint</c>, in percent; from memory, the samples have none), else opaque at the start to transparent at the
    /// end, which is what 04-fx-feathers-other p4 exports (a DeviceGray shading from 1 to 0).
    /// </summary>
    private static List<(double Opacity, double Location, double Midpoint)> FeatherStops(IReadOnlyList<XElement> chain, string container)
    {
        var stops = chain.Select(element => element.Element(container)?.Element("GradientFeatherSetting"))
            .Select(setting => setting?.Elements("OpacityGradientStop").ToList())
            .FirstOrDefault(list => list is { Count: >= 2 });
        if (stops is null)
        {
            return [(1, 0, 0.5), (0, 1, 0.5)];
        }
        return stops
            .Select(stop => (Values.Number(stop.Attribute("Opacity")?.Value, 100) / 100, Values.Number(stop.Attribute("Location")?.Value, 0) / 100, Values.Number(stop.Attribute("Midpoint")?.Value, 50) / 100))
            .OrderBy(stop => stop.Item2)
            .ToList();
    }

    /// <summary>Whether the object styles of a chain apply an effect (see <see cref="AttributeResolver"/>'s category flags).</summary>
    private static bool StylesApply(IReadOnlyList<XElement> chain, string category, string enable)
    {
        for (var i = 1; i < chain.Count; i++)
        {
            if (chain[i].Element(category)?.Attribute(enable)?.Value is { } value)
            {
                return value != "false";
            }
        }
        return true;
    }

    /// <summary>
    /// The offset (y down) of a shadow cast away from light at <paramref name="angle"/> degrees (counter-clockwise from
    /// the right): an inner shadow at 135° and distance 8.49 is the shape's outside moved 6 pt right and 6 pt down, so
    /// it darkens the top and left edges inside (04-fx-shadows-glows p2).
    /// </summary>
    private static (double X, double Y) LightOffset(double angle, double distance)
    {
        var radians = angle * Math.PI / 180;
        return (-distance * Math.Cos(radians), distance * Math.Sin(radians));
    }

    /// <summary>The document's global light angle (<c>TransparencyPreference@GlobalLightAngle</c>, 120° by default).</summary>
    private double GlobalLightAngle() => globalLightAngle ??= Values.Number(
        document.Package.TryGetXml("Resources/Preferences.xml")?.Root?.Element("TransparencyPreference")?.Attribute("GlobalLightAngle")?.Value, 120);

    private ResolvedColor Color(string? reference, ResolvedColor fallback) =>
        reference is null or "n" ? fallback : document.Swatches.ResolveColor(reference) ?? fallback;
}
