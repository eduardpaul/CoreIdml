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

    /// <summary>How open stroke ends are drawn (lines and open paths).</summary>
    public LineCap? EndCap { get; init; }

    /// <summary>Opacity of the whole item in percent (100 = opaque), InDesign's Effects > Transparency.</summary>
    public double? Opacity { get; init; }
}

/// <summary>Ends of open strokes, as InDesign's Stroke panel names them.</summary>
public enum LineCap
{
    Butt,
    Round,
    Projecting,
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

/// <summary>How text wraps around an item (Window &gt; Text Wrap).</summary>
public enum TextWrapMode
{
    None,

    /// <summary>Around the item's bounding box (with its stroke), grown by the offsets.</summary>
    BoundingBox,

    /// <summary>Around the item's shape (its path), grown by the top offset.</summary>
    ObjectShape,

    /// <summary>No text beside the item: lines jump below it.</summary>
    JumpObject,

    /// <summary>The text after the item goes on in the next column or frame.</summary>
    NextColumn,
}

/// <summary>Which sides of a wrapped item text may go (<c>TextWrapSide</c>).</summary>
public enum TextWrapSide
{
    BothSides,
    LeftSide,
    RightSide,
    SideTowardsSpine,
    SideAwayFromSpine,

    /// <summary>The wider side of each line.</summary>
    LargestArea,
}

/// <summary>
/// A text wrap: <paramref name="Mode"/>, the distance kept from the item on each side (<paramref name="Offset"/>, an
/// object-shape wrap uses the top one all round), the sides text may take, and <paramref name="Inverse"/> to keep the
/// text inside the item instead.
/// </summary>
public sealed record TextWrap(TextWrapMode Mode, Spacing Offset = default, TextWrapSide Side = TextWrapSide.BothSides, bool Inverse = false);

/// <summary>How a text frame sizes itself to its text (Text Frame Options &gt; Auto-Size, <c>AutoSizingType</c>).</summary>
public enum AutoSizing
{
    Off,
    HeightOnly,
    WidthOnly,
    HeightAndWidth,
    HeightAndWidthProportionally,
}

/// <summary>The point of an auto-sizing frame that stays put (<c>AutoSizingReferencePoint</c>).</summary>
public enum AutoSizingReferencePoint
{
    TopLeftPoint,
    TopCenterPoint,
    TopRightPoint,
    LeftCenterPoint,
    CenterPoint,
    RightCenterPoint,
    BottomLeftPoint,
    BottomCenterPoint,
    BottomRightPoint,
}

/// <summary>Where a text frame's first baseline goes (Text Frame Options &gt; Baseline Options, <c>FirstBaselineOffset</c>).</summary>
public enum FirstBaselineOffset
{
    /// <summary>The tallest glyph's ascent below the top of the frame (InDesign's default).</summary>
    AscentOffset,
    CapHeight,
    LeadingOffset,
    XHeight,
    EmboxHeight,

    /// <summary>Exactly the minimum offset below the top.</summary>
    FixedHeight,
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
        if (style.EndCap is { } cap)
        {
            SetAttribute("EndCap", cap switch
            {
                LineCap.Round => "RoundEndCap",
                LineCap.Projecting => "ProjectingEndCap",
                _ => "ButtEndCap",
            });
        }
        if (style.Opacity is { } opacity)
        {
            SetOpacity(opacity);
        }
        if (style.Rotation is { } rotation && rotation != 0)
        {
            Rotate(rotation);
        }
    }

    // <Item><TransparencySetting><BlendingSetting Opacity="50" /></TransparencySetting></Item>, after Properties.
    private void SetOpacity(double opacity)
    {
        if (opacity is < 0 or > 100)
        {
            throw new IdmlException($"Opacity {opacity} is outside 0 to 100.");
        }
        var transparency = Element.Element("TransparencySetting");
        if (transparency is null)
        {
            transparency = new XElement("TransparencySetting");
            if (Element.Element("Properties") is { } properties)
            {
                properties.AddAfterSelf(transparency);
            }
            else
            {
                Element.AddFirst(transparency);
            }
        }
        var blending = transparency.Element("BlendingSetting");
        if (blending is null)
        {
            blending = new XElement("BlendingSetting");
            transparency.AddFirst(blending);
        }
        blending.SetAttributeValue("Opacity", IdmlXml.Format(opacity));
    }

    /// <summary>
    /// How text in other frames wraps around the item (Window &gt; Text Wrap, <c>TextWrapPreference</c>); null when
    /// the item has none of its own (no wrap, or its object style's). Setting null removes the item's own.
    /// </summary>
    public TextWrap? TextWrap
    {
        get
        {
            var preference = Element.Element("TextWrapPreference");
            if (preference is null)
            {
                return null;
            }
            var offset = preference.Element(IdmlXml.Properties)?.Element("TextWrapOffset");
            double Offset(string side) => IdmlXml.ParseDouble(offset?.Attribute(side)?.Value) ?? 0;
            return new TextWrap(
                preference.Attribute("TextWrapMode")?.Value switch
                {
                    "BoundingBoxTextWrap" => TextWrapMode.BoundingBox,
                    "Contour" => TextWrapMode.ObjectShape,
                    "JumpObjectTextWrap" => TextWrapMode.JumpObject,
                    "NextColumnTextWrap" => TextWrapMode.NextColumn,
                    _ => TextWrapMode.None,
                },
                new Spacing(Offset("Top"), Offset("Right"), Offset("Bottom"), Offset("Left")),
                Enum.TryParse<TextWrapSide>(preference.Attribute("TextWrapSide")?.Value, out var side) ? side : TextWrapSide.BothSides,
                preference.Attribute("Inverse")?.Value == "true");
        }
        set
        {
            Element.Element("TextWrapPreference")?.Remove();
            if (value is null)
            {
                return;
            }
            // As 02-item-text-wrap: the mode, side and inverse flag as attributes, the offsets in Properties, and for an
            // object-shape wrap no ContourOption (InDesign follows the frame's path, "Same As Clipping").
            var offset = value.Offset;
            var preference = new XElement("TextWrapPreference",
                new XAttribute("Inverse", value.Inverse ? "true" : "false"),
                new XAttribute("ApplyToMasterPageOnly", "false"),
                new XAttribute("TextWrapSide", value.Side.ToString()),
                new XAttribute("TextWrapMode", TextWrapModeName(value.Mode)),
                new XElement(IdmlXml.Properties,
                    new XElement("TextWrapOffset",
                        new XAttribute("Top", IdmlXml.Format(offset.Top)),
                        new XAttribute("Left", IdmlXml.Format(offset.Left)),
                        new XAttribute("Bottom", IdmlXml.Format(offset.Bottom)),
                        new XAttribute("Right", IdmlXml.Format(offset.Right)))));
            var before = Element.Elements().LastOrDefault(child => child.Name.LocalName is "Properties" or "TransparencySetting" or "StrokeTransparencySetting"
                or "FillTransparencySetting" or "ContentTransparencySetting" or "TextFramePreference" or "BaselineFrameGridOption"
                or "TextFrameFootnoteOptionsObject" or "AnchoredObjectSetting");
            if (before is null)
            {
                Element.AddFirst(preference);
            }
            else
            {
                before.AddAfterSelf(preference);
            }
        }
    }

    private static string TextWrapModeName(TextWrapMode mode) => mode switch
    {
        TextWrapMode.BoundingBox => "BoundingBoxTextWrap",
        TextWrapMode.ObjectShape => "Contour",
        TextWrapMode.JumpObject => "JumpObjectTextWrap",
        TextWrapMode.NextColumn => "NextColumnTextWrap",
        _ => "None",
    };

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
    /// are unbound, so no <c>XMLContent</c> reference dangles. Text frames removed (the item, or frames inside it) are
    /// taken out of their threads, and their story goes too when no other frame shows it.
    /// </summary>
    public virtual void Remove() => Document.RemoveItems([this]);

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
        localName is "TextFrame" or "EndnoteTextFrame" or "Rectangle" or "Oval" or "Polygon" or "GraphicLine" or "Group" || GenericKinds.Contains(localName);

    /// <summary>Page items whose child elements <see cref="SelfAndDescendants"/> visits: groups and frames.</summary>
    internal static bool HoldsItems(string localName) => localName is "Group" or "TextFrame" or "EndnoteTextFrame" or "Rectangle" or "Oval" or "Polygon";

    internal static PageItem? TryWrap(IdmlDocument document, Spread spread, XElement element) => element.Name.LocalName switch
    {
        // The frame InDesign 2018+ creates for a document's endnotes (11-long-footnotes-endnotes p6) is a text frame of its own name.
        "TextFrame" or "EndnoteTextFrame" => new TextFrame(document, spread, element),
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

    /// <summary>The frame grows or shrinks to its text (06-frame-autosize writes it on <c>TextFramePreference</c>).</summary>
    public AutoSizing AutoSizing
    {
        get => Enum.TryParse<AutoSizing>(ExistingPreference?.Attribute("AutoSizingType")?.Value, out var value) ? value : AutoSizing.Off;
        set => Preference.SetAttributeValue("AutoSizingType", value.ToString());
    }

    /// <summary>The point that stays put while the frame auto-sizes (InDesign's default: the centre).</summary>
    public AutoSizingReferencePoint AutoSizingReferencePoint
    {
        get => Enum.TryParse<AutoSizingReferencePoint>(ExistingPreference?.Attribute("AutoSizingReferencePoint")?.Value, out var value) ? value : AutoSizingReferencePoint.CenterPoint;
        set => Preference.SetAttributeValue("AutoSizingReferencePoint", value.ToString());
    }

    /// <summary>The height an auto-sizing frame never shrinks below; null for none.</summary>
    public double? MinimumHeightForAutoSizing
    {
        get => ExistingPreference?.Attribute("UseMinimumHeightForAutoSizing")?.Value == "true"
            ? IdmlXml.ParseDouble(ExistingPreference.Attribute("MinimumHeightForAutoSizing")?.Value) ?? 0
            : null;
        set
        {
            Preference.SetAttributeValue("UseMinimumHeightForAutoSizing", value is null ? "false" : "true");
            if (value is { } height)
            {
                Preference.SetAttributeValue("MinimumHeightForAutoSizing", IdmlXml.Format(height));
            }
        }
    }

    /// <summary>When the story ends in the frame, its columns end at the same depth (<c>VerticalBalanceColumns</c>).</summary>
    public bool BalanceColumns
    {
        get => ExistingPreference?.Attribute("VerticalBalanceColumns")?.Value == "true";
        set => Preference.SetAttributeValue("VerticalBalanceColumns", value ? "true" : "false");
    }

    /// <summary>Where the first baseline goes below the top of the frame (inside its inset).</summary>
    public FirstBaselineOffset FirstBaselineOffset
    {
        get => Enum.TryParse<FirstBaselineOffset>(ExistingPreference?.Attribute("FirstBaselineOffset")?.Value, out var value) ? value : FirstBaselineOffset.AscentOffset;
        set => Preference.SetAttributeValue("FirstBaselineOffset", value.ToString());
    }

    /// <summary>The least distance from the top of the frame to its first baseline (all of it with <see cref="FirstBaselineOffset.FixedHeight"/>).</summary>
    public double MinimumFirstBaselineOffset
    {
        get => IdmlXml.ParseDouble(ExistingPreference?.Attribute("MinimumFirstBaselineOffset")?.Value) ?? 0;
        set => Preference.SetAttributeValue("MinimumFirstBaselineOffset", IdmlXml.Format(value));
    }

    /// <summary>Text in this frame ignores every other item's text wrap.</summary>
    public bool IgnoreWrap
    {
        get => ExistingPreference?.Attribute("IgnoreWrap")?.Value == "true";
        set => Preference.SetAttributeValue("IgnoreWrap", value ? "true" : "false");
    }

    /// <summary>
    /// Lets text overflowing this frame continue in <paramref name="next"/>, as InDesign threads frames:
    /// <paramref name="next"/> and the frames threaded after it leave their thread (whose earlier frames keep their
    /// story) and show this frame's story; their own story is discarded when no frame shows it any more. When this
    /// frame already continued in another frame, that frame follows the last of them (they are inserted).
    /// </summary>
    public void ThreadTo(TextFrame next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Id == Id)
        {
            throw new IdmlException("A text frame cannot be threaded to itself.");
        }
        if (NextFrameId == next.Id)
        {
            return;
        }
        var frames = new Dictionary<string, TextFrame>(StringComparer.Ordinal);
        foreach (var frame in Document.AllTextFrames())
        {
            frames.TryAdd(frame.Id, frame);
        }
        TextFrame? Find(string? id) => id is not null && frames.TryGetValue(id, out var frame) ? frame : null;

        // next and the frames after it move here, in their order.
        var moving = new List<TextFrame>();
        for (var frame = (TextFrame?)next; frame is not null && moving.Count <= frames.Count; frame = Find(frame.NextFrameId))
        {
            if (frame.Id == Id)
            {
                throw new IdmlException($"Text frame {next.Id} comes before {Id} in its thread; threading them would make a loop.");
            }
            moving.Add(frame);
        }
        var oldNext = Find(NextFrameId);
        var oldStory = next.StoryId;

        // Cut next from the frames before it, link it after this frame, and the old continuation after the moved frames.
        Find(next.PreviousFrameId)?.SetAttribute("NextTextFrame", IdmlNames.NoReference);
        SetAttribute("NextTextFrame", next.Id);
        next.SetAttribute("PreviousTextFrame", Id);
        var last = moving[^1];
        if (oldNext is not null)
        {
            last.SetAttribute("NextTextFrame", oldNext.Id);
            oldNext.SetAttribute("PreviousTextFrame", last.Id);
        }
        else
        {
            last.SetAttribute("NextTextFrame", IdmlNames.NoReference);
        }

        if (oldStory != StoryId)
        {
            moving.ForEach(frame => frame.SetAttribute("ParentStory", StoryId));
            if (!frames.Values.Any(frame => frame.StoryId == oldStory) && Document.FindStory(oldStory) is { } orphan)
            {
                Document.DeleteStory(orphan);
                // A tagged frame's story is bound from the structure; walking every story here made threading quadratic.
                Document.Tags.UnbindStructure(oldStory);
            }
        }
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

        var frame = InnerBounds;
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
        var frame = InnerBounds;
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
