using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;

namespace CoreIdml.Pdf.Rendering;

/// <summary>Text wrap (<c>TextWrapPreference</c>) and text in shaped frames: the <see cref="TextArea"/> of a text frame.</summary>
internal sealed partial class IdmlRenderer
{
    // The wrap regions other items put on each text frame, by frame id: they do not change while the document renders.
    private readonly Dictionary<string, List<WrapRegion>> wrapsByFrame = new(StringComparer.Ordinal);

    // Text wrap only affects text beneath (Preferences > Composition, TextPreference@ZOrderTextWrap).
    private bool? wrapBeneathOnly;

    // The opaque strips of placed images with alpha-channel contours, by graphic id (see AlphaShape).
    private readonly Dictionary<string, List<Rect>> alphaShapes = new(StringComparer.Ordinal);

    // The alpha a pixel needs to be inside an alpha-channel contour, and the most strips of rows an image is read in.
    private const int AlphaThreshold = 230;
    private const int MaxAlphaStrips = 512;

    /// <summary>
    /// Where the lines of <paramref name="frame"/> may go in its columns (inner bounds <paramref name="inner"/>, text
    /// inset with the stroke <paramref name="inset"/>): its own shape when it is not a plain rectangle, less the wraps of
    /// the items around it; null when nothing limits the columns.
    /// </summary>
    private TextArea? TextAreaOf(TextFrame frame, IReadOnlyList<XElement> chain, Rect inner, Spacing inset)
    {
        var shape = FrameShape(frame, inner, inset);
        var wraps = WrapsOn(frame, chain);
        if (shape is null && wraps.Count == 0)
        {
            return null;
        }
        return new TextArea(shape is null ? wraps : [shape, .. wraps]);
    }

    /// <summary>
    /// The shape a text frame keeps its text in, less its inset (a shaped frame has one, <c>InsetSpacing</c> as a single
    /// value) and the inside of its stroke: an oval, a polygon, or a rectangle with corner options
    /// (06-frame-shapes p1, 02-item-corners p4: the first line starts where the rounded corner, shrunk by the inset,
    /// leaves room). Null for a plain rectangle, whose columns are its text area.
    /// </summary>
    private WrapRegion? FrameShape(TextFrame frame, Rect inner, Spacing inset)
    {
        var contours = frame.Contours;
        if (RoundedCorners(frame, contours) is { } corners)
        {
            contours = [CornerContour(inner, corners)];
        }
        else if (contours.Count == 1 && IsAxisRectangle(contours[0]))
        {
            return null;
        }
        var outlines = TextArea.Flatten(contours, CoreIdml.Matrix.Identity);
        return outlines.Count == 0 ? null : new WrapRegion(WrapMode.Inside, Bounds(outlines), Spacing.All(inset.Top), Outlines: outlines);
    }

    private static bool IsAxisRectangle(PathContour contour)
    {
        var points = contour.Points;
        if (contour.IsOpen || points.Count != 4 || points.Any(p => p.LeftDirection != p.Anchor || p.RightDirection != p.Anchor))
        {
            return false;
        }
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (points[i].Anchor, points[(i + 1) % 4].Anchor);
            if (a.X != b.X && a.Y != b.Y)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The wraps of the items on the frame's spread (and on its page's master) that push its text, in the frame's inner
    /// coordinates. None for a frame with Ignore Text Wrap (06-frame-basics p6) or one anchored in a story; only items
    /// above it with "Text Wrap Only Affects Text Beneath". Hidden items do not wrap; items on a hidden layer do,
    /// unless the layer suppresses text wrap when hidden (<c>Layer@IgnoreWrap</c>, from memory). Master items wrap the
    /// text of the pages using the master unless they apply to the master page only.
    /// </summary>
    private List<WrapRegion> WrapsOn(TextFrame frame, IReadOnlyList<XElement> chain)
    {
        if (wrapsByFrame.TryGetValue(frame.Id, out var cached))
        {
            return cached;
        }
        var regions = new List<WrapRegion>();
        wrapsByFrame[frame.Id] = regions;
        if (AttributeResolver.FramePreference(chain, "IgnoreWrap") == "true" || IsInStory(frame.Element))
        {
            return regions;
        }
        wrapBeneathOnly ??= document.Package.TryGetXml("Resources/Preferences.xml")?.Root?.Element("TextPreference")?.Attribute("ZOrderTextWrap")?.Value == "true";
        var toFrame = Invert(frame.SpreadTransform);
        var leftPage = frame.Page?.IsLeftPage == true;
        var beneath = true;
        foreach (var item in frame.Spread.AllPageItems)
        {
            if (item.Element == frame.Element)
            {
                beneath = false;
                continue;
            }
            if (!(beneath && wrapBeneathOnly == true) && WrapOf(item, item.SpreadTransform.Then(toFrame), leftPage, masterItem: false) is { } region)
            {
                regions.Add(region);
            }
        }
        if (frame.Spread is not MasterSpread && frame.Page is { AppliedMaster: { } master } page && master.Pages.Count > 0)
        {
            var masterPage = master.Pages.Count > 1 && !page.IsLeftPage ? master.Pages[^1] : master.Pages[0];
            var toPage = Invert(masterPage.Transform).Then(page.MasterPageTransform).Then(page.Transform);
            var overridden = (page.GetAttribute("OverrideList") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var area = masterPage.SpreadBounds;
            foreach (var item in master.AllPageItems)
            {
                if (!overridden.Contains(item.Id) && Intersects(item.SpreadBounds, area)
                    && WrapOf(item, item.SpreadTransform.Then(toPage).Then(toFrame), leftPage, masterItem: true) is { } region)
                {
                    regions.Add(region);
                }
            }
        }
        return regions;
    }

    /// <summary>
    /// The wrap region of <paramref name="item"/> (mapped by <paramref name="transform"/> into the frame), or null when
    /// it does not wrap text. A graphic frame without a wrap of its own takes its graphic's (02-item-text-wrap p8: the
    /// image carries the object-shape wrap).
    /// </summary>
    private WrapRegion? WrapOf(PageItem item, CoreIdml.Matrix transform, bool leftPage, bool masterItem)
    {
        if (!item.Visible || item.Element.Ancestors().Any(ancestor => ancestor.Attribute("Visible")?.Value == "false")
            || (item.LayerId is { } layerId && document.Layers.All.FirstOrDefault(layer => layer.Id == layerId) is { Visible: false } layer
                && layer.GetAttribute("IgnoreWrap") == "true"))
        {
            return null;
        }
        var preference = WrapPreference(item.Element);
        var graphic = (item as GraphicFrame)?.Graphic;
        if (Mode(preference) == "None" && graphic is not null && WrapPreference(graphic) is { } inner && Mode(inner) != "None")
        {
            preference = inner;
        }
        var mode = Mode(preference);
        if (preference is null || mode == "None" || (masterItem && preference.Attribute("ApplyToMasterPageOnly")?.Value == "true"))
        {
            return null;
        }
        var offset = preference.Element("Properties")?.Element("TextWrapOffset");
        double Offset(string side) => Math.Max(0, Values.Number(offset?.Attribute(side)?.Value, 0));
        var spacing = new Spacing(Offset("Top"), Offset("Right"), Offset("Bottom"), Offset("Left"));
        var inverse = preference.Attribute("Inverse")?.Value == "true";
        var side = preference.Attribute("TextWrapSide")?.Value switch
        {
            "LeftSide" => WrapSide.Left,
            "RightSide" => WrapSide.Right,
            "LargestArea" => WrapSide.Largest,
            // Towards the spine: the right side of a left page, the left side of a right page.
            "SideTowardsSpine" => leftPage ? WrapSide.Right : WrapSide.Left,
            "SideAwayFromSpine" => leftPage ? WrapSide.Left : WrapSide.Right,
            _ => WrapSide.Both,
        };
        var outlines = Outlines(item, transform);
        if (outlines.Count == 0)
        {
            return null;
        }
        // The stroke is part of the object: the wrap starts at its outer edge, an inverted wrap at its inner edge
        // (02-item-text-wrap p2: 0.5 pt outside the 1 pt stroke; p9: 0.5 pt inside it).
        var weight = StrokeWeight(item);
        var outside = (resolver.Item(item.Element, "StrokeAlignment") ?? "CenterAlignment") switch
        {
            "InsideAlignment" => 0,
            "OutsideAlignment" => weight,
            _ => weight / 2,
        };
        var stroke = inverse ? weight - outside : outside;
        var box = Bounds(outlines);
        var wrapMode = inverse ? WrapMode.Inside : mode switch
        {
            "JumpObjectTextWrap" => WrapMode.Jump,
            "NextColumnTextWrap" => WrapMode.NextColumn,
            _ => WrapMode.Around,
        };
        if (mode == "Contour" && ContourOutlines(preference, outlines, graphic, transform) is { } contour)
        {
            return new WrapRegion(wrapMode, Bounds(contour), Spacing.All(spacing.Top + stroke), side, contour);
        }
        if (mode == "Contour")
        {
            return null;
        }
        var grown = new Spacing(spacing.Top + stroke, spacing.Right + stroke, spacing.Bottom + stroke, spacing.Left + stroke);
        return new WrapRegion(wrapMode, box, grown, side);
    }

    /// <summary>
    /// The shape an object-shape wrap follows (<c>ContourOption@ContourType</c>): the frame's path for Same as
    /// Clipping, Graphic Frame and a user-modified path, its box for Bounding Box, the opaque part of a placed PNG for
    /// Alpha Channel (see <see cref="AlphaShape"/>). Null for the other contours read from the image (detect edges,
    /// Photoshop path), which are not supported, and for an alpha channel that cannot be read: such an item does not wrap.
    /// </summary>
    private List<Outline>? ContourOutlines(XElement preference, List<Outline> outlines, XElement? graphic, CoreIdml.Matrix transform) =>
        (preference.Element("ContourOption")?.Attribute("ContourType")?.Value ?? "SameAsClipping") switch
        {
            "BoundingBox" => [BoxOutline(Bounds(outlines))],
            "AlphaChannel" => graphic is null ? null : AlphaShape(graphic) is { Count: > 0 } strips
                ? strips.Select(strip => BoxOutline(strip, CoreIdml.Matrix.Parse(graphic.Attribute("ItemTransform")?.Value).Then(transform))).ToList()
                : null,
            "DetectEdges" or "PhotoshopPath" => null,
            _ => outlines,
        };

    private static Outline BoxOutline(Rect box, CoreIdml.Matrix? transform = null)
    {
        Point[] corners = [new(box.Left, box.Top), new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom)];
        return new(transform is { } matrix ? [.. corners.Select(matrix.Apply)] : corners, true);
    }

    /// <summary>
    /// The opaque part of a placed PNG with an alpha channel, in the image's inner coordinates (its
    /// <c>GraphicBounds</c>): for each strip of rows, the box from the first pixel whose alpha reaches 230 to the last
    /// (inside edges are not followed, <c>IncludeInsideEdges="false"</c>). 02-item-text-wrap p8 wraps a feathered
    /// ellipse: every line beside it ends where a threshold from 220 to 230 puts the edge, and 230 also keeps the next
    /// word off each line (25 is InDesign's default threshold, from memory). Tall images are read in at most
    /// <see cref="MaxAlphaStrips"/> strips. Empty when the image cannot be read or has no alpha.
    /// </summary>
    private List<Rect> AlphaShape(XElement graphic)
    {
        var id = graphic.Attribute("Self")?.Value ?? string.Empty;
        if (alphaShapes.TryGetValue(id, out var cached))
        {
            return cached;
        }
        var strips = new List<Rect>();
        alphaShapes[id] = strips;
        if (ResolveLink(graphic.Element("Link")?.Attribute("LinkResourceURI")?.Value) is not { } path || !File.Exists(path))
        {
            return strips;
        }
        Imaging.RasterImage raster;
        try
        {
            var data = File.ReadAllBytes(path);
            if (!Imaging.PngCodec.IsPng(data))
            {
                return strips;
            }
            raster = Imaging.PngCodec.Decode(data);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return strips;
        }
        if (raster.Channels is not (2 or 4) || raster.Width == 0 || raster.Height == 0)
        {
            return strips;
        }
        var bounds = graphic.Element("Properties")?.Element("GraphicBounds");
        var left = Values.Number(bounds?.Attribute("Left")?.Value, 0);
        var top = Values.Number(bounds?.Attribute("Top")?.Value, 0);
        var right = Values.Number(bounds?.Attribute("Right")?.Value, left + raster.Width);
        var bottom = Values.Number(bounds?.Attribute("Bottom")?.Value, top + raster.Height);
        var (scaleX, scaleY) = ((right - left) / raster.Width, (bottom - top) / raster.Height);
        var rows = Math.Max(1, (raster.Height + MaxAlphaStrips - 1) / MaxAlphaStrips);
        var alpha = raster.Channels - 1;
        for (var row = 0; row < raster.Height; row += rows)
        {
            var (first, last) = (int.MaxValue, -1);
            for (var y = row; y < Math.Min(raster.Height, row + rows); y++)
            {
                var offset = y * raster.Width * raster.Channels;
                for (var x = 0; x < raster.Width; x++)
                {
                    if (raster.Pixels[offset + (x * raster.Channels) + alpha] >= AlphaThreshold)
                    {
                        (first, last) = (Math.Min(first, x), Math.Max(last, x));
                    }
                }
            }
            if (last >= 0)
            {
                strips.Add(Rect.FromEdges(left + (first * scaleX), top + (row * scaleY), left + ((last + 1) * scaleX), top + (Math.Min(raster.Height, row + rows) * scaleY)));
            }
        }
        return strips;
    }

    // The item's paths in the frame (a group's are its items' paths).
    private List<Outline> Outlines(PageItem item, CoreIdml.Matrix transform)
    {
        if (item is Group)
        {
            var toItem = Invert(item.SpreadTransform);
            return item.SelfAndDescendants().Where(child => child is not Group)
                .SelectMany(child => Outlines(child, child.SpreadTransform.Then(toItem).Then(transform))).ToList();
        }
        var contours = item.Contours;
        if (RoundedCorners(item, contours) is { } corners)
        {
            contours = [CornerContour(item.InnerBounds, corners)];
        }
        return TextArea.Flatten(contours, transform);
    }

    private double StrokeWeight(PageItem item)
    {
        var weight = Values.Number(resolver.Item(item.Element, "StrokeWeight"), 0);
        return weight <= 0 || resolver.Item(item.Element, "StrokeColor") is null or "Swatch/None" ? 0 : weight;
    }

    // The item's TextWrapPreference, or its object style's when the style controls text wrap.
    private XElement? WrapPreference(XElement element)
    {
        if (element.Element("TextWrapPreference") is { } own)
        {
            return own;
        }
        var chain = resolver.ObjectChain(element);
        return AttributeResolver.StylesControl(chain, "EnableTextWrapAndOthers")
            ? chain.Skip(1).Select(style => style.Element("TextWrapPreference")).FirstOrDefault(found => found is not null)
            : null;
    }

    private static string Mode(XElement? preference) => preference?.Attribute("TextWrapMode")?.Value ?? "None";

    private static Rect Bounds(IReadOnlyList<Outline> outlines)
    {
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var point in outlines.SelectMany(outline => outline.Points))
        {
            (left, top, right, bottom) = (Math.Min(left, point.X), Math.Min(top, point.Y), Math.Max(right, point.X), Math.Max(bottom, point.Y));
        }
        return left <= right ? Rect.FromEdges(left, top, right, bottom) : default;
    }
}
