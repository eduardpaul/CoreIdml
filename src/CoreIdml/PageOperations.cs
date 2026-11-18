using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Xml;

namespace CoreIdml;

public sealed partial class IdmlDocument
{
    /// <summary>Inserts a new page so that it becomes page <paramref name="index"/> (0-based; the page count appends it).</summary>
    public Page InsertPage(int index)
    {
        var count = Pages.Count;
        if (index < 0 || index > count)
        {
            throw new IdmlException($"Page index {index} is out of range: the document has {count} page(s), so a page can be inserted at 0 to {count}.");
        }
        AddPage();
        if (index < count)
        {
            MovePage(count, index);
        }
        return Pages[index];
    }

    /// <summary>Moves page <paramref name="from"/> so that it becomes page <paramref name="to"/> (0-based), with its items.</summary>
    public void MovePage(int from, int to)
    {
        var count = Pages.Count;
        CheckIndex(from, count);
        CheckIndex(to, count);
        var order = Enumerable.Range(0, count).ToList();
        order.RemoveAt(from);
        order.Insert(to, from);
        ReorderPages(order);
    }

    /// <summary>
    /// Deletes page <paramref name="index"/> (0-based) and the items on it (text frames take their story along unless
    /// another frame still shows it; threads are joined). The following pages move up, keeping their items.
    /// </summary>
    public void DeletePage(int index)
    {
        var pages = Pages;
        CheckIndex(index, pages.Count);
        if (pages.Count == 1)
        {
            throw new IdmlException("A document needs at least one page; this is its only page.");
        }
        var page = pages[index];
        foreach (var item in page.PageItems)
        {
            item.Remove();
        }
        var order = Enumerable.Range(0, pages.Count).Where(i => i != index).ToList();
        Repaginate(pages, order);
    }

    /// <summary>
    /// Puts the pages in a new order: <paramref name="order"/> lists every current page index once, in the new order.
    /// Pages are laid out into spreads again (one per spread, or facing pairs after a single first page), their items
    /// move with them, page numbers and sections follow, master pages and threads are untouched.
    /// </summary>
    public void ReorderPages(IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var pages = Pages;
        if (order.Count != pages.Count || order.Distinct().Count() != pages.Count || order.Any(index => index < 0 || index >= pages.Count))
        {
            throw new IdmlException($"The page order must list each of the {pages.Count} page indices (0 to {pages.Count - 1}) exactly once.");
        }
        Repaginate(pages, order);
    }

    private static void CheckIndex(int index, int count)
    {
        if (index < 0 || index >= count)
        {
            throw new IdmlException(IdmlErrorKind.NotFound, $"Page index {index} does not exist (the document has {count} page(s), 0 to {count - 1}).");
        }
    }

    /// <summary>Lays <paramref name="pages"/> out again in <paramref name="order"/>; pages left out are dropped (their items must be gone).</summary>
    private void Repaginate(IReadOnlyList<Page> pages, IReadOnlyList<int> order)
    {
        var spreads = Spreads;
        var facing = FacingPages;
        var firstSpreadPages = spreads.Count > 0 ? spreads[0].Pages.Count : 1;
        if (spreads.Any(spread => spread.Pages.Count > (facing ? 2 : 1)) || (facing && firstSpreadPages > 2))
        {
            throw new IdmlException(IdmlErrorKind.Unsupported, "This document has custom multi-page spreads; reordering or deleting its pages is not supported. Rearrange them in InDesign.");
        }

        // Snapshot every page with its own items before anything moves.
        var numbered = pages.Select((page, index) => page.Name == (index + 1).ToString(CultureInfo.InvariantCulture)).All(match => match);
        var slots = pages.Select(page => (Page: page.Element, Origin: page.SpreadBounds, Items: page.PageItems.Select(item => item.Element).ToList())).ToList();
        var sections = DesignMap.Elements("Section").Select(section => (Section: section, Start: section.Attribute("PageStart")?.Value)).ToList();
        foreach (var slot in slots)
        {
            slot.Page.Remove();
            slot.Items.ForEach(item => item.Remove());
        }

        // Facing documents: a first spread of one right-hand page (as InDesign starts them), then pairs.
        var groups = new List<List<int>>();
        for (var i = 0; i < order.Count;)
        {
            var size = !facing ? 1 : groups.Count == 0 && firstSpreadPages == 1 ? 1 : 2;
            groups.Add(order.Skip(i).Take(size).ToList());
            i += size;
        }
        var targets = new List<Spread>();
        for (var i = 0; i < groups.Count; i++)
        {
            targets.Add(i < spreads.Count ? spreads[i] : CreateSpread(targets[^1].Transform.Ty + PageSize.Height + SpreadVerticalGap));
        }
        foreach (var surplus in spreads.Skip(groups.Count))
        {
            // Items left on a dropped spread's pasteboard go to the last spread's pasteboard.
            foreach (var item in surplus.Element.Elements().Where(element => PageItem.IsPageItemKind(element.Name.LocalName)).ToList())
            {
                item.Remove();
                targets[^1].Element.Add(item);
            }
            RemoveManifestReference(surplus.PartPath);
            Package.Remove(surplus.PartPath);
        }

        for (var g = 0; g < groups.Count; g++)
        {
            var spread = targets[g].Element;
            var group = groups[g];
            var anchor = spread.Elements().FirstOrDefault(element => element.Name.LocalName is not ("Properties" or "FlattenerPreference"));
            for (var position = 0; position < group.Count; position++)
            {
                var slot = slots[group[position]];
                var size = SizeOf(slot.Page);
                // Non-facing: centred on the spread origin. Facing: a single right page at 0; a pair left of 0 and at 0.
                var x = !facing ? -size.Width / 2 : group.Count == 2 && position == 0 ? -size.Width : 0;
                slot.Page.SetAttributeValue("ItemTransform", Matrix.Translation(x, -size.Height / 2).ToString());
                if (anchor is null)
                {
                    spread.Add(slot.Page);
                }
                else
                {
                    anchor.AddBeforeSelf(slot.Page);
                }
                var origin = new Page(this, targets[g], slot.Page).SpreadBounds;
                var offset = Matrix.Translation(origin.X - slot.Origin.X, origin.Y - slot.Origin.Y);
                foreach (var item in slot.Items)
                {
                    item.SetAttributeValue("ItemTransform", Matrix.Parse(item.Attribute("ItemTransform")?.Value).Then(offset).ToString());
                    spread.Add(item);
                }
            }
            spread.SetAttributeValue("PageCount", group.Count.ToString(CultureInfo.InvariantCulture));
            spread.SetAttributeValue("BindingLocation", facing && group.Count == 2 ? "1" : "0");
        }

        var newOrder = order.Select(index => slots[index].Page).ToList();
        if (numbered)
        {
            for (var i = 0; i < newOrder.Count; i++)
            {
                newOrder[i].SetAttributeValue("Name", (i + 1).ToString(CultureInfo.InvariantCulture));
            }
        }
        UpdateSections(sections, newOrder);
        DocumentPreference?.SetAttributeValue("PagesPerDocument", newOrder.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Sections start at the same page as before (the next one if it was deleted); the first starts at page 1.</summary>
    private static void UpdateSections(List<(XElement Section, string? Start)> sections, List<XElement> pages)
    {
        if (sections.Count == 0)
        {
            return;
        }
        var position = pages.Select((page, index) => (Id: page.Attribute("Self")?.Value ?? string.Empty, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var starts = sections
            .Select(entry => (entry.Section, Index: entry.Start is not null && position.TryGetValue(entry.Start, out var index) ? index : -1))
            .ToList();
        foreach (var (section, _) in starts.Where(entry => entry.Index < 0).Skip(starts[0].Index < 0 ? 1 : 0))
        {
            section.Remove();
        }
        var kept = starts.Where(entry => entry.Section.Parent is not null).OrderBy(entry => entry.Index).ToList();
        kept[0] = (kept[0].Section, 0);
        kept = kept.GroupBy(entry => entry.Index).Select(group =>
        {
            foreach (var duplicate in group.Skip(1))
            {
                duplicate.Section.Remove();
            }
            return group.First();
        }).OrderBy(entry => entry.Index).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            var end = i + 1 < kept.Count ? kept[i + 1].Index : pages.Count;
            kept[i].Section.SetAttributeValue("PageStart", pages[kept[i].Index].Attribute("Self")?.Value);
            kept[i].Section.SetAttributeValue("Length", (end - kept[i].Index).ToString(CultureInfo.InvariantCulture));
        }
    }

    private Size SizeOf(XElement page)
    {
        var bounds = IdmlXml.ParseList(page.Attribute("GeometricBounds")?.Value);
        return bounds.Length == 4 ? new Size(bounds[3] - bounds[1], bounds[2] - bounds[0]) : PageSize;
    }
}
