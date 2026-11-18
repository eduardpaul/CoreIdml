namespace CoreIdml.Pdf;

/// <summary>Where fonts come from and what replaces a font that is not installed.</summary>
public sealed class PdfFontOptions
{
    /// <summary>Directories searched (recursively) for .ttf, .otf, .ttc and .otc files, before the system fonts.</summary>
    public IList<string> FontDirectories { get; } = [];

    /// <summary>
    /// Font sources searched first, in order: user fonts in app storage, fonts loaded from streams or bytes
    /// (<see cref="MemoryFontSource"/>), an optional free-font set. See <see cref="AddFont(string, byte[])"/>.
    /// </summary>
    public IList<IFontSource> Sources { get; } = [];

    /// <summary>Adds one font file held in memory.</summary>
    public PdfFontOptions AddFont(string name, byte[] data)
    {
        var memory = Sources.OfType<MemoryFontSource>().FirstOrDefault();
        if (memory is null)
        {
            memory = new MemoryFontSource();
            Sources.Add(memory);
        }
        memory.Add(name, data);
        return this;
    }

    /// <summary>Adds a folder of font files (also usable at runtime for app storage on Android and iOS).</summary>
    public PdfFontOptions AddDirectory(string directory)
    {
        Sources.Add(new DirectoryFontSource(directory));
        return this;
    }

    /// <summary>Also search the platform's font folders (Linux, Android, macOS, iOS, Windows). Turn off for reproducible output.</summary>
    public bool UseSystemFonts { get; set; } = true;

    /// <summary>
    /// Family replacements tried when a family is not found, e.g. <c>["Minion Pro"] = "Liberation Serif"</c>.
    /// Keys are compared ignoring case and spaces.
    /// </summary>
    public IDictionary<string, string> Substitutions { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Families tried, in order, for a missing family (after <see cref="Substitutions"/>) and for characters the
    /// chosen font lacks. When empty, a built-in list of common serif/sans/mono families is used, chosen by the
    /// requested family's name. The last resort is the PDF standard fonts (Times, Helvetica, Courier; not embedded).
    /// </summary>
    public IList<string> FallbackFamilies { get; } = [];
}

/// <summary>Options for <see cref="IdmlPdf.Export(IdmlDocument, Stream, PdfExportOptions?)"/>.</summary>
public sealed class PdfExportOptions
{
    /// <summary>1-based page numbers to export (document order); null exports every page.</summary>
    public IReadOnlyList<int>? Pages { get; set; }

    public PdfFontOptions Fonts { get; set; } = new();

    /// <summary>
    /// Folders searched for linked images whose link path does not exist (by file name), e.g. the IDML's
    /// <c>Links</c> folder. The folder of <see cref="DocumentPath"/> and its <c>Links</c> subfolder are always searched.
    /// </summary>
    public IList<string> LinkDirectories { get; } = [];

    /// <summary>
    /// Explicit link replacements: key = a link URI or just its file name ("photo.jpg", compared ignoring case after
    /// URL-decoding), value = local file to use. Checked before the link's own path and <see cref="LinkDirectories"/>.
    /// </summary>
    public IDictionary<string, string> LinkRemap { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path of the IDML file, used to resolve relative links. Optional.</summary>
    public string? DocumentPath { get; set; }

    /// <summary>Draw a grey crossed box where a linked image cannot be found (InDesign would use its stored preview).</summary>
    public bool MarkMissingImages { get; set; }

    /// <summary>Paint master page items.</summary>
    public bool IncludeMasterItems { get; set; } = true;

    /// <summary>Compress page content streams (turn off to inspect the PDF by hand).</summary>
    public bool CompressContent { get; set; } = true;

    /// <summary>Document title written to the PDF metadata.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Called after each page with (pages done, pages in total); return false to cancel. A cancelled export throws
    /// <see cref="OperationCanceledException"/> and the output is incomplete.
    /// </summary>
    public Func<int, int, bool>? Progress { get; set; }

    /// <summary>Cancels the export between pages (<see cref="OperationCanceledException"/>).</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Opt-in size cap for placed images: an image whose resolution at its largest placement is more than 10% above
    /// this many pixels per inch is downsampled to it (JPEGs re-encoded at <see cref="JpegQuality"/>, PNGs kept
    /// lossless). Null (the default) embeds every image as it is. CMYK JPEGs are always kept as they are.
    /// </summary>
    public double? MaxImageDpi { get; set; }

    /// <summary>JPEG quality (1-100) for images re-encoded by <see cref="MaxImageDpi"/>. Default 85.</summary>
    public int JpegQuality { get; set; } = 85;
}

/// <summary>Options for <see cref="IdmlPdf.Import(Stream, PdfImportOptions?)"/>.</summary>
public sealed class PdfImportOptions
{
    /// <summary>1-based page numbers to import; null imports every page.</summary>
    public IReadOnlyList<int>? Pages { get; set; }

    /// <summary>
    /// Folder where extracted images are written (IDML links images, it cannot embed them). When null, images are
    /// skipped and reported in <see cref="PdfImportResult.Warnings"/>.
    /// </summary>
    public string? ImageDirectory { get; set; }

    /// <summary>Keep the PDF's line breaks as forced line breaks instead of joining lines into flowing paragraphs.</summary>
    public bool PreserveLineBreaks { get; set; }

    /// <summary>Import filled and stroked rectangles and straight lines as shapes.</summary>
    public bool ImportShapes { get; set; } = true;

    /// <summary>Lines whose gap is larger than this many times the line's leading start a new text frame.</summary>
    public double BlockGapFactor { get; set; } = 1.6;

    /// <summary>Optional: font sources to check the rebuilt document's fonts against (see <see cref="ImportedFont.Available"/>).</summary>
    public PdfFontOptions? Fonts { get; set; }

    /// <summary>Extra space added around each text frame (points), so the same text fits again in InDesign.</summary>
    public double FramePadding { get; set; } = 2;

    /// <summary>Called after each page with (pages done, pages in total); return false to cancel (<see cref="OperationCanceledException"/>).</summary>
    public Func<int, int, bool>? Progress { get; set; }

    /// <summary>Cancels the import between pages (<see cref="OperationCanceledException"/>).</summary>
    public CancellationToken CancellationToken { get; set; }
}
