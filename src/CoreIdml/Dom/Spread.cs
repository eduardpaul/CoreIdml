using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>A spread: one or two pages plus the page items laid out on them, stored in its own package part.</summary>
public class Spread : IdmlNode
{
    internal Spread(IdmlDocument document, string partPath, XElement element)
        : base(document, element)
    {
        PartPath = partPath;
    }

    /// <summary>Package path of the part holding this spread (e.g. <c>Spreads/Spread_u1f0.xml</c>).</summary>
    public string PartPath { get; }

    public Matrix Transform => Matrix.Parse(GetAttribute("ItemTransform"));

    public IReadOnlyList<Page> Pages => Element.Elements("Page").Select(page => new Page(Document, this, page)).ToList();

    /// <summary>Top-level page items in z-order (back to front).</summary>
    public IReadOnlyList<PageItem> PageItems =>
        Element.Elements().Select(element => PageItem.TryWrap(Document, this, element)).OfType<PageItem>().ToList();

    /// <summary>Every page item, including items nested inside groups.</summary>
    public IEnumerable<PageItem> AllPageItems => PageItems.SelectMany(item => item.SelfAndDescendants());

    /// <summary>
    /// The text frames in <see cref="AllPageItems"/> order (only those of <paramref name="storyId"/> when given),
    /// walking the XML the same way (into groups and frames) without wrapping every other item.
    /// </summary>
    internal List<TextFrame> TextFrames(string? storyId = null)
    {
        var frames = new List<TextFrame>();
        CollectTextFrames(Element, storyId, frames);
        return frames;
    }

    private void CollectTextFrames(XElement container, string? storyId, List<TextFrame> output)
    {
        foreach (var child in container.Elements())
        {
            var kind = child.Name.LocalName;
            if (!PageItem.IsPageItemKind(kind))
            {
                continue;
            }
            if (kind == "TextFrame" && (storyId is null || (child.Attribute("ParentStory")?.Value ?? string.Empty) == storyId))
            {
                output.Add(new TextFrame(Document, this, child));
            }
            if (PageItem.HoldsItems(kind))
            {
                CollectTextFrames(child, storyId, output);
            }
        }
    }

    internal Page AppendPage(double x, Size size, MasterSpread? master, string? name = null)
    {
        var pages = Element.Elements("Page").ToList();
        var page = new XElement("Page",
            new XAttribute("Self", Document.NewId()),
            new XAttribute("GeometricBounds", IdmlXml.FormatList(0, 0, size.Height, size.Width)),
            new XAttribute("ItemTransform", Matrix.Translation(x, -size.Height / 2).ToString()),
            new XAttribute("Name", name ?? NextPageName()),
            new XAttribute("AppliedTrapPreset", "TrapPreset/$ID/kDefaultTrapStyleName"),
            new XAttribute("OverrideList", string.Empty),
            new XAttribute("AppliedMaster", master?.Id ?? IdmlNames.NoReference),
            new XAttribute("MasterPageTransform", Matrix.Identity.ToString()),
            new XAttribute("TabOrder", string.Empty),
            new XAttribute("GridStartingPoint", "TopOutside"),
            new XAttribute("UseMasterGrid", "true"),
            new XElement("Properties", new XElement("PageColor", new XAttribute("type", "enumeration"), "UseMasterColor")));

        var margins = Document.DefaultMarginPreference is { } defaults
            ? new XElement(defaults)
            : new XElement("MarginPreference");
        if (Document.DefaultMarginPreference is null)
        {
            IdmlDocument.SetMargins(margins, Spacing.All(36), 1, 12, size.Width);
        }
        page.Add(margins);

        // Pages come before page items; a new page goes right after the last existing page.
        if (pages.Count > 0)
        {
            pages[^1].AddAfterSelf(page);
        }
        else if (Element.Elements().FirstOrDefault(element => element.Name.LocalName is not ("Properties" or "FlattenerPreference")) is { } firstItem)
        {
            firstItem.AddBeforeSelf(page);
        }
        else
        {
            Element.Add(page);
        }

        var count = pages.Count + 1;
        SetAttribute("PageCount", count.ToString(CultureInfo.InvariantCulture));
        if (this is not MasterSpread)
        {
            SetAttribute("BindingLocation", Document.FacingPages && (count == 2 || x < 0) ? "1" : "0");
        }
        return new Page(Document, this, page);
    }

    private string NextPageName()
    {
        var pages = Document.Pages;
        var lastName = pages.Count > 0 ? pages[^1].Name : null;
        return int.TryParse(lastName, CultureInfo.InvariantCulture, out var number)
            ? (number + 1).ToString(CultureInfo.InvariantCulture)
            : (Document.Pages.Count + 1).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>A master (parent) spread whose items repeat on every page that applies it.</summary>
public sealed class MasterSpread : Spread
{
    internal MasterSpread(IdmlDocument document, string partPath, XElement element)
        : base(document, partPath, element)
    {
    }

    public string Name => GetAttribute("Name") ?? string.Empty;
}
