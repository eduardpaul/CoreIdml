using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>Fill and stroke of a shape or frame. Unset properties keep the object style's values.</summary>
public sealed class ShapeStyle
{
    public Swatch? Fill { get; init; }

    /// <summary>Fill tint in percent (100 = solid).</summary>
    public double? FillTint { get; init; }

    public Swatch? Stroke { get; init; }

    public double? StrokeWeight { get; init; }

    public double? StrokeTint { get; init; }

    /// <summary>Solid, dashed or dotted stroke (InDesign's built-in stroke styles).</summary>
    public StrokeKind? StrokeKind { get; init; }

    /// <summary>Rounded-corner radius applied to all four corners.</summary>
    public double? CornerRadius { get; init; }

    /// <summary>Rotation in degrees around the item centre (counter-clockwise, like InDesign).</summary>
    public double? Rotation { get; init; }
}

/// <summary>Stroke styles every InDesign document has.</summary>
public enum StrokeKind
{
    Solid,

    /// <summary>InDesign's "Dashed (3 and 2)".</summary>
    Dashed,

    /// <summary>InDesign's "Dotted".</summary>
    Dotted,
}

/// <summary>How an image is fitted into its frame.</summary>
public enum ImageFit
{
    /// <summary>Scale proportionally so the frame is completely covered (cropping overflow), centred.</summary>
    Fill,

    /// <summary>Scale proportionally so the whole image is visible, centred.</summary>
    Fit,

    /// <summary>Scale each axis independently to the frame size (distorts).</summary>
    Stretch,

    /// <summary>Actual size, centred in the frame.</summary>
    Center,

    /// <summary>Actual size, anchored at the frame's top-left corner.</summary>
    Original,
}

/// <summary>Anything placed on a spread: frames, shapes, lines, groups.</summary>
public abstract class PageItem : IdmlNode
{
    private static readonly HashSet<string> GenericKinds =
        ["Button", "MultiStateObject", "EPSText", "FormField", "CheckBox", "ComboBox", "ListBox", "RadioButton", "TextBox", "SignatureField"];

    private protected PageItem(IdmlDocument document, Spread spread, XElement element)
        : base(document, element)
    {
        Spread = spread;
    }

    public Spread Spread { get; }

    /// <summary>The element name: TextFrame, Rectangle, Oval, Polygon, GraphicLine, Group, ...</summary>
    public string Kind => Element.Name.LocalName;

    /// <summary>The name shown in the Layers panel; <c>null</c> when unnamed.</summary>
    public string? Name
    {
        get
        {
            var name = GetAttribute("Name");
            return string.IsNullOrEmpty(name) || name == IdmlNames.EmptyString ? null : name;
        }
        set => SetAttribute("Name", string.IsNullOrEmpty(value) ? IdmlNames.EmptyString : value);
    }

    /// <summary>The script label (Window &gt; Utilities &gt; Script Label), often used to mark template slots.</summary>
    public string? Label
    {
        get => Element.Element(IdmlXml.Properties)?.Element("Label")?.Elements("KeyValuePair")
            .FirstOrDefault(pair => pair.Attribute("Key")?.Value == "Label")?.Attribute("Value")?.Value;
        set
        {
            var properties = IdmlXml.GetOrCreateProperties(Element);
            properties.Element("Label")?.Remove();
            if (!string.IsNullOrEmpty(value))
            {
                properties.Add(new XElement("Label", new XElement("KeyValuePair", new XAttribute("Key", "Label"), new XAttribute("Value", value))));
            }
        }
    }

    public Matrix Transform
    {
        get => Matrix.Parse(GetAttribute("ItemTransform"));
        set => SetAttribute("ItemTransform", value.ToString());
    }

    /// <summary>The parent group, if nested.</summary>
    public PageItem? Parent => Element.Parent is { } parent && parent.Name.LocalName != "Spread" && parent.Name.LocalName != "MasterSpread"
        ? TryWrap(Document, Spread, parent)
        : null;

    /// <summary>Item transform composed with every parent group's transform: inner coordinates to spread coordinates.</summary>
    public Matrix SpreadTransform
    {
        get
        {
            var matrix = Transform;
            for (var parent = Parent; parent is not null; parent = parent.Parent)
            {
                matrix = matrix.Then(parent.Transform);
            }
            return matrix;
        }
    }

    /// <summary>Bounding box in spread coordinates.</summary>
    public virtual Rect SpreadBounds
    {
        get
        {
            var matrix = SpreadTransform;
            var points = new List<Point>(4);
            foreach (var anchor in PathGeometry.Anchors(Element))
            {
                points.Add(matrix.Apply(anchor));
            }
            if (points.Count == 0)
            {
                var origin = matrix.Apply(new Point(0, 0));
                return new Rect(origin.X, origin.Y, 0, 0);
            }
            return Rect.FromEdges(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }
    }

    /// <summary>The item's paths (anchors and Bézier handles) in its inner coordinates; apply <see cref="SpreadTransform"/> for spread coordinates.</summary>
    public IReadOnlyList<PathContour> Contours => PathGeometry.Contours(Element);

    /// <summary>Bounding box of the path anchors in the item's inner coordinates (the geometric bounds before the transform).</summary>
    public Rect InnerBounds
    {
        get
        {
            var anchors = PathGeometry.Anchors(Element).ToList();
            return anchors.Count == 0
                ? new Rect(0, 0, 0, 0)
                : Rect.FromEdges(anchors.Min(p => p.X), anchors.Min(p => p.Y), anchors.Max(p => p.X), anchors.Max(p => p.Y));
        }
    }

    /// <summary>The page under the item's centre, if any.</summary>
    public Page? Page
    {
        get
        {
            var center = SpreadBounds.Center;
            return Spread.Pages.FirstOrDefault(page => page.SpreadBounds.Contains(center));
        }
    }

    /// <summary>Bounding box relative to <see cref="Page"/> (spread coordinates when off-page).</summary>
    public Rect Bounds => Page is { } page ? page.FromSpread(SpreadBounds) : SpreadBounds;

    public bool Visible
    {
        get => GetAttribute("Visible") != "false";
        set => SetAttribute("Visible", value ? "true" : "false");
    }

    public bool Locked
    {
        get => GetAttribute("Locked") == "true";
        set => SetAttribute("Locked", value ? "true" : "false");
    }

    public string? LayerId => GetAttribute("ItemLayer");

    public void MoveToLayer(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        SetAttribute("ItemLayer", layer.Id);
    }

    /// <summary>Applies fill, stroke, corners and rotation; properties left null are not touched.</summary>
    public void ApplyStyle(ShapeStyle? style)
    {
        if (style is null)
        {
            return;
        }
        if (style.Fill is { } fill)
        {
            SetAttribute("FillColor", fill.Reference);
        }
        if (style.FillTint is { } fillTint)
        {
            SetAttribute("FillTint", IdmlXml.Format(fillTint));
        }
        if (style.Stroke is { } stroke)
        {
            SetAttribute("StrokeColor", stroke.Reference);
        }
        if (style.StrokeWeight is { } weight)
        {
            SetAttribute("StrokeWeight", IdmlXml.Format(weight));
        }
        if (style.StrokeTint is { } strokeTint)
        {
            SetAttribute("StrokeTint", IdmlXml.Format(strokeTint));
        }
        if (style.StrokeKind is { } kind)
        {
            SetAttribute("StrokeType", kind switch
            {
                StrokeKind.Dashed => "StrokeStyle/$ID/Canned Dashed 3x2",
                StrokeKind.Dotted => "StrokeStyle/$ID/Canned Dotted",
                _ => "StrokeStyle/$ID/Solid",
            });
        }
        if (style.CornerRadius is { } radius)
        {
            var option = radius > 0 ? "RoundedCorner" : "None";
            foreach (var corner in (string[])["TopLeft", "TopRight", "BottomLeft", "BottomRight"])
            {
                SetAttribute(corner + "CornerOption", option);
                SetAttribute(corner + "CornerRadius", IdmlXml.Format(radius));
            }
        }
        if (style.Rotation is { } rotation && rotation != 0)
        {
            Rotate(rotation);
        }
    }

    /// <summary>
    /// Moves and resizes a rectangle, oval or text frame to <paramref name="pageBounds"/> (page coordinates), dropping
    /// any rotation. Meant for laying out generated items before content is fitted into them, e.g. growing a text
    /// frame to its measured text; a placed image keeps its own transform, so re-place it afterwards.
    /// </summary>
    public void SetBounds(Rect pageBounds)
    {
        if (Kind is not ("Rectangle" or "Oval" or "TextFrame"))
        {
            throw new IdmlException($"Only rectangles, ovals and text frames can be resized this way, not {Kind}.");
        }
        if (Parent is not null)
        {
            throw new IdmlException("Items inside a group can't be resized this way.");
        }
        var spreadBounds = Page is { } page ? page.ToSpread(pageBounds) : pageBounds;
        var properties = IdmlXml.GetOrCreateProperties(Element);
        properties.Element("PathGeometry")?.Remove();
        properties.AddFirst(Kind == "Oval"
            ? PathGeometry.Ellipse(pageBounds.Width, pageBounds.Height)
            : PathGeometry.Rectangle(pageBounds.Width, pageBounds.Height));
        Transform = Matrix.Translation(spreadBounds.Center.X, spreadBounds.Center.Y);
        if (Element.Element("TextFramePreference") is { } preference && preference.Attribute("TextColumnFixedWidth") is not null)
        {
            preference.SetAttributeValue("TextColumnFixedWidth", IdmlXml.Format(pageBounds.Width));
        }
    }

    /// <summary>Rotates the item around its centre by <paramref name="degrees"/> (counter-clockwise).</summary>
    public void Rotate(double degrees)
    {
        var anchors = PathGeometry.Anchors(Element).ToList();
        var center = anchors.Count == 0
            ? new Point(0, 0)
            : new Point((anchors.Min(p => p.X) + anchors.Max(p => p.X)) / 2, (anchors.Min(p => p.Y) + anchors.Max(p => p.Y)) / 2);
        Transform = Matrix.Translation(-center.X, -center.Y)
            .Then(Matrix.Rotation(degrees))
            .Then(Matrix.Translation(center.X, center.Y))
            .Then(Transform);
    }

    /// <summary>
    /// Removes the item from its spread. XML tags bound to it (or to its placed graphic) stay in the structure but
    /// are unbound, so no <c>XMLContent</c> reference dangles.
    /// </summary>
    public virtual void Remove()
    {
        var ids = Element.DescendantsAndSelf().Select(element => element.Attribute("Self")?.Value).OfType<string>().ToArray();
        Element.Remove();
        Document.Tags.Unbind(ids);
    }

    internal IEnumerable<PageItem> SelfAndDescendants()
    {
        yield return this;
        if (this is Group group)
        {
            foreach (var child in group.Items.SelectMany(item => item.SelfAndDescendants()))
            {
                yield return child;
            }
        }
        else if (this is GraphicFrame or TextFrame)
        {
            // Frames can hold nested page items (pasted inside); expose them too.
            foreach (var child in Element.Elements().Select(element => TryWrap(Document, Spread, element)).OfType<PageItem>().SelectMany(item => item.SelfAndDescendants()))
            {
                yield return child;
            }
        }
    }

    /// <summary>Element names <see cref="TryWrap"/> turns into page items.</summary>
    internal static bool IsPageItemKind(string localName) =>
        localName is "TextFrame" or "Rectangle" or "Oval" or "Polygon" or "GraphicLine" or "Group" || GenericKinds.Contains(localName);

    /// <summary>Page items whose child elements <see cref="SelfAndDescendants"/> visits: groups and frames.</summary>
    internal static bool HoldsItems(string localName) => localName is "Group" or "TextFrame" or "Rectangle" or "Oval" or "Polygon";

    internal static PageItem? TryWrap(IdmlDocument document, Spread spread, XElement element) => element.Name.LocalName switch
    {
        "TextFrame" => new TextFrame(document, spread, element),
        "Rectangle" or "Oval" or "Polygon" => new GraphicFrame(document, spread, element),
        "GraphicLine" => new GraphicLine(document, spread, element),
        "Group" => new Group(document, spread, element),
        var name when GenericKinds.Contains(name) => new GenericPageItem(document, spread, element),
        _ => null,
    };
}

/// <summary>A text frame. Its text lives in <see cref="Story"/>, which may flow through several threaded frames.</summary>
public sealed class TextFrame : PageItem
{
    internal TextFrame(IdmlDocument document, Spread spread, XElement element)
        : base(document, spread, element)
    {
    }

    public string StoryId => GetAttribute("ParentStory") ?? string.Empty;

    public Story Story => Document.FindStory(StoryId) ?? throw new IdmlException(IdmlErrorKind.NotFound, $"Story '{StoryId}' of text frame '{Id}' is missing.");

    public string? NextFrameId => GetAttribute("NextTextFrame") is { } id && id != IdmlNames.NoReference ? id : null;

    public string? PreviousFrameId => GetAttribute("PreviousTextFrame") is { } id && id != IdmlNames.NoReference ? id : null;

    public int Columns
    {
        get => int.TryParse(ExistingPreference?.Attribute("TextColumnCount")?.Value, CultureInfo.InvariantCulture, out var value) ? value : 1;
        set => Preference.SetAttributeValue("TextColumnCount", Math.Max(1, value).ToString(CultureInfo.InvariantCulture));
    }

    public double ColumnGutter
    {
        get => IdmlXml.ParseDouble(ExistingPreference?.Attribute("TextColumnGutter")?.Value) ?? 12;
        set => Preference.SetAttributeValue("TextColumnGutter", IdmlXml.Format(value));
    }

    /// <summary>Space between the frame edge and the text.</summary>
    public Spacing Inset
    {
        get
        {
            var values = ExistingPreference?.Element(IdmlXml.Properties)?.Element("InsetSpacing")?.Elements("ListItem")
                .Select(item => IdmlXml.ParseDouble(item.Value) ?? 0).ToList();
            // IDML order: top, left, bottom, right.
            return values is { Count: 4 } ? new Spacing(values[0], values[3], values[2], values[1]) : default;
        }
        set
        {
            var properties = IdmlXml.GetOrCreateProperties(Preference);
            properties.Element("InsetSpacing")?.Remove();
            properties.Add(new XElement("InsetSpacing", new XAttribute("type", "list"),
                ((double[])[value.Top, value.Left, value.Bottom, value.Right]).Select(v => new XElement("ListItem", new XAttribute("type", "unit"), IdmlXml.Format(v)))));
        }
    }

    public VerticalJustification VerticalJustification
    {
        get => Enum.TryParse<VerticalJustification>((ExistingPreference?.Attribute("VerticalJustification")?.Value ?? "TopAlign").Replace("Align", string.Empty, StringComparison.Ordinal), out var value)
            ? value
            : VerticalJustification.Top;
        set => Preference.SetAttributeValue("VerticalJustification", value == VerticalJustification.Justify ? "JustifyAlign" : value + "Align");
    }

    /// <summary>Lets text overflowing this frame continue in <paramref name="next"/> (its own story is discarded).</summary>
    public void ThreadTo(TextFrame next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.StoryId != StoryId)
        {
            var orphan = Document.FindStory(next.StoryId);
            if (orphan is not null && !Document.AllPageItems.OfType<TextFrame>().Any(frame => frame.Id != next.Id && frame.StoryId == orphan.Id))
            {
                Document.DeleteStory(orphan);
            }
            next.SetAttribute("ParentStory", StoryId);
        }
        SetAttribute("NextTextFrame", next.Id);
        next.SetAttribute("PreviousTextFrame", Id);
    }

    /// <summary>Appends a table whose columns share the frame's inner width.</summary>
    public Table AddTable(int rows, int columns, TableOptions? options = null)
    {
        options ??= new TableOptions();
        if (options.ColumnWidths is null)
        {
            var inset = Inset;
            var width = SpreadBounds.Width - inset.Left - inset.Right;
            options = options with { ColumnWidths = Enumerable.Repeat(width / columns, columns).ToArray() };
        }
        return Story.AddTable(rows, columns, options);
    }

    /// <summary>Removes the frame; threaded neighbours are joined, and the story goes too when no other frame shows it.</summary>
    public override void Remove()
    {
        var storyId = StoryId;
        var frames = Document.AllPageItems.OfType<TextFrame>().ToList();
        var previous = frames.FirstOrDefault(frame => frame.Id == PreviousFrameId);
        var next = frames.FirstOrDefault(frame => frame.Id == NextFrameId);
        previous?.SetAttribute("NextTextFrame", next?.Id ?? IdmlNames.NoReference);
        next?.SetAttribute("PreviousTextFrame", previous?.Id ?? IdmlNames.NoReference);

        var shared = frames.Any(frame => frame.Id != Id && frame.StoryId == storyId);
        base.Remove();
        if (!shared && Document.FindStory(storyId) is { } story)
        {
            Document.DeleteStory(story);
            Document.Tags.Unbind(storyId);
        }
    }

    /// <summary>Read side: never creates the element (reading must not mark the spread as modified).</summary>
    private XElement? ExistingPreference => Element.Element("TextFramePreference");

    private XElement Preference
    {
        get
        {
            var preference = Element.Element("TextFramePreference");
            if (preference is null)
            {
                preference = new XElement("TextFramePreference");
                if (Element.Element(IdmlXml.Properties) is { } properties)
                {
                    properties.AddAfterSelf(preference);
                }
                else
                {
                    Element.AddFirst(preference);
                }
            }
            return preference;
        }
    }
}

public enum VerticalJustification
{
    Top,
    Center,
    Bottom,
    Justify,
}

/// <summary>A rectangle, oval or polygon: a shape that can also hold an image.</summary>
public sealed class GraphicFrame : PageItem
{
    private static readonly HashSet<string> ContentKinds = ["Image", "PDF", "EPS", "ImportedPage", "WMF", "PICT"];

    internal GraphicFrame(IdmlDocument document, Spread spread, XElement element)
        : base(document, spread, element)
    {
    }

    /// <summary>The placed graphic element (Image, PDF, EPS, ...), if any.</summary>
    public XElement? Graphic => Element.Elements().FirstOrDefault(element => ContentKinds.Contains(element.Name.LocalName));

    /// <summary>The link URI of the placed graphic (e.g. <c>file:/Users/me/photo.jpg</c>).</summary>
    public string? LinkUri => Graphic?.Element("Link")?.Attribute("LinkResourceURI")?.Value;

    /// <summary>Places an image, replacing any current content, and positions it with <paramref name="fit"/>.</summary>
    public void PlaceImage(ImageSource image, ImageFit fit = ImageFit.Fill)
    {
        ArgumentNullException.ThrowIfNull(image);
        // Keep the previous graphic's and link's ids: XML tags bind to the graphic (XMLContent), and callers may hold a
        // link id from a summary; both must keep working after the image is replaced.
        var previousId = Graphic?.Attribute("Self")?.Value;
        var previousLinkId = Graphic?.Element("Link")?.Attribute("Self")?.Value;
        Graphic?.Remove();

        var anchors = PathGeometry.Anchors(Element).ToList();
        var frame = anchors.Count == 0
            ? new Rect(0, 0, 0, 0)
            : Rect.FromEdges(anchors.Min(p => p.X), anchors.Min(p => p.Y), anchors.Max(p => p.X), anchors.Max(p => p.Y));
        var fitted = ComputePlacement(frame, image.UprightWidthPoints, image.UprightHeightPoints, fit);
        var placement = image.OrientationTransform.Then(fitted);

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var graphic = new XElement("Image",
            new XAttribute("Self", previousId ?? Document.NewId()),
            new XAttribute("Space", "$ID/#Links_RGB"),
            new XAttribute("ActualPpi", IdmlXml.FormatList(image.DpiX, image.DpiY)),
            new XAttribute("EffectivePpi", IdmlXml.FormatList(Math.Round(image.DpiX / fitted.A), Math.Round(image.DpiY / fitted.D))),
            new XAttribute("ImageRenderingIntent", "UseColorSettings"),
            new XAttribute("LocalDisplaySetting", "Default"),
            new XAttribute("ImageTypeName", image.Format.TypeName),
            new XAttribute("AppliedObjectStyle", IdmlNames.NoObjectStyle),
            new XAttribute("ItemTransform", placement.ToString()),
            new XElement("Properties",
                new XElement("Profile", new XAttribute("type", "string"), "$ID/None"),
                new XElement("GraphicBounds",
                    new XAttribute("Left", "0"),
                    new XAttribute("Top", "0"),
                    new XAttribute("Right", IdmlXml.Format(image.WidthPoints)),
                    new XAttribute("Bottom", IdmlXml.Format(image.HeightPoints)))),
            new XElement("Link",
                new XAttribute("Self", previousLinkId ?? Document.NewId()),
                new XAttribute("AssetURL", IdmlNames.EmptyString),
                new XAttribute("AssetID", IdmlNames.EmptyString),
                new XAttribute("LinkResourceURI", image.LinkUri),
                new XAttribute("LinkResourceFormat", image.Format.TypeName),
                new XAttribute("StoredState", "Normal"),
                new XAttribute("LinkClassID", "35906"),
                new XAttribute("LinkClientID", "257"),
                new XAttribute("LinkResourceModified", "false"),
                new XAttribute("LinkObjectModified", "false"),
                new XAttribute("ShowInUI", "true"),
                new XAttribute("CanEmbed", "true"),
                new XAttribute("CanUnembed", "true"),
                new XAttribute("CanPackage", "true"),
                new XAttribute("ImportPolicy", "NoAutoImport"),
                new XAttribute("ExportPolicy", "NoAutoExport"),
                new XAttribute("LinkImportStamp", "file 0 0"),
                new XAttribute("LinkImportModificationTime", now),
                new XAttribute("LinkImportTime", now),
                new XAttribute("LinkResourceSize", "0~" + image.FileSize.ToString("x", CultureInfo.InvariantCulture))));
        Element.Add(graphic);
        SetAttribute("ContentType", "GraphicType");
    }

    /// <summary>
    /// Places one page of a PDF (SimpleIDML's import_pdf), replacing any content. The page size comes from the PDF's
    /// MediaBox when <paramref name="pdfPath"/> is a readable local file, else from <paramref name="pageSize"/>.
    /// </summary>
    /// <remarks><c>crop</c> is a PDFCrop value: CropContentVisibleLayers (default), CropArt, CropPDF, CropTrim, CropBleed, CropMedia, CropContentAllLayers, CropContent.</remarks>
    public void PlacePdf(string pdfPath, int pageNumber = 1, string crop = "CropContentVisibleLayers", ImageFit fit = ImageFit.Fit, Size? pageSize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        var isLocal = !pdfPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && File.Exists(pdfPath);
        var size = pageSize ?? (isLocal ? PdfInfo.ReadMediaBox(pdfPath) : null)
            ?? throw new IdmlException("The PDF page size is unknown: pass pageSize or a readable local file.");

        var previousId = Graphic?.Attribute("Self")?.Value;
        var previousLinkId = Graphic?.Element("Link")?.Attribute("Self")?.Value;
        Graphic?.Remove();
        var anchors = PathGeometry.Anchors(Element).ToList();
        var frame = anchors.Count == 0
            ? new Rect(0, 0, 0, 0)
            : Rect.FromEdges(anchors.Min(p => p.X), anchors.Min(p => p.Y), anchors.Max(p => p.X), anchors.Max(p => p.Y));
        var placement = ComputePlacement(frame, size.Width, size.Height, fit);
        const string format = "$ID/Adobe Portable Document Format (PDF)";
        var pdf = new XElement("PDF",
            new XAttribute("Self", previousId ?? Document.NewId()),
            new XAttribute("GrayVectorPolicy", "IgnoreAll"),
            new XAttribute("RGBVectorPolicy", "IgnoreAll"),
            new XAttribute("CMYKVectorPolicy", "IgnoreAll"),
            new XAttribute("LocalDisplaySetting", "Default"),
            new XAttribute("ImageTypeName", format),
            new XAttribute("AppliedObjectStyle", IdmlNames.NoObjectStyle),
            new XAttribute("ItemTransform", placement.ToString()),
            new XAttribute("Visible", "true"),
            new XAttribute("Name", IdmlNames.EmptyString),
            new XElement("Properties",
                new XElement("GraphicBounds",
                    new XAttribute("Left", "0"),
                    new XAttribute("Top", "0"),
                    new XAttribute("Right", IdmlXml.Format(size.Width)),
                    new XAttribute("Bottom", IdmlXml.Format(size.Height)))),
            new XElement("PDFAttribute",
                new XAttribute("PageNumber", pageNumber.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("PDFCrop", crop),
                new XAttribute("TransparentBackground", "true")),
            new XElement("Link",
                new XAttribute("Self", previousLinkId ?? Document.NewId()),
                new XAttribute("AssetURL", IdmlNames.EmptyString),
                new XAttribute("AssetID", IdmlNames.EmptyString),
                new XAttribute("LinkResourceURI", ImageSource.ToLinkUriOrKeep(isLocal ? Path.GetFullPath(pdfPath) : pdfPath)),
                new XAttribute("LinkResourceFormat", format),
                new XAttribute("StoredState", "Normal"),
                new XAttribute("LinkResourceModified", "false"),
                new XAttribute("LinkObjectModified", "false"),
                new XAttribute("ShowInUI", "true"),
                new XAttribute("CanEmbed", "true"),
                new XAttribute("CanUnembed", "true"),
                new XAttribute("CanPackage", "true"),
                new XAttribute("ImportPolicy", "NoAutoImport"),
                new XAttribute("ExportPolicy", "NoAutoExport")));
        Element.Add(pdf);
        SetAttribute("ContentType", "GraphicType");
    }

    /// <summary>Id of the placed graphic's link, if any.</summary>
    public string? LinkId => Graphic?.Element("Link")?.Attribute("Self")?.Value;

    /// <summary>Points the placed graphic at another file (URI or local path), keeping its position and scale.</summary>
    public void Relink(string uriOrPath)
    {
        var link = Graphic?.Element("Link") ?? throw new IdmlException($"Frame '{Id}' has no placed graphic to relink.");
        link.SetAttributeValue("LinkResourceURI", ImageSource.ToLinkUriOrKeep(uriOrPath));
    }

    /// <summary>Removes the placed graphic, leaving an empty frame (tags bound to the graphic move to the frame).</summary>
    public void ClearContent()
    {
        var graphicId = Graphic?.Attribute("Self")?.Value;
        Graphic?.Remove();
        SetAttribute("ContentType", "Unassigned");
        if (graphicId is not null)
        {
            Document.Tags.Rebind(graphicId, Id);
        }
    }

    internal static Matrix ComputePlacement(Rect frame, double imageWidth, double imageHeight, ImageFit fit)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return Matrix.Translation(frame.X, frame.Y);
        }
        var scaleX = frame.Width / imageWidth;
        var scaleY = frame.Height / imageHeight;
        (scaleX, scaleY) = fit switch
        {
            ImageFit.Fill => (Math.Max(scaleX, scaleY), Math.Max(scaleX, scaleY)),
            ImageFit.Fit => (Math.Min(scaleX, scaleY), Math.Min(scaleX, scaleY)),
            ImageFit.Stretch => (scaleX, scaleY),
            _ => (1d, 1d),
        };
        var width = imageWidth * scaleX;
        var height = imageHeight * scaleY;
        var x = fit == ImageFit.Original ? frame.X : frame.X + ((frame.Width - width) / 2);
        var y = fit == ImageFit.Original ? frame.Y : frame.Y + ((frame.Height - height) / 2);
        return new Matrix(scaleX, 0, 0, scaleY, x, y);
    }
}

public sealed class GraphicLine : PageItem
{
    internal GraphicLine(IdmlDocument document, Spread spread, XElement element)
        : base(document, spread, element)
    {
    }
}

/// <summary>A group of page items.</summary>
public sealed class Group : PageItem
{
    internal Group(IdmlDocument document, Spread spread, XElement element)
        : base(document, spread, element)
    {
    }

    public IReadOnlyList<PageItem> Items =>
        Element.Elements().Select(element => TryWrap(Document, Spread, element)).OfType<PageItem>().ToList();

    public override Rect SpreadBounds
    {
        get
        {
            var children = Items.Select(item => item.SpreadBounds).ToList();
            return children.Count == 0
                ? base.SpreadBounds
                : Rect.FromEdges(children.Min(r => r.Left), children.Min(r => r.Top), children.Max(r => r.Right), children.Max(r => r.Bottom));
        }
    }
}

/// <summary>Page item kinds without a dedicated wrapper (buttons, form fields, ...).</summary>
public sealed class GenericPageItem : PageItem
{
    internal GenericPageItem(IdmlDocument document, Spread spread, XElement element)
        : base(document, spread, element)
    {
    }
}
