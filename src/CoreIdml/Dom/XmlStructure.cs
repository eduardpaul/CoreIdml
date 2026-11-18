using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>
/// An element of the document's XML structure (Structure pane in InDesign). Tagged stories and frames are how
/// SimpleIDML-style workflows bind data to a layout: tag a frame "nombre" in InDesign, then fill it by tag name or
/// import a whole XML file onto the structure.
/// </summary>
/// <remarks>
/// IDML splits the structure across parts: the backing story holds the tree, and a tag bound to a story
/// (<c>XMLContent</c> = story id) continues inside that story as a wrapper element with the same <c>Self</c>.
/// <see cref="ContentElement"/> and <see cref="Children"/> follow that link, so the tree reads as one piece.
/// </remarks>
public sealed class XmlElementNode : IdmlNode
{
    internal XmlElementNode(IdmlDocument document, XElement element, XmlElementNode? parent = null)
        : base(document, element)
    {
        Parent = parent;
    }

    /// <summary>The tag name ("title" for <c>MarkupTag="XMLTag/title"</c>).</summary>
    public string Tag => TagCatalog.TagName(GetAttribute("MarkupTag"));

    /// <summary>Id of the story or page item (or placed graphic) this element is bound to, if any.</summary>
    public string? ContentId => GetAttribute("XMLContent");

    /// <summary>The parent node, when this node was reached by navigating from <see cref="TagCatalog.Root"/>.</summary>
    public XmlElementNode? Parent { get; }

    /// <summary>The element holding this node's text and children: the in-story wrapper for a tagged story, else itself.</summary>
    public XElement ContentElement
    {
        get
        {
            if (ContentId is { } id && Document.FindStory(id) is { } story)
            {
                var self = Id;
                var wrapper = story.Element.Descendants("XMLElement").FirstOrDefault(element => element.Attribute("Self")?.Value == self);
                if (wrapper is not null)
                {
                    return wrapper;
                }
            }
            return Element;
        }
    }

    /// <summary>Child elements in document order, followed across part boundaries.</summary>
    public IReadOnlyList<XmlElementNode> Children
    {
        get
        {
            var content = ContentElement;
            return content.Descendants("XMLElement")
                .Where(child => child.Ancestors("XMLElement").FirstOrDefault() == content)
                .Select(child => new XmlElementNode(Document, child, this))
                .ToList();
        }
    }

    /// <summary>Absolute path such as <c>/Root/module[2]/headline</c> (indices only where siblings share a tag).</summary>
    public string Path
    {
        get
        {
            if (Parent is null)
            {
                return "/" + Tag;
            }
            var siblings = Parent.Children.Where(child => child.Tag == Tag).ToList();
            var suffix = siblings.Count > 1
                ? "[" + (siblings.FindIndex(child => child.Element == Element) + 1).ToString(CultureInfo.InvariantCulture) + "]"
                : string.Empty;
            return Parent.Path + "/" + Tag + suffix;
        }
    }

    /// <summary>The story this element is bound to, when it tags a whole text frame.</summary>
    public Story? BoundStory => ContentId is { } id ? Document.FindStory(id) : null;

    /// <summary>Id of the story containing this element, when it sits inside one.</summary>
    public string? ContainingStoryId => Element.Ancestors("Story").FirstOrDefault()?.Attribute("Self")?.Value;

    /// <summary>The page item this element is bound to, when it tags a frame.</summary>
    /// <remarks>InDesign binds a tagged graphic to the placed image, not its frame; both resolve to the frame.</remarks>
    public PageItem? BoundPageItem => ContentId is { } id
        ? Document.AllPageItems.FirstOrDefault(item => item.Id == id)
            ?? Document.AllPageItems.OfType<GraphicFrame>().FirstOrDefault(frame => frame.Graphic?.Attribute("Self")?.Value == id)
        : null;

    /// <summary>Whether this is the copy inside a story (as opposed to the backing-story structure entry).</summary>
    public bool IsInStory => Element.Ancestors("Story").Any();

    /// <summary>The element's text, nested tags included ("\n" between paragraphs).</summary>
    public string Text => BoundStory is { } story && ContentElement == Element ? story.Text : TextOps.GetText(ContentElement);

    /// <summary>Replaces the tagged text (nested tags included), keeping the tag and the first run's formatting.</summary>
    public void SetText(string text)
    {
        if (BoundStory is { } story)
        {
            // Bound to a whole story (possibly the one containing it): rewrite that story inside its tag.
            story.SetText(text);
        }
        else if (IsInStory && ContentId is null)
        {
            TextOps.SetText(Element, text);
        }
        else
        {
            throw new IdmlException($"XML element '{Tag}' ({Id}) is not bound to text.");
        }
    }

    /// <summary>XML attributes (<c>XMLAttribute</c> children) by name.</summary>
    public IReadOnlyDictionary<string, string> Attributes =>
        AttributeElements().GroupBy(attribute => attribute.Attribute("Name")?.Value ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.First().Attribute("Value")?.Value ?? string.Empty, StringComparer.Ordinal);

    public string? GetXmlAttribute(string name) =>
        AttributeElements().FirstOrDefault(attribute => attribute.Attribute("Name")?.Value == name)?.Attribute("Value")?.Value;

    /// <summary>Sets an XML attribute on both copies of the element (structure entry and in-story wrapper).</summary>
    public void SetXmlAttribute(string name, string value)
    {
        foreach (var target in Copies())
        {
            var attribute = target.Elements("XMLAttribute").FirstOrDefault(element => element.Attribute("Name")?.Value == name);
            if (attribute is null)
            {
                target.AddFirst(new XElement("XMLAttribute",
                    new XAttribute("Self", Id + "XMLAttributen" + name),
                    new XAttribute("Name", name),
                    new XAttribute("Value", value)));
            }
            else
            {
                attribute.SetAttributeValue("Value", value);
            }
        }
    }

    /// <summary>Attaches an InDesign note (Window &gt; Notes) at the start of the element's text.</summary>
    public void AddNote(string text, string author = "Idml", DateTime? when = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var content = ContentElement;
        if (!content.Ancestors("Story").Any())
        {
            throw new IdmlException($"XML element '{Tag}' ({Id}) is not in a story; notes live in text.");
        }
        var stamp = (when ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var note = new XElement("Note",
            new XAttribute("Collapsed", "false"),
            new XAttribute("CreationDate", stamp),
            new XAttribute("ModificationDate", stamp),
            new XAttribute("UserName", author),
            new XAttribute("AppliedDocumentUser", IdmlNames.NoReference),
            new XElement("ParagraphStyleRange",
                new XAttribute("AppliedParagraphStyle", IdmlNames.BasicParagraphStyle),
                new XElement("CharacterStyleRange",
                    new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle),
                    TextOps.TextNodes(text))));
        // A Note is inline text: it must sit inside a character run.
        var run = content.Ancestors("CharacterStyleRange").Any()
            ? content
            : content.Descendants("CharacterStyleRange").FirstOrDefault();
        if (run is null)
        {
            run = new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle));
            var range = content.Descendants("ParagraphStyleRange").FirstOrDefault();
            (range ?? content).Add(run);
        }
        var first = run.Elements().FirstOrDefault(element => element.Name.LocalName is not ("Properties" or "XMLAttribute"));
        if (first is null)
        {
            run.Add(note);
        }
        else
        {
            first.AddBeforeSelf(note);
        }
    }

    private IEnumerable<XElement> AttributeElements() => Copies().SelectMany(copy => copy.Elements("XMLAttribute"));

    private IEnumerable<XElement> Copies() => ContentElement == Element ? [Element] : [Element, ContentElement];

    public override string ToString() => $"<{Tag}> {Id}";
}

/// <summary>XML tags (XML/Tags.xml), tag-to-style mappings (XML/Mapping.xml) and the XML structure.</summary>
public sealed partial class TagCatalog
{
    private const string TagsPath = "XML/Tags.xml";
    private const string BackingStoryPath = "XML/BackingStory.xml";
    private const string MappingPath = "XML/Mapping.xml";

    private readonly IdmlDocument document;

    internal TagCatalog(IdmlDocument document)
    {
        this.document = document;
    }

    public IReadOnlyList<string> Names =>
        document.Package.TryGetXml(TagsPath)?.Root?.Elements("XMLTag").Select(tag => tag.Attribute("Name")?.Value ?? string.Empty).ToList() ?? [];

    /// <summary>The root element of the XML structure, if the document has one.</summary>
    public XmlElementNode? Root =>
        document.Package.TryGetXml(BackingStoryPath)?.Root?.Element("XmlStory")?.Descendants("XMLElement").FirstOrDefault() is { } root
            ? new XmlElementNode(document, root)
            : null;

    /// <summary>Every node of the structure in document order (depth first).</summary>
    public IEnumerable<XmlElementNode> AllNodes => Root is { } root ? Walk(root) : [];

    /// <summary>
    /// Every XML element with the tag: structure entries bound to frames and inline tags inside stories. A tagged
    /// story appears twice in IDML (structure entry + in-story copy, same id); only one copy is returned.
    /// </summary>
    public IReadOnlyList<XmlElementNode> FindElements(string tag)
    {
        var markup = "XMLTag/" + IdmlXml.EscapeSelfName(tag);
        // The same element can appear twice in stories: as a reference inside its parent story and as the wrapper
        // inside the story it tags. Keep one per Self, preferring the copy that holds the content.
        var inStories = document.Stories
            .SelectMany(story => story.Element.Descendants("XMLElement"))
            .Where(element => element.Attribute("MarkupTag")?.Value == markup)
            .Select(element => new XmlElementNode(document, element))
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(node => node.ContentId is null || node.ContentId == node.ContainingStoryId) ?? group.First())
            .ToList();
        var inStoryIds = inStories.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var structure = (document.Package.TryGetXml(BackingStoryPath)?.Root?.Descendants("XMLElement") ?? [])
            .Where(element => element.Attribute("MarkupTag")?.Value == markup && !inStoryIds.Contains(element.Attribute("Self")?.Value ?? string.Empty))
            .Select(element => new XmlElementNode(document, element));
        return [.. structure, .. inStories];
    }

    /// <summary>
    /// Nodes matching a path: <c>/Root/module[2]/headline</c> (absolute, 1-based indices like XPath), <c>*</c> for any
    /// tag, or <c>//headline</c> for every match at any depth.
    /// </summary>
    public IReadOnlyList<XmlElementNode> Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Root is not { } root)
        {
            return [];
        }
        IEnumerable<XmlElementNode> current;
        string rest;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var (firstSegment, remaining) = SplitFirst(path[2..]);
            var (tag, index) = ParseSegment(firstSegment);
            var matches = Walk(root).Where(node => tag == "*" || node.Tag == tag).ToList();
            current = index is { } i ? matches.Skip(i - 1).Take(1) : matches;
            rest = remaining;
        }
        else
        {
            var (firstSegment, remaining) = SplitFirst(path.TrimStart('/'));
            var (tag, _) = ParseSegment(firstSegment);
            current = tag == "*" || tag == root.Tag ? [root] : [];
            rest = remaining;
        }
        while (rest.Length > 0)
        {
            var (segment, remaining) = SplitFirst(rest);
            var (tag, index) = ParseSegment(segment);
            current = current.SelectMany(node =>
            {
                var children = node.Children.Where(child => tag == "*" || child.Tag == tag).ToList();
                return index is { } i ? children.Skip(i - 1).Take(1) : children;
            }).ToList();
            rest = remaining;
        }
        return current.ToList();
    }

    /// <summary>The single node at <paramref name="path"/>; throws when none or several match.</summary>
    public XmlElementNode SelectSingle(string path)
    {
        var nodes = Select(path);
        return nodes.Count switch
        {
            1 => nodes[0],
            0 => throw new IdmlException(IdmlErrorKind.NotFound, $"No XML element matches '{path}'."),
            _ => throw new IdmlException($"{nodes.Count} XML elements match '{path}'; add an index such as [1]."),
        };
    }

    /// <summary>Adds a tag if missing.</summary>
    public void Ensure(string tag, string color = "LightBlue")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var root = TagsRoot();
        var self = "XMLTag/" + IdmlXml.EscapeSelfName(tag);
        if (root.Elements("XMLTag").Any(element => element.Attribute("Self")?.Value == self))
        {
            return;
        }
        root.Add(new XElement("XMLTag",
            new XAttribute("Self", self),
            new XAttribute("Name", tag),
            new XElement("Properties", new XElement("TagColor", new XAttribute("type", "enumeration"), color))));
    }

    /// <summary>Tags a whole story (the text of a frame), so it can be filled by tag and exported as XML.</summary>
    public XmlElementNode TagStory(Story story, string tag)
    {
        ArgumentNullException.ThrowIfNull(story);
        Ensure(tag);
        var root = RequireRoot();
        var self = root.Id + "i" + document.NewId();
        var markup = "XMLTag/" + IdmlXml.EscapeSelfName(tag);
        root.Element.Add(new XElement("XMLElement", new XAttribute("Self", self), new XAttribute("MarkupTag", markup), new XAttribute("XMLContent", story.Id)));

        var wrapper = new XElement("XMLElement", new XAttribute("Self", self), new XAttribute("MarkupTag", markup), new XAttribute("XMLContent", story.Id));
        var ranges = story.Element.Elements("ParagraphStyleRange").ToList();
        if (ranges.Count > 0)
        {
            ranges[0].AddBeforeSelf(wrapper);
            foreach (var range in ranges)
            {
                range.Remove();
                wrapper.Add(range);
            }
        }
        else
        {
            story.Element.Add(wrapper);
        }
        return new XmlElementNode(document, wrapper);
    }

    /// <summary>Tags a frame (typically an image frame) so it can be found and filled by tag.</summary>
    public XmlElementNode TagPageItem(PageItem item, string tag)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is TextFrame frame)
        {
            return TagStory(frame.Story, tag);
        }
        Ensure(tag);
        var root = RequireRoot();
        var element = new XElement("XMLElement",
            new XAttribute("Self", root.Id + "i" + document.NewId()),
            new XAttribute("MarkupTag", "XMLTag/" + IdmlXml.EscapeSelfName(tag)),
            new XAttribute("XMLContent", item.Id));
        root.Element.Add(element);
        return new XmlElementNode(document, element);
    }

    // ---- tag → character style mappings (XML/Mapping.xml) ---------------------------------------------

    /// <summary>Tag name → character style reference, from XML/Mapping.xml (Map Tags to Styles in InDesign).</summary>
    public IReadOnlyDictionary<string, string> CharacterStyleMappings =>
        (document.Package.TryGetXml(MappingPath)?.Root?.Elements("XMLImportMap") ?? [])
            .Where(map => map.Attribute("MappedStyle")?.Value?.StartsWith("CharacterStyle/", StringComparison.Ordinal) == true)
            .GroupBy(map => TagName(map.Attribute("MarkupTag")?.Value))
            .ToDictionary(group => group.Key, group => group.First().Attribute("MappedStyle")!.Value, StringComparer.Ordinal);

    /// <summary>Maps a tag to a character style: XML imported with that tag inside running text gets the style.</summary>
    public void MapToCharacterStyle(string tag, CharacterStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        Ensure(tag);
        var xml = document.Package.TryGetXml(MappingPath);
        if (xml?.Root is null)
        {
            xml = new XDocument(IdmlXml.PackageRoot("Mapping", document.DomVersion));
            document.Package.SetXml(MappingPath, xml);
            document.AddManifestReference("Mapping", MappingPath);
        }
        var markup = "XMLTag/" + IdmlXml.EscapeSelfName(tag);
        var map = xml.Root!.Elements("XMLImportMap").FirstOrDefault(element => element.Attribute("MarkupTag")?.Value == markup);
        if (map is null)
        {
            xml.Root.Add(new XElement("XMLImportMap",
                new XAttribute("Self", "di" + document.NewId()),
                new XAttribute("MarkupTag", markup),
                new XAttribute("MappedStyle", style.Reference)));
        }
        else
        {
            map.SetAttributeValue("MappedStyle", style.Reference);
        }
    }

    // ---- binding maintenance ------------------------------------------------------------------------

    /// <summary>Removes <c>XMLContent</c> bindings to the given ids (the tags stay, empty), so nothing dangles.</summary>
    internal void Unbind(params string[] ids)
    {
        if (ids.Length == 0)
        {
            return;
        }
        var set = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var element in BindingCarriers())
        {
            if (element.Attribute("XMLContent") is { } binding && set.Contains(binding.Value))
            {
                binding.Remove();
            }
        }
    }

    /// <summary>Moves <c>XMLContent</c> bindings from one id to another (a removed image to its frame).</summary>
    internal void Rebind(string oldId, string newId)
    {
        foreach (var element in BindingCarriers())
        {
            if (element.Attribute("XMLContent")?.Value == oldId)
            {
                element.SetAttributeValue("XMLContent", newId);
            }
        }
    }

    /// <summary>XMLElements of the backing story and of every story.</summary>
    internal List<XElement> BindingCarriers()
    {
        var backing = document.Package.TryGetXml(BackingStoryPath)?.Root?.Descendants("XMLElement") ?? [];
        return backing.Concat(document.Stories.SelectMany(story => story.Element.Descendants("XMLElement"))).ToList();
    }

    internal static string TagName(string? markupTag) =>
        markupTag is null ? string.Empty : markupTag.StartsWith("XMLTag/", StringComparison.Ordinal) ? markupTag[7..].Replace("%3a", ":", StringComparison.Ordinal) : markupTag;

    internal XmlElementNode RequireRoot() => Root ?? throw new IdmlException("The document has no XML structure root (XML/BackingStory.xml).");

    /// <summary>A valid XML name for a tag (tags are XML names already; this guards odd ones).</summary>
    internal static string SafeName(string tag) => string.IsNullOrEmpty(tag) ? "_" : XmlConvert.EncodeLocalName(tag);

    internal static IEnumerable<XmlElementNode> Walk(XmlElementNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Walk))
        {
            yield return child;
        }
    }

    private static (string Segment, string Remainder) SplitFirst(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (path, string.Empty) : (path[..slash], path[(slash + 1)..]);
    }

    private static (string Tag, int? Index) ParseSegment(string segment)
    {
        var bracket = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
        {
            return (segment, null);
        }
        if (!segment.EndsWith(']') || !int.TryParse(segment.AsSpan(bracket + 1, segment.Length - bracket - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 1)
        {
            throw new IdmlException($"Invalid path segment '{segment}'; use tag or tag[n] with n starting at 1.");
        }
        return (segment[..bracket], index);
    }

    private XElement TagsRoot()
    {
        var xml = document.Package.TryGetXml(TagsPath);
        if (xml?.Root is null)
        {
            var root = IdmlXml.PackageRoot("Tags", document.DomVersion);
            xml = new XDocument(root);
            document.Package.SetXml(TagsPath, xml);
            document.AddManifestReference("Tags", TagsPath);
        }
        return xml.Root!;
    }
}
