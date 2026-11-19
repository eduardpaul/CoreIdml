using CoreIdml.Pdf.Fonts;
using CoreIdml.Pdf.Import;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf;

/// <summary>What happened during an export: everything that makes the PDF differ from what InDesign would print.</summary>
public sealed class PdfExportResult
{
    public int PageCount { get; init; }

    /// <summary>Fonts that were not installed and what replaced them.</summary>
    public IReadOnlyList<FontSubstitution> FontSubstitutions { get; init; } = [];

    /// <summary>Link URIs of images that could not be found or read.</summary>
    public IReadOnlyList<string> MissingImages { get; init; } = [];

    /// <summary>Stories whose text did not fit their frames (ids).</summary>
    public IReadOnlyList<string> OversetStories { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A font the rebuilt IDML uses: its InDesign family and style, the PDF font it came from, where it is used.</summary>
public sealed record ImportedFont(string Family, string Style, string PdfFontName)
{
    public List<int> Pages { get; } = [];

    /// <summary>Found in <see cref="PdfImportOptions.Fonts"/>; null when no font sources were given to check against.</summary>
    public bool? Available { get; set; }
}

/// <summary>What happened during an import.</summary>
public sealed class PdfImportResult
{
    public required IdmlDocument Document { get; init; }

    public int TextFrames { get; init; }

    public int ImageFrames { get; init; }

    public int Shapes { get; init; }

    /// <summary>Paths of the image files written to <see cref="PdfImportOptions.ImageDirectory"/>.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>Fonts the rebuilt document asks for (install these, or the text reflows in InDesign).</summary>
    public IReadOnlyList<ImportedFont> Fonts { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// IDML ↔ PDF conversion.
/// <list type="bullet">
/// <item><see cref="Export(IdmlDocument, Stream, PdfExportOptions?)"/> renders pages with this library's own text
/// composer and PDF writer (fonts embedded as subsets). It approximates InDesign; see the README for what differs.</item>
/// <item><see cref="Import(Stream, PdfImportOptions?)"/> rebuilds an editable IDML from a PDF's text, images and shapes.</item>
/// </list>
/// Managed code only, Native AOT compatible.
/// </summary>
public static class IdmlPdf
{
    /// <summary>
    /// The font families an export with these options can use (its font folders and sources, then the system fonts
    /// unless <see cref="PdfFontOptions.UseSystemFonts"/> is off), each with its styles, sorted by family. These are the
    /// names a document's or a style's font family and style must match to be used without substitution.
    /// </summary>
    public static IReadOnlyList<AvailableFont> ListFonts(PdfFontOptions? options = null) => new FontLibrary(options ?? new PdfFontOptions()).Families();

    /// <summary>Renders the document to PDF.</summary>
    public static PdfExportResult Export(IdmlDocument document, Stream output, PdfExportOptions? options = null) =>
        Export(document, output, options, linkFolder: null);

    /// <summary>Renders the document; relative links resolve against <paramref name="linkFolder"/> unless <see cref="PdfExportOptions.DocumentPath"/> is set.</summary>
    private static PdfExportResult Export(IdmlDocument document, Stream output, PdfExportOptions? options, string? linkFolder)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        using var renderer = new IdmlRenderer(document, options ?? new PdfExportOptions(), linkFolder);
        return renderer.Render(output);
    }

    /// <summary>Renders the document to a PDF file; relative image links resolve against <paramref name="pdfPath"/>'s folder unless <see cref="PdfExportOptions.DocumentPath"/> is set.</summary>
    public static PdfExportResult Export(IdmlDocument document, string pdfPath, PdfExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        // Written next to the target and moved into place when complete, so a failed or cancelled export leaves no
        // truncated file behind (and does not destroy an earlier PDF of the same name).
        var full = Path.GetFullPath(pdfPath);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            PdfExportResult result;
            using (var stream = File.Create(temporary))
            {
                result = Export(document, stream, options, Path.GetDirectoryName(full));
            }
            File.Move(temporary, full, overwrite: true);
            return result;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Builds an IDML document from a PDF.</summary>
    public static PdfImportResult Import(Stream pdf, PdfImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var buffer = new MemoryStream();
        pdf.CopyTo(buffer);
        return new PdfImporter(options ?? new PdfImportOptions()).Import(buffer.ToArray());
    }

    /// <summary>
    /// Converts a PDF file into an IDML file. Images are written to a "&lt;name&gt;-links" folder next to the IDML
    /// unless <see cref="PdfImportOptions.ImageDirectory"/> is set.
    /// </summary>
    public static PdfImportResult Import(string pdfPath, string idmlPath, PdfImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(idmlPath);
        options ??= new PdfImportOptions();
        if (options.ImageDirectory is null)
        {
            var fullPath = Path.GetFullPath(idmlPath);
            options.ImageDirectory = Path.Combine(Path.GetDirectoryName(fullPath)!, Path.GetFileNameWithoutExtension(fullPath) + "-links");
        }
        var result = new PdfImporter(options).Import(File.ReadAllBytes(pdfPath));
        result.Document.Save(idmlPath);
        return result;
    }
}
