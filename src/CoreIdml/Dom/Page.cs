using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>
/// A page. Methods that add items take rectangles in page coordinates: origin at the page's top-left corner,
/// points, y growing downwards.
/// </summary>
public sealed class Page : IdmlNode
{
    internal Page(IdmlDocument document, Spread spread, XElement element)
        : base(document, element)
    {
        Spread = spread;
    }

    public Spread Spread { get; }

    /// <summary>The page label as shown in the Pages panel ("1", "2", "iv", ...).</summary>
    public string Name => GetAttribute("Name") ?? string.Empty;

    /// <summary>Position of this page in the whole document (0-based).</summary>
    public int Index => Document.Pages.ToList().FindIndex(page => page.Id == Id);

    public Size Size
    {
        get
        {
            var bounds = IdmlXml.ParseList(GetAttribute("GeometricBounds"));
            return bounds.Length == 4 ? new Size(bounds[3] - bounds[1], bounds[2] - bounds[0]) : Document.PageSize;
        }
    }

    /// <summary>The master spread applied to this page, if any.</summary>
    public MasterSpread? AppliedMaster
    {
        get
        {
            var id = GetAttribute("AppliedMaster");
            return string.IsNullOrEmpty(id) || id == IdmlNames.NoReference ? null : Document.MasterSpreads.FirstOrDefault(master => master.Id == id);
        }
    }

    /// <summary>Extra transform applied to master items on this page (<c>MasterPageTransform</c>).</summary>
    public Matrix MasterPageTransform => Matrix.Parse(GetAttribute("MasterPageTransform"));

    /// <summary>Left page of a facing-pages spread.</summary>
    public bool IsLeftPage => Transform.Tx < 0 && Document.FacingPages;

    public Matrix Transform => Matrix.Parse(GetAttribute("ItemTransform"));

    /// <summary>The page rectangle in spread coordinates.</summary>
    public Rect SpreadBounds
    {
        get
        {
            var bounds = IdmlXml.ParseList(GetAttribute("GeometricBounds"));
            var (top, left, bottom, right) = bounds.Length == 4 ? (bounds[0], bounds[1], bounds[2], bounds[3]) : (0d, 0d, Size.Height, Size.Width);
            var transform = Transform;
            var topLeft = transform.Apply(new Point(left, top));
            var bottomRight = transform.Apply(new Point(right, bottom));
            return Rect.FromEdges(
                Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y),
                Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y));
        }
    }

    public Spacing Margins
    {
        get
        {
            var margins = Element.Element("MarginPreference");
            double Read(string name) => IdmlXml.ParseDouble(margins?.Attribute(name)?.Value) ?? 0;
            return new Spacing(Read("Top"), Read("Right"), Read("Bottom"), Read("Left"));
        }
        set
        {
            var margins = Element.Element("MarginPreference");
            if (margins is null)
            {
                margins = new XElement("MarginPreference");
                Element.Add(margins);
            }
            // A damaged ColumnCount (0 or negative) is read as one column, as InDesign does.
            var columns = int.TryParse(margins.Attribute("ColumnCount")?.Value, CultureInfo.InvariantCulture, out var count) && count >= 1 ? count : 1;
            var gutter = IdmlXml.ParseDouble(margins.Attribute("ColumnGutter")?.Value) ?? 12;
            IdmlDocument.SetMargins(margins, value, columns, gutter, Size.Width);
        }
    }

    /// <summary>The area inside the margins, in page coordinates.</summary>
    public Rect ContentBounds
    {
        get
        {
            var margins = Margins;
            var size = Size;
            return new Rect(margins.Left, margins.Top, size.Width - margins.Left - margins.Right, size.Height - margins.Top - margins.Bottom);
        }
    }

    /// <summary>
    /// Page items whose centre lies on this page. Each item belongs to one page only: an item centred exactly on the
    /// edge two pages share (the spine of a facing spread) belongs to the first of them.
    /// </summary>
    public IReadOnlyList<PageItem> PageItems
    {
        get
        {
            var bounds = SpreadBounds;
            // Pages of a spread do not overlap: only a centre on this page's edge can be on an earlier page as well.
            List<Rect>? earlier = null;
            return Spread.PageItems.Where(item =>
            {
                var center = item.SpreadBounds.Center;
                if (!bounds.Contains(center))
                {
                    return false;
                }
                if (center.X > bounds.Left && center.X < bounds.Right && center.Y > bounds.Top && center.Y < bounds.Bottom)
                {
                    return true;
                }
                earlier ??= Spread.Pages.TakeWhile(page => page.Element != Element).Select(page => page.SpreadBounds).ToList();
                return !earlier.Exists(other => other.Contains(center));
            }).ToList();
        }
    }

    /// <summary>Converts a page rectangle into the spread coordinates IDML stores.</summary>
    public Rect ToSpread(Rect pageRect)
    {
        var origin = SpreadBounds;
        return pageRect.Offset(origin.X, origin.Y);
    }

    /// <summary>Converts spread coordinates into page coordinates.</summary>
    public Rect FromSpread(Rect spreadRect)
    {
        var origin = SpreadBounds;
        return spreadRect.Offset(-origin.X, -origin.Y);
    }

    /// <summary>Adds an empty text frame with a new story.</summary>
    public TextFrame AddTextFrame(Rect bounds, string? name = null) => AddTextFrame(bounds, name, out _);

    private TextFrame AddTextFrame(Rect bounds, string? name, out Story story)
    {
        story = Document.CreateStory();
        var element = NewItem("TextFrame", bounds, name, IdmlNames.NormalTextFrameStyle, PathGeometry.Rectangle(bounds.Width, bounds.Height));
        element.SetAttributeValue("ParentStory", story.Id);
        element.SetAttributeValue("PreviousTextFrame", IdmlNames.NoReference);
        element.SetAttributeValue("NextTextFrame", IdmlNames.NoReference);
        element.SetAttributeValue("ContentType", "TextType");
        element.Add(new XElement("TextFramePreference",
            new XAttribute("TextColumnCount", "1"),
            new XAttribute("TextColumnFixedWidth", IdmlXml.Format(bounds.Width)),
            new XAttribute("UseFixedColumnWidth", "false"),
            new XAttribute("VerticalJustification", "TopAlign")));
        return (TextFrame)Append(element);
    }

    /// <summary>Adds a text frame holding plain text ("\n" separates paragraphs).</summary>
    public TextFrame AddText(Rect bounds, string text, ParagraphStyle? style = null, string? name = null)
    {
        // The new story is the frame's story (fresh id), no need to look it up.
        var frame = AddTextFrame(bounds, name, out var story);
        story.SetText(text, style);
        return frame;
    }

    public GraphicFrame AddRectangle(Rect bounds, ShapeStyle? style = null, string? name = null)
    {
        var element = NewItem("Rectangle", bounds, name, IdmlNames.NormalGraphicsFrameStyle, PathGeometry.Rectangle(bounds.Width, bounds.Height));
        element.SetAttributeValue("ContentType", "Unassigned");
        var item = (GraphicFrame)Append(element);
        item.ApplyStyle(style);
        return item;
    }

    public GraphicFrame AddEllipse(Rect bounds, ShapeStyle? style = null, string? name = null)
    {
        var element = NewItem("Oval", bounds, name, IdmlNames.NormalGraphicsFrameStyle, PathGeometry.Ellipse(bounds.Width, bounds.Height));
        element.SetAttributeValue("ContentType", "Unassigned");
        var item = (GraphicFrame)Append(element);
        item.ApplyStyle(style);
        return item;
    }

    /// <summary>Adds a straight line between two points in page coordinates.</summary>
    public GraphicLine AddLine(Point from, Point to, ShapeStyle? style = null, string? name = null)
    {
        var bounds = Rect.FromEdges(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));
        var center = bounds.Center;
        var geometry = PathGeometry.Line(new Point(from.X - center.X, from.Y - center.Y), new Point(to.X - center.X, to.Y - center.Y));
        var element = NewItem("GraphicLine", bounds, name, IdmlNames.NormalGraphicsFrameStyle, geometry);
        var item = (GraphicLine)Append(element);
        item.ApplyStyle(style ?? new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 1 });
        return item;
    }

    /// <summary>
    /// Adds a frame holding an image read from a local file (size and resolution come from its header). Image frames
    /// have no stroke, as in InDesign (its "[Normal Graphics Frame]" style would draw a 1 pt black border); call
    /// <see cref="PageItem.ApplyStyle"/> on the result to give one a border.
    /// </summary>
    public GraphicFrame AddImage(Rect bounds, string imagePath, ImageFit fit = ImageFit.Fill, string? name = null) =>
        AddImage(bounds, ImageSource.FromFile(imagePath), fit, name);

    public GraphicFrame AddImage(Rect bounds, ImageSource image, ImageFit fit = ImageFit.Fill, string? name = null)
    {
        var frame = AddRectangle(bounds, new ShapeStyle { Fill = Swatch.None, Stroke = Swatch.None }, name);
        frame.PlaceImage(image, fit);
        return frame;
    }

    private XElement NewItem(string kind, Rect pageBounds, string? name, string objectStyle, XElement geometry)
    {
        var spreadBounds = ToSpread(pageBounds);
        var center = spreadBounds.Center;
        return new XElement(kind,
            new XAttribute("Self", Document.NewId()),
            new XAttribute("ItemLayer", Document.ActiveLayerId),
            new XAttribute("Locked", "false"),
            new XAttribute("AppliedObjectStyle", objectStyle),
            new XAttribute("Visible", "true"),
            new XAttribute("Name", string.IsNullOrEmpty(name) ? IdmlNames.EmptyString : name),
            new XAttribute("ItemTransform", Matrix.Translation(center.X, center.Y).ToString()),
            new XElement("Properties", geometry));
    }

    private PageItem Append(XElement element)
    {
        Spread.Element.Add(element);
        return PageItem.TryWrap(Document, Spread, element)!;
    }
}
