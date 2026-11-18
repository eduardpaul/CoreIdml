using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Packaging;
using CoreIdml.Xml;

namespace CoreIdml;

/// <summary>
/// An InDesign document (the role <c>WordprocessingDocument</c> plays for .docx in the OpenXML SDK).
/// Create a blank one with <see cref="Create"/>, or open a template made in InDesign with <see cref="Open(string)"/>.
/// Not thread-safe: use one instance per thread.
/// </summary>
public sealed partial class IdmlDocument : IDisposable
{
    private const string GraphicPath = "Resources/Graphic.xml";
    private const string StylesPath = "Resources/Styles.xml";
    private const string PreferencesPath = "Resources/Preferences.xml";
    private const double SpreadVerticalGap = 180;

    private long nextId = -1;

    // FindStory's index, and the version of designmap.xml it was built from (counted by DesignMap's Changed event).
    private Dictionary<string, Story>? storyIndex;
    private long storyIndexManifestVersion;
    private long manifestVersion;

    private IdmlDocument(IdmlPackage package)
    {
        Package = package;
        DesignMap = package.GetXml(IdmlNames.DesignMapPath).Root
            ?? throw new IdmlException(IdmlErrorKind.InvalidFormat, "designmap.xml has no root element.");
        if (DesignMap.Name.LocalName != "Document")
        {
            throw new IdmlException(IdmlErrorKind.InvalidFormat, "designmap.xml root is not a <Document> element.");
        }
        DesignMap.Changed += (_, _) => manifestVersion++;
        Styles = new StyleCatalog(this);
        Swatches = new SwatchCatalog(this);
        Layers = new LayerCatalog(this);
        Tags = new TagCatalog(this);
    }

    /// <summary>The underlying zip package, for parts this API does not model.</summary>
    public IdmlPackage Package { get; }

    /// <summary>The <c>&lt;Document&gt;</c> element of designmap.xml.</summary>
    public XElement DesignMap { get; }

    /// <summary>The IDML DOM version this document was written with (e.g. "7.5"); new parts use the same.</summary>
    public string DomVersion => DesignMap.Attribute("DOMVersion")?.Value ?? "7.5";

    public StyleCatalog Styles { get; }

    public SwatchCatalog Swatches { get; }

    public LayerCatalog Layers { get; }

    /// <summary>XML structure tags (Window &gt; Utilities &gt; Tags in InDesign).</summary>
    public TagCatalog Tags { get; }

    /// <summary>Default page size from the document preferences.</summary>
    public Size PageSize
    {
        get
        {
            var preference = DocumentPreference;
            return new Size(
                IdmlXml.ParseDouble(preference?.Attribute("PageWidth")?.Value) ?? PageSizes.A4.Width,
                IdmlXml.ParseDouble(preference?.Attribute("PageHeight")?.Value) ?? PageSizes.A4.Height);
        }
    }

    public bool FacingPages => DocumentPreference?.Attribute("FacingPages")?.Value == "true";

    /// <summary>Spreads in document order.</summary>
    public IReadOnlyList<Spread> Spreads =>
        ManifestPaths("Spread").Select(path => new Spread(this, path, RootChild(path, "Spread"))).ToList();

    public IReadOnlyList<MasterSpread> MasterSpreads =>
        ManifestPaths("MasterSpread").Select(path => new MasterSpread(this, path, RootChild(path, "MasterSpread"))).ToList();

    /// <summary>All document pages in order (master pages excluded).</summary>
    public IReadOnlyList<Page> Pages => Spreads.SelectMany(spread => spread.Pages).ToList();

    /// <summary>All stories (the text flowing through text frames, tables included).</summary>
    public IReadOnlyList<Story> Stories =>
        ManifestPaths("Story").Select(path => new Story(this, path, RootChild(path, "Story"))).ToList();

    /// <summary>Page items on every spread, nested items (groups) included.</summary>
    public IEnumerable<PageItem> AllPageItems => Spreads.SelectMany(spread => spread.AllPageItems);

    internal XElement? DocumentPreference =>
        Package.TryGetXml(PreferencesPath)?.Root?.Element("DocumentPreference");

    internal XElement GraphicRoot => Package.GetXml(GraphicPath).Root!;

    internal XElement StylesRoot => Package.GetXml(StylesPath).Root!;

    /// <summary>Creates a blank document from InDesign's default skeleton.</summary>
    public static IdmlDocument Create(DocumentOptions? options = null)
    {
        options ??= new DocumentOptions();
        if (options.PageCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PageCount must be at least 1.");
        }
        if (options.PageSize.Width <= 0 || options.PageSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PageSize must be positive.");
        }

        var package = IdmlPackage.Create();
        foreach (var (path, resource) in BlankParts)
        {
            package.SetBytes(path, ReadBlankResource(resource));
        }

        var document = new IdmlDocument(package);
        document.ApplyDocumentSetup(options);
        document.AddMasterSpread("A", "Master");
        for (var i = 0; i < options.PageCount; i++)
        {
            document.AddPage();
        }
        return document;
    }

    /// <summary>Opens an .idml file.</summary>
    /// <exception cref="IdmlException">The file is not an IDML package.</exception>
    public static IdmlDocument Open(string path) => new(IdmlPackage.Open(path));

    /// <summary>Opens an .idml file from a stream, which is left open.</summary>
    /// <exception cref="IdmlException">The stream does not hold an IDML package.</exception>
    public static IdmlDocument Open(Stream stream) => new(IdmlPackage.Open(stream));

    /// <summary>Opens an .idml file held in memory. The data is not kept after the call.</summary>
    /// <exception cref="IdmlException">The data is not an IDML package.</exception>
    public static IdmlDocument Open(ReadOnlyMemory<byte> data) => new(IdmlPackage.Open(data));

    /// <summary>Opens an .idml file from a stream without blocking on it. The stream is left open.</summary>
    /// <exception cref="IdmlException">The stream does not hold an IDML package.</exception>
    public static async Task<IdmlDocument> OpenAsync(Stream stream, CancellationToken cancellationToken = default) =>
        new(await IdmlPackage.OpenAsync(stream, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes the document to a file.</summary>
    public void Save(string path, IdmlSaveOptions? options = null) => Package.Save(path, options);

    /// <summary>Writes the document to a stream, which is left open.</summary>
    public void Save(Stream stream, IdmlSaveOptions? options = null) => Package.Save(stream, options);

    /// <summary>Writes the document to a stream without blocking on it. The stream is left open.</summary>
    public Task SaveAsync(Stream stream, IdmlSaveOptions? options = null, CancellationToken cancellationToken = default) =>
        Package.SaveAsync(stream, options, cancellationToken);

    /// <summary>The document as the bytes of an .idml file.</summary>
    public byte[] ToArray(IdmlSaveOptions? options = null) => Package.ToArray(options);

    /// <summary>Paths of the spread parts, in document order.</summary>
    public IReadOnlyList<string> SpreadPaths => [.. ManifestPaths("Spread")];

    /// <summary>Paths of the master spread parts, in document order.</summary>
    public IReadOnlyList<string> MasterSpreadPaths => [.. ManifestPaths("MasterSpread")];

    /// <summary>Paths of the story parts, in manifest order.</summary>
    public IReadOnlyList<string> StoryPaths => [.. ManifestPaths("Story")];

    /// <summary>Nothing to release today (everything is in memory); kept so callers can use <c>using</c> like OpenXML.</summary>
    public void Dispose()
    {
    }

    /// <summary>Appends a page at the end of the document, honouring facing pages.</summary>
    public Page AddPage()
    {
        var size = PageSize;
        var spreads = Spreads;
        var last = spreads.Count > 0 ? spreads[^1] : null;
        var masters = MasterSpreads;
        var master = masters.Count > 0 ? masters[0] : null;

        if (FacingPages && last is not null && last.Pages.Count == 1 && last.Pages[0].IsLeftPage)
        {
            var right = last.AppendPage(0, size, master);
            UpdatePageCounts(right);
            return right;
        }

        var x = FacingPages ? (last is null ? 0 : -size.Width) : -size.Width / 2;
        var spreadY = last is null ? 0 : last.Transform.Ty + size.Height + SpreadVerticalGap;
        var spread = CreateSpread(spreadY);
        var page = spread.AppendPage(x, size, master);
        UpdatePageCounts(page);
        return page;
    }

    /// <summary>Finds a page item by its name (Layers panel) or script label.</summary>
    public PageItem? FindPageItem(string name) =>
        AllPageItems.FirstOrDefault(item => item.Name == name) ?? AllPageItems.FirstOrDefault(item => item.Label == name);

    public IEnumerable<PageItem> FindPageItems(string name) =>
        AllPageItems.Where(item => item.Name == name || item.Label == name);

    public Story? FindStory(string id)
    {
        // TextFrame.Story comes here for every frame, so stories are indexed by id (first in manifest order wins,
        // like a scan of Stories). A hit is checked against the package and the manifest; a miss rescans.
        if (id is null)
        {
            return null;
        }
        if (storyIndex is not null && storyIndex.TryGetValue(id, out var indexed) && IsStillIndexed(indexed, id))
        {
            return indexed;
        }
        var index = new Dictionary<string, Story>(StringComparer.Ordinal);
        foreach (var story in Stories)
        {
            index.TryAdd(story.Id, story);
        }
        storyIndex = index;
        storyIndexManifestVersion = manifestVersion;
        return index.GetValueOrDefault(id);
    }

    /// <summary>The story is still the one its part holds, with the same id, and still listed in designmap.xml.</summary>
    private bool IsStillIndexed(Story story, string id) =>
        story.Id == id
        && Package.Contains(story.PartPath)
        && Package.GetXml(story.PartPath).Root?.Element("Story") == story.Element
        && (storyIndexManifestVersion == manifestVersion || ManifestPaths("Story").Contains(story.PartPath, StringComparer.Ordinal));

    /// <summary>Replaces text in every story. Matches within a single text run; returns the number of replacements.</summary>
    public int ReplaceText(string find, string replacement, StringComparison comparison = StringComparison.Ordinal) =>
        Stories.Sum(story => story.ReplaceText(find, replacement, comparison));

    // ---- Ids -------------------------------------------------------------------------------------

    /// <summary>A fresh <c>Self</c> id ("u" + hex) not used anywhere in the package.</summary>
    public string NewId()
    {
        if (nextId < 0)
        {
            nextId = ScanMaxId() + 1;
        }
        return "u" + (nextId++).ToString("x", CultureInfo.InvariantCulture);
    }

    private long ScanMaxId()
    {
        long max = 0x100;
        foreach (var path in Package.PartPaths)
        {
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var text = Encoding.UTF8.GetString(Package.GetBytes(path).Span);
            foreach (Match match in SelfIdPattern().Matches(text))
            {
                if (long.TryParse(match.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) && value > max)
                {
                    max = value;
                }
            }
        }
        return max;
    }

    [GeneratedRegex("Self=\"u([0-9a-f]{1,15})")]
    private static partial Regex SelfIdPattern();

    // ---- designmap manifest ----------------------------------------------------------------------

    internal IEnumerable<string> ManifestPaths(string kind) =>
        DesignMap.Elements(IdmlNames.PackagingNamespace + kind)
            .Select(element => element.Attribute("src")?.Value)
            .Where(path => path is not null && Package.Contains(path))
            .Select(path => path!);

    internal XElement RootChild(string path, string localName) =>
        Package.GetXml(path).Root?.Element(localName)
            ?? throw new IdmlException(IdmlErrorKind.InvalidFormat, $"Part '{path}' has no <{localName}> element.");

    /// <summary>Adds an <c>idPkg:*</c> reference in the position InDesign itself uses.</summary>
    internal void AddManifestReference(string kind, string path)
    {
        var reference = new XElement(IdmlNames.PackagingNamespace + kind, new XAttribute("src", path));
        var anchor = LastOf(kind) ?? kind switch
        {
            "MasterSpread" => LastOf("Layer", local: true) ?? LastOf("Tags"),
            "Spread" => LastOf("MasterSpread") ?? LastOf("Layer", local: true),
            "Story" => LastOf("BackingStory") ?? LastOf("Spread"),
            _ => null,
        };
        if (anchor is null)
        {
            DesignMap.Add(reference);
        }
        else
        {
            anchor.AddAfterSelf(reference);
        }
    }

    internal void RemoveManifestReference(string path)
    {
        DesignMap.Elements().Where(element => element.Name.Namespace == IdmlNames.PackagingNamespace && element.Attribute("src")?.Value == path).Remove();
    }

    private XElement? LastOf(string kind, bool local = false) =>
        DesignMap.Elements(local ? kind : IdmlNames.PackagingNamespace + kind).LastOrDefault();

    internal string ActiveLayerId =>
        DesignMap.Attribute("ActiveLayer")?.Value
            ?? DesignMap.Elements("Layer").FirstOrDefault()?.Attribute("Self")?.Value
            ?? Layers.Add("Layer 1").Id;

    // ---- Stories ---------------------------------------------------------------------------------

    internal Story CreateStory()
    {
        var id = NewId();
        var path = $"Stories/Story_{id}.xml";
        var story = new XElement("Story",
            new XAttribute("Self", id),
            new XAttribute("AppliedTOCStyle", IdmlNames.NoReference),
            new XAttribute("TrackChanges", "false"),
            new XAttribute("StoryTitle", IdmlNames.EmptyString),
            new XAttribute("AppliedNamedGrid", IdmlNames.NoReference),
            new XElement("StoryPreference",
                new XAttribute("OpticalMarginAlignment", "false"),
                new XAttribute("OpticalMarginSize", "12"),
                new XAttribute("FrameType", "TextFrameType"),
                new XAttribute("StoryOrientation", "Horizontal"),
                new XAttribute("StoryDirection", "LeftToRightDirection")),
            new XElement("InCopyExportOption",
                new XAttribute("IncludeGraphicProxies", "true"),
                new XAttribute("IncludeAllResources", "false")));
        var root = IdmlXml.PackageRoot("Story", DomVersion);
        root.Add(story);
        Package.SetXml(path, new XDocument(root));
        AddManifestReference("Story", path);
        var storyList = DesignMap.Attribute("StoryList")?.Value;
        DesignMap.SetAttributeValue("StoryList", string.IsNullOrEmpty(storyList) ? id : storyList + " " + id);
        return new Story(this, path, story);
    }

    internal void DeleteStory(Story story)
    {
        Package.Remove(story.PartPath);
        RemoveManifestReference(story.PartPath);
        var ids = (DesignMap.Attribute("StoryList")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => id != story.Id);
        DesignMap.SetAttributeValue("StoryList", string.Join(' ', ids));
    }

    // ---- Spreads and pages -----------------------------------------------------------------------

    private Spread CreateSpread(double y)
    {
        var id = NewId();
        var path = $"Spreads/Spread_{id}.xml";
        var spread = new XElement("Spread",
            new XAttribute("Self", id),
            new XAttribute("FlattenerOverride", "Default"),
            new XAttribute("AllowPageShuffle", "true"),
            new XAttribute("ItemTransform", Matrix.Translation(0, y).ToString()),
            new XAttribute("ShowMasterItems", "true"),
            new XAttribute("PageCount", "0"),
            new XAttribute("BindingLocation", "0"),
            new XAttribute("PageTransitionType", "None"),
            new XAttribute("PageTransitionDirection", "NotApplicable"),
            new XAttribute("PageTransitionDuration", "Medium"));
        var root = IdmlXml.PackageRoot("Spread", DomVersion);
        root.Add(spread);
        Package.SetXml(path, new XDocument(root));
        AddManifestReference("Spread", path);
        return new Spread(this, path, spread);
    }

    private void AddMasterSpread(string prefix, string baseName)
    {
        var size = PageSize;
        var id = NewId();
        var path = $"MasterSpreads/MasterSpread_{id}.xml";
        var master = new XElement("MasterSpread",
            new XAttribute("Self", id),
            new XAttribute("ItemTransform", Matrix.Identity.ToString()),
            new XAttribute("OverriddenPageItemProps", string.Empty),
            new XAttribute("Name", $"{prefix}-{baseName}"),
            new XAttribute("NamePrefix", prefix),
            new XAttribute("BaseName", baseName),
            new XAttribute("ShowMasterItems", "true"),
            new XAttribute("PageCount", "0"),
            new XElement("Properties", new XElement("PageColor", new XAttribute("type", "enumeration"), "UseMasterColor")));
        var root = IdmlXml.PackageRoot("MasterSpread", DomVersion);
        root.Add(master);
        Package.SetXml(path, new XDocument(root));
        AddManifestReference("MasterSpread", path);

        var wrapper = new MasterSpread(this, path, master);
        if (FacingPages)
        {
            wrapper.AppendPage(-size.Width, size, master: null, name: prefix);
            wrapper.AppendPage(0, size, master: null, name: prefix);
        }
        else
        {
            wrapper.AppendPage(-size.Width / 2, size, master: null, name: prefix);
        }
    }

    private void UpdatePageCounts(Page newPage)
    {
        var pages = Pages;
        DocumentPreference?.SetAttributeValue("PagesPerDocument", pages.Count.ToString(CultureInfo.InvariantCulture));

        var section = DesignMap.Elements("Section").LastOrDefault();
        if (section is null)
        {
            section = new XElement("Section",
                new XAttribute("Self", NewId()),
                new XAttribute("Length", "0"),
                new XAttribute("Name", string.Empty),
                new XAttribute("ContinueNumbering", "false"),
                new XAttribute("IncludeSectionPrefix", "false"),
                new XAttribute("Marker", string.Empty),
                new XAttribute("PageStart", newPage.Id),
                new XAttribute("SectionPrefix", string.Empty),
                new XAttribute("PageNumberStart", "1"),
                new XElement("Properties", new XElement("PageNumberStyle", new XAttribute("type", "enumeration"), "Arabic")));
            var anchor = LastOf("Spread");
            if (anchor is null)
            {
                DesignMap.Add(section);
            }
            else
            {
                anchor.AddAfterSelf(section);
            }
        }
        var length = int.TryParse(section.Attribute("Length")?.Value, CultureInfo.InvariantCulture, out var value) ? value : 0;
        section.SetAttributeValue("Length", (length + 1).ToString(CultureInfo.InvariantCulture));
    }

    private void ApplyDocumentSetup(DocumentOptions options)
    {
        var preference = DocumentPreference ?? throw new IdmlException("Blank skeleton has no DocumentPreference.");
        preference.SetAttributeValue("PageWidth", IdmlXml.Format(options.PageSize.Width));
        preference.SetAttributeValue("PageHeight", IdmlXml.Format(options.PageSize.Height));
        preference.SetAttributeValue("FacingPages", options.FacingPages ? "true" : "false");
        preference.SetAttributeValue("PagesPerDocument", "0");
        var bleed = IdmlXml.Format(options.Bleed);
        preference.SetAttributeValue("DocumentBleedTopOffset", bleed);
        preference.SetAttributeValue("DocumentBleedBottomOffset", bleed);
        preference.SetAttributeValue("DocumentBleedInsideOrLeftOffset", bleed);
        preference.SetAttributeValue("DocumentBleedOutsideOrRightOffset", bleed);

        var margins = preference.Parent!.Element("MarginPreference");
        if (margins is not null)
        {
            SetMargins(margins, options.Margins, options.Columns, options.ColumnGutter, options.PageSize.Width);
        }
    }

    /// <summary>The document-wide default margin preference (copied onto new pages).</summary>
    internal XElement? DefaultMarginPreference => Package.TryGetXml(PreferencesPath)?.Root?.Element("MarginPreference");

    internal static void SetMargins(XElement marginPreference, Spacing margins, int columns, double gutter, double pageWidth)
    {
        marginPreference.SetAttributeValue("ColumnCount", columns.ToString(CultureInfo.InvariantCulture));
        marginPreference.SetAttributeValue("ColumnGutter", IdmlXml.Format(gutter));
        marginPreference.SetAttributeValue("Top", IdmlXml.Format(margins.Top));
        marginPreference.SetAttributeValue("Bottom", IdmlXml.Format(margins.Bottom));
        marginPreference.SetAttributeValue("Left", IdmlXml.Format(margins.Left));
        marginPreference.SetAttributeValue("Right", IdmlXml.Format(margins.Right));
        marginPreference.SetAttributeValue("ColumnDirection", "Horizontal");
        var contentWidth = pageWidth - margins.Left - margins.Right;
        var columnWidth = (contentWidth - (gutter * (columns - 1))) / columns;
        var positions = new List<double>();
        for (var i = 0; i < columns; i++)
        {
            var start = i * (columnWidth + gutter);
            positions.Add(start);
            positions.Add(start + columnWidth);
        }
        marginPreference.SetAttributeValue("ColumnsPositions", IdmlXml.FormatList([.. positions]));
    }

    // ---- Blank skeleton --------------------------------------------------------------------------

    private static readonly (string Path, string Resource)[] BlankParts =
    [
        (IdmlNames.ContainerPath, "container.xml"),
        (IdmlNames.DesignMapPath, "designmap.xml"),
        (GraphicPath, "Graphic.xml"),
        ("Resources/Fonts.xml", "Fonts.xml"),
        (StylesPath, "Styles.xml"),
        (PreferencesPath, "Preferences.xml"),
        ("XML/BackingStory.xml", "BackingStory.xml"),
        ("XML/Tags.xml", "Tags.xml"),
    ];

    private static byte[] ReadBlankResource(string name)
    {
        using var stream = typeof(IdmlDocument).Assembly.GetManifestResourceStream("CoreIdml.Blank." + name)
            ?? throw new IdmlException($"Embedded resource '{name}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Version of this library.</summary>
    public static string LibraryVersion { get; } =
        typeof(IdmlDocument).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
