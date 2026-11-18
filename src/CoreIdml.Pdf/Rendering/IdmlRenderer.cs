using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Writing;
using static CoreIdml.Pdf.Writing.PdfObjectWriter;

namespace CoreIdml.Pdf.Rendering;

/// <summary>
/// Paints IDML pages into a PDF: master items, then spread items in stacking order; frame paths with fills and
/// strokes (CMYK/RGB swatches, tints, opacity, rounded corners, stroke alignment), placed JPEG/PNG images clipped to
/// their frames, and story text laid out by <see cref="TextComposer"/>.
/// </summary>
internal sealed partial class IdmlRenderer : IDisposable
{
    private readonly IdmlDocument document;
    private readonly PdfExportOptions options;
    private readonly FontLibrary fonts;
    private readonly AttributeResolver resolver;
    private readonly StoryReader reader;
    private readonly TextComposer composer;
    private readonly Dictionary<string, StoryLayout> storyLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DrawOp>> frameOps = new(StringComparer.Ordinal);
    private readonly HashSet<string> hiddenLayers = new(StringComparer.Ordinal);
    private readonly List<string> missingImages = [];
    private readonly List<string> warnings = [];
    private PdfDocumentBuilder? pdf;

    private PdfDocumentBuilder Pdf => pdf ?? throw new InvalidOperationException("Render has not started.");

    public IdmlRenderer(IdmlDocument document, PdfExportOptions options)
    {
        this.document = document;
        this.options = options;
        fonts = new FontLibrary(options.Fonts);
        resolver = new AttributeResolver(document);
        reader = new StoryReader(document, resolver, fonts);
        composer = new TextComposer(document, reader, fonts);
        foreach (var layer in document.Layers.All.Where(layer => !layer.Visible || !layer.Printable))
        {
            hiddenLayers.Add(layer.Id);
        }
    }

    internal FontLibrary Fonts => fonts;

    public void Dispose() => pdf?.Dispose();

    /// <summary>Lays out one story into its frames (for tests and diagnostics).</summary>
    internal StoryLayout LayoutStory(Story story) =>
        composer.Compose(story.Element, story.Frames.Select(frame => FrameArea(frame, frame.Page?.Name ?? "1")).ToList());

    /// <summary>Lays out a story into the given frame areas (see <see cref="TextMeasurer.CheckFit(Story)"/>).</summary>
    internal StoryLayout Compose(Story story, IReadOnlyList<FrameArea> areas) => composer.Compose(story.Element, areas);

    /// <summary>Lays out a frame's story with no height limit (see <see cref="TextMeasurer"/>).</summary>
    internal TextMeasure MeasureFrame(TextFrame frame)
    {
        const double Unlimited = 100_000;
        var area = FrameArea(frame, frame.Page?.Name ?? "1");
        var columns = area.Columns.Select(column => column with { Height = Unlimited }).ToList();
        if (columns.Count == 0)
        {
            return default;
        }
        var layout = composer.Compose(frame.Story.Element, [area with { Columns = columns, VerticalJustification = "TopAlign" }]);
        if (layout.Lines.Count == 0)
        {
            return default;
        }
        var inset = FrameInset(frame.Element);
        var top = columns[0].Y;
        var bottom = layout.Lines.Max(line => line.Baseline) + layout.LastDescent;
        return new TextMeasure(layout.Lines.Max(line => line.Width), bottom - top + inset.Top + inset.Bottom, layout.Lines.Count);
    }

    public PdfExportResult Render(Stream output)
    {
        pdf = new PdfDocumentBuilder(output, options.CompressContent);
        var pages = document.Pages;
        var selected = options.Pages is { Count: > 0 } wanted
            ? wanted.Where(number => number >= 1 && number <= pages.Count).Select(number => pages[number - 1]).ToList()
            : pages.ToList();
        foreach (var missing in options.Pages?.Where(number => number < 1 || number > pages.Count) ?? [])
        {
            warnings.Add($"Page {missing} does not exist: the document has {pages.Count} page(s), numbered from 1.");
        }

        if (options.MaxImageDpi is { } maxDpi)
        {
            // Opt-in downsampling: each image is written once, at the resolution its largest placement needs, and
            // prepared on other cores while the text is composed.
            Pdf.PrefetchLimitedImages(ImageLimits(selected, maxDpi), options.JpegQuality);
        }
        // Large PNGs are decoded and compressed on other cores while the text is composed.
        Pdf.PrefetchPngs(PlacedPngs(selected));
        ComposeStories();
        for (var done = 0; done < selected.Count;)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            RenderPage(selected[done]);
            done++;
            if (options.Progress is { } progress && !progress(done, selected.Count))
            {
                throw new OperationCanceledException("The PDF export was cancelled.");
            }
        }
        Pdf.Finish(options.Title);
        Pdf.Dispose();
        // Where each missing font is used, like InDesign's Missing Fonts dialog.
        var pagesByStory = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var spread in document.Spreads)
        {
            foreach (var frame in spread.AllPageItems.OfType<TextFrame>())
            {
                if (frame.Page?.Name is { } name)
                {
                    (pagesByStory.TryGetValue(frame.StoryId, out var set) ? set : pagesByStory[frame.StoryId] = new SortedSet<string>(StringComparer.Ordinal)).Add(name);
                }
            }
        }
        foreach (var master in document.MasterSpreads)
        {
            foreach (var frame in master.AllPageItems.OfType<TextFrame>())
            {
                (pagesByStory.TryGetValue(frame.StoryId, out var set) ? set : pagesByStory[frame.StoryId] = new SortedSet<string>(StringComparer.Ordinal)).Add("master " + master.Name);
            }
        }
        foreach (var substitution in fonts.Substitutions)
        {
            substitution.Pages.Clear();
            substitution.Pages.AddRange(substitution.Stories.SelectMany(story => pagesByStory.GetValueOrDefault(story) ?? []).Distinct().Order(StringComparer.Ordinal));
        }
        return new PdfExportResult
        {
            PageCount = Pdf.PageCount,
            FontSubstitutions = fonts.Substitutions.ToList(),
            MissingImages = missingImages.Distinct(StringComparer.Ordinal).ToList(),
            OversetStories = storyLayouts.Where(pair => pair.Value.Overset).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    // ---- text ----------------------------------------------------------------------------------------

    private void ComposeStories()
    {
        var framesByStory = document.Spreads
            .SelectMany(spread => spread.AllPageItems)
            .OfType<TextFrame>()
            .GroupBy(frame => frame.StoryId, StringComparer.Ordinal);
        foreach (var group in framesByStory)
        {
            var story = document.FindStory(group.Key);
            if (story is null)
            {
                continue;
            }
            // The group holds the story's frames in document order, as Story.Frames would find them.
            var ordered = Story.ThreadOrder([.. group]);
            var areas = ordered.Select(frame => FrameArea(frame, frame.Page?.Name ?? "1")).ToList();
            var layout = composer.Compose(story.Element, areas);
            storyLayouts[story.Id] = layout;
            foreach (var (frameId, ops) in layout.OpsByFrame)
            {
                frameOps[frameId] = ops;
            }
        }
    }

    /// <summary>Composes a master-page story for one document page (page numbers differ per page).</summary>
    private List<DrawOp> MasterFrameOps(TextFrame frame, MasterSpread master, string pageName)
    {
        var key = frame.Id + "@" + pageName;
        if (frameOps.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var story = document.FindStory(frame.StoryId);
        if (story is null)
        {
            return [];
        }
        var chain = master.AllPageItems.OfType<TextFrame>().Where(f => f.StoryId == frame.StoryId).ToList();
        var ordered = OrderThread(chain);
        var layout = composer.Compose(story.Element, ordered.Select(f => FrameArea(f, pageName)).ToList());
        foreach (var (frameId, ops) in layout.OpsByFrame)
        {
            frameOps[frameId + "@" + pageName] = ops;
        }
        return frameOps.TryGetValue(key, out var result) ? result : [];
    }

    private static List<TextFrame> OrderThread(List<TextFrame> frames)
    {
        var ordered = new List<TextFrame>();
        var current = frames.FirstOrDefault(frame => frame.PreviousFrameId is null || frames.All(f => f.Id != frame.PreviousFrameId)) ?? frames.FirstOrDefault();
        while (current is not null && !ordered.Contains(current))
        {
            ordered.Add(current);
            var next = current.NextFrameId;
            current = next is null ? null : frames.FirstOrDefault(frame => frame.Id == next);
        }
        ordered.AddRange(frames.Where(frame => !ordered.Contains(frame)));
        return ordered;
    }

    internal FrameArea FrameArea(TextFrame frame, string pageName)
    {
        var element = frame.Element;
        var inner = frame.InnerBounds;
        var chain = resolver.ObjectChain(element);
        string? Preference(string name) => AttributeResolver.FramePreference(chain, name);
        var inset = FrameInset(chain);
        var columns = Math.Max(1, (int)Values.Number(Preference("TextColumnCount"), 1));
        var gutter = Values.Number(Preference("TextColumnGutter"), 12);
        var width = inner.Width - inset.Left - inset.Right;
        var columnWidth = (width - (gutter * (columns - 1))) / columns;
        if (Preference("UseFixedColumnWidth") == "true")
        {
            columnWidth = Values.Number(Preference("TextColumnFixedWidth"), columnWidth);
        }
        var height = inner.Height - inset.Top - inset.Bottom;
        var rects = new List<Rect>();
        if (columnWidth > 0 && height > 0)
        {
            for (var i = 0; i < columns; i++)
            {
                rects.Add(new Rect(inner.X + inset.Left + (i * (columnWidth + gutter)), inner.Y + inset.Top, columnWidth, height));
            }
        }
        return new FrameArea(
            frame.Id,
            rects,
            Preference("VerticalJustification") ?? "TopAlign",
            Preference("FirstBaselineOffset") ?? "AscentOffset",
            Values.Number(Preference("MinimumFirstBaselineOffset"), 0),
            pageName);
    }

    private Spacing FrameInset(XElement frame) => FrameInset(resolver.ObjectChain(frame));

    private static Spacing FrameInset(IReadOnlyList<XElement> objectChain)
    {
        var preference = AttributeResolver.FramePreferenceSource(objectChain, "InsetSpacing");
        var inset = preference?.Element("Properties")?.Element("InsetSpacing");
        if (inset is null)
        {
            return default;
        }
        var items = inset.Elements("ListItem").Select(item => Values.Number(item.Value, 0)).ToList();
        if (items.Count == 4)
        {
            // IDML order: top, left, bottom, right.
            return new Spacing(items[0], items[3], items[2], items[1]);
        }
        var single = Values.Number(inset.Value, 0);
        return Spacing.All(single);
    }

    // ---- pages ---------------------------------------------------------------------------------------

    private void RenderPage(Page page)
    {
        var size = page.Size;
        var origin = page.SpreadBounds;
        var content = new ContentBuilder();
        // PDF space has y up from the bottom; draw everything in IDML spread coordinates (y down).
        content.Transform(new CoreIdml.Matrix(1, 0, 0, -1, 0, size.Height));
        content.Transform(CoreIdml.Matrix.Translation(-origin.X, -origin.Y));

        if (options.IncludeMasterItems && page.AppliedMaster is { } master)
        {
            DrawMaster(page, master, content, origin, depth: 0);
        }
        var pageRect = origin;
        foreach (var item in page.Spread.PageItems)
        {
            if (Intersects(item.SpreadBounds, pageRect))
            {
                DrawItem(item, content, page.Name, null);
            }
        }
        Pdf.AddPage(size.Width, size.Height, content);
    }

    private void DrawMaster(Page page, MasterSpread master, ContentBuilder content, Rect pageOrigin, int depth)
    {
        var masterPages = master.Pages;
        if (masterPages.Count == 0 || depth > 4)
        {
            return;
        }
        // Facing pages: left pages use the master's left page, right pages its right page.
        var masterPage = masterPages.Count > 1 && !page.IsLeftPage ? masterPages[^1] : masterPages[0];
        if (masterPages.Count > 1 && page.IsLeftPage)
        {
            masterPage = masterPages[0];
        }
        // A master based on another master paints the parent's items first.
        if (masterPage.AppliedMaster is { } parent && parent.Id != master.Id)
        {
            DrawMaster(page, parent, content, pageOrigin, depth + 1);
        }
        var masterOrigin = masterPage.SpreadBounds;
        var overridden = (page.GetAttribute("OverrideList") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        content.Save();
        // Master spread → master page space → MasterPageTransform (page space, i.e. GeometricBounds coordinates) → page → spread.
        content.Transform(Invert(masterPage.Transform).Then(page.MasterPageTransform).Then(page.Transform));
        foreach (var item in master.PageItems)
        {
            if (!overridden.Contains(item.Id) && Intersects(item.SpreadBounds, masterOrigin))
            {
                DrawItem(item, content, page.Name, master);
            }
        }
        content.Restore();
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.Left <= b.Right && a.Right >= b.Left && a.Top <= b.Bottom && a.Bottom >= b.Top;

    // ---- page items ----------------------------------------------------------------------------------

    private bool IsHidden(PageItem item)
    {
        if (!item.Visible || item.GetAttribute("Nonprinting") == "true")
        {
            return true;
        }
        return item.LayerId is { } layer && hiddenLayers.Contains(layer);
    }

    private void DrawItem(PageItem item, ContentBuilder content, string pageName, MasterSpread? master)
    {
        if (IsHidden(item))
        {
            return;
        }
        if (item is Group group)
        {
            content.Save();
            ApplyOpacity(item.Element, content);
            foreach (var child in group.Items)
            {
                DrawItem(child, content, pageName, master);
            }
            content.Restore();
            return;
        }

        var element = item.Element;
        content.Save();
        ApplyOpacity(element, content);
        content.Transform(item.SpreadTransform);
        var contours = item.Contours;
        var closed = contours.Count > 0 && contours.All(contour => !contour.IsOpen);

        // Fill.
        if (closed && ResolveColor(element, "FillColor", "FillTint") is { } fill)
        {
            AppendPath(item, contours, content);
            content.FillColor(fill).Fill(evenOdd: contours.Count > 1);
        }

        // Content: image, nested items, text.
        switch (item)
        {
            case GraphicFrame frame:
                DrawGraphic(frame, contours, content);
                break;
            case TextFrame textFrame:
                var ops = IsInStory(textFrame.Element)
                    ? AnchoredFrameOps(textFrame, pageName)
                    : master is null
                        ? frameOps.GetValueOrDefault(textFrame.Id) ?? []
                        : MasterFrameOps(textFrame, master, pageName);
                DrawOps(ops, content, item, pageName, master);
                break;
        }
        // Items pasted inside the frame, clipped to it. Their SpreadTransform already includes this frame's.
        var nested = item is GraphicFrame or TextFrame
            ? element.Elements().Select(child => NestedItem(item, child)).OfType<PageItem>().ToList()
            : [];
        if (nested.Count > 0)
        {
            content.Save();
            if (closed)
            {
                AppendPath(item, contours, content);
                content.Clip(contours.Count > 1);
            }
            content.Transform(Invert(item.SpreadTransform));
            foreach (var child in nested)
            {
                DrawItem(child, content, pageName, master);
            }
            content.Restore();
        }

        // Stroke on top.
        DrawStroke(item, contours, closed, content);
        content.Restore();
    }

    private static PageItem? NestedItem(PageItem parent, XElement child) =>
        child.Name.LocalName is "Rectangle" or "Oval" or "Polygon" or "GraphicLine" or "TextFrame" or "Group"
            ? parent.Spread.AllPageItems.FirstOrDefault(candidate => candidate.Element == child)
            : null;

    private static CoreIdml.Matrix Invert(CoreIdml.Matrix m)
    {
        var determinant = (m.A * m.D) - (m.B * m.C);
        if (Math.Abs(determinant) < 1e-12)
        {
            return CoreIdml.Matrix.Identity;
        }
        var a = m.D / determinant;
        var b = -m.B / determinant;
        var c = -m.C / determinant;
        var d = m.A / determinant;
        return new CoreIdml.Matrix(a, b, c, d, -((m.Tx * a) + (m.Ty * c)), -((m.Tx * b) + (m.Ty * d)));
    }

    private void ApplyOpacity(XElement element, ContentBuilder content)
    {
        var blending = element.Element("TransparencySetting")?.Element("BlendingSetting");
        var opacity = Values.Number(blending?.Attribute("Opacity")?.Value, 100);
        if (opacity < 100)
        {
            content.GraphicsState(Pdf.Opacity(opacity / 100, opacity / 100));
        }
    }

    private ResolvedColor? ResolveColor(XElement element, string colorName, string tintName)
    {
        var reference = resolver.Item(element, colorName);
        if (reference is null)
        {
            return null;
        }
        var color = document.Swatches.ResolveColor(reference);
        if (color is null)
        {
            if (reference.StartsWith("Gradient/", StringComparison.Ordinal))
            {
                warnings.Add($"Gradient {reference} is not rendered (item {element.Attribute("Self")?.Value}).");
            }
            return null;
        }
        var tint = Values.Number(resolver.Item(element, tintName), -1);
        return tint >= 0 ? color.WithTint(tint) : color;
    }

    private void AppendPath(PageItem item, IReadOnlyList<PathContour> contours, ContentBuilder content)
    {
        if (RoundedCorners(item, contours) is { } corners)
        {
            AppendCornerRectangle(item.InnerBounds, corners, content);
            return;
        }
        foreach (var contour in contours)
        {
            var points = contour.Points;
            content.MoveTo(points[0].Anchor.X, points[0].Anchor.Y);
            var count = contour.IsOpen ? points.Count - 1 : points.Count;
            for (var i = 0; i < count; i++)
            {
                var from = points[i];
                var to = points[(i + 1) % points.Count];
                if (from.RightDirection == from.Anchor && to.LeftDirection == to.Anchor)
                {
                    content.LineTo(to.Anchor.X, to.Anchor.Y);
                }
                else
                {
                    content.CurveTo(from.RightDirection.X, from.RightDirection.Y, to.LeftDirection.X, to.LeftDirection.Y, to.Anchor.X, to.Anchor.Y);
                }
            }
            if (!contour.IsOpen)
            {
                content.ClosePath();
            }
        }
    }

    /// <summary>Corner options of a plain rectangle (null when it has none or is not a 4-point rectangle).</summary>
    private (string Option, double Radius)[]? RoundedCorners(PageItem item, IReadOnlyList<PathContour> contours)
    {
        if (item.Kind is not ("Rectangle" or "TextFrame") || contours.Count != 1 || contours[0].Points.Count != 4
            || contours[0].Points.Any(p => p.LeftDirection != p.Anchor || p.RightDirection != p.Anchor))
        {
            return null;
        }
        var corners = new (string, double)[4];
        var any = false;
        var names = (string[])["TopLeft", "TopRight", "BottomRight", "BottomLeft"];
        for (var i = 0; i < 4; i++)
        {
            var option = resolver.Item(item.Element, names[i] + "CornerOption") ?? "None";
            var radius = Values.Number(resolver.Item(item.Element, names[i] + "CornerRadius"), 0);
            corners[i] = (option, radius);
            any |= option != "None" && radius > 0;
        }
        return any ? corners : null;
    }

    private static void AppendCornerRectangle(Rect r, (string Option, double Radius)[] corners, ContentBuilder content)
    {
        const double Kappa = 0.5522847498307936;
        var max = Math.Min(r.Width, r.Height) / 2;
        double R(int i) => corners[i].Option == "None" ? 0 : Math.Min(corners[i].Radius, max);
        bool Bevel(int i) => corners[i].Option is "BevelCorner" or "InsetCorner";
        double tl = R(0), tr = R(1), br = R(2), bl = R(3);
        content.MoveTo(r.Left + tl, r.Top);
        content.LineTo(r.Right - tr, r.Top);
        if (tr > 0)
        {
            if (Bevel(1)) { content.LineTo(r.Right, r.Top + tr); }
            else { content.CurveTo(r.Right - tr + (tr * Kappa), r.Top, r.Right, r.Top + tr - (tr * Kappa), r.Right, r.Top + tr); }
        }
        content.LineTo(r.Right, r.Bottom - br);
        if (br > 0)
        {
            if (Bevel(2)) { content.LineTo(r.Right - br, r.Bottom); }
            else { content.CurveTo(r.Right, r.Bottom - br + (br * Kappa), r.Right - br + (br * Kappa), r.Bottom, r.Right - br, r.Bottom); }
        }
        content.LineTo(r.Left + bl, r.Bottom);
        if (bl > 0)
        {
            if (Bevel(3)) { content.LineTo(r.Left, r.Bottom - bl); }
            else { content.CurveTo(r.Left + bl - (bl * Kappa), r.Bottom, r.Left, r.Bottom - bl + (bl * Kappa), r.Left, r.Bottom - bl); }
        }
        content.LineTo(r.Left, r.Top + tl);
        if (tl > 0)
        {
            if (Bevel(0)) { content.LineTo(r.Left + tl, r.Top); }
            else { content.CurveTo(r.Left, r.Top + tl - (tl * Kappa), r.Left + tl - (tl * Kappa), r.Top, r.Left + tl, r.Top); }
        }
        content.ClosePath();
    }

    private void DrawStroke(PageItem item, IReadOnlyList<PathContour> contours, bool closed, ContentBuilder content)
    {
        var element = item.Element;
        var weight = Values.Number(resolver.Item(element, "StrokeWeight"), 0);
        if (weight <= 0 || contours.Count == 0 || ResolveColor(element, "StrokeColor", "StrokeTint") is not { } color)
        {
            return;
        }
        var alignment = resolver.Item(element, "StrokeAlignment") ?? "CenterAlignment";
        content.Save();
        content.StrokeColor(color);
        content.LineCap((resolver.Item(element, "EndCap") ?? "ButtEndCap") switch { "RoundEndCap" => 1, "ProjectingEndCap" => 2, _ => 0 });
        content.LineJoin((resolver.Item(element, "EndJoin") ?? "MiterEndJoin") switch { "RoundEndJoin" => 1, "BevelEndJoin" => 2, _ => 0 });
        content.MiterLimit(Values.Number(resolver.Item(element, "MiterLimit"), 4));
        var type = resolver.Item(element, "StrokeType") ?? string.Empty;
        if (type.Contains("Dashed", StringComparison.Ordinal))
        {
            content.Dash(DashPattern(element, type, weight));
        }
        else if (type.Contains("Dotted", StringComparison.Ordinal))
        {
            content.LineCap(1).Dash([0, weight * 2]);
        }
        if (closed && alignment is "InsideAlignment" or "OutsideAlignment")
        {
            // Stroke twice as wide, clipped to the inside (or outside) of the path.
            if (alignment == "OutsideAlignment")
            {
                var bounds = item.InnerBounds.Inflate((weight * 2) + 10);
                content.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            AppendPath(item, contours, content);
            content.Clip(evenOdd: alignment == "OutsideAlignment" || contours.Count > 1);
            content.LineWidth(weight * 2);
        }
        else
        {
            content.LineWidth(weight);
        }
        AppendPath(item, contours, content);
        content.Stroke();
        content.Restore();
    }

    /// <summary>
    /// Dash lengths of a dashed stroke in points. The canned styles ("Canned Dashed 3x2", "... 4x4") scale with the
    /// weight; the custom "Dashed" style has its lengths in StrokeDashAndGap, which InDesign writes as an attribute
    /// ("2.5 3.39") or, from a style, as a Properties list of ListItem values.
    /// </summary>
    private double[] DashPattern(XElement element, string type, double weight)
    {
        var canned = CannedDash().Match(type);
        if (canned.Success)
        {
            return [weight * Values.Number(canned.Groups[1].Value, 3), weight * Values.Number(canned.Groups[2].Value, 2)];
        }
        var source = resolver.ObjectChain(element)
            .Select(e => e.Element(IdmlXml.Properties)?.Element("StrokeDashAndGap") is { } list && list.HasElements
                ? string.Join(' ', list.Elements().Select(item => item.Value))
                : e.Attribute("StrokeDashAndGap")?.Value)
            .FirstOrDefault(value => value is not null);
        var custom = Values.List(source);
        return custom.Length >= 2 && custom.Any(length => length > 0) ? custom : [weight * 3, weight * 2];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Canned Dashed (\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)")]
    private static partial System.Text.RegularExpressions.Regex CannedDash();

    private void DrawGraphic(GraphicFrame frame, IReadOnlyList<PathContour> contours, ContentBuilder content)
    {
        var graphic = frame.Graphic;
        if (graphic is null)
        {
            return;
        }
        var uri = graphic.Element("Link")?.Attribute("LinkResourceURI")?.Value;
        if (graphic.Name.LocalName is not ("Image" or "PDF" or "ImportedPage"))
        {
            warnings.Add($"Placed {graphic.Name.LocalName} graphics are not rendered ({uri}).");
            return;
        }
        var path = ResolveLink(uri);
        if (graphic.Name.LocalName != "Image" && path is not null && !IsRasterFile(path))
        {
            DrawPlacedPdf(frame, graphic, path, contours, content);
            return;
        }
        var image = path is null ? null : Pdf.Image(path);
        var boundsElement = graphic.Element("Properties")?.Element("GraphicBounds");
        var left = Values.Number(boundsElement?.Attribute("Left")?.Value, 0);
        var top = Values.Number(boundsElement?.Attribute("Top")?.Value, 0);
        var right = Values.Number(boundsElement?.Attribute("Right")?.Value, 0);
        var bottom = Values.Number(boundsElement?.Attribute("Bottom")?.Value, 0);
        if (image is not null && (right <= left || bottom <= top))
        {
            right = left + image.Width;
            bottom = top + image.Height;
        }
        content.Save();
        if (contours.Count > 0 && contours.All(c => !c.IsOpen))
        {
            AppendPath(frame, contours, content);
            content.Clip(contours.Count > 1);
        }
        content.Transform(CoreIdml.Matrix.Parse(graphic.Attribute("ItemTransform")?.Value));
        if (image is not null)
        {
            content.Transform(new CoreIdml.Matrix(right - left, 0, 0, -(bottom - top), left, bottom));
            content.XObject(image.Name);
        }
        else
        {
            missingImages.Add(uri ?? "(no link)");
            if (options.MarkMissingImages)
            {
                var grey = new ResolvedColor(ColorSpaceKind.Gray, [0.5]);
                content.LineWidth(0.5).StrokeColor(grey);
                content.Rectangle(left, top, right - left, bottom - top).Stroke();
                content.MoveTo(left, top).LineTo(right, bottom).MoveTo(right, top).LineTo(left, bottom).Stroke();
            }
        }
        content.Restore();
    }

    /// <summary>The PNG files placed on <paramref name="pages"/> and their masters (a superset of what gets drawn).</summary>
    private List<string> PlacedPngs(List<Page> pages)
    {
        var paths = new List<string>();
        foreach (var frame in RenderedSpreads(pages).SelectMany(spread => spread.AllPageItems).OfType<GraphicFrame>())
        {
            if (!IsHidden(frame) && frame.Graphic is { Name.LocalName: "Image" or "PDF" or "ImportedPage" } graphic
                && ResolveLink(graphic.Element("Link")?.Attribute("LinkResourceURI")?.Value) is { } path
                && Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) && !paths.Contains(path))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary>
    /// For <see cref="PdfExportOptions.MaxImageDpi"/>: the pixel size each placed JPEG/PNG needs at <paramref name="dpi"/>,
    /// from its largest placement (graphic bounds scaled by the image's and its frame's transforms).
    /// </summary>
    private Dictionary<string, (int Width, int Height)> ImageLimits(List<Page> pages, double dpi)
    {
        var limits = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);
        foreach (var frame in RenderedSpreads(pages).SelectMany(spread => spread.AllPageItems).OfType<GraphicFrame>())
        {
            if (IsHidden(frame) || frame.Graphic is not { Name.LocalName: "Image" } graphic
                || ResolveLink(graphic.Element("Link")?.Attribute("LinkResourceURI")?.Value) is not { } path || !IsRasterFile(path))
            {
                continue;
            }
            var bounds = graphic.Element("Properties")?.Element("GraphicBounds");
            var width = Values.Number(bounds?.Attribute("Right")?.Value, 0) - Values.Number(bounds?.Attribute("Left")?.Value, 0);
            var height = Values.Number(bounds?.Attribute("Bottom")?.Value, 0) - Values.Number(bounds?.Attribute("Top")?.Value, 0);
            var matrix = CoreIdml.Matrix.Parse(graphic.Attribute("ItemTransform")?.Value).Then(frame.SpreadTransform);
            var pixelsWide = (int)Math.Ceiling(width * Math.Sqrt((matrix.A * matrix.A) + (matrix.B * matrix.B)) * dpi / 72);
            var pixelsHigh = (int)Math.Ceiling(height * Math.Sqrt((matrix.C * matrix.C) + (matrix.D * matrix.D)) * dpi / 72);
            if (pixelsWide <= 0 || pixelsHigh <= 0)
            {
                continue;
            }
            limits[path] = limits.TryGetValue(path, out var known) ? (Math.Max(known.Width, pixelsWide), Math.Max(known.Height, pixelsHigh)) : (pixelsWide, pixelsHigh);
        }
        return limits;
    }

    /// <summary>The spreads drawn for <paramref name="pages"/>: their own and the masters they apply (and those masters' masters).</summary>
    private List<Spread> RenderedSpreads(List<Page> pages)
    {
        var spreads = new List<Spread>();
        foreach (var page in pages)
        {
            if (!spreads.Exists(spread => spread.Element == page.Spread.Element))
            {
                spreads.Add(page.Spread);
            }
            var master = options.IncludeMasterItems ? page.AppliedMaster : null;
            while (master is not null && !spreads.Exists(spread => spread.Element == master.Element))
            {
                spreads.Add(master);
                var masterPages = master.Pages;
                master = masterPages.Count > 0 ? masterPages[0].AppliedMaster : null;
            }
        }
        return spreads;
    }

    private static bool IsRasterFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";

    /// <summary>A placed PDF page (or InDesign page placed as PDF), drawn as vector content clipped to its frame.</summary>
    private void DrawPlacedPdf(GraphicFrame frame, XElement graphic, string path, IReadOnlyList<PathContour> contours, ContentBuilder content)
    {
        var attributes = graphic.Element("PDFAttribute");
        var pageNumber = (int)Values.Number(attributes?.Attribute("PageNumber")?.Value ?? graphic.Attribute("PageNumber")?.Value, 1);
        var crop = attributes?.Attribute("PDFCrop")?.Value ?? "CropContentVisibleLayers";
        var form = Pdf.PlacedPdf(path, pageNumber, crop, out var error);
        if (form is null)
        {
            warnings.Add($"Placed PDF {Path.GetFileName(path)} page {pageNumber} could not be read: {error}");
            return;
        }
        var bounds = graphic.Element("Properties")?.Element("GraphicBounds");
        var left = Values.Number(bounds?.Attribute("Left")?.Value, 0);
        var top = Values.Number(bounds?.Attribute("Top")?.Value, 0);
        var right = Values.Number(bounds?.Attribute("Right")?.Value, left + form.X1 - form.X0);
        var bottom = Values.Number(bounds?.Attribute("Bottom")?.Value, top + form.Y1 - form.Y0);
        content.Save();
        if (contours.Count > 0 && contours.All(c => !c.IsOpen))
        {
            AppendPath(frame, contours, content);
            content.Clip(contours.Count > 1);
        }
        content.Transform(CoreIdml.Matrix.Parse(graphic.Attribute("ItemTransform")?.Value));
        // GraphicBounds of a placed PDF are the cropped region in the PDF's own user space (points; x as is). InDesign
        // shows that space flipped vertically within the bounds, so PDF y maps to Top + Bottom - y.
        _ = (left, right);
        content.Transform(new CoreIdml.Matrix(1, 0, 0, -1, 0, top + bottom));
        content.XObject(form.Name);
        content.Restore();
    }

    /// <summary>Finds a linked file: the URI's own path, else the same file name in the link folders.</summary>
    internal string? ResolveLink(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }
        if (options.LinkRemap.TryGetValue(uri, out var mapped))
        {
            return mapped;
        }
        var path = uri;
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..];
            while (path.StartsWith("//", StringComparison.Ordinal))
            {
                path = path[1..];
            }
            path = Uri.UnescapeDataString(path);
            // "/C:/dir/a.jpg" → "C:/dir/a.jpg".
            if (path.Length > 3 && path[0] == '/' && path[2] == ':' && char.IsLetter(path[1]))
            {
                path = path[1..];
            }
        }
        if (File.Exists(path))
        {
            return path;
        }
        // Classic Mac paths use ':' separators.
        var name = Path.GetFileName(path.Replace(':', '/').Replace('\\', '/'));
        if (options.LinkRemap.TryGetValue(name, out var byName))
        {
            return byName;
        }
        var folders = new List<string>(options.LinkDirectories);
        if (options.DocumentPath is { } documentPath && Path.GetDirectoryName(Path.GetFullPath(documentPath)) is { } folder)
        {
            folders.Add(folder);
            folders.Add(Path.Combine(folder, "Links"));
        }
        foreach (var directory in folders)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    // ---- composed text -------------------------------------------------------------------------------

    private static bool IsInStory(XElement element) => element.Ancestors("Story").Any();

    /// <summary>
    /// The text of a frame anchored in another story: its own story, composed into it alone (anchored frames are not
    /// on a spread, so <see cref="ComposeStories"/> does not see them).
    /// </summary>
    private List<DrawOp> AnchoredFrameOps(TextFrame frame, string pageName)
    {
        var key = frame.Id + "@" + pageName;
        if (frameOps.TryGetValue(key, out var cached))
        {
            return cached;
        }
        frameOps[key] = [];
        if (document.FindStory(frame.StoryId) is { } story)
        {
            var layout = composer.Compose(story.Element, [FrameArea(frame, pageName)]);
            frameOps[key] = layout.OpsByFrame.GetValueOrDefault(frame.Id) ?? [];
        }
        return frameOps[key];
    }

    // Ops of the frame they were laid out in; items anchored in its text are wrapped on that frame's spread.
    private void DrawOps(IReadOnlyList<DrawOp> ops, ContentBuilder content, PageItem frame, string pageName, MasterSpread? master)
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case PageItemOp anchored when PageItem.TryWrap(document, frame.Spread, anchored.Element) is { } item:
                    content.Save();
                    content.Transform(anchored.Placement);
                    DrawItem(item, content, pageName, master);
                    content.Restore();
                    break;
                case RectOp rect:
                    content.FillColor(rect.Color).Rectangle(rect.Rect.X, rect.Rect.Y, rect.Rect.Width, rect.Rect.Height).Fill();
                    break;
                case LineOp line:
                    content.Save().LineWidth(line.Width).StrokeColor(line.Color).LineCap(0);
                    content.MoveTo(line.From.X, line.From.Y).LineTo(line.To.X, line.To.Y).Stroke();
                    content.Restore();
                    break;
                case GlyphRunOp run:
                    DrawGlyphs(run, content);
                    break;
            }
        }
    }

    private void DrawGlyphs(GlyphRunOp run, ContentBuilder content)
    {
        if (run.Glyphs.Count == 0 || run.Color is null)
        {
            return;
        }
        var font = Pdf.Font(run.Font);
        var scale = run.Size * run.HorizontalScale;
        var array = new StringBuilder("[");
        var pending = new List<ushort>();
        foreach (var glyph in run.Glyphs)
        {
            font.Use(glyph.Glyph, glyph.Text);
            pending.Add(glyph.Glyph);
            var adjustment = scale <= 0 ? 0 : (glyph.NaturalAdvance - glyph.Advance) / scale * 1000;
            if (Math.Abs(adjustment) >= 0.05)
            {
                array.Append(font.Hex(pending)).Append(' ').Append(Number(adjustment)).Append(' ');
                pending.Clear();
            }
        }
        if (pending.Count > 0)
        {
            array.Append(font.Hex(pending));
        }
        array.Append("] TJ");
        content.FillColor(run.Color);
        content.Raw("BT");
        content.Raw($"/{font.Name} {Number(run.Size)} Tf");
        if (Math.Abs(run.HorizontalScale - 1) > 1e-6)
        {
            content.Raw($"{Number(run.HorizontalScale * 100)} Tz");
        }
        content.Raw($"1 0 0 -1 {Number(run.X)} {Number(run.Y)} Tm");
        content.Raw(array.ToString());
        content.Raw("ET");
    }

    internal static string Describe(PageItem item) => string.Create(CultureInfo.InvariantCulture, $"{item.Kind} {item.Id}");
}
