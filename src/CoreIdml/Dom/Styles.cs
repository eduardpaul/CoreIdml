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
