using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>A paragraph, character or object style.</summary>
public abstract class Style : IdmlNode
{
    private protected Style(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    /// <summary>The reference used by <c>Applied*Style</c> attributes (the style's Self, e.g. <c>ParagraphStyle/Title</c>).</summary>
    public string Reference => Id;

    /// <summary>The name as stored (built-in styles start with <c>$ID/</c>).</summary>
    public string Name => GetAttribute("Name") ?? string.Empty;

    /// <summary>The name as InDesign shows it.</summary>
    public string DisplayName => StyleCatalog.DisplayName(Name);

    public override string ToString() => DisplayName;
}

public sealed class ParagraphStyle : Style
{
    internal ParagraphStyle(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    public void ApplyFormat(ParagraphFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.ApplyTo(Element);
    }

    /// <summary>The style applied to the paragraph that follows when typing Enter in InDesign.</summary>
    public void SetNextStyle(ParagraphStyle next)
    {
        ArgumentNullException.ThrowIfNull(next);
        SetAttribute("NextStyle", next.Reference);
    }
}

public sealed class CharacterStyle : Style
{
    internal CharacterStyle(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    public void ApplyFormat(CharacterFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.ApplyTo(Element);
    }
}

/// <summary>
/// An object style: the fill, stroke, corners, text frame options and transparency that page items take from it, for the
/// categories it controls (<c>EnableFill</c>, <c>EnableStroke</c>, ...), unless an item overrides them.
/// </summary>
public sealed class ObjectStyle : Style
{
    internal ObjectStyle(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    /// <summary>
    /// Sets the style's values: each category given (fill, stroke, corners, text frame general options, transparency)
    /// is turned on (<c>EnableFill</c>, ...); the others keep their setting.
    /// </summary>
    public void ApplyFormat(ObjectStyleFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.ApplyTo(Element);
    }
}

/// <summary>
/// What an object style sets, by category: fill, stroke, corners, the text frame's general options (columns, inset,
/// vertical justification) and the object's opacity. Null properties are not set.
/// </summary>
public sealed class ObjectStyleFormat
{
    public Swatch? Fill { get; init; }

    public double? FillTint { get; init; }

    public Swatch? Stroke { get; init; }

    public double? StrokeWeight { get; init; }

    public double? StrokeTint { get; init; }

    public StrokeKind? StrokeKind { get; init; }

    /// <summary>Rounded corners of this radius on all four corners (0: square).</summary>
    public double? CornerRadius { get; init; }

    /// <summary>Opacity of the whole object in percent.</summary>
    public double? Opacity { get; init; }

    public int? Columns { get; init; }

    public double? ColumnGutter { get; init; }

    public Spacing? Inset { get; init; }

    public VerticalJustification? VerticalJustification { get; init; }

    internal bool SetsFill => Fill is not null || FillTint is not null;

    internal bool SetsStroke => Stroke is not null || StrokeWeight is not null || StrokeTint is not null || StrokeKind is not null;

    internal bool SetsTextFrameOptions => Columns is not null || ColumnGutter is not null || Inset is not null || VerticalJustification is not null;

    /// <summary>
    /// Writes the values as InDesign stores an object style (02-item-object-styles): fill, stroke and corner attributes
    /// on the style with their category flags, a <c>TextFramePreference</c> child for the text frame options, and the
    /// opacity in <c>TransparencySetting</c> with <c>ObjectStyleObjectEffectsCategorySettings@EnableTransparency</c>.
    /// </summary>
    internal void ApplyTo(XElement style)
    {
        static void Set(XElement element, string name, double? value)
        {
            if (value is { } number)
            {
                element.SetAttributeValue(name, IdmlXml.Format(number));
            }
        }
        if (SetsFill)
        {
            style.SetAttributeValue("EnableFill", "true");
            style.SetAttributeValue("FillColor", Fill?.Reference ?? style.Attribute("FillColor")?.Value);
            Set(style, "FillTint", FillTint);
        }
        if (SetsStroke)
        {
            style.SetAttributeValue("EnableStroke", "true");
            style.SetAttributeValue("StrokeColor", Stroke?.Reference ?? style.Attribute("StrokeColor")?.Value);
            Set(style, "StrokeWeight", StrokeWeight);
            Set(style, "StrokeTint", StrokeTint);
            if (StrokeKind is { } kind)
            {
                style.SetAttributeValue("StrokeType", PageItem.StrokeTypeReference(kind));
            }
        }
        if (CornerRadius is { } radius)
        {
            style.SetAttributeValue("EnableStrokeAndCornerOptions", "true");
            var option = radius > 0 ? "RoundedCorner" : "None";
            style.SetAttributeValue("CornerOption", option);
            style.SetAttributeValue("CornerRadius", IdmlXml.Format(radius));
            foreach (var corner in (string[])["TopLeft", "TopRight", "BottomLeft", "BottomRight"])
            {
                style.SetAttributeValue(corner + "CornerOption", option);
                style.SetAttributeValue(corner + "CornerRadius", IdmlXml.Format(radius));
            }
        }
        if (SetsTextFrameOptions)
        {
            style.SetAttributeValue("EnableTextFrameGeneralOptions", "true");
            var preference = style.Element("TextFramePreference");
            if (preference is null)
            {
                preference = new XElement("TextFramePreference");
                IdmlXml.GetOrCreateProperties(style).AddAfterSelf(preference);
            }
            if (Columns is { } columns)
            {
                preference.SetAttributeValue("TextColumnCount", Math.Max(1, columns).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            Set(preference, "TextColumnGutter", ColumnGutter);
            if (VerticalJustification is { } justification)
            {
                preference.SetAttributeValue("VerticalJustification", TextFrame.VerticalJustificationName(justification));
            }
            if (Inset is { } inset)
            {
                TextFrame.WriteInset(preference, inset);
            }
        }
        if (Opacity is { } opacity)
        {
            if (opacity is < 0 or > 100)
            {
                throw new IdmlException($"Opacity {opacity} is outside 0 to 100.");
            }
            style.Element("TransparencySetting")?.Remove();
            style.Element("ObjectStyleObjectEffectsCategorySettings")?.Remove();
            var anchor = style.Elements().LastOrDefault(child => child.Name.LocalName is "Properties" or "TextFramePreference") ?? IdmlXml.GetOrCreateProperties(style);
            // Only the object's transparency: effects the style does not set stay the item's own.
            anchor.AddAfterSelf(
                new XElement("TransparencySetting", new XElement("BlendingSetting", new XAttribute("Opacity", IdmlXml.Format(opacity)))),
                new XElement("ObjectStyleObjectEffectsCategorySettings",
                    new XAttribute("EnableTransparency", "true"),
                    ((string[])["EnableDropShadow", "EnableFeather", "EnableInnerShadow", "EnableOuterGlow", "EnableInnerGlow", "EnableBevelEmboss",
                        "EnableSatin", "EnableDirectionalFeather", "EnableGradientFeather"]).Select(name => new XAttribute(name, "false"))));
        }
    }
}

/// <summary>Paragraph, character and object styles of the document (Resources/Styles.xml).</summary>
public sealed class StyleCatalog
{
    private readonly IdmlDocument document;

    internal StyleCatalog(IdmlDocument document)
    {
        this.document = document;
    }

    public IReadOnlyList<ParagraphStyle> ParagraphStyles =>
        ParagraphGroup.Descendants("ParagraphStyle").Select(element => new ParagraphStyle(document, element)).ToList();

    public IReadOnlyList<CharacterStyle> CharacterStyles =>
        CharacterGroup.Descendants("CharacterStyle").Select(element => new CharacterStyle(document, element)).ToList();

    /// <summary>The object styles, built-in ones ([None], [Normal Text Frame], ...) included.</summary>
    public IReadOnlyList<ObjectStyle> ObjectStyles =>
        document.StylesRoot.Element("RootObjectStyleGroup")?.Descendants("ObjectStyle").Select(element => new ObjectStyle(document, element)).ToList() ?? [];

    /// <summary>InDesign's [Basic Paragraph].</summary>
    public ParagraphStyle BasicParagraph => FindParagraphStyle("[Basic Paragraph]")
        ?? throw new IdmlException("The document has no [Basic Paragraph] style.");

    /// <summary>Finds a paragraph style by display name, stored name or reference.</summary>
    public ParagraphStyle? FindParagraphStyle(string name) =>
        Find(ParagraphGroup, "ParagraphStyle", name) is { } element ? new ParagraphStyle(document, element) : null;

    public CharacterStyle? FindCharacterStyle(string name) =>
        Find(CharacterGroup, "CharacterStyle", name) is { } element ? new CharacterStyle(document, element) : null;

    /// <summary>Adds a paragraph style, or updates the existing one with the same name.</summary>
    public ParagraphStyle AddParagraphStyle(string name, ParagraphFormat? format = null, ParagraphStyle? basedOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var element = Find(ParagraphGroup, "ParagraphStyle", name);
        if (element is null)
        {
            var reference = "ParagraphStyle/" + IdmlXml.EscapeSelfName(name);
            element = new XElement("ParagraphStyle",
                new XAttribute("Self", reference),
                new XAttribute("Name", name),
                new XAttribute("Imported", "false"),
                new XAttribute("NextStyle", reference),
                new XAttribute("KeyboardShortcut", "0 0"),
                new XElement("Properties",
                    new XElement("BasedOn", new XAttribute("type", "object"), basedOn?.Reference ?? IdmlNames.BasicParagraphStyle),
                    new XElement("PreviewColor", new XAttribute("type", "enumeration"), "Nothing")));
            ParagraphGroup.Add(element);
        }
        else if (basedOn is not null)
        {
            IdmlXml.SetProperty(element, "BasedOn", "object", basedOn.Reference);
        }
        var style = new ParagraphStyle(document, element);
        if (format is not null)
        {
            style.ApplyFormat(format);
        }
        return style;
    }

    /// <summary>Adds a character style, or updates the existing one with the same name.</summary>
    public CharacterStyle AddCharacterStyle(string name, CharacterFormat? format = null, CharacterStyle? basedOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var element = Find(CharacterGroup, "CharacterStyle", name);
        if (element is null)
        {
            element = new XElement("CharacterStyle",
                new XAttribute("Self", "CharacterStyle/" + IdmlXml.EscapeSelfName(name)),
                new XAttribute("Imported", "false"),
                new XAttribute("KeyboardShortcut", "0 0"),
                new XAttribute("Name", name),
                new XElement("Properties",
                    basedOn is null
                        ? new XElement("BasedOn", new XAttribute("type", "string"), "$ID/[No character style]")
                        : new XElement("BasedOn", new XAttribute("type", "object"), basedOn.Reference),
                    new XElement("PreviewColor", new XAttribute("type", "enumeration"), "Nothing")));
            CharacterGroup.Add(element);
        }
        else if (basedOn is not null)
        {
            IdmlXml.SetProperty(element, "BasedOn", "object", basedOn.Reference);
        }
        var style = new CharacterStyle(document, element);
        if (format is not null)
        {
            style.ApplyFormat(format);
        }
        return style;
    }

    /// <summary>Finds an object style by display name ("[Normal Text Frame]", "Card"), stored name or reference.</summary>
    public ObjectStyle? FindObjectStyle(string name) =>
        Find(ObjectGroup, "ObjectStyle", name) is { } element ? new ObjectStyle(document, element) : null;

    /// <summary>
    /// Adds an object style, or updates the existing one with the same name. A new style turns on the categories
    /// <paramref name="format"/> sets; the others it leaves alone (<c>EnableFill="false"</c>, ...), or takes from
    /// <paramref name="basedOn"/>, so items keep their own values there.
    /// </summary>
    public ObjectStyle AddObjectStyle(string name, ObjectStyleFormat? format = null, ObjectStyle? basedOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var element = Find(ObjectGroup, "ObjectStyle", name);
        if (element is null)
        {
            element = new XElement("ObjectStyle",
                new XAttribute("Self", "ObjectStyle/" + IdmlXml.EscapeSelfName(name)),
                new XAttribute("Name", name),
                new XElement("Properties",
                    basedOn is null
                        ? new XElement("BasedOn", new XAttribute("type", "string"), "$ID/[None]")
                        : new XElement("BasedOn", new XAttribute("type", "object"), basedOn.Reference)));
            // The categories of the object style dialog, off unless the base style turns them on.
            var chain = basedOn is null ? [] : new AttributeResolver(document).StyleChain(basedOn.Reference);
            foreach (var category in (string[])["EnableFill", "EnableStroke", "EnableStrokeAndCornerOptions", "EnableParagraphStyle", "EnableTextFrameGeneralOptions",
                "EnableTextFrameBaselineOptions", "EnableTextFrameAutoSizingOptions", "EnableTextFrameColumnRuleOptions", "EnableTextFrameFootnoteOptions",
                "EnableStoryOptions", "EnableTextWrapAndOthers", "EnableAnchoredObjectOptions", "EnableFrameFittingOptions", "EnableTransformAttributes"])
            {
                var enabled = chain.Count > 0 && chain.Select(style => style.Attribute(category)?.Value).FirstOrDefault(value => value is not null) == "true";
                element.Add(new XAttribute(category, enabled ? "true" : "false"));
            }
            ObjectGroup.Add(element);
        }
        else if (basedOn is not null)
        {
            IdmlXml.SetProperty(element, "BasedOn", "object", basedOn.Reference);
        }
        var objectStyle = new ObjectStyle(document, element);
        if (format is not null)
        {
            objectStyle.ApplyFormat(format);
        }
        return objectStyle;
    }

    /// <summary>"ParagraphStyle/$ID/NormalParagraphStyle" → "[Basic Paragraph]", "CharacterStyle/Group%3aBold" → "Group:Bold".</summary>
    internal static string? DisplayNameFromReference(string? reference)
    {
        if (reference is null || reference == IdmlNames.NoReference)
        {
            return null;
        }
        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        var name = slash >= 0 ? reference[(slash + 1)..] : reference;
        return DisplayName(name.Replace("%3a", ":", StringComparison.Ordinal).Replace("%25", "%", StringComparison.Ordinal));
    }

    internal static string DisplayName(string storedName) => storedName switch
    {
        "$ID/NormalParagraphStyle" => "[Basic Paragraph]",
        _ when storedName.StartsWith("$ID/", StringComparison.Ordinal) => storedName[4..],
        _ => storedName,
    };

    private XElement ParagraphGroup => Group("RootParagraphStyleGroup");

    private XElement CharacterGroup => Group("RootCharacterStyleGroup");

    private XElement ObjectGroup => Group("RootObjectStyleGroup");

    private XElement Group(string name)
    {
        var root = document.StylesRoot;
        var group = root.Element(name);
        if (group is null)
        {
            group = new XElement(name, new XAttribute("Self", document.NewId()));
            root.AddFirst(group);
        }
        return group;
    }

    private static XElement? Find(XElement group, string kind, string name)
    {
        var escaped = kind + "/" + IdmlXml.EscapeSelfName(name);
        return group.Descendants(kind).FirstOrDefault(element =>
        {
            var stored = element.Attribute("Name")?.Value ?? string.Empty;
            var self = element.Attribute("Self")?.Value;
            return stored == name || self == name || self == escaped || DisplayName(stored) == name;
        });
    }
}
