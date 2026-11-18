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
[SupportedOSPlatform("browser")]
public static partial class WasmExports
{
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

    /// <summary>Throws for anything but success; the wrapper turns the throw into its own error type.</summary>
    private static void Check(IdmlStatus status)
    {
        if (status != IdmlStatus.Ok)
        {
            throw Failure(status);
        }
    }

    private static InvalidOperationException Failure(IdmlStatus status) => new(IdmlInterop.LastErrorMessage ?? status.ToString());
}
