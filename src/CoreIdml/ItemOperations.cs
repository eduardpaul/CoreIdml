using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Xml;

namespace CoreIdml;

public sealed partial class IdmlDocument
{
    /// <summary>
    /// Copies a top-level page item (with everything inside it) onto <paramref name="targetPage"/> (default: the item's
    /// own page), at the same position on that page moved by <paramref name="offsetX"/>/<paramref name="offsetY"/> points.
    /// Every id is new. A text frame gets its own copy of the story (unthreaded, untagged); placed images keep their links.
    /// </summary>
    public PageItem DuplicatePageItem(PageItem item, Page? targetPage = null, double offsetX = 0, double offsetY = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Parent is not null)
        {
            throw new IdmlException($"Item {item.Id} is inside a group; duplicate the group instead.");
        }
        var sourcePage = item.Page;
        targetPage ??= sourcePage;
        var copy = new XElement(item.Element);
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        void MapIds(XElement element)
        {
            foreach (var descendant in element.DescendantsAndSelf())
            {
                if (descendant.Attribute("Self")?.Value is { } self && !idMap.ContainsKey(self))
                {
                    idMap[self] = NewId();
                }
            }
        }
        MapIds(copy);
        var stories = copy.DescendantsAndSelf("TextFrame")
            .Select(frame => frame.Attribute("ParentStory")?.Value)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select(FindStory)
            .OfType<Story>()
            .Select(story => new XElement(story.Element))
            .ToList();
        stories.ForEach(MapIds);
        var copiedFrames = copy.DescendantsAndSelf("TextFrame").Select(frame => frame.Attribute("Self")!.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var element in stories.Prepend(copy).SelectMany(root => root.DescendantsAndSelf()))
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName is "NextTextFrame" or "PreviousTextFrame" && !copiedFrames.Contains(attribute.Value))
                {
                    attribute.Value = IdmlNames.NoReference;
                }
                else if (idMap.TryGetValue(attribute.Value, out var mapped))
                {
                    attribute.Value = mapped;
                }
            }
        }
        foreach (var story in stories)
        {
            // The copy is not part of the XML structure: unwrap its tags, keeping their text.
            foreach (var tag in story.Descendants("XMLElement").Reverse().ToList())
            {
                tag.ReplaceWith(tag.Nodes().Where(node => node is not XElement { Name.LocalName: "XMLAttribute" or "Properties" }).ToList());
            }
            var id = story.Attribute("Self")!.Value;
            var path = $"Stories/Story_{id}.xml";
            var root = IdmlXml.PackageRoot("Story", DomVersion);
            root.Add(story);
            Package.SetXml(path, new XDocument(root));
            AddManifestReference("Story", path);
            var storyList = DesignMap.Attribute("StoryList")?.Value;
            DesignMap.SetAttributeValue("StoryList", string.IsNullOrEmpty(storyList) ? id : storyList + " " + id);
        }

        var dx = offsetX;
        var dy = offsetY;
        if (targetPage is not null && sourcePage is not null)
        {
            var from = sourcePage.SpreadBounds;
            var to = targetPage.SpreadBounds;
            dx += to.X - from.X;
            dy += to.Y - from.Y;
        }
        copy.SetAttributeValue("ItemTransform", Matrix.Parse(copy.Attribute("ItemTransform")?.Value).Then(Matrix.Translation(dx, dy)).ToString());
        var spread = targetPage?.Spread ?? item.Spread;
        if (spread.Element == item.Element.Parent)
        {
            item.Element.AddAfterSelf(copy);
        }
        else
        {
            spread.Element.Add(copy);
        }
        return PageItem.TryWrap(this, spread, copy)!;
    }

    /// <summary>Moves a top-level page item by <paramref name="dx"/>/<paramref name="dy"/> points, onto another page when given (same position on it).</summary>
    public void MovePageItem(PageItem item, double dx, double dy, Page? targetPage = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Parent is not null)
        {
            throw new IdmlException($"Item {item.Id} is inside a group; move the group instead.");
        }
        if (targetPage is not null && item.Page is { } sourcePage && targetPage.Id != sourcePage.Id)
        {
            var from = sourcePage.SpreadBounds;
            var to = targetPage.SpreadBounds;
            dx += to.X - from.X;
            dy += to.Y - from.Y;
            if (targetPage.Spread.Element != item.Spread.Element)
            {
                var element = item.Element;
                element.Remove();
                targetPage.Spread.Element.Add(element);
                item = PageItem.TryWrap(this, targetPage.Spread, element)!;
            }
        }
        item.Transform = item.Transform.Then(Matrix.Translation(dx, dy));
    }
}
