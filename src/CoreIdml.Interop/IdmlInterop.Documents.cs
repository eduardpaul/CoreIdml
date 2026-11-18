using CoreIdml.Json;
using CoreIdml.Pdf;
using CoreIdml.Pdf.Json;

namespace CoreIdml.Interop;

public static partial class IdmlInterop
{
    // ---- Creating documents -----------------------------------------------------------------------------------

    /// <summary>Creates a document from a JSON document spec; <c>null</c> or empty creates one blank A4 page.</summary>
    public static IdmlStatus DocumentCreate(string? specJson, out nint document)
    {
        nint handle = 0;
        var status = Run(() => handle = Register(IdmlJson.CreateDocument(string.IsNullOrWhiteSpace(specJson) ? null : specJson)));
        document = handle;
        return status;
    }

    /// <summary>Adds the colours, styles and pages of a JSON document spec; its pages are appended.</summary>
    public static IdmlStatus DocumentApply(nint document, string specJson) =>
        WithDocument(document, idml => IdmlJson.Apply(idml, specJson));

    // ---- Reading ----------------------------------------------------------------------------------------------

    /// <summary>The JSON summary for programs and agents; <paramref name="optionsJson"/> may be <c>null</c>.</summary>
    public static IdmlStatus DocumentSummary(nint document, string? optionsJson, out string json) =>
        WithDocument(document, idml => IdmlJson.SummarizeToJson(idml, NullIfEmpty(optionsJson)), out json);

    /// <summary>One story in full, with its indexed content runs, as JSON.</summary>
    public static IdmlStatus DocumentStory(nint document, string storyId, out string json) =>
        WithDocument(document, idml => IdmlJson.GetStoryJson(idml, storyId), out json);

    /// <summary>The XML structure as a JSON tree, or <c>null</c> (as JSON) when the document has none.</summary>
    public static IdmlStatus DocumentXmlStructure(nint document, out string json) =>
        WithDocument(document, idml => IdmlJson.XmlStructureJson(idml), out json);

    /// <summary>The XML structure, or the subtree at <paramref name="at"/>, as XML.</summary>
    public static IdmlStatus DocumentExportXml(nint document, string? at, out string xml) =>
        WithDocument(document, idml => IdmlJson.ExportXml(idml, NullIfEmpty(at)), out xml);

    /// <summary>The validation report as JSON.</summary>
    public static IdmlStatus DocumentValidate(nint document, out string json) =>
        WithDocument(document, IdmlJson.ValidateToJson, out json);

    /// <summary>What changed between two documents, as JSON.</summary>
    public static IdmlStatus DocumentDiff(nint before, nint after, out string json)
    {
        var result = string.Empty;
        var status = WithDocuments(before, after, (a, b) => result = IdmlJson.DiffJson(a, b));
        json = result;
        return status;
    }

    // ---- Editing ----------------------------------------------------------------------------------------------

    /// <summary>Fills a template from JSON fill data; the report says what was filled and what was missing.</summary>
    public static IdmlStatus DocumentFill(nint document, string dataJson, out string reportJson) =>
        WithDocument(document, idml => IdmlJson.Fill(idml, dataJson), out reportJson);

    /// <summary>Replaces text in every story.</summary>
    public static IdmlStatus DocumentReplaceText(nint document, string find, string replacement, out int count)
    {
        var replaced = 0;
        var status = WithDocument(document, idml =>
        {
            ArgumentException.ThrowIfNullOrEmpty(find);
            replaced = idml.ReplaceText(find, replacement);
        });
        count = replaced;
        return status;
    }

    /// <summary>Sets the text of one content run of a story, keeping its formatting.</summary>
    public static IdmlStatus DocumentSetRunText(nint document, string storyId, int runIndex, string text, bool force) =>
        WithDocument(document, idml => idml.SetRunText(storyId, runIndex, text, force));

    /// <summary>Points a link at another file; with <paramref name="refit"/>, the image is fitted to its frame again.</summary>
    public static IdmlStatus DocumentRelink(nint document, string linkId, string uriOrPath, bool refit) =>
        WithDocument(document, idml =>
        {
            ArgumentException.ThrowIfNullOrEmpty(linkId);
            ArgumentException.ThrowIfNullOrEmpty(uriOrPath);
            idml.RelinkImage(linkId, uriOrPath, refit ? Dom.ImageFit.Fill : null);
        });

    /// <summary>Imports XML onto the structure with SimpleIDML's semantics; returns the JSON report.</summary>
    public static IdmlStatus DocumentImportXml(nint document, string xml, string? at, string? optionsJson, out string reportJson) =>
        WithDocument(document, idml =>
        {
            ArgumentException.ThrowIfNullOrEmpty(xml);
            return IdmlJson.ImportXml(idml, xml, NullIfEmpty(at), NullIfEmpty(optionsJson));
        }, out reportJson);

    /// <summary>Appends pages of <paramref name="source"/> (all of them when <paramref name="pages"/> is empty).</summary>
    public static IdmlStatus DocumentImportPages(nint document, nint source, ReadOnlySpan<int> pages, out int added)
    {
        var indices = pages.ToArray();
        var count = 0;
        var status = WithDocuments(document, source, (target, from) => count = target.ImportPages(from, indices).Count);
        added = count;
        return status;
    }

    // ---- PDF --------------------------------------------------------------------------------------------------

    /// <summary>Renders the document to a PDF file; returns the JSON export report.</summary>
    public static IdmlStatus DocumentExportPdf(nint document, string path, string? optionsJson, Func<int, int, bool>? progress, out string reportJson) =>
        WithDocument(document, idml =>
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            return PdfJson.ToJson(IdmlPdf.Export(idml, path, ExportOptions(optionsJson, progress)));
        }, out reportJson);

    /// <summary>Renders the document to a PDF in memory; returns the PDF and the JSON export report.</summary>
    public static IdmlStatus DocumentExportPdfBytes(nint document, string? optionsJson, Func<int, int, bool>? progress, out byte[] pdf, out string reportJson)
    {
        byte[] bytes = [];
        var status = WithDocument(document, idml =>
        {
            using var output = new MemoryStream();
            var report = PdfJson.ToJson(IdmlPdf.Export(idml, output, ExportOptions(optionsJson, progress)));
            bytes = output.ToArray();
            return report;
        }, out reportJson);
        pdf = bytes;
        return status;
    }

    /// <summary>Builds an editable document from a PDF read from <paramref name="pdf"/>; returns its handle and the JSON report.</summary>
    public static IdmlStatus PdfImport(Stream pdf, string? optionsJson, Func<int, int, bool>? progress, out nint document, out string reportJson)
    {
        nint handle = 0;
        var report = string.Empty;
        var status = Run(() =>
        {
            var options = PdfJson.ParseImportOptions(NullIfEmpty(optionsJson));
            options.Progress = progress;
            var result = IdmlPdf.Import(pdf, options);
            report = PdfJson.ToJson(result);
            handle = Register(result.Document);
        });
        document = handle;
        reportJson = report;
        return status;
    }

    /// <summary>Builds an editable document from a PDF file.</summary>
    public static IdmlStatus PdfImportFile(string path, string? optionsJson, Func<int, int, bool>? progress, out nint document, out string reportJson)
    {
        nint handle = 0;
        var report = string.Empty;
        var status = Run(() =>
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            using var stream = File.OpenRead(path);
            if (PdfImport(stream, optionsJson, progress, out handle, out report) != IdmlStatus.Ok)
            {
                // Already recorded by the inner call: keep its status and message.
                throw new RecordedFailure();
            }
        });
        document = handle;
        reportJson = report;
        return status;
    }

    private static PdfExportOptions ExportOptions(string? optionsJson, Func<int, int, bool>? progress)
    {
        var options = PdfJson.ParseExportOptions(NullIfEmpty(optionsJson));
        options.Progress = progress;
        return options;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
