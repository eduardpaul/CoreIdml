using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Xml;

namespace CoreIdml;

public sealed partial class IdmlDocument
{
    /// <summary>
    /// Copies pages of another document to the end of this one (SimpleIDML's add_page_from_idml / insert_idml):
    /// their page items, the stories they show, images, tables, XML tags and structure entries. Every id is remapped
    /// so nothing collides; styles, swatches, tags, mappings and fonts missing here are copied once (matched by id),
    /// and layers are matched by name. Items keep their position on the page. Master-page items are not copied:
    /// new pages use this document's first master.
    /// </summary>
    /// <param name="source">The document to copy from (left unchanged). It may be this same document.</param>
    /// <param name="pageIndices">0-based pages of <paramref name="source"/>; all pages when empty.</param>
    /// <returns>The new pages, in order.</returns>
    public IReadOnlyList<Page> ImportPages(IdmlDocument source, params int[] pageIndices)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourcePages = source.Pages;
        var indices = pageIndices.Length == 0 ? Enumerable.Range(0, sourcePages.Count).ToArray() : pageIndices;
        foreach (var index in indices)
        {
            if (index < 0 || index >= sourcePages.Count)
            {
                throw new IdmlException(IdmlErrorKind.NotFound, $"Source page index {index} does not exist (the document has {sourcePages.Count} pages).");
            }
        }

        MergeResources(source);
        var layerMap = source.Layers.All.ToDictionary(layer => layer.Id, layer => Layers.Add(layer.Name).Id, StringComparer.Ordinal);

        // What gets copied: top-level items on the chosen pages and the stories their text frames show.
        var pageItems = indices.Select(index => (Page: sourcePages[index], Items: sourcePages[index].PageItems)).ToList();
        var copiedItems = pageItems.SelectMany(entry => entry.Items).ToList();
        var storyIds = copiedItems.SelectMany(item => item.SelfAndDescendants()).OfType<TextFrame>()
            .Select(frame => frame.StoryId).Distinct(StringComparer.Ordinal).ToList();
        var stories = storyIds.Select(source.FindStory).OfType<Story>().ToList();

        // One old → new id map for everything copied, so cross references (threads, XMLContent, wrapper/structure
        // pairs sharing a Self) stay consistent.
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = Tags.RequireRoot();
        void MapIds(XElement element)
        {
            foreach (var descendant in element.DescendantsAndSelf())
            {
                if (descendant.Attribute("Self")?.Value is { } self && !idMap.ContainsKey(self))
                {
                    idMap[self] = descendant.Name.LocalName == "XMLElement" ? root.Id + "i" + NewId() : NewId();
                }
            }
        }
        copiedItems.ForEach(item => MapIds(item.Element));
        stories.ForEach(story => MapIds(story.Element));

        var copiedContent = idMap.Keys.ToHashSet(StringComparer.Ordinal);
        var structureEntries = source.Package.TryGetXml("XML/BackingStory.xml")?.Root?.Descendants("XMLElement")
            .Where(element => element.Attribute("XMLContent")?.Value is { } id && copiedContent.Contains(id))
            .ToList() ?? [];
        structureEntries.ForEach(element => MapIds(new XElement(element.Name, element.Attributes())));

        var newPages = new List<Page>();
        foreach (var (sourcePage, items) in pageItems)
        {
            var page = AddPage();
            newPages.Add(page);
            var from = sourcePage.SpreadBounds;
            var to = page.SpreadBounds;
            var offset = Matrix.Translation(to.X - from.X, to.Y - from.Y);
            foreach (var item in items)
            {
                var copy = Remap(new XElement(item.Element), idMap, layerMap);
                copy.SetAttributeValue("ItemTransform", Matrix.Parse(copy.Attribute("ItemTransform")?.Value).Then(offset).ToString());
                page.Spread.Element.Add(copy);
            }
        }

        foreach (var story in stories)
        {
            var newId = idMap[story.Id];
            var path = $"Stories/Story_{newId}.xml";
            var storyRoot = IdmlXml.PackageRoot("Story", DomVersion);
            storyRoot.Add(Remap(new XElement(story.Element), idMap, layerMap));
            Package.SetXml(path, new XDocument(storyRoot));
            AddManifestReference("Story", path);
            var storyList = DesignMap.Attribute("StoryList")?.Value;
            DesignMap.SetAttributeValue("StoryList", string.IsNullOrEmpty(storyList) ? newId : storyList + " " + newId);
        }

        foreach (var entry in structureEntries)
        {
            var copy = new XElement(entry.Name, entry.Attributes(), entry.Elements("XMLAttribute").Select(attribute => new XElement(attribute)));
            root.Element.Add(Remap(copy, idMap, layerMap));
        }

        // Threads or tags pointing at things that were not copied must not dangle.
        var known = new HashSet<string>(idMap.Values, StringComparer.Ordinal);
        foreach (var page in newPages)
        {
            foreach (var frame in page.Spread.Element.Descendants("TextFrame"))
            {
                foreach (var name in (string[])["NextTextFrame", "PreviousTextFrame"])
                {
                    if (frame.Attribute(name)?.Value is { } target && target != IdmlNames.NoReference && !known.Contains(target) && !idMap.ContainsKey(target))
                    {
                        frame.SetAttributeValue(name, IdmlNames.NoReference);
                    }
                }
            }
        }
        foreach (var story in stories.Select(story => RequireStory(idMap[story.Id])))
        {
            foreach (var element in story.Element.Descendants("XMLElement"))
            {
                if (element.Attribute("XMLContent") is { } binding && !known.Contains(binding.Value))
                {
                    binding.Remove();
                }
            }
        }
        return newPages;
    }

    /// <summary>Copies every page of <paramref name="source"/> after the last page of this document.</summary>
    public IReadOnlyList<Page> AppendDocument(IdmlDocument source) => ImportPages(source);

    private static XElement Remap(XElement element, Dictionary<string, string> idMap, Dictionary<string, string> layerMap)
    {
        foreach (var descendant in element.DescendantsAndSelf())
        {
            foreach (var attribute in descendant.Attributes())
            {
                if (attribute.Name.LocalName == "ItemLayer" && layerMap.TryGetValue(attribute.Value, out var layer))
                {
                    attribute.Value = layer;
                }
                else if (idMap.TryGetValue(attribute.Value, out var mapped))
                {
                    attribute.Value = mapped;
                }
            }
        }
        return element;
    }

    /// <summary>Copies resources of <paramref name="source"/> that this document lacks, matched by Self id.</summary>
    private void MergeResources(IdmlDocument source)
    {
        if (ReferenceEquals(source, this))
        {
            return;
        }
        MergeChildren(source.GraphicRoot, GraphicRoot);
        MergeChildren(source.StylesRoot, StylesRoot);
        foreach (var tag in source.Tags.Names)
        {
            Tags.Ensure(tag);
        }
        foreach (var (tag, styleReference) in source.Tags.CharacterStyleMappings)
        {
            if (!Tags.CharacterStyleMappings.ContainsKey(tag) && Styles.FindCharacterStyle(styleReference) is { } style)
            {
                Tags.MapToCharacterStyle(tag, style);
            }
        }
        var fonts = Package.TryGetXml("Resources/Fonts.xml")?.Root;
        var sourceFonts = source.Package.TryGetXml("Resources/Fonts.xml")?.Root;
        if (fonts is not null && sourceFonts is not null)
        {
            var names = fonts.Elements("FontFamily").Select(family => family.Attribute("Name")?.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var family in sourceFonts.Elements("FontFamily").Where(family => !names.Contains(family.Attribute("Name")?.Value)))
            {
                fonts.Add(new XElement(family));
            }
        }
    }

    private static void MergeChildren(XElement source, XElement target)
    {
        var existing = target.Descendants().Select(element => element.Attribute("Self")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var child in source.Elements())
        {
            // Root style groups are singletons whose ids differ between documents: match them by element name.
            if (child.Name.LocalName.StartsWith("Root", StringComparison.Ordinal) && target.Element(child.Name) is { } rootGroup)
            {
                MergeChildren(child, rootGroup);
                continue;
            }
            var self = child.Attribute("Self")?.Value;
            if (self is null)
            {
                continue;
            }
            if (!existing.Contains(self))
            {
                target.Add(new XElement(child));
            }
            else if (child.Elements().Any(grandchild => grandchild.Attribute("Self") is not null)
                && target.Descendants(child.Name).FirstOrDefault(element => element.Attribute("Self")?.Value == self) is { } match)
            {
                // A style group present in both: merge its members.
                MergeChildren(child, match);
            }
        }
    }
}
