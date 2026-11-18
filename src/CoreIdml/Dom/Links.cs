using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>A link to an external file (placed image, PDF, ...): the <c>Link</c> element inside a placed graphic.</summary>
public sealed class ImageLink : IdmlNode
{
    internal ImageLink(IdmlDocument document, XElement element, string partPath)
        : base(document, element)
    {
        PartPath = partPath;
    }

    /// <summary>Package part holding the link (a spread, master spread, or story for images anchored in text).</summary>
    public string PartPath { get; }

    public string Uri => GetAttribute("LinkResourceURI") ?? string.Empty;

    /// <summary>InDesign's format name, e.g. <c>$ID/JPEG</c>.</summary>
    public string? Format => GetAttribute("LinkResourceFormat");

    /// <summary>The placed graphic (Image, PDF, EPS...).</summary>
    public XElement? Graphic => Element.Parent;

    public string? GraphicId => Graphic?.Attribute("Self")?.Value;

    /// <summary>The frame holding the graphic.</summary>
    public string? FrameId => Graphic?.Parent?.Attribute("Self")?.Value;

    public string? ActualPpi => Graphic?.Attribute("ActualPpi")?.Value;

    public string? EffectivePpi => Graphic?.Attribute("EffectivePpi")?.Value;

    /// <summary>True for links in text (anchored or inline graphics).</summary>
    public bool IsInStory => PartPath.StartsWith("Stories/", StringComparison.Ordinal);

    /// <summary>
    /// Points the link at another file (URI or local path). With <paramref name="refit"/> and a readable local
    /// JPEG/PNG, the image is re-measured and fitted; otherwise position and scale are kept.
    /// </summary>
    public void Relink(string uriOrPath, ImageFit? refit = null)
    {
        if (refit is { } fit && !IsInStory && Document.AllPageItems.OfType<GraphicFrame>().FirstOrDefault(frame => frame.Id == FrameId) is { } frame)
        {
            var path = uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? null : uriOrPath;
            if (path is not null && File.Exists(path))
            {
                frame.PlaceImage(ImageSource.FromFile(path), fit);
                return;
            }
        }
        SetAttribute("LinkResourceURI", ImageSource.ToLinkUriOrKeep(uriOrPath));
    }
}

/// <summary>A font family used by the document (Resources/Fonts.xml).</summary>
public sealed record FontFamilyInfo(string Name, IReadOnlyList<string> Styles, IReadOnlyList<string> Statuses);

public sealed partial class LayerCatalog
{
    /// <summary>Whether any page item (masters included) sits on the layer.</summary>
    public bool IsUsed(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return ItemsOn(layer.Id).Any();
    }

    /// <summary>
    /// Removes a layer. Its items are deleted, or moved to <paramref name="moveItemsTo"/>; the active layer moves to
    /// another layer when needed. The last layer cannot be removed.
    /// </summary>
    public void Remove(Layer layer, Layer? moveItemsTo = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var layers = All;
        if (layers.Count <= 1)
        {
            throw new IdmlException("A document needs at least one layer.");
        }
        foreach (var item in ItemsOn(layer.Id).ToList())
        {
            if (moveItemsTo is not null)
            {
                item.SetAttributeValue("ItemLayer", moveItemsTo.Id);
            }
            else if (PageItem.TryWrap(document, new Spread(document, string.Empty, item.Ancestors().First(ancestor => ancestor.Name.LocalName is "Spread" or "MasterSpread")), item) is { } wrapped)
            {
                wrapped.Remove();
            }
            else
            {
                item.Remove();
            }
        }
        layer.Element.Remove();
        if (document.DesignMap.Attribute("ActiveLayer")?.Value == layer.Id)
        {
            document.DesignMap.SetAttributeValue("ActiveLayer", (moveItemsTo ?? All[0]).Id);
        }
    }

    /// <summary>Removes layers no page item uses; returns how many were removed.</summary>
    public int RemoveUnused()
    {
        var removed = 0;
        foreach (var layer in All)
        {
            if (All.Count > 1 && !IsUsed(layer))
            {
                Remove(layer);
                removed++;
            }
        }
        return removed;
    }

    private IEnumerable<XElement> ItemsOn(string layerId) =>
        document.Spreads.Cast<Spread>().Concat(document.MasterSpreads)
            .SelectMany(spread => spread.Element.Descendants())
            .Where(element => element.Attribute("ItemLayer")?.Value == layerId);
}
