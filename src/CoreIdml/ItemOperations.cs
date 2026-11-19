using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Xml;

namespace CoreIdml;

public sealed partial class IdmlDocument
{
    /// <summary>Every text frame on the document and master spreads (frames anchored in stories are not page items).</summary>
    internal IEnumerable<TextFrame> AllTextFrames() =>
        Spreads.Concat<Spread>(MasterSpreads).SelectMany(spread => spread.TextFrames());

    /// <summary>
    /// Removes page items (with everything inside them) in one pass: threads are joined around the text frames removed,
    /// a story goes when no remaining frame shows it, and the XML bindings of everything removed are dropped. The
    /// document and every story are walked once, however many items go.
    /// </summary>
    internal void RemoveItems(IReadOnlyCollection<PageItem> items)
    {
        var attached = items.Where(item => item.Element.Parent is not null).ToList();
        if (attached.Count == 0)
        {
            return;
        }
        var ids = attached.SelectMany(item => item.Element.DescendantsAndSelf()).Select(element => element.Attribute("Self")?.Value).OfType<string>().ToList();
        var removedFrames = attached.SelectMany(item => item.SelfAndDescendants()).OfType<TextFrame>().ToList();
        if (removedFrames.Count > 0)
        {
            var frames = new Dictionary<string, TextFrame>(StringComparer.Ordinal);
            foreach (var frame in AllTextFrames())
            {
                frames.TryAdd(frame.Id, frame);
            }
            var removed = removedFrames.Select(frame => frame.Id).ToHashSet(StringComparer.Ordinal);

            // The nearest frame that stays, walking the thread through removed frames.
            TextFrame? Staying(string? id, bool forward)
            {
                for (var steps = 0; id is not null && steps <= frames.Count && frames.TryGetValue(id, out var frame); steps++)
                {
                    if (!removed.Contains(id))
                    {
                        return frame;
                    }
                    id = forward ? frame.NextFrameId : frame.PreviousFrameId;
                }
                return null;
            }
            foreach (var frame in removedFrames)
            {
                var previous = Staying(frame.PreviousFrameId, forward: false);
                var next = Staying(frame.NextFrameId, forward: true);
                previous?.SetAttribute("NextTextFrame", next?.Id ?? IdmlNames.NoReference);
                next?.SetAttribute("PreviousTextFrame", previous?.Id ?? IdmlNames.NoReference);
            }

            var shown = frames.Values.Where(frame => !removed.Contains(frame.Id)).Select(frame => frame.StoryId).ToHashSet(StringComparer.Ordinal);
            foreach (var storyId in removedFrames.Select(frame => frame.StoryId).Distinct(StringComparer.Ordinal).ToList())
            {
                if (!shown.Contains(storyId) && FindStory(storyId) is { } story)
                {
                    DeleteStory(story);
                    ids.Add(storyId);
                }
            }
        }
        attached.ForEach(item => item.Element.Remove());
        Tags.Unbind([.. ids]);
    }

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
            AddStoryPart(story);
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
