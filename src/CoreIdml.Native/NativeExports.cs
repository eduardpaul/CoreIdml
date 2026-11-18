using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using CoreIdml.Interop;

namespace CoreIdml.Native;

/// <summary>
/// The exported C functions; <c>include/coreidml.h</c> declares and documents each one, and
/// <c>tests/CoreIdml.Abi.Tests</c> fails when the two disagree on a name, a parameter or a type.
/// Everything here converts pointers to managed values and back, and calls <see cref="IdmlInterop"/>.
/// </summary>
internal static unsafe partial class NativeExports
{
    private static byte* version;

    [ThreadStatic]
    private static byte* lastErrorMessage;

    [ThreadStatic]
    private static string? lastErrorMessageSource;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_abi_version")]
    public static int AbiVersion() => IdmlInterop.AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_version")]
    public static byte* Version()
    {
        if (version is null)
        {
            // Two threads may both get here; the loser's copy is a one-off leak of a few bytes.
            version = AllocateUtf8(IdmlInterop.LibraryVersion);
        }
        return version;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_last_error_code")]
    public static int LastErrorCode() => (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_last_error_message")]
    public static byte* LastErrorMessage()
    {
        var message = IdmlInterop.LastErrorMessage;
        if (!ReferenceEquals(message, lastErrorMessageSource))
        {
            NativeMemory.Free(lastErrorMessage);
            lastErrorMessage = message is null ? null : AllocateUtf8(message);
            lastErrorMessageSource = message;
        }
        return lastErrorMessage;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_free")]
    public static void Free(void* pointer) => NativeMemory.Free(pointer);

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_open")]
    public static int DocumentOpen(byte* data, nuint length, nint* document)
    {
        if (document is null)
        {
            return Fail("document must not be NULL.");
        }
        *document = 0;
        if (data is null || length == 0)
        {
            return Fail("data must hold an .idml file.");
        }
        try
        {
            // Read in place: the package copies what it keeps, part by part, as it decompresses.
            using var stream = new UnmanagedMemoryStream(data, checked((long)length));
            var status = IdmlInterop.DocumentOpen(stream, out var handle);
            *document = handle;
            return (int)status;
        }
#pragma warning disable CA1031 // Nothing may escape into native code.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return (int)IdmlInterop.Fail(exception);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_open_file")]
    public static int DocumentOpenFile(byte* path, nint* document)
    {
        if (document is null)
        {
            return Fail("document must not be NULL.");
        }
        *document = 0;
        if (!TryReadUtf8(path, "path", out var value))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        var status = IdmlInterop.DocumentOpenFile(value, out var handle);
        *document = handle;
        return (int)status;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_info")]
    public static int DocumentInfo(nint document, byte** json)
    {
        if (json is null)
        {
            return Fail("json must not be NULL.");
        }
        *json = null;
        var status = IdmlInterop.DocumentInfo(document, out var utf8);
        if (status == IdmlStatus.Ok)
        {
            *json = AllocateTerminated(utf8);
        }
        return (int)status;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_read_part")]
    public static int DocumentReadPart(nint document, byte* path, byte** data, nuint* length)
    {
        if (data is null || length is null)
        {
            return Fail("data and length must not be NULL.");
        }
        *data = null;
        *length = 0;
        if (!TryReadUtf8(path, "path", out var value))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        var status = IdmlInterop.DocumentReadPart(document, value, out var bytes);
        if (status == IdmlStatus.Ok)
        {
            *data = AllocateCopy(bytes.Span);
            *length = (nuint)bytes.Length;
        }
        return (int)status;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_write_part")]
    public static int DocumentWritePart(nint document, byte* path, byte* data, nuint length)
    {
        if (data is null && length != 0)
        {
            return Fail("data must not be NULL when length is not 0.");
        }
        if (length > (nuint)Array.MaxLength)
        {
            return Fail("data is larger than a part can be.");
        }
        if (!TryReadUtf8(path, "path", out var value))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        return (int)IdmlInterop.DocumentWritePart(document, value, new ReadOnlySpan<byte>(data, (int)length));
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_save")]
    public static int DocumentSave(nint document, byte** data, nuint* length)
    {
        if (data is null || length is null)
        {
            return Fail("data and length must not be NULL.");
        }
        *data = null;
        *length = 0;
        var status = IdmlInterop.DocumentSave(document, out var bytes);
        if (status == IdmlStatus.Ok)
        {
            *data = AllocateCopy(bytes);
            *length = (nuint)bytes.Length;
        }
        return (int)status;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_save_file")]
    public static int DocumentSaveFile(nint document, byte* path) =>
        TryReadUtf8(path, "path", out var value)
            ? (int)IdmlInterop.DocumentSaveFile(document, value)
            : (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_close")]
    public static void DocumentClose(nint document) => IdmlInterop.DocumentClose(document);

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static int Fail(string message) => (int)IdmlInterop.Fail(IdmlStatus.InvalidArgument, message);

    /// <summary>
    /// Reads a NUL-terminated UTF-8 argument. Text that is not UTF-8 is refused instead of being repaired with
    /// replacement characters: a path or an id that silently changed would fail later, somewhere less obvious.
    /// </summary>
    private static bool TryReadUtf8(byte* value, string name, out string result)
    {
        result = string.Empty;
        if (value is null)
        {
            IdmlInterop.Fail(IdmlStatus.InvalidArgument, $"{name} must not be NULL.");
            return false;
        }
        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value);
        if (!Utf8.IsValid(bytes))
        {
            IdmlInterop.Fail(IdmlStatus.InvalidArgument, $"{name} is not valid UTF-8.");
            return false;
        }
        result = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>A copy the caller releases with coreidml_free. Never NULL, so NULL always means "no result".</summary>
    private static byte* AllocateCopy(ReadOnlySpan<byte> bytes)
    {
        var buffer = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        return buffer;
    }

    private static byte* AllocateTerminated(ReadOnlySpan<byte> utf8)
    {
        var buffer = (byte*)NativeMemory.Alloc((nuint)utf8.Length + 1);
        utf8.CopyTo(new Span<byte>(buffer, utf8.Length));
        buffer[utf8.Length] = 0;
        return buffer;
    }

    private static byte* AllocateUtf8(string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        var buffer = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, length));
        buffer[length] = 0;
        return buffer;
    }
}
