using System.Text.Json;

namespace CoreIdml.Interop;

/// <summary>
/// Everything a language binding can ask of the library. The rules hold for every method:
/// <list type="bullet">
/// <item>No exception leaves it. A failure is an <see cref="IdmlStatus"/> other than <see cref="IdmlStatus.Ok"/>,
/// with the explanation in <see cref="LastErrorMessage"/> for the calling thread.</item>
/// <item>Documents are named by handles that are never reused; a closed or made-up handle gives
/// <see cref="IdmlStatus.InvalidHandle"/>.</item>
/// <item>A handle may be used from any thread; calls on one document are serialized.</item>
/// <item>Structured results are UTF-8 JSON with camelCase names.</item>
/// </list>
/// The transports (<c>CoreIdml.Native</c>, <c>CoreIdml.Wasm</c>) only convert arguments and results; they hold no
/// behaviour of their own.
/// </summary>
public static partial class IdmlInterop
{
    /// <summary>Bumped on any breaking change to the binding contract (<c>COREIDML_ABI_VERSION</c>).</summary>
    public const int AbiVersion = 1;

    private static readonly HandleTable<Slot> Documents = new();

    [ThreadStatic]
    private static IdmlStatus lastErrorStatus;

    [ThreadStatic]
    private static string? lastErrorMessage;

    /// <summary>Version of the library.</summary>
    public static string LibraryVersion => IdmlDocument.LibraryVersion;

    /// <summary>Status of the last call on this thread: <see cref="IdmlStatus.Ok"/> unless it failed.</summary>
    public static IdmlStatus LastErrorStatus => lastErrorStatus;

    /// <summary>Why the last call on this thread failed, or <c>null</c> if it did not.</summary>
    public static string? LastErrorMessage => lastErrorMessage;

    /// <summary>Number of documents that are open, for leak checks in tests.</summary>
    public static int OpenDocumentCount => Documents.Count;

    /// <summary>Opens the .idml file held by <paramref name="data"/>, which is read to its end and not kept.</summary>
    public static IdmlStatus DocumentOpen(Stream data, out nint document)
    {
        nint handle = 0;
        var status = Run(() => handle = Register(IdmlDocument.Open(data)));
        document = handle;
        return status;
    }

    /// <summary>Opens an .idml file.</summary>
    public static IdmlStatus DocumentOpenFile(string path, out nint document)
    {
        nint handle = 0;
        var status = Run(() => handle = Register(IdmlDocument.Open(path)));
        document = handle;
        return status;
    }

    /// <summary>Describes the document: a <see cref="Interop.DocumentInfo"/> as UTF-8 JSON.</summary>
    public static IdmlStatus DocumentInfo(nint document, out byte[] utf8Json)
    {
        byte[] json = [];
        var status = WithDocument(document, idml =>
        {
            var package = idml.Package;
            List<PartInfo> parts = [with(capacity: package.PartPaths.Count)];
            foreach (var path in package.PartPaths)
            {
                parts.Add(new PartInfo(path, package.GetBytes(path).Length, package.IsModified(path)));
            }
            var info = new DocumentInfo(idml.DomVersion, parts, idml.SpreadPaths, idml.MasterSpreadPaths, idml.StoryPaths);
            json = JsonSerializer.SerializeToUtf8Bytes(info, InteropJson.Contract.DocumentInfo);
        });
        utf8Json = json;
        return status;
    }

    /// <summary>The bytes of one part as they would be written now.</summary>
    public static IdmlStatus DocumentReadPart(nint document, string path, out ReadOnlyMemory<byte> data)
    {
        ReadOnlyMemory<byte> bytes = default;
        var status = WithDocument(document, idml => bytes = idml.Package.GetBytes(path));
        data = bytes;
        return status;
    }

    /// <summary>Adds or replaces one part. The data is copied.</summary>
    public static IdmlStatus DocumentWritePart(nint document, string path, ReadOnlySpan<byte> data)
    {
        // Copied before taking the lock: the span cannot be captured, and the caller's memory is not ours to keep.
        var copy = data.ToArray();
        return WithDocument(document, idml => idml.Package.SetBytes(path, copy));
    }

    /// <summary>The document as the bytes of an .idml file.</summary>
    public static IdmlStatus DocumentSave(nint document, out byte[] data)
    {
        byte[] bytes = [];
        var status = WithDocument(document, idml => bytes = idml.ToArray());
        data = bytes;
        return status;
    }

    /// <summary>Writes the document to a file.</summary>
    public static IdmlStatus DocumentSaveFile(nint document, string path) => WithDocument(document, idml => idml.Save(path));

    /// <summary>
    /// Releases a document. Closing a handle twice, or one that never existed, does nothing: finalizers in garbage
    /// collected languages may run after an explicit close.
    /// </summary>
    public static void DocumentClose(nint document) => Documents.Remove(document);

    /// <summary>Records a failure found by a transport before it could call in (a null pointer, text that is not UTF-8).</summary>
    public static IdmlStatus Fail(IdmlStatus status, string message)
    {
        lastErrorStatus = status;
        lastErrorMessage = message;
        return status;
    }

    /// <summary>Records an exception caught by a transport, with the same mapping every method here uses.</summary>
    public static IdmlStatus Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var status = exception switch
        {
            IdmlException idml => idml.Kind switch
            {
                IdmlErrorKind.InvalidFormat => IdmlStatus.InvalidFormat,
                IdmlErrorKind.NotFound => IdmlStatus.NotFound,
                IdmlErrorKind.Unsupported => IdmlStatus.Unsupported,
                IdmlErrorKind.InvalidOperation => IdmlStatus.InvalidOperation,
                _ => IdmlStatus.Failed,
            },
            ArgumentException => IdmlStatus.InvalidArgument,
            OperationCanceledException => IdmlStatus.Cancelled,
            IOException or UnauthorizedAccessException => IdmlStatus.Io,
            JsonException => IdmlStatus.InvalidArgument,
            _ => IdmlStatus.Failed,
        };
        // A failure we did not foresee names its type: that is what makes a bug report useful.
        return Fail(status, status == IdmlStatus.Failed ? $"{exception.GetType().Name}: {exception.Message}" : exception.Message);
    }

    private static nint Register(IdmlDocument document) => Documents.Add(new Slot(document));

    private static IdmlStatus WithDocument(nint document, Func<IdmlDocument, string> action, out string result)
    {
        var value = string.Empty;
        var status = WithDocument(document, idml => value = action(idml));
        result = value;
        return status;
    }

    /// <summary>Runs with both documents locked, always in handle order, so two threads cannot deadlock.</summary>
    private static IdmlStatus WithDocuments(nint first, nint second, Action<IdmlDocument, IdmlDocument> action)
    {
        if (!Documents.TryGet(first, out var a) || !Documents.TryGet(second, out var b))
        {
            return Fail(IdmlStatus.InvalidHandle, "The handle is not an open document.");
        }
        if (ReferenceEquals(a, b))
        {
            lock (a.Gate)
            {
                return Run(() => action(a.Document, a.Document));
            }
        }
        var (outer, inner) = first < second ? (a, b) : (b, a);
        lock (outer.Gate)
        {
            lock (inner.Gate)
            {
                return Run(() => action(a.Document, b.Document));
            }
        }
    }

    private static IdmlStatus WithDocument(nint document, Action<IdmlDocument> action)
    {
        if (!Documents.TryGet(document, out var slot))
        {
            return Fail(IdmlStatus.InvalidHandle, "The handle is not an open document.");
        }
        lock (slot.Gate)
        {
            return Run(() => action(slot.Document));
        }
    }

    private static IdmlStatus Run(Action action)
    {
        try
        {
            lastErrorStatus = IdmlStatus.Ok;
            lastErrorMessage = null;
            action();
            return IdmlStatus.Ok;
        }
        catch (RecordedFailure)
        {
            return lastErrorStatus;
        }
#pragma warning disable CA1031 // Nothing may escape into another language's runtime.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Fail(exception);
        }
    }

    /// <summary>Unwinds out of <see cref="Run"/> after a nested call already recorded its failure.</summary>
    private sealed class RecordedFailure : Exception;

    /// <summary>A document and the lock that serializes calls on it.</summary>
    private sealed class Slot(IdmlDocument document)
    {
        public IdmlDocument Document { get; } = document;

        public Lock Gate { get; } = new();
    }
}
