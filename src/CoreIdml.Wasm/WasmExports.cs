using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using CoreIdml.Interop;

namespace CoreIdml.Wasm;

/// <summary>
/// The functions JavaScript calls (<c>bindings/wasm</c> wraps them). They mirror the C API one to one, with
/// JavaScript's conventions: a result is returned, and a failure is thrown. The wrapper then reads
/// <see cref="LastErrorCode"/> so the error it raises carries the same status code as in every other binding.
/// Byte arrays are copied across the boundary in both directions.
/// </summary>
/// <remarks>
/// Calls that have two results (a PDF and its report, a document and its import report) return the first and keep
/// the second for <see cref="TakeReport"/>, which the wrapper calls right after. JavaScript on this runtime is single
/// threaded, so nothing can run in between.
/// </remarks>
[SupportedOSPlatform("browser")]
public static partial class WasmExports
{
    [ThreadStatic]
    private static string? report;

    /// <summary>ABI version of this build.</summary>
    [JSExport]
    public static int AbiVersion() => IdmlInterop.AbiVersion;

    /// <summary>Version of the library.</summary>
    [JSExport]
    public static string Version() => IdmlInterop.LibraryVersion;

    /// <summary>Status code of the last call: 0 unless it failed.</summary>
    [JSExport]
    public static int LastErrorCode() => (int)IdmlInterop.LastErrorStatus;

    /// <summary>Why the last call failed, or <c>null</c> if it did not.</summary>
    [JSExport]
    public static string? LastErrorMessage() => IdmlInterop.LastErrorMessage;

    /// <summary>The JSON report of the last PDF export or PDF import, handed over once.</summary>
    [JSExport]
    public static string? TakeReport()
    {
        var result = report;
        report = null;
        return result;
    }

    // ---- Documents ----------------------------------------------------------------------------------------------

    /// <summary>Opens an .idml file held in memory and returns its handle.</summary>
    [JSExport]
    public static int DocumentOpen(byte[] data)
    {
        if (data is null)
        {
            throw Failure(IdmlInterop.Fail(IdmlStatus.InvalidArgument, "data must hold an .idml file."));
        }
        using var stream = new ReadOnlyMemoryStream(data);
        Check(IdmlInterop.DocumentOpen(stream, out var document));
        return (int)document;
    }

    /// <summary>Describes the document as JSON.</summary>
    [JSExport]
    public static string DocumentInfo(int document)
    {
        Check(IdmlInterop.DocumentInfo(document, out var json));
        return Encoding.UTF8.GetString(json);
    }

    /// <summary>Reads one part of the package.</summary>
    [JSExport]
    public static byte[] DocumentReadPart(int document, string path)
    {
        Check(IdmlInterop.DocumentReadPart(document, path, out var data));
        return data.ToArray();
    }

    /// <summary>Adds or replaces one part of the package.</summary>
    [JSExport]
    public static void DocumentWritePart(int document, string path, byte[] data) =>
        Check(IdmlInterop.DocumentWritePart(document, path, data));

    /// <summary>Saves the document as the bytes of an .idml file.</summary>
    [JSExport]
    public static byte[] DocumentSave(int document)
    {
        Check(IdmlInterop.DocumentSave(document, out var data));
        return data;
    }

    /// <summary>Releases a document.</summary>
    [JSExport]
    public static void DocumentClose(int document) => IdmlInterop.DocumentClose(document);

    // ---- Creating documents -------------------------------------------------------------------------------------

    /// <summary>Creates a document from a JSON document spec; <c>null</c> creates one blank A4 page.</summary>
    [JSExport]
    public static int DocumentCreate(string? specJson)
    {
        Check(IdmlInterop.DocumentCreate(specJson, out var document));
        return (int)document;
    }

    /// <summary>Adds the colours, styles and pages of a JSON document spec.</summary>
    [JSExport]
    public static void DocumentApply(int document, string specJson) => Check(IdmlInterop.DocumentApply(document, specJson));

    // ---- Reading ------------------------------------------------------------------------------------------------

    /// <summary>The JSON summary; <paramref name="optionsJson"/> may be <c>null</c>.</summary>
    [JSExport]
    public static string DocumentSummary(int document, string? optionsJson) =>
        Text(IdmlInterop.DocumentSummary(document, optionsJson, out var json), json);

    /// <summary>One story in full as JSON.</summary>
    [JSExport]
    public static string DocumentStory(int document, string storyId) =>
        Text(IdmlInterop.DocumentStory(document, storyId, out var json), json);

    /// <summary>The XML structure as a JSON tree, or <c>"null"</c>.</summary>
    [JSExport]
    public static string DocumentXmlStructure(int document) =>
        Text(IdmlInterop.DocumentXmlStructure(document, out var json), json);

    /// <summary>The XML structure, or the subtree at <paramref name="at"/>, as XML.</summary>
    [JSExport]
    public static string DocumentExportXml(int document, string? at) =>
        Text(IdmlInterop.DocumentExportXml(document, at, out var xml), xml);

    /// <summary>The validation report as JSON.</summary>
    [JSExport]
    public static string DocumentValidate(int document) =>
        Text(IdmlInterop.DocumentValidate(document, out var json), json);

    /// <summary>What changed between two documents, as JSON.</summary>
    [JSExport]
    public static string DocumentDiff(int before, int after) =>
        Text(IdmlInterop.DocumentDiff(before, after, out var json), json);

    // ---- Editing ------------------------------------------------------------------------------------------------

    /// <summary>Fills a template from JSON fill data; returns the JSON report.</summary>
    [JSExport]
    public static string DocumentFill(int document, string dataJson) =>
        Text(IdmlInterop.DocumentFill(document, dataJson, out var json), json);

    /// <summary>Replaces text in every story; returns the number of replacements.</summary>
    [JSExport]
    public static int DocumentReplaceText(int document, string find, string replacement)
    {
        Check(IdmlInterop.DocumentReplaceText(document, find, replacement, out var count));
        return count;
    }

    /// <summary>Sets the text of one content run of a story.</summary>
    [JSExport]
    public static void DocumentSetRunText(int document, string storyId, int runIndex, string text, bool force) =>
        Check(IdmlInterop.DocumentSetRunText(document, storyId, runIndex, text, force));

    /// <summary>Points a link at another file.</summary>
    [JSExport]
    public static void DocumentRelink(int document, string linkId, string uriOrPath, bool refit) =>
        Check(IdmlInterop.DocumentRelink(document, linkId, uriOrPath, refit));

    /// <summary>Imports XML onto the structure; returns the JSON report.</summary>
    [JSExport]
    public static string DocumentImportXml(int document, string xml, string? at, string? optionsJson) =>
        Text(IdmlInterop.DocumentImportXml(document, xml, at, optionsJson, out var json), json);

    /// <summary>Appends pages of <paramref name="source"/> (every page when <paramref name="pages"/> is empty).</summary>
    [JSExport]
    public static int DocumentImportPages(int document, int source, [JSMarshalAs<JSType.Array<JSType.Number>>] int[] pages)
    {
        Check(IdmlInterop.DocumentImportPages(document, source, pages ?? [], out var added));
        return added;
    }

    // ---- PDF ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Renders the document to a PDF in memory and returns it; the JSON report follows with <see cref="TakeReport"/>.
    /// <paramref name="progress"/> is called after each page and returns <c>false</c> to cancel.
    /// </summary>
    [JSExport]
    public static byte[] DocumentExportPdfBytes(
        int document,
        string? optionsJson,
        [JSMarshalAs<JSType.Function<JSType.Number, JSType.Number, JSType.Boolean>>] Func<int, int, bool>? progress)
    {
        report = null;
        Check(IdmlInterop.DocumentExportPdfBytes(document, optionsJson, progress, out var pdf, out var json));
        report = json;
        return pdf;
    }

    /// <summary>
    /// Builds an editable document from the bytes of a PDF and returns its handle; the JSON report follows with
    /// <see cref="TakeReport"/>. Images are written to the <c>imageDirectory</c> of the options.
    /// </summary>
    [JSExport]
    public static int PdfImport(
        byte[] data,
        string? optionsJson,
        [JSMarshalAs<JSType.Function<JSType.Number, JSType.Number, JSType.Boolean>>] Func<int, int, bool>? progress)
    {
        report = null;
        if (data is null || data.Length == 0)
        {
            throw Failure(IdmlInterop.Fail(IdmlStatus.InvalidArgument, "data must hold a PDF file."));
        }
        using var stream = new ReadOnlyMemoryStream(data);
        Check(IdmlInterop.PdfImport(stream, optionsJson, progress, out var document, out var json));
        report = json;
        return (int)document;
    }

    // ---- The runtime's in-memory file system ---------------------------------------------------------------------
    // Fonts, placed images and the images of a PDF import are files there: the options of the library name folders,
    // and in a browser the only folders are these.

    /// <summary>Writes a file, creating its folders.</summary>
    [JSExport]
    public static void FileWrite(string path, byte[] data) => FileSystem(() =>
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(RequirePath(path)));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(path, data ?? []);
    });

    /// <summary>Reads a file.</summary>
    [JSExport]
    public static byte[] FileRead(string path)
    {
        byte[] data = [];
        FileSystem(() => data = File.ReadAllBytes(RequirePath(path)));
        return data;
    }

    /// <summary>The names in a folder (files and folders), sorted; none when it does not exist.</summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Array<JSType.String>>]
    public static string[] FileList(string directory)
    {
        string[] names = [];
        FileSystem(() =>
        {
            if (Directory.Exists(RequirePath(directory)))
            {
                names = Directory.EnumerateFileSystemEntries(directory).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal).ToArray();
            }
        });
        return names;
    }

    /// <summary>Whether a file or folder exists.</summary>
    [JSExport]
    public static bool FileExists(string path) => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    /// <summary>Deletes a file, or a folder with everything in it. Nothing happens when it does not exist.</summary>
    [JSExport]
    public static void FileDelete(string path) => FileSystem(() =>
    {
        if (Directory.Exists(RequirePath(path)))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    });

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>Throws for anything but success; the wrapper turns the throw into its own error type.</summary>
    private static void Check(IdmlStatus status)
    {
        if (status != IdmlStatus.Ok)
        {
            throw Failure(status);
        }
    }

    private static string Text(IdmlStatus status, string result)
    {
        Check(status);
        return result;
    }

    /// <summary>Runs a file system call with the status mapping of the library (IO errors, bad arguments).</summary>
    private static void FileSystem(Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // Every failure becomes a status, as in the library.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            throw Failure(IdmlInterop.Fail(exception));
        }
    }

    private static string RequirePath(string path) =>
        string.IsNullOrEmpty(path) ? throw new ArgumentException("path must not be empty.", nameof(path)) : path;

    private static InvalidOperationException Failure(IdmlStatus status) => new(IdmlInterop.LastErrorMessage ?? status.ToString());
}
