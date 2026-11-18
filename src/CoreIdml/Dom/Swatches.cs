using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>A reference to a swatch (colour, tint, gradient or None) usable as fill or stroke.</summary>
public sealed record Swatch(string Reference, string Name)
{
    public static readonly Swatch None = new("Swatch/None", "None");
    public static readonly Swatch Black = new("Color/Black", "Black");
    public static readonly Swatch Paper = new("Color/Paper", "Paper");
    public static readonly Swatch Registration = new("Color/Registration", "Registration");

    public override string ToString() => Name;
}

/// <summary>Swatches of the document (Resources/Graphic.xml).</summary>
public sealed class SwatchCatalog
{
    private static readonly HashSet<string> SwatchKinds = ["Color", "Swatch", "Tint", "Gradient", "MixedInk", "MixedInkGroup"];

    private readonly IdmlDocument document;

    internal SwatchCatalog(IdmlDocument document)
    {
        this.document = document;
    }

    public IReadOnlyList<Swatch> All =>
        document.GraphicRoot.Elements()
            .Where(element => SwatchKinds.Contains(element.Name.LocalName))
            .Select(element => new Swatch(element.Attribute("Self")?.Value ?? string.Empty, element.Attribute("Name")?.Value ?? string.Empty))
            .ToList();

    /// <summary>Finds a swatch by name ("Black", "Brand Red") or reference ("Color/Black").</summary>
    public Swatch? Find(string name) =>
        All.FirstOrDefault(swatch => swatch.Name == name || swatch.Reference == name || swatch.Reference == "Color/" + IdmlXml.EscapeSelfName(name));

    /// <summary>Adds (or updates) a process CMYK colour; values are percentages 0-100.</summary>
    public Swatch AddCmyk(string name, double cyan, double magenta, double yellow, double black) =>
        AddColor(name, "CMYK", IdmlXml.FormatList(Clamp(cyan, 100), Clamp(magenta, 100), Clamp(yellow, 100), Clamp(black, 100)));

    /// <summary>Adds (or updates) a process RGB colour; values 0-255.</summary>
    public Swatch AddRgb(string name, double red, double green, double blue) =>
        AddColor(name, "RGB", IdmlXml.FormatList(Clamp(red, 255), Clamp(green, 255), Clamp(blue, 255)));

    /// <summary>Adds (or updates) an RGB colour from "#RRGGBB".</summary>
    public Swatch AddHex(string name, string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var value = hex.TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new ArgumentException($"'{hex}' is not a #RRGGBB colour.", nameof(hex));
        }
        return AddRgb(name, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    private Swatch AddColor(string name, string space, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var reference = "Color/" + IdmlXml.EscapeSelfName(name);
        var root = document.GraphicRoot;
        var element = root.Elements("Color").FirstOrDefault(color => color.Attribute("Self")?.Value == reference);
        if (element is null)
        {
            element = new XElement("Color",
                new XAttribute("Self", reference),
                new XAttribute("Model", "Process"),
                new XAttribute("Space", space),
                new XAttribute("ColorValue", value),
                new XAttribute("ColorOverride", "Normal"),
                new XAttribute("AlternateSpace", "NoAlternateColor"),
                new XAttribute("AlternateColorValue", string.Empty),
                new XAttribute("Name", name),
                new XAttribute("ColorEditable", "true"),
                new XAttribute("ColorRemovable", "true"),
                new XAttribute("Visible", "true"),
                new XAttribute("SwatchCreatorID", "7937"));
            if (root.Elements("Color").LastOrDefault() is { } lastColor)
            {
                lastColor.AddAfterSelf(element);
            }
            else
            {
                root.AddFirst(element);
            }
        }
        else
        {
            element.SetAttributeValue("Space", space);
            element.SetAttributeValue("ColorValue", value);
        }
        return new Swatch(reference, name);
    }

    private static double Clamp(double value, double max) => Math.Round(Math.Clamp(value, 0, max), 4);

    /// <summary>
    /// The device colour a swatch reference paints with: process and spot colours (by their process values),
    /// tints (base colour scaled), Paper (white) and Registration. Returns null for None, gradients, mixed inks
    /// and unknown references.
    /// </summary>
    public ResolvedColor? ResolveColor(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference is "Swatch/None" or IdmlNames.NoReference)
        {
            return null;
        }
        var element = document.GraphicRoot.Elements().FirstOrDefault(e => e.Attribute("Self")?.Value == reference);
        switch (element?.Name.LocalName)
        {
            case "Color":
                return ParseColor(element);
            case "Tint" when element.Attribute("BaseColor")?.Value is { } baseReference && baseReference != reference:
                var tint = IdmlXml.ParseDouble(element.Attribute("TintValue")?.Value) ?? 100;
                return ResolveColor(baseReference)?.WithTint(tint);
            default:
                return null;
        }
    }

    private static ResolvedColor? ParseColor(XElement color)
    {
        var values = IdmlXml.ParseList(color.Attribute("ColorValue")?.Value);
        var space = color.Attribute("Space")?.Value;
        if (color.Attribute("ColorOverride")?.Value == "Specialregistration")
        {
            return new ResolvedColor(ColorSpaceKind.Cmyk, [1, 1, 1, 1]);
        }
        switch (space)
        {
            case "CMYK" when values.Length == 4:
                return new ResolvedColor(ColorSpaceKind.Cmyk, values.Select(v => Math.Clamp(v / 100, 0, 1)).ToArray());
            case "RGB" when values.Length == 3:
                return new ResolvedColor(ColorSpaceKind.Rgb, values.Select(v => Math.Clamp(v / 255, 0, 1)).ToArray());
            case "LAB" when values.Length == 3:
                return LabToRgb(values[0], values[1], values[2]);
            default:
                return null;
        }
    }

    /// <summary>CIE L*a*b* (D50, as InDesign stores it) to sRGB, without chromatic adaptation (approximate).</summary>
    private static ResolvedColor LabToRgb(double l, double a, double b)
    {
        double F(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - (4.0 / 29));
        var fy = (l + 16) / 116;
        var x = 0.9642 * F(fy + (a / 500));
        var y = 1.0 * F(fy);
        var z = 0.8249 * F(fy - (b / 200));
        double Gamma(double c) => Math.Clamp(c <= 0.0031308 ? 12.92 * c : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055, 0, 1);
        var r = Gamma((3.1338561 * x) - (1.6168667 * y) - (0.4906146 * z));
        var g = Gamma((-0.9787684 * x) + (1.9161415 * y) + (0.0334540 * z));
        var bl = Gamma((0.0719453 * x) - (0.2289914 * y) + (1.4052427 * z));
        return new ResolvedColor(ColorSpaceKind.Rgb, [r, g, bl]);
    }
}

/// <summary>A document layer.</summary>
public sealed class Layer : IdmlNode
{
    internal Layer(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    public string Name => GetAttribute("Name") ?? string.Empty;

    public bool Visible
    {
        get => GetAttribute("Visible") != "false";
        set => SetAttribute("Visible", value ? "true" : "false");
    }

    public bool Locked
    {
        get => GetAttribute("Locked") == "true";
        set => SetAttribute("Locked", value ? "true" : "false");
    }

    public bool Printable
    {
        get => GetAttribute("Printable") != "false";
        set => SetAttribute("Printable", value ? "true" : "false");
    }
}

/// <summary>Layers of the document (in designmap.xml).</summary>
public sealed partial class LayerCatalog
{
    private readonly IdmlDocument document;

    internal LayerCatalog(IdmlDocument document)
    {
        this.document = document;
    }

    public IReadOnlyList<Layer> All => document.DesignMap.Elements("Layer").Select(element => new Layer(document, element)).ToList();

    public Layer? Find(string name) => All.FirstOrDefault(layer => layer.Name == name);

    /// <summary>The layer new page items go to.</summary>
    public Layer? Active => All.FirstOrDefault(layer => layer.Id == document.DesignMap.Attribute("ActiveLayer")?.Value) ?? (All is [var first, ..] ? first : null);

    public void Activate(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        document.DesignMap.SetAttributeValue("ActiveLayer", layer.Id);
    }

    /// <summary>Adds a layer (or returns the existing one with that name).</summary>
    public Layer Add(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Find(name) is { } existing)
        {
            return existing;
        }
        var element = new XElement("Layer",
            new XAttribute("Self", document.NewId()),
            new XAttribute("Name", name),
            new XAttribute("Visible", "true"),
            new XAttribute("Locked", "false"),
            new XAttribute("IgnoreWrap", "false"),
            new XAttribute("ShowGuides", "true"),
            new XAttribute("LockGuides", "false"),
            new XAttribute("UI", "true"),
            new XAttribute("Expendable", "true"),
            new XAttribute("Printable", "true"),
            new XElement("Properties", new XElement("LayerColor", new XAttribute("type", "enumeration"), "LightBlue")));
        var anchor = document.DesignMap.Elements("Layer").LastOrDefault()
            ?? document.DesignMap.Elements(IdmlNames.PackagingNamespace + "Tags").LastOrDefault();
        if (anchor is null)
        {
            document.DesignMap.Add(element);
        }
        else
        {
            anchor.AddAfterSelf(element);
        }
        if (document.DesignMap.Attribute("ActiveLayer") is null)
        {
            document.DesignMap.SetAttributeValue("ActiveLayer", element.Attribute("Self")!.Value);
        }
        return new Layer(document, element);
    }
}
