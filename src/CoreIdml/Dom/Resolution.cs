using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>
/// Resolves effective attribute values through IDML's inheritance, the way InDesign applies them:
/// <list type="bullet">
/// <item>text: run (local formatting), character style and its BasedOn chain, paragraph (local), paragraph style and its BasedOn chain;</item>
/// <item>page items: the item, then its object style and the object style's BasedOn chain;</item>
/// <item>table cells: the cell, then its cell style chain.</item>
/// </list>
/// A value is looked up as an attribute first, then as a <c>Properties</c> child (<c>AppliedFont</c>, <c>Leading</c>,
/// <c>BasedOn</c>, ...). Read-only and cached per instance; create one per rendering pass.
/// </summary>
public sealed class AttributeResolver
{
    private const int MaxDepth = 32;

    private readonly IdmlDocument document;
    private readonly Dictionary<string, XElement?> styleCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<XElement>> chainCache = new(StringComparer.Ordinal);

    public AttributeResolver(IdmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
    }

    /// <summary>The raw value of <paramref name="name"/> on one element (attribute, else <c>Properties</c> child).</summary>
    public static string? Own(XElement element, string name)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Attribute(name)?.Value ?? element.Element(IdmlXml.Properties)?.Element(name)?.Value;
    }

    /// <summary>
    /// Elements that define the formatting of text at <paramref name="element"/> (a Content, Br, run or anything
    /// inside a run), most specific first.
    /// </summary>
    public IReadOnlyList<XElement> TextChain(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var run = element.AncestorsAndSelf("CharacterStyleRange").FirstOrDefault();
        var paragraph = element.AncestorsAndSelf("ParagraphStyleRange").FirstOrDefault();
        var chain = new List<XElement>(12);
        if (run is not null)
        {
            chain.Add(run);
            chain.AddRange(StyleChain(run.Attribute("AppliedCharacterStyle")?.Value));
        }
        if (paragraph is not null)
        {
            chain.Add(paragraph);
            chain.AddRange(StyleChain(paragraph.Attribute("AppliedParagraphStyle")?.Value));
        }
        return chain;
    }

    /// <summary>The effective value of a character or paragraph attribute for text at <paramref name="element"/>.</summary>
    public string? Text(XElement element, string name) => First(TextChain(element), name);

    /// <summary>The effective value of a paragraph attribute (paragraph and paragraph style chain only).</summary>
    public string? Paragraph(XElement paragraphRange, string name)
    {
        ArgumentNullException.ThrowIfNull(paragraphRange);
        return Own(paragraphRange, name) ?? First(StyleChain(paragraphRange.Attribute("AppliedParagraphStyle")?.Value), name);
    }

    /// <summary>The item, then its object style chain.</summary>
    public IReadOnlyList<XElement> ObjectChain(XElement item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var chain = new List<XElement>(4) { item };
        chain.AddRange(StyleChain(item.Attribute("AppliedObjectStyle")?.Value));
        return chain;
    }

    /// <summary>The effective value of a page item attribute (FillColor, StrokeWeight, corner options, ...).</summary>
    public string? Item(XElement item, string name) => First(ObjectChain(item), name);

    /// <summary>
    /// The effective value of a <c>TextFramePreference</c> attribute (TextColumnCount, FirstBaselineOffset, ...) or
    /// property (InsetSpacing) of a text frame: its own preference, then its object style's.
    /// </summary>
    public XElement? FramePreferenceSource(XElement textFrame, string name)
    {
        ArgumentNullException.ThrowIfNull(textFrame);
        return FramePreferenceSource(ObjectChain(textFrame), name);
    }

    public string? FramePreference(XElement textFrame, string name) =>
        FramePreferenceSource(textFrame, name) is { } preference ? Own(preference, name) : null;

    /// <summary><see cref="FramePreferenceSource(XElement, string)"/> for a frame's <see cref="ObjectChain"/> computed once.</summary>
    internal static XElement? FramePreferenceSource(IReadOnlyList<XElement> objectChain, string name)
    {
        foreach (var element in objectChain)
        {
            if (element.Element("TextFramePreference") is { } preference && Own(preference, name) is not null)
            {
                return preference;
            }
        }
        return null;
    }

    /// <summary><see cref="FramePreference(XElement, string)"/> for a frame's <see cref="ObjectChain"/> computed once.</summary>
    internal static string? FramePreference(IReadOnlyList<XElement> objectChain, string name) =>
        FramePreferenceSource(objectChain, name) is { } preference ? Own(preference, name) : null;

    /// <summary>The cell, then its cell style chain.</summary>
    public string? Cell(XElement cell, string name)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return Own(cell, name) ?? First(StyleChain(cell.Attribute("AppliedCellStyle")?.Value), name);
    }

    /// <summary>The table, then its table style chain.</summary>
    public string? Table(XElement table, string name)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Own(table, name) ?? First(StyleChain(table.Attribute("AppliedTableStyle")?.Value), name);
    }

    /// <summary>A style (paragraph, character, object, cell, table) and its BasedOn ancestors, most specific first.</summary>
    public IReadOnlyList<XElement> StyleChain(string? reference)
    {
        if (reference is null || reference == IdmlNames.NoReference)
        {
            return [];
        }
        if (chainCache.TryGetValue(reference, out var cached))
        {
            return cached;
        }
        var chain = new List<XElement>();
        var current = reference;
        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            var style = FindStyle(current);
            if (style is null || chain.Contains(style))
            {
                break;
            }
            chain.Add(style);
            var basedOn = style.Element(IdmlXml.Properties)?.Element("BasedOn");
            var value = basedOn?.Value;
            if (string.IsNullOrEmpty(value) || value == IdmlNames.NoReference)
            {
                break;
            }
            // BasedOn is an object reference ("ParagraphStyle/Body") or, for built-ins, a bare name ("$ID/[No paragraph style]").
            current = basedOn!.Attribute("type")?.Value == "object" || (value.Contains('/', StringComparison.Ordinal) && !value.StartsWith("$ID/", StringComparison.Ordinal))
                ? value
                : style.Name.LocalName + "/" + value;
        }
        chainCache[reference] = chain;
        return chain;
    }

    /// <summary>Finds a style element by its Self reference in Resources/Styles.xml.</summary>
    public XElement? FindStyle(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!styleCache.TryGetValue(reference, out var style))
        {
            if (styleCache.Count == 0)
            {
                foreach (var element in document.StylesRoot.Descendants())
                {
                    if (element.Attribute("Self")?.Value is { } self)
                    {
                        styleCache.TryAdd(self, element);
                    }
                }
                styleCache.TryGetValue(reference, out style);
            }
            styleCache.TryAdd(reference, style);
        }
        return style;
    }

    /// <summary>The first value of <paramref name="name"/> along a chain from <see cref="TextChain"/> or <see cref="ObjectChain"/>.</summary>
    internal static string? First(IReadOnlyList<XElement> chain, string name)
    {
        foreach (var element in chain)
        {
            if (Own(element, name) is { } value)
            {
                return value;
            }
        }
        return null;
    }
}

/// <summary>The colour space of a <see cref="ResolvedColor"/>.</summary>
public enum ColorSpaceKind
{
    Gray,
    Rgb,
    Cmyk,
}

/// <summary>A device colour with components in 0..1 (gray: 1 = white; CMYK: 1 = full ink).</summary>
public sealed record ResolvedColor(ColorSpaceKind Space, IReadOnlyList<double> Components)
{
    public static readonly ResolvedColor White = new(ColorSpaceKind.Cmyk, [0, 0, 0, 0]);

    /// <summary>Applies a tint in percent (100 = unchanged), as InDesign does for process colours.</summary>
    public ResolvedColor WithTint(double percent)
    {
        if (percent is < 0 or >= 100)
        {
            return this;
        }
        var t = percent / 100;
        return Space switch
        {
            ColorSpaceKind.Cmyk => this with { Components = Components.Select(value => value * t).ToArray() },
            // Additive spaces tint towards white.
            _ => this with { Components = Components.Select(value => 1 - ((1 - value) * t)).ToArray() },
        };
    }

    /// <summary>Approximate sRGB (naive, uncalibrated CMYK conversion), 0..1 per channel.</summary>
    public (double R, double G, double B) ToRgb() => Space switch
    {
        ColorSpaceKind.Gray => (Components[0], Components[0], Components[0]),
        ColorSpaceKind.Rgb => (Components[0], Components[1], Components[2]),
        _ => ((1 - Components[0]) * (1 - Components[3]), (1 - Components[1]) * (1 - Components[3]), (1 - Components[2]) * (1 - Components[3])),
    };
}

/// <summary>One anchor of a page item path with its Bézier direction handles (inner coordinates).</summary>
public readonly record struct PathPoint(Point Anchor, Point LeftDirection, Point RightDirection);

/// <summary>One closed or open sub-path of a page item.</summary>
public sealed record PathContour(bool IsOpen, IReadOnlyList<PathPoint> Points);
