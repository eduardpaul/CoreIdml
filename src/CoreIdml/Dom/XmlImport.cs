using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>Options for <see cref="TagCatalog.ImportXml(System.Xml.Linq.XElement, string, XmlImportOptions)"/>.</summary>
public sealed class XmlImportOptions
{
    /// <summary>
    /// Turns an <c>href</c> value into an image to place. Return null to only relink (keep size and position).
    /// By default local files (paths or <c>file:</c> URIs that exist) are placed and refitted, anything else relinked.
    /// </summary>
    public Func<string, ImageSource?>? ResolveImage { get; init; }

    public ImageFit ImageFit { get; init; } = ImageFit.Fill;

    /// <summary>Extra tag → character style name mappings, on top of the document's XML/Mapping.xml.</summary>
    public Dictionary<string, string> CharacterStyles { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Drop the simpleidml-* control attributes instead of storing them as XML attributes (default).</summary>
    public bool StripControlAttributes { get; init; } = true;
}

/// <summary>What <see cref="TagCatalog.ImportXml(System.Xml.Linq.XElement, string, XmlImportOptions)"/> did.</summary>
public sealed class XmlImportReport
{
    /// <summary>Structure elements whose content or attributes were written.</summary>
    public int Updated { get; set; }

    /// <summary>Elements created for unmatched source elements mapped to character styles.</summary>
    public int Added { get; set; }

    /// <summary>Elements removed with <c>simpleidml-setcontent="delete"</c>.</summary>
    public int Deleted { get; set; }

    /// <summary>Source elements with no counterpart in the structure (and no style mapping), as source paths.</summary>
    public List<string> Unmatched { get; } = [];

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// XML import/export over the document's XML structure, with SimpleIDML's semantics so existing data files work:
/// <list type="bullet">
/// <item><c>simpleidml-setcontent="false"</c>: keep this element's text (children are still imported).</item>
/// <item><c>simpleidml-setcontent="delete"</c>: remove the element and the frame or story it is bound to.</item>
/// <item><c>simpleidml-setcontent="clear"</c>: empty the element (keeping its style) before importing.</item>
/// <item><c>simpleidml-setcontent="remove-previous-br"</c>: remove paragraph breaks right before the element.</item>
/// <item><c>simpleidml-ignorecontent="true"</c>: skip this element and its descendants, unless a descendant has
///   <c>simpleidml-forcecontent="true"</c>. Flags combine with commas.</item>
/// <item><c>href</c>: place/relink the image of the bound frame; an empty value removes the frame.</item>
/// <item>Source children not in the structure are created only when their tag maps to a character style
///   (XML/Mapping.xml or <see cref="XmlImportOptions.CharacterStyles"/>), nesting styles like HTML.</item>
/// </list>
/// </summary>
public sealed partial class TagCatalog
{
    public const string SetContentAttribute = "simpleidml-setcontent";
    public const string IgnoreContentAttribute = "simpleidml-ignorecontent";
    public const string ForceContentAttribute = "simpleidml-forcecontent";

    private static readonly string[] MergedStyleAttributes =
        ["PointSize", "FontStyle", "HorizontalScale", "Tracking", "FillColor", "Capitalization", "Position"];

    private static readonly string[] InheritedRunAttributes =
        ["PointSize", "FontStyle", "HorizontalScale", "Tracking", "FillColor", "FillTint", "Capitalization", "Position"];

    private static readonly string[] InheritedRunProperties = ["Leading", "AppliedFont"];

    // InDesign has no "Bold" + "Italic" → "Bold Italic" logic; nesting <bold><italic> must produce the face name.
    private static readonly Dictionary<(string, string), string> MergeableFontStyles = new()
    {
        [("Condensed", "Light")] = "Condensed Light",
        [("Bold", "Italic")] = "Bold Italic",
        [("SemiBold", "Italic")] = "SemiBold Italic",
        [("Semibold", "Italic")] = "Semibold Italic",
    };

    /// <summary>Exports the structure (or the subtree at <paramref name="at"/>) as XML, like File &gt; Export &gt; XML.</summary>
    public XElement ExportXml(string? at = null, bool includeImageLinks = true)
    {
        var node = at is null ? RequireRoot() : SelectSingle(at);
        return Export(node, includeImageLinks);
    }

    /// <summary>Imports XML onto the structure element at <paramref name="at"/> (default: the root).</summary>
    public XmlImportReport ImportXml(XElement source, string at = "/Root", XmlImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new XmlImportOptions();
        var target = SelectSingle(at);
        var report = new XmlImportReport();
        var mappings = new Dictionary<string, string>(CharacterStyleMappings, StringComparer.Ordinal);
        foreach (var (tag, styleName) in options.CharacterStyles)
        {
            mappings[tag] = document.Styles.FindCharacterStyle(styleName)?.Reference
                ?? throw new IdmlException(IdmlErrorKind.NotFound, $"Character style '{styleName}' (mapped from tag '{tag}') does not exist.");
        }
        var context = new ImportContext(options, report, mappings);
        ClearMarked(source, target);
        Import(source, target, ignoredByParent: false, context, [target.Tag]);
        return report;
    }

    public XmlImportReport ImportXml(string xml, string at = "/Root", XmlImportOptions? options = null) =>
        ImportXml(ParseData(xml), at, options);

    /// <summary>
    /// Parses XML data keeping its whitespace, with line endings normalized to "\n" as the XML specification asks
    /// (XElement.Parse keeps "\r\n" in text, so a file saved on Windows would otherwise put "\r" into the stories).
    /// </summary>
    internal static XElement ParseData(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return XElement.Parse(xml.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), LoadOptions.PreserveWhitespace);
    }

    // ---- export ---------------------------------------------------------------------------------------

    private XElement Export(XmlElementNode node, bool includeImageLinks)
    {
        var result = new XElement(SafeName(node.Tag));
        foreach (var (name, value) in node.Attributes)
        {
            result.SetAttributeValue(SafeName(name), value);
        }
        if (includeImageLinks && result.Attribute("href") is null && node.BoundPageItem is GraphicFrame { LinkUri: { } uri })
        {
            result.SetAttributeValue("href", uri);
        }
        var content = node.ContentElement;
        ExportContent(content, content, node, result, includeImageLinks);
        return result;
    }

    private void ExportContent(XElement element, XElement owner, XmlElementNode node, XElement result, bool includeImageLinks)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "XMLElement":
                    result.Add(Export(new XmlElementNode(document, child, node), includeImageLinks));
                    break;
                case "Content":
                    result.Add(child.Value);
                    break;
                case "Br":
                    result.Add("\n");
                    break;
                case "Properties" or "XMLAttribute" or "Note":
                    break;
                case "Change" when TrackedChanges.IsDeletion(child):
                    // Deleted tracked text is not part of the current text (see TrackedChanges).
                    break;
                default:
                    ExportContent(child, owner, node, result, includeImageLinks);
                    break;
            }
        }
    }

    // ---- import ---------------------------------------------------------------------------------------

    private sealed record ImportContext(XmlImportOptions Options, XmlImportReport Report, Dictionary<string, string> Mappings);

    private static (HashSet<string> SetContent, bool Ignore, bool Force) Flags(XElement source) =>
        ((source.Attribute(SetContentAttribute)?.Value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
         source.Attribute(IgnoreContentAttribute)?.Value == "true",
         source.Attribute(ForceContentAttribute)?.Value == "true");

    private static void ClearMarked(XElement source, XmlElementNode target)
    {
        if (Flags(source).SetContent.Contains("clear"))
        {
            ClearKeepingStyle(target.ContentElement);
        }
        foreach (var (sourceChild, targetChild) in Match(source, target.Children))
        {
            if (targetChild is not null)
            {
                ClearMarked(sourceChild, targetChild);
            }
        }
    }

    private void Import(XElement source, XmlElementNode target, bool ignoredByParent, ImportContext context, List<string> path)
    {
        var (setContent, ignore, force) = Flags(source);
        if (!ignoredByParent || force)
        {
            foreach (var attribute in source.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                var name = attribute.Name.LocalName;
                var isControl = name is SetContentAttribute or IgnoreContentAttribute or ForceContentAttribute;
                if (name == "href")
                {
                    if (ApplyHref(target, attribute.Value, context))
                    {
                        context.Report.Deleted++;
                        return;
                    }
                }
                if (!isControl || !context.Options.StripControlAttributes)
                {
                    target.SetXmlAttribute(name, attribute.Value);
                }
            }
            if (setContent.Contains("remove-previous-br"))
            {
                RemovePreviousBreaks(target.ContentElement);
            }
            if (setContent.Contains("delete"))
            {
                Delete(target);
                context.Report.Deleted++;
                return;
            }
            var text = LeadingText(source);
            // Indentation before the first child of a pretty-printed container is not content.
            var isLayoutWhitespace = source.HasElements && string.IsNullOrWhiteSpace(text);
            if (!setContent.Contains("false") && !isLayoutWhitespace)
            {
                if (target.BoundPageItem is null || target.BoundStory is not null)
                {
                    if (target.Children.Count == 0 && target.BoundStory is { } story && target.ContentElement == target.Element)
                    {
                        story.SetText(text);
                    }
                    else if (target.ContentElement.Ancestors("Story").Any())
                    {
                        TextOps.SetOwnText(target.ContentElement, text);
                    }
                    else if (text.Trim().Length > 0)
                    {
                        context.Report.Warnings.Add($"{target.Path}: not bound to text; its text was not imported.");
                    }
                }
            }
            context.Report.Updated++;
        }

        var ignoreChildren = ignore || (ignoredByParent && !force);
        var targetChildren = target.Children;
        foreach (var (sourceChild, targetChild) in Match(source, targetChildren))
        {
            var childPath = new List<string>(path) { sourceChild.Name.LocalName };
            if (targetChild is not null)
            {
                Import(sourceChild, targetChild, ignoreChildren, context, childPath);
            }
            else if (!ignoreChildren && context.Mappings.ContainsKey(sourceChild.Name.LocalName))
            {
                ImportNew(sourceChild, target, context, childPath);
            }
            else if (!ignoreChildren)
            {
                context.Report.Unmatched.Add("/" + string.Join('/', childPath));
            }
        }
    }

    /// <summary>
    /// Pairs source children with structure children in order. Unlike SimpleIDML, which only compares with the
    /// current structure child, this looks ahead: a source that leaves out an element (say, the picture) still
    /// matches the elements after it.
    /// </summary>
    private static List<(XElement Source, XmlElementNode? Target)> Match(XElement source, IReadOnlyList<XmlElementNode> targets)
    {
        var result = new List<(XElement, XmlElementNode?)>();
        var next = 0;
        foreach (var element in source.Elements())
        {
            var found = -1;
            for (var i = next; i < targets.Count; i++)
            {
                if (targets[i].Tag == element.Name.LocalName)
                {
                    found = i;
                    break;
                }
            }
            if (found >= 0)
            {
                result.Add((element, targets[found]));
                next = found + 1;
            }
            else
            {
                result.Add((element, null));
            }
        }
        return result;
    }

    /// <summary>Text before the first child element (SimpleIDML's <c>element.text</c>).</summary>
    private static string LeadingText(XElement source) =>
        string.Concat(source.Nodes().TakeWhile(node => node is not XElement).OfType<XText>().Select(text => text.Value));

    /// <returns>True when the element was deleted (empty href).</returns>
    private bool ApplyHref(XmlElementNode target, string href, ImportContext context)
    {
        var item = target.BoundPageItem;
        if (href.Length == 0)
        {
            if (item is not null)
            {
                item.Remove();
            }
            Unbind(target.ContentId is { } id ? [id] : []);
            return item is not null;
        }
        if (item is not GraphicFrame frame)
        {
            context.Report.Warnings.Add($"{target.Path}: href given but the element is not bound to a graphic frame.");
            return false;
        }
        var image = context.Options.ResolveImage is { } resolve ? resolve(href) : DefaultResolve(href);
        if (image is not null)
        {
            frame.PlaceImage(image, context.Options.ImageFit);
        }
        else if (frame.Graphic is not null)
        {
            frame.Relink(href);
        }
        else
        {
            context.Report.Warnings.Add($"{target.Path}: '{href}' cannot be read and the frame has no image to relink.");
        }
        return false;
    }

    private static ImageSource? DefaultResolve(string href)
    {
        var path = href;
        if (href.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // file:/C:/x.jpg (Windows) or file:/home/x.jpg, file:///home/x.jpg (Unix).
            var trimmed = Uri.UnescapeDataString(href["file:".Length..]).TrimStart('/');
            path = OperatingSystem.IsWindows() ? trimmed : "/" + trimmed;
        }
        try
        {
            return File.Exists(path) ? ImageSource.FromFile(path) : null;
        }
        catch (IdmlException)
        {
            // Not a JPEG/PNG we can measure (PDF, TIFF, ...): relink only.
            return null;
        }
    }

    private void Delete(XmlElementNode target)
    {
        var self = target.Id;
        if (target.BoundStory is { } story)
        {
            foreach (var frame in story.Frames)
            {
                frame.Remove();
            }
        }
        else if (target.BoundPageItem is { } item)
        {
            item.Remove();
        }
        // Remove every copy (structure entry, in-story wrapper, reference placeholder).
        foreach (var copy in BindingCarriers().Where(element => element.Attribute("Self")?.Value == self))
        {
            copy.Remove();
        }
    }

    private static void RemovePreviousBreaks(XElement element)
    {
        // Walk backwards through the text before the element until the previous tag.
        foreach (var previous in element.ElementsBeforeSelfInDocument().ToList())
        {
            if (previous.Name.LocalName == "XMLElement")
            {
                break;
            }
            if (previous.Name.LocalName == "Br")
            {
                previous.Remove();
            }
        }
    }

    private static void ClearKeepingStyle(XElement element)
    {
        var range = element.Descendants("ParagraphStyleRange").FirstOrDefault();
        var run = element.Descendants("CharacterStyleRange").FirstOrDefault();
        element.Nodes().Where(node => node is not XElement { Name.LocalName: "XMLAttribute" or "Properties" }).Remove();
        if (range is not null)
        {
            var newRange = TextOps.ShallowCopy(range);
            if (run is not null)
            {
                newRange.Add(TextOps.ShallowCopy(run));
            }
            element.Add(newRange);
        }
        else if (run is not null && !element.Ancestors("CharacterStyleRange").Any())
        {
            element.Add(TextOps.ShallowCopy(run));
        }
    }

    /// <summary>Creates a tag for an unmatched source element mapped to a character style, with its text and tail.</summary>
    private void ImportNew(XElement source, XmlElementNode parent, ImportContext context, List<string> path)
    {
        var tag = source.Name.LocalName;
        Ensure(tag);
        var host = parent.ContentElement;
        var parentRun = host.Descendants("CharacterStyleRange").LastOrDefault() ?? host.Ancestors("CharacterStyleRange").FirstOrDefault();
        var element = new XElement("XMLElement",
            new XAttribute("Self", parent.Id + "i" + document.NewId()),
            new XAttribute("MarkupTag", "XMLTag/" + IdmlXml.EscapeSelfName(tag)));
        foreach (var attribute in source.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            var name = attribute.Name.LocalName;
            if (!(name is SetContentAttribute or IgnoreContentAttribute or ForceContentAttribute) || !context.Options.StripControlAttributes)
            {
                element.Add(new XElement("XMLAttribute",
                    new XAttribute("Self", element.Attribute("Self")!.Value + "XMLAttributen" + name),
                    new XAttribute("Name", name),
                    new XAttribute("Value", attribute.Value)));
            }
        }

        var insideRun = host.Ancestors("CharacterStyleRange").Any() && !host.Elements("CharacterStyleRange").Any();
        var run = insideRun ? null : NewStyledRun(path, parentRun, context);
        var text = LeadingText(source);
        if (run is null)
        {
            element.Add(TextOps.TextNodes(text));
        }
        else
        {
            run.Add(TextOps.TextNodes(text));
            element.Add(run);
        }

        // Paragraph-level hosts (a tagged story) hold ranges: the new inline tag goes into the last paragraph.
        var container = host.Elements("ParagraphStyleRange").LastOrDefault() ?? host;
        container.Add(element);
        context.Report.Added++;

        var node = new XmlElementNode(document, element, parent);
        foreach (var child in source.Elements())
        {
            ImportNew(child, node, context, [.. path, child.Name.LocalName]);
        }

        if (source.NextNode is XText { Value.Length: > 0 } tail)
        {
            if (insideRun || parentRun is null)
            {
                container.Add(TextOps.TextNodes(tail.Value));
            }
            else
            {
                var tailRun = TextOps.ShallowCopy(parentRun);
                tailRun.Add(TextOps.TextNodes(tail.Value));
                container.Add(tailRun);
            }
        }
    }

    /// <summary>
    /// The run for a new nested tag: the outermost mapped style along the path is applied, inner mapped styles add
    /// their attributes locally (Bold inside Italic gives "Bold Italic"), and local formatting of the surrounding run
    /// carries over where the style does not set it.
    /// </summary>
    private XElement NewStyledRun(List<string> path, XElement? parentRun, ImportContext context)
    {
        var styles = path
            .Where(context.Mappings.ContainsKey)
            .Select(tag => document.Styles.FindCharacterStyle(context.Mappings[tag]))
            .OfType<CharacterStyle>()
            .ToList();
        if (styles.Count == 0)
        {
            return parentRun is null
                ? new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle))
                : TextOps.ShallowCopy(parentRun);
        }
        var root = styles[0];
        var run = new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", root.Reference));
        foreach (var inner in styles.Skip(1))
        {
            foreach (var name in MergedStyleAttributes)
            {
                if (inner.Element.Attribute(name)?.Value is not { } value)
                {
                    continue;
                }
                if (name == "FontStyle")
                {
                    var applied = run.Attribute(name)?.Value ?? root.Element.Attribute(name)?.Value;
                    value = applied is not null && MergeableFontStyles.TryGetValue((applied, value), out var merged) ? merged : value;
                }
                run.SetAttributeValue(name, value);
            }
        }
        if (parentRun is not null)
        {
            foreach (var name in InheritedRunAttributes)
            {
                if (parentRun.Attribute(name)?.Value is { } value && run.Attribute(name) is null && root.Element.Attribute(name) is null)
                {
                    run.SetAttributeValue(name, value);
                }
            }
            foreach (var name in InheritedRunProperties)
            {
                if (IdmlXml.GetProperty(parentRun, name) is not null && IdmlXml.GetProperty(root.Element, name) is null
                    && parentRun.Element(IdmlXml.Properties)?.Element(name) is { } property)
                {
                    IdmlXml.GetOrCreateProperties(run).Add(new XElement(property));
                }
            }
        }
        return run;
    }
}

internal static class XNodeExtensions
{
    /// <summary>Elements before this one in document order, nearest first (the XPath preceding axis, reversed).</summary>
    public static IEnumerable<XElement> ElementsBeforeSelfInDocument(this XElement element)
    {
        for (XElement? current = element; current is not null && current.Name.LocalName != "Story"; current = current.Parent)
        {
            foreach (var sibling in current.ElementsBeforeSelf().Reverse())
            {
                foreach (var descendant in sibling.DescendantsAndSelf().Reverse())
                {
                    yield return descendant;
                }
            }
        }
    }
}
