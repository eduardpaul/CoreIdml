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
    // Page-dependent markers and variables, for the document being rendered.
    private TextVariables? variables;
    // The document page being drawn: master text is composed for it.
    private Page? drawingPage;

    private PdfDocumentBuilder Pdf => pdf ?? throw new InvalidOperationException("Render has not started.");

    /// <summary>
    /// The folder relative links resolve against: the folder of <see cref="PdfExportOptions.DocumentPath"/>, else
    /// the link folder given to the constructor (the PDF file's folder, when exporting to a file); null resolves them
    /// against the working directory.
    /// </summary>
    private readonly string? relativeLinkFolder;

    public IdmlRenderer(IdmlDocument document, PdfExportOptions options, string? linkFolder = null)
    {
        this.document = document;
        this.options = options;
        relativeLinkFolder = options.DocumentPath is { } documentPath ? Path.GetDirectoryName(Path.GetFullPath(documentPath)) : linkFolder;
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
        var inset = TextInset(resolver.ObjectChain(frame.Element));
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
        variables = new TextVariables(document);
        ComposeStories(variables);
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
        AssignPages(document, fonts.Substitutions);
        foreach (var (font, codePoints) in fonts.MissingGlyphs)
        {
            var characters = string.Join(" ", codePoints.Select(c => string.Create(CultureInfo.InvariantCulture, $"{char.ConvertFromUtf32(c)} (U+{c:X4})")));
            warnings.Add($"{font} has no glyph for {characters}: left out, as InDesign does (it highlights them in pink).");
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

    /// <summary>
    /// Fills <see cref="FontSubstitution.Pages"/>: where each missing font is used, like InDesign's Missing Fonts
    /// dialog (page names, and "master &lt;name&gt;" for stories on master spreads).
    /// </summary>
    internal static void AssignPages(IdmlDocument document, IEnumerable<FontSubstitution> substitutions)
    {
        var pagesByStory = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string storyId, string page) =>
            (pagesByStory.TryGetValue(storyId, out var set) ? set : pagesByStory[storyId] = new SortedSet<string>(StringComparer.Ordinal)).Add(page);
        foreach (var spread in document.Spreads)
        {
            foreach (var frame in spread.AllPageItems.OfType<TextFrame>())
            {
                if (frame.Page?.Name is { } name)
                {
                    Add(frame.StoryId, name);
                }
            }
        }
        foreach (var master in document.MasterSpreads)
        {
            foreach (var frame in master.AllPageItems.OfType<TextFrame>())
            {
                Add(frame.StoryId, "master " + master.Name);
            }
        }
        foreach (var substitution in substitutions)
        {
            substitution.Pages.Clear();
            substitution.Pages.AddRange(substitution.Stories.SelectMany(story => pagesByStory.GetValueOrDefault(story) ?? []).Distinct().Order(StringComparer.Ordinal));
        }
    }

    // ---- text ----------------------------------------------------------------------------------------

    private void ComposeStories(TextVariables pageText)
    {
        var framesByStory = document.Spreads
            .SelectMany(spread => spread.AllPageItems)
            .OfType<TextFrame>()
            .GroupBy(frame => frame.StoryId, StringComparer.Ordinal);
        var composed = new List<(Story Story, IReadOnlyList<TextFrame> Frames)>();
        foreach (var group in framesByStory)
        {
            var story = document.FindStory(group.Key);
            if (story is null)
            {
                continue;
            }
            // The group holds the story's frames in document order, as Story.Frames would find them.
            var ordered = Story.ThreadOrder([.. group]);
            ComposeStory(story, ordered);
            composed.Add((story, ordered));
        }
        // Running headers print text from the composed pages: compose the stories holding one again, now that the
        // pages are known.
        pageText.UseLayouts(composed.Select(entry => (entry.Frames, storyLayouts[entry.Story.Id])));
        foreach (var (story, ordered) in composed.Where(entry => pageText.HasRunningHeader(entry.Story.Element)))
        {
            ComposeStory(story, ordered);
        }
    }

    private void ComposeStory(Story story, IReadOnlyList<TextFrame> ordered)
    {
        foreach (var frame in ordered)
        {
            frameOps.Remove(frame.Id);
        }
        var areas = ordered.Select(frame => FrameArea(frame, frame.Page?.Name ?? "1") with { Markers = Markers(frame, frame.Page) }).ToList();
        var layout = composer.Compose(story.Element, areas);
        storyLayouts[story.Id] = layout;
        foreach (var (frameId, ops) in layout.OpsByFrame)
        {
            frameOps[frameId] = ops;
        }
    }

    private FrameMarkers Markers(TextFrame frame, Page? page) => variables?.For(frame, page) ?? FrameMarkers.None;

    /// <summary>
    /// Composes a master-page story for the document page being drawn (page numbers, section markers and variables
    /// differ per page).
    /// </summary>
    private List<DrawOp> MasterFrameOps(TextFrame frame, MasterSpread master, string pageName)
    {
        var pageKey = "@" + (drawingPage?.Id ?? pageName);
        var key = frame.Id + pageKey;
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
        var ordered = Story.ThreadOrder(chain);
        var layout = composer.Compose(story.Element, ordered.Select(f => FrameArea(f, pageName) with { Markers = Markers(f, drawingPage) }).ToList());
        // Reported like a page story; overset on any page it is drawn on counts.
        if (layout.Overset || !storyLayouts.ContainsKey(story.Id))
        {
            storyLayouts[story.Id] = layout;
        }
        foreach (var (frameId, ops) in layout.OpsByFrame)
        {
            frameOps[frameId + pageKey] = ops;
        }
        return frameOps.TryGetValue(key, out var result) ? result : [];
    }

    internal FrameArea FrameArea(TextFrame frame, string pageName)
    {
        var element = frame.Element;
        var inner = frame.InnerBounds;
        var chain = resolver.ObjectChain(element);
        string? Preference(string name) => AttributeResolver.FramePreference(chain, name);
        var inset = TextInset(chain);
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
        var page = frame.Page;
        return new FrameArea(
            frame.Id,
            rects,
            Preference("VerticalJustification") ?? "TopAlign",
            Preference("FirstBaselineOffset") ?? "AscentOffset",
            Values.Number(Preference("MinimumFirstBaselineOffset"), 0),
            pageName,
            BaselineGridOf(frame, chain, inner.Y, inner.Y + inset.Top))
        {
            PageIndex = page?.Index ?? -1,
            OddPage = page is null || IsOddPage(page),
            Bounds = inner,
            Page = page is null ? null : PageArea(frame, page),
        };
    }

    /// <summary>
    /// The frame's page in the frame's inner coordinates (the bounding box, for a rotated frame), for anchored objects
    /// positioned relative to the page edge or margins.
    /// </summary>
    private static PageArea PageArea(TextFrame frame, Page page)
    {
        var toFrame = Invert(frame.SpreadTransform);
        Rect InFrame(Rect spread)
        {
            var corners = new[] { new Point(spread.Left, spread.Top), new Point(spread.Right, spread.Top), new Point(spread.Left, spread.Bottom), new Point(spread.Right, spread.Bottom) }
                .Select(toFrame.Apply).ToList();
            return Rect.FromEdges(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
        }
        var bounds = page.SpreadBounds;
        var margins = page.Margins;
        var inside = Rect.FromEdges(bounds.Left + margins.Left, bounds.Top + margins.Top, bounds.Right - margins.Right, bounds.Bottom - margins.Bottom);
        return new PageArea(InFrame(bounds), InFrame(inside), page.IsLeftPage);
    }

    /// <summary>
    /// The baseline grid of a text frame, in its inner coordinates. A custom frame grid (<c>BaselineFrameGridOption</c>
    /// with <c>UseCustomBaselineFrameGrid</c>, each attribute from the frame, its object style or the document default)
    /// starts its offset below the top of the frame, of its inset, of the page or of the page's top margin; otherwise the
    /// document grid (<c>GridPreference</c>) starts <c>BaselineStart</c> below the top of the page or of its top margin.
    /// Measured in 06-frame-baseline-grid: the 14 pt document grid from 36 pt below the page top (p1 to p5), a 20 pt frame
    /// grid 10 pt below the frame top (p5), 18 pt frame grids from the frame top and from the top of a 30 pt inset (p6).
    /// A page-relative grid needs the frame on a page with no rotation, skew or scale (otherwise null: not measured).
    /// </summary>
    private BaselineGrid? BaselineGridOf(TextFrame frame, IReadOnlyList<XElement> chain, double frameTop, double insetTop)
    {
        var preferences = document.Package.TryGetXml("Resources/Preferences.xml")?.Root;
        // An object style that leaves the baseline options alone (EnableTextFrameBaselineOptions="false") gives no grid.
        var sources = AttributeResolver.StylesControl(chain, "EnableTextFrameBaselineOptions") ? chain : chain.Take(1);
        string? FrameGrid(string name) => sources.Select(element => element.Element("BaselineFrameGridOption"))
            .Append(preferences?.Element("BaselineFrameGridOption"))
            .Select(option => option?.Attribute(name)?.Value)
            .FirstOrDefault(value => value is not null);
        double start, increment;
        bool fromMargin;
        if (FrameGrid("UseCustomBaselineFrameGrid") == "true")
        {
            start = Values.Number(FrameGrid("StartingOffsetForBaselineFrameGrid"), 0);
            increment = Values.Number(FrameGrid("BaselineFrameGridIncrement"), 12);
            switch (FrameGrid("BaselineFrameGridRelativeOption"))
            {
                case "TopOfFrame":
                    return Grid(frameTop + start);
                case "TopOfPage":
                    fromMargin = false;
                    break;
                case "TopOfMargin":
                    fromMargin = true;
                    break;
                default:
                    return Grid(insetTop + start);
            }
        }
        else
        {
            var grid = preferences?.Element("GridPreference");
            start = Values.Number(grid?.Attribute("BaselineStart")?.Value, 36);
            increment = Values.Number(grid?.Attribute("BaselineDivision")?.Value, 12);
            fromMargin = grid?.Attribute("BaselineGridRelativeOption")?.Value == "TopOfMarginOfBaselineGridRelativeOption";
        }
        // Anchored frames are not on a spread: their page position is only known when the anchor is placed.
        var onSpread = frame.Element.Ancestors().Any(element => element.Name.LocalName is "Spread" or "MasterSpread");
        var transform = frame.SpreadTransform;
        if (!onSpread || frame.Page is not { } page || transform is not { A: 1, B: 0, C: 0, D: 1 })
        {
            return null;
        }
        // Inner y = page y + page top (spread) - the frame's translation.
        return Grid(start + (fromMargin ? page.Margins.Top : 0) + page.SpreadBounds.Y - transform.Ty);

        BaselineGrid? Grid(double top) => increment > 0 ? new BaselineGrid(top, increment) : null;
    }

    /// <summary>
    /// Whether a page counts as odd for "start on next odd page": its page number (the last digit of its name, "A-12"
    /// is even), or, for a name without digits (roman numerals), the right-hand side in a facing document, or the
    /// position in the document.
    /// </summary>
    private static bool IsOddPage(Page page)
    {
        var digit = page.Name.LastOrDefault(char.IsAsciiDigit);
        if (digit != default)
        {
            return (digit - '0') % 2 == 1;
        }
        return page.Document.FacingPages ? !page.IsLeftPage : page.Index % 2 == 0;
    }

    /// <summary>
    /// How far the text of a frame stays inside its path: the inset spacing plus the part of a visible stroke that
    /// falls inside the path. Measured with centred strokes: text starts half the stroke weight further in on every
    /// side (02-item-object-styles p1: a 2 pt stroke and an 8 pt inset put the text 9 pt in, and the 540 pt frame has
    /// two 255 pt columns; 06-frame-autosize: 1 pt strokes, text 0.5 pt in; 06-frame-basics: 0.5 pt strokes, 0.25 pt).
    /// A stroke with no colour takes no room (01-doc-masters: 1 pt strokes of [None], text at the inset). An inside
    /// stroke counts its whole weight and an outside one nothing (by the same rule; not in the samples).
    /// </summary>
    private Spacing TextInset(IReadOnlyList<XElement> objectChain)
    {
        var inset = FrameInset(objectChain);
        var element = objectChain[0];
        var weight = Values.Number(resolver.Item(element, "StrokeWeight"), 0);
        if (weight <= 0 || resolver.Item(element, "StrokeColor") is null or "Swatch/None")
        {
            return inset;
        }
        var inside = (resolver.Item(element, "StrokeAlignment") ?? "CenterAlignment") switch
        {
            "InsideAlignment" => weight,
            "OutsideAlignment" => 0,
            _ => weight / 2,
        };
        return new Spacing(inset.Top + inside, inset.Right + inside, inset.Bottom + inside, inset.Left + inside);
    }

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
        drawingPage = page;
        var size = page.Size;
        var origin = page.SpreadBounds;
        var bleed = options.IncludeBleed ? Bleed(page) : default;
        // The PDF page: the trim size, or with the bleed the trim plus the bleed on each side (MediaBox and BleedBox),
        // the trim (TrimBox) inside it.
        var pageRect = Outset(origin, bleed);
        var content = new ContentBuilder();
        // PDF space has y up from the bottom; draw everything in IDML spread coordinates (y down).
        content.Transform(new CoreIdml.Matrix(1, 0, 0, -1, 0, pageRect.Height));
        content.Transform(CoreIdml.Matrix.Translation(-pageRect.X, -pageRect.Y));

        if (options.IncludeMasterItems && page.AppliedMaster is { } master)
        {
            DrawMaster(page, master, content, bleed, depth: 0);
        }
        foreach (var item in page.Spread.PageItems)
        {
            if (Intersects(item.SpreadBounds, pageRect))
            {
                DrawItem(item, content, page.Name, null);
            }
        }
        Pdf.AddPage(pageRect.Width, pageRect.Height, new Rect(bleed.Left, bleed.Bottom, size.Width, size.Height), content);
    }

    /// <summary>
    /// The document's bleed on each side of <paramref name="page"/> (File &gt; Document Setup, <c>DocumentBleed*Offset</c>
    /// in the document preferences). In a facing-pages document the inside of a left page is its right edge.
    /// 01-doc-bleed-slug exports 9 pt on every side as a 630 × 810 pt page with the 612 × 792 pt TrimBox at (9, 9)
    /// (<c>reference-bleed.pdf</c>, Use Document Bleed Settings).
    /// </summary>
    private Spacing Bleed(Page page)
    {
        var preference = document.DocumentPreference;
        double Offset(string name) => Math.Max(0, Values.Number(preference?.Attribute(name)?.Value, 0));
        var (inside, outside) = (Offset("DocumentBleedInsideOrLeftOffset"), Offset("DocumentBleedOutsideOrRightOffset"));
        return new Spacing(Offset("DocumentBleedTopOffset"), page.IsLeftPage ? inside : outside, Offset("DocumentBleedBottomOffset"), page.IsLeftPage ? outside : inside);
    }

    private void DrawMaster(Page page, MasterSpread master, ContentBuilder content, Spacing bleed, int depth)
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
            DrawMaster(page, parent, content, bleed, depth + 1);
        }
        var masterArea = Outset(masterPage.SpreadBounds, bleed);
        var overridden = (page.GetAttribute("OverrideList") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        content.Save();
        // Master spread → master page space → MasterPageTransform (page space, i.e. GeometricBounds coordinates) → page → spread.
        content.Transform(Invert(masterPage.Transform).Then(page.MasterPageTransform).Then(page.Transform));
        foreach (var item in master.PageItems)
        {
            if (!overridden.Contains(item.Id) && Intersects(item.SpreadBounds, masterArea))
            {
                DrawItem(item, content, page.Name, master);
            }
        }
        content.Restore();
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.Left <= b.Right && a.Right >= b.Left && a.Top <= b.Bottom && a.Bottom >= b.Top;

    private static Rect Outset(Rect rect, Spacing by) => Rect.FromEdges(rect.Left - by.Left, rect.Top - by.Top, rect.Right + by.Right, rect.Bottom + by.Bottom);

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
        if (closed && Gradient(item, "Fill") is { } gradientFill)
        {
            // Clipped to the path, the shading painted in the gradient's space (InDesign writes the same).
            content.Save();
            AppendPath(item, contours, content);
            content.Clip(evenOdd: contours.Count > 1);
            content.Transform(gradientFill.Space);
            content.PaintShading(Pdf.Shading(gradientFill.Shading));
            content.Restore();
        }
        else if (closed && ResolveColor(element, "FillColor", "FillTint") is { } fill)
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

    /// <summary>The opacity of an item, its own or from its object style (<see cref="AttributeResolver.Blending"/>).</summary>
    private void ApplyOpacity(XElement element, ContentBuilder content)
    {
        var opacity = Values.Number(resolver.Blending(element, "Opacity"), 100);
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
            return null;
        }
        var tint = Values.Number(resolver.Item(element, tintName), -1);
        return tint >= 0 ? color.WithTint(tint) : color;
    }

    /// <summary>
    /// The gradient an item's fill or stroke (<paramref name="part"/> "Fill" or "Stroke") paints with, as a shading
    /// and the matrix from its unit space to the item's inner coordinates; null when the colour is not a gradient.
    /// </summary>
    private (GradientShading Shading, CoreIdml.Matrix Space)? Gradient(PageItem item, string part)
    {
        var element = item.Element;
        var reference = resolver.Item(element, part + "Color");
        if (reference is null || !reference.StartsWith("Gradient/", StringComparison.Ordinal))
        {
            return null;
        }
        if (document.Swatches.ResolveGradient(reference) is not { } gradient)
        {
            warnings.Add($"Gradient {reference} has a stop colour that cannot be drawn (item {element.Attribute("Self")?.Value}): not rendered.");
            return null;
        }
        var start = Values.List(resolver.Item(element, "Gradient" + part + "Start"));
        var space = GradientSpace(
            gradient.Type,
            start.Length == 2 ? new Point(start[0], start[1]) : default,
            Values.Number(resolver.Item(element, "Gradient" + part + "Length"), -1),
            Values.Number(resolver.Item(element, "Gradient" + part + "Angle"), 0),
            item.InnerBounds);
        return (GradientShading.From(gradient), space);
    }

    /// <summary>
    /// The matrix from a shading's unit space (axial along x from 0 to 1, radial of radius 1 round the origin) to an
    /// item's inner coordinates. As in 03-color-gradients, <c>GradientFillStart</c> is in the item's inner coordinates
    /// (the start of a linear gradient, the centre of a radial one), <c>GradientFillLength</c> is the length (radius)
    /// and <c>GradientFillAngle</c> turns it counter-clockwise on the page (y points down, so the axis is (cos a,
    /// −sin a)): InDesign writes the item at angle 30 with start (217.67, 201) and length 166.67 as the matrix
    /// [144.34 83.33 83.33 −144.34 222.67 586] in its y-up page space. A length of 0 or −1 (text, object style
    /// defaults, items never given a gradient position) spans <paramref name="bounds"/>: a linear gradient runs
    /// across the bounds along the angle (03-color-gradients p4 and 07-char-outlined-text p4 fill text with patterns
    /// spanning the frame's width at angle 0); a radial one is centred with a radius of half the larger side, which is
    /// what InDesign stored for the squares, circle and hexagon of 03-color-gradients p3 (not checked on other shapes).
    /// </summary>
    internal static CoreIdml.Matrix GradientSpace(GradientType type, Point start, double length, double angle, Rect bounds)
    {
        // The unit axis (dx, dy) in y-down coordinates.
        var radians = angle * Math.PI / 180;
        var (dx, dy) = (Math.Cos(radians), -Math.Sin(radians));
        if (length <= 0)
        {
            var center = bounds.Center;
            if (type == GradientType.Radial)
            {
                start = center;
                length = Math.Max(bounds.Width, bounds.Height) / 2;
            }
            else
            {
                // The bounds' extent along the axis, through the centre.
                var half = (Math.Abs(dx) * bounds.Width / 2) + (Math.Abs(dy) * bounds.Height / 2);
                start = new Point(center.X - (dx * half), center.Y - (dy * half));
                length = 2 * half;
            }
        }
        if (length <= 0)
        {
            length = 1;
        }
        // x along the axis, y perpendicular to it (a radial shading is round in both).
        return new CoreIdml.Matrix(length * dx, length * dy, -length * dy, length * dx, start.X, start.Y);
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
        if (weight <= 0 || contours.Count == 0)
        {
            return;
        }
        var gradient = Gradient(item, "Stroke");
        var color = gradient is null ? ResolveColor(element, "StrokeColor", "StrokeTint") : null;
        if (gradient is null && color is null)
        {
            return;
        }
        var alignment = resolver.Item(element, "StrokeAlignment") ?? "CenterAlignment";
        content.Save();
        if (gradient is { } paint)
        {
            // A shading pattern, as InDesign writes gradient strokes; its matrix is in the page's default space.
            content.PatternColor(Pdf.Pattern(paint.Shading, paint.Space.Then(content.CurrentMatrix)), stroke: true);
        }
        else
        {
            content.StrokeColor(color!);
        }
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
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            // "file://host/share/a.jpg" (a UNC path) or "file://localhost/C:/a.jpg", as the packager reads them.
            path = CoreIdml.Packaging.DocumentPackager.LocalPath(path)!;
        }
        else if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
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
        // A relative path is relative to the document (not to the process's working directory) when its folder is known.
        if (relativeLinkFolder is not null && !Path.IsPathRooted(path) && !path.Contains(':', StringComparison.Ordinal))
        {
            var relative = Path.Combine(relativeLinkFolder, path);
            if (File.Exists(relative))
            {
                return relative;
            }
        }
        else if (File.Exists(path))
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
            var layout = composer.Compose(story.Element, [FrameArea(frame, pageName) with { Markers = Markers(frame, drawingPage) }]);
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
                    DrawGlyphs(run, content, frame.InnerBounds);
                    break;
                case TextRuleOp rule:
                    DrawTextRule(rule, content);
                    break;
            }
        }
    }

    /// <summary>
    /// Draws a glyph run: its fill, then a stroke on the outlines as InDesign writes text strokes
    /// (07-char-outlined-text): text render mode 1 (stroke only) at the stroke weight, in the stroke colour, join and
    /// miter limit. An outside stroke (the default alignment) under a fill is painted first at twice the weight, and
    /// the fill over it hides the inner half (red fill + 2 pt black stroke: <c>4 w</c>, <c>1 Tr</c>, then the fill);
    /// with no fill it is centred on the outline at its weight (<c>0.5 w</c> for 0.5 pt). A centred stroke goes over
    /// the fill (from memory: no sample has one). A gradient stroke is a shading pattern spanning the frame, like a
    /// gradient fill (03-color-gradients p4).
    /// </summary>
    private void DrawGlyphs(GlyphRunOp run, ContentBuilder content, Rect frameBounds)
    {
        var filled = run.Color is not null || run.Gradient is not null;
        if (run.Glyphs.Count == 0 || (!filled && run.Stroke is null))
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
        var text = array.ToString();
        if (run.Stroke is { } stroke && (stroke.Outside || !filled))
        {
            DrawText(run, font.Name, text, content, frameBounds, filled ? 2 * stroke.Weight : stroke.Weight);
        }
        if (filled)
        {
            if (run.Gradient is { } gradient)
            {
                // A shading pattern laid over the frame, as InDesign fills gradient text (03-color-gradients p4).
                content.PatternColor(TextPattern(gradient, content, frameBounds), stroke: false);
            }
            else
            {
                content.FillColor(run.Color!);
            }
            DrawText(run, font.Name, text, content, frameBounds, strokeWidth: null);
        }
        if (run.Stroke is { Outside: false } centred && filled)
        {
            DrawText(run, font.Name, text, content, frameBounds, centred.Weight);
        }
    }

    /// <summary>
    /// One text object of a glyph run: filled in the current fill colour, or stroked (render mode 1) with
    /// <paramref name="strokeWidth"/> in the run's stroke paint, inside its own graphics state. Skew shears the text
    /// matrix about the baseline: 07-char-basics p5 writes 15° as <c>24 0 6.4308 24</c> (tan 15° × 24).
    /// </summary>
    private void DrawText(GlyphRunOp run, string fontName, string glyphs, ContentBuilder content, Rect frameBounds, double? strokeWidth)
    {
        if (strokeWidth is { } width)
        {
            var stroke = run.Stroke!;
            content.Save();
            if (stroke.Gradient is { } gradient)
            {
                content.PatternColor(TextPattern(gradient, content, frameBounds), stroke: true);
            }
            else
            {
                content.StrokeColor(stroke.Color!);
            }
            content.LineWidth(width).LineJoin(stroke.Join).MiterLimit(stroke.MiterLimit);
        }
        content.Raw("BT");
        content.Raw($"/{fontName} {Number(run.Size)} Tf");
        if (Math.Abs(run.HorizontalScale - 1) > 1e-6)
        {
            content.Raw($"{Number(run.HorizontalScale * 100)} Tz");
        }
        if (strokeWidth is not null)
        {
            content.Raw("1 Tr");
        }
        var shear = Math.Tan(run.Skew * Math.PI / 180) * run.VerticalScale;
        content.Raw($"1 0 {Number(shear)} {Number(-run.VerticalScale)} {Number(run.X)} {Number(run.Y)} Tm");
        content.Raw(glyphs);
        content.Raw("ET");
        if (strokeWidth is not null)
        {
            content.Restore();
        }
    }

    /// <summary>The shading pattern of a gradient text fill or stroke, spanning the frame (see <see cref="GradientSpace"/>).</summary>
    private string TextPattern(TextGradient gradient, ContentBuilder content, Rect frameBounds)
    {
        var space = GradientSpace(gradient.Gradient.Type, gradient.Start, gradient.Length, gradient.Angle, frameBounds);
        return Pdf.Pattern(GradientShading.From(gradient.Gradient), space.Then(content.CurrentMatrix));
    }

    internal static string Describe(PageItem item) => string.Create(CultureInfo.InvariantCulture, $"{item.Kind} {item.Id}");
}
