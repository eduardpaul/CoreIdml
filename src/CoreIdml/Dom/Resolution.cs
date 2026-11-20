using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>
/// Resolves effective attribute values through IDML's inheritance, the way InDesign applies them:
/// <list type="bullet">
/// <item>text: run (local formatting), character style and its BasedOn chain, paragraph (local), paragraph style and its BasedOn chain;</item>
/// <item>page items: the item, then its object style and the object style's BasedOn chain;</item>
/// <item>table cells: the cell, then its cell style chain (or its table style's region cell style chain).</item>
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

    /// <summary>
    /// The effective value of a page item attribute (FillColor, StrokeWeight, corner options, ...): the item's own
    /// value, else its object style chain's, unless the style leaves the attribute's category alone
    /// (<see cref="ObjectStyleCategory"/>).
    /// </summary>
    public string? Item(XElement item, string name)
    {
        var chain = ObjectChain(item);
        return ObjectStyleCategory(name) is { } category && !StylesControl(chain, category) ? Own(item, name) : First(chain, name);
    }

    /// <summary>
    /// The object style category (<c>EnableFill</c>, <c>EnableStroke</c>, <c>EnableStrokeAndCornerOptions</c>) that
    /// holds a page item attribute, or null when the attribute belongs to none of them. An object style whose category
    /// is off (its check box cleared in Object Style Options) does not touch those attributes: the item keeps its own
    /// values. In 02-item-object-styles p3, "OS Stroke Only" (<c>EnableFill="false"</c>) leaves the ovals' fill alone
    /// and "OS Fill Only" (<c>EnableStroke="false"</c>) the polygon's stroke; InDesign writes those values on the item.
    /// Which attribute is in which category follows InDesign's dialog (from memory, not measured: the samples only
    /// clear Fill and Stroke): weight, type, colour, tint, gap and overprint are Stroke; alignment, caps, joins,
    /// arrowheads and corners are Stroke and Corner Options.
    /// </summary>
    internal static string? ObjectStyleCategory(string name) => name switch
    {
        "FillColor" or "FillTint" or "OverprintFill" => "EnableFill",
        _ when name.StartsWith("GradientFill", StringComparison.Ordinal) => "EnableFill",
        "StrokeWeight" or "StrokeType" or "StrokeColor" or "StrokeTint" or "OverprintStroke" or "GapColor" or "GapTint"
            or "OverprintGap" or "StrokeDashAndGap" => "EnableStroke",
        _ when name.StartsWith("GradientStroke", StringComparison.Ordinal) => "EnableStroke",
        "StrokeAlignment" or "EndCap" or "EndJoin" or "MiterLimit" or "LeftLineEnd" or "RightLineEnd" or "LeftArrowHeadScale"
            or "RightArrowHeadScale" or "ArrowHeadAlignment" => "EnableStrokeAndCornerOptions",
        _ when name.EndsWith("CornerOption", StringComparison.Ordinal) || name.EndsWith("CornerRadius", StringComparison.Ordinal)
            => "EnableStrokeAndCornerOptions",
        _ => null,
    };

    /// <summary>
    /// The text frame category that holds a <c>TextFramePreference</c> attribute: baseline options (first baseline
    /// offset), auto-sizing, column rules, footnotes, and general options for everything else (columns, inset, vertical
    /// justification, ignore wrap). In 02-item-object-styles p1 "OS Text Frame" enables only the general options
    /// (two columns, 8 pt inset).
    /// </summary>
    internal static string FramePreferenceCategory(string name) =>
        name is "FirstBaselineOffset" or "MinimumFirstBaselineOffset" ? "EnableTextFrameBaselineOptions"
        : name.Contains("AutoSizing", StringComparison.Ordinal) ? "EnableTextFrameAutoSizingOptions"
        : name.StartsWith("ColumnRule", StringComparison.Ordinal) ? "EnableTextFrameColumnRuleOptions"
        : name.StartsWith("Footnotes", StringComparison.Ordinal) ? "EnableTextFrameFootnoteOptions"
        : "EnableTextFrameGeneralOptions";

    /// <summary>
    /// Whether the object styles of an <see cref="ObjectChain"/> control a category (<c>EnableFill</c>,
    /// <c>EnableTextFrameGeneralOptions</c>, ...). The most specific style that says decides for its whole BasedOn
    /// chain: a style inherits the values of the style it is based on, so clearing a category leaves those alone too.
    /// A style without the flag (the built-in [None]) controls every category.
    /// </summary>
    internal static bool StylesControl(IReadOnlyList<XElement> objectChain, string category)
    {
        for (var i = 1; i < objectChain.Count; i++)
        {
            if (objectChain[i].Attribute(category)?.Value is { } value)
            {
                return value != "false";
            }
        }
        return true;
    }

    /// <summary>
    /// The effective <c>BlendingSetting</c> attribute (Opacity, BlendMode, ...) of a page item: its own
    /// <c>TransparencySetting</c>, then its object style chain's when the style applies its object transparency
    /// (<c>ObjectStyleObjectEffectsCategorySettings@EnableTransparency</c>).
    /// </summary>
    public string? Blending(XElement item, string name)
    {
        ArgumentNullException.ThrowIfNull(item);
        var chain = ObjectChain(item);
        var styles = true;
        for (var i = 1; i < chain.Count; i++)
        {
            if (chain[i].Element("ObjectStyleObjectEffectsCategorySettings")?.Attribute("EnableTransparency")?.Value is { } value)
            {
                styles = value != "false";
                break;
            }
        }
        for (var i = 0; i < (styles ? chain.Count : 1); i++)
        {
            if (chain[i].Element("TransparencySetting")?.Element("BlendingSetting")?.Attribute(name)?.Value is { } value)
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>
    /// The effective value of a <c>TextFramePreference</c> attribute (TextColumnCount, FirstBaselineOffset, ...) or
    /// property (InsetSpacing) of a text frame: its own preference, then its object style's when the style controls
    /// the attribute's category (<see cref="FramePreferenceCategory"/>).
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
        var count = StylesControl(objectChain, FramePreferenceCategory(name)) ? objectChain.Count : Math.Min(1, objectChain.Count);
        for (var i = 0; i < count; i++)
        {
            if (objectChain[i].Element("TextFramePreference") is { } preference && Own(preference, name) is not null)
            {
                return preference;
            }
        }
        return null;
    }

    /// <summary><see cref="FramePreference(XElement, string)"/> for a frame's <see cref="ObjectChain"/> computed once.</summary>
    internal static string? FramePreference(IReadOnlyList<XElement> objectChain, string name) =>
        FramePreferenceSource(objectChain, name) is { } preference ? Own(preference, name) : null;

    /// <summary>
    /// The cell, then its cell style chain. A cell without a cell style of its own ([None]) takes the cell style its
    /// table style gives its region instead (<see cref="RegionCellStyle"/>).
    /// </summary>
    public string? Cell(XElement cell, string name)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (Own(cell, name) is { } value)
        {
            return value;
        }
        var style = cell.Attribute("AppliedCellStyle")?.Value;
        if (style is null or IdmlNames.NoCellStyle && RegionCellStyle(cell) is { } region)
        {
            style = region;
        }
        return First(StyleChain(style), name);
    }

    /// <summary>
    /// The cell style the table style applies to the region of <paramref name="cell"/>, or null. Header and footer
    /// rows come before the left and right columns, which come before the body (InDesign's order of precedence;
    /// in 09-style-table-cell-styles p2 the corner cells take the header and footer styles). A region marked "same
    /// as body" (<c>HeaderRegionSameAsBodyRegion</c>, ...) takes the body's style.
    /// </summary>
    public string? RegionCellStyle(XElement cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.Parent is not { Name.LocalName: "Table" } table)
        {
            return null;
        }
        var name = (cell.Attribute("Name")?.Value ?? "0:0").Split(':');
        var column = IdmlXml.ParseDouble(name[0]) ?? 0;
        var row = name.Length > 1 ? IdmlXml.ParseDouble(name[1]) ?? 0 : 0;
        var columnSpan = IdmlXml.ParseDouble(cell.Attribute("ColumnSpan")?.Value) ?? 1;
        double Count(string attribute) => IdmlXml.ParseDouble(table.Attribute(attribute)?.Value) ?? 0;
        var header = Count("HeaderRowCount");
        var footer = Count("FooterRowCount");
        var rows = header + Count("BodyRowCount") + footer;
        var columns = Count("ColumnCount");
        var region = row < header ? "Header"
            : row >= rows - footer ? "Footer"
            : column == 0 ? "LeftColumn"
            : column + columnSpan >= columns ? "RightColumn"
            : "Body";
        if (region != "Body" && Table(table, region + "RegionSameAsBodyRegion") == "true")
        {
            region = "Body";
        }
        return Table(table, region + "RegionCellStyle") is { } style and not IdmlNames.NoReference ? style : null;
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
