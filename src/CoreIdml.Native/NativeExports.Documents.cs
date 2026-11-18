using System.Runtime.InteropServices;
using CoreIdml.Interop;

namespace CoreIdml.Native;

/// <summary>Creating, reading, editing and PDF; see include/coreidml.h for what each function does.</summary>
internal static unsafe partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_create")]
    public static int DocumentCreate(byte* specJson, nint* document)
    {
        if (document is null)
        {
            return Fail("document must not be NULL.");
        }
        *document = 0;
        if (!TryReadOptionalUtf8(specJson, "spec_json", out var spec))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        var status = IdmlInterop.DocumentCreate(spec, out var handle);
        *document = handle;
        return (int)status;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_apply")]
    public static int DocumentApply(nint document, byte* specJson) =>
        TryReadUtf8(specJson, "spec_json", out var spec)
            ? (int)IdmlInterop.DocumentApply(document, spec)
            : (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_summary")]
    public static int DocumentSummary(nint document, byte* optionsJson, byte** json)
    {
        if (!TryPrepare(json, "json") || !TryReadOptionalUtf8(optionsJson, "options_json", out var options))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        return Text(IdmlInterop.DocumentSummary(document, options, out var result), result, json);
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_story")]
    public static int DocumentStory(nint document, byte* storyId, byte** json)
    {
        if (!TryPrepare(json, "json") || !TryReadUtf8(storyId, "story_id", out var id))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        return Text(IdmlInterop.DocumentStory(document, id, out var result), result, json);
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_xml_structure")]
    public static int DocumentXmlStructure(nint document, byte** json) =>
        TryPrepare(json, "json")
            ? Text(IdmlInterop.DocumentXmlStructure(document, out var result), result, json)
            : (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_export_xml")]
    public static int DocumentExportXml(nint document, byte* at, byte** xml)
    {
        if (!TryPrepare(xml, "xml") || !TryReadOptionalUtf8(at, "at", out var path))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        return Text(IdmlInterop.DocumentExportXml(document, path, out var result), result, xml);
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_validate")]
    public static int DocumentValidate(nint document, byte** json) =>
        TryPrepare(json, "json")
            ? Text(IdmlInterop.DocumentValidate(document, out var result), result, json)
            : (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_diff")]
    public static int DocumentDiff(nint before, nint after, byte** json) =>
        TryPrepare(json, "json")
            ? Text(IdmlInterop.DocumentDiff(before, after, out var result), result, json)
            : (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_fill")]
    public static int DocumentFill(nint document, byte* dataJson, byte** reportJson)
    {
        if (!TryPrepare(reportJson, "report_json") || !TryReadUtf8(dataJson, "data_json", out var data))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        return Text(IdmlInterop.DocumentFill(document, data, out var result), result, reportJson);
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_replace_text")]
    public static int DocumentReplaceText(nint document, byte* find, byte* replacement, int* count)
    {
        if (count is null)
        {
            return Fail("count must not be NULL.");
        }
        *count = 0;
        if (!TryReadUtf8(find, "find", out var findText) || !TryReadUtf8(replacement, "replacement", out var replacementText))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        var status = IdmlInterop.DocumentReplaceText(document, findText, replacementText, out var replaced);
        *count = replaced;
        return (int)status;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_set_run_text")]
    public static int DocumentSetRunText(nint document, byte* storyId, int runIndex, byte* text, int force) =>
        TryReadUtf8(storyId, "story_id", out var id) && TryReadUtf8(text, "text", out var value)
            ? (int)IdmlInterop.DocumentSetRunText(document, id, runIndex, value, force != 0)
            : (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_relink")]
    public static int DocumentRelink(nint document, byte* linkId, byte* uriOrPath, int refit) =>
        TryReadUtf8(linkId, "link_id", out var id) && TryReadUtf8(uriOrPath, "uri_or_path", out var target)
            ? (int)IdmlInterop.DocumentRelink(document, id, target, refit != 0)
            : (int)IdmlInterop.LastErrorStatus;

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_import_xml")]
    public static int DocumentImportXml(nint document, byte* xml, byte* at, byte* optionsJson, byte** reportJson)
    {
        if (!TryPrepare(reportJson, "report_json")
            || !TryReadUtf8(xml, "xml", out var source)
            || !TryReadOptionalUtf8(at, "at", out var path)
            || !TryReadOptionalUtf8(optionsJson, "options_json", out var options))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        return Text(IdmlInterop.DocumentImportXml(document, source, path, options, out var result), result, reportJson);
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_import_pages")]
    public static int DocumentImportPages(nint document, nint source, int* pages, nuint count, int* added)
    {
        if (added is null)
        {
            return Fail("added must not be NULL.");
        }
        *added = 0;
        if (pages is null && count != 0)
        {
            return Fail("pages must not be NULL when count is not 0.");
        }
        if (count > int.MaxValue)
        {
            return Fail("count is too large.");
        }
        var status = IdmlInterop.DocumentImportPages(document, source, new ReadOnlySpan<int>(pages, (int)count), out var result);
        *added = result;
        return (int)status;
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_export_pdf")]
    public static int DocumentExportPdf(nint document, byte* path, byte* optionsJson, nint progress, void* userData, byte** reportJson)
    {
        if (!TryPrepare(reportJson, "report_json")
            || !TryReadUtf8(path, "path", out var file)
            || !TryReadOptionalUtf8(optionsJson, "options_json", out var options))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        return Text(IdmlInterop.DocumentExportPdf(document, file, options, Progress(progress, userData), out var report), report, reportJson);
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_document_export_pdf_bytes")]
    public static int DocumentExportPdfBytes(nint document, byte* optionsJson, nint progress, void* userData, byte** data, nuint* length, byte** reportJson)
    {
        if (data is null || length is null)
        {
            return Fail("data and length must not be NULL.");
        }
        *data = null;
        *length = 0;
        if (!TryPrepare(reportJson, "report_json") || !TryReadOptionalUtf8(optionsJson, "options_json", out var options))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        var status = IdmlInterop.DocumentExportPdfBytes(document, options, Progress(progress, userData), out var pdf, out var report);
        if (status == IdmlStatus.Ok)
        {
            *data = AllocateCopy(pdf);
            *length = (nuint)pdf.Length;
        }
        return Text(status, report, reportJson);
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_pdf_import")]
    public static int PdfImport(byte* data, nuint length, byte* optionsJson, nint progress, void* userData, nint* document, byte** reportJson)
    {
        if (document is null)
        {
            return Fail("document must not be NULL.");
        }
        *document = 0;
        if (!TryPrepare(reportJson, "report_json") || !TryReadOptionalUtf8(optionsJson, "options_json", out var options))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        if (data is null || length == 0)
        {
            return Fail("data must hold a PDF file.");
        }
        try
        {
            using var stream = new UnmanagedMemoryStream(data, checked((long)length));
            var status = IdmlInterop.PdfImport(stream, options, Progress(progress, userData), out var handle, out var report);
            *document = handle;
            return Text(status, report, reportJson);
        }
#pragma warning disable CA1031 // Nothing may escape into native code.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return (int)IdmlInterop.Fail(exception);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "coreidml_pdf_import_file")]
    public static int PdfImportFile(byte* path, byte* optionsJson, nint progress, void* userData, nint* document, byte** reportJson)
    {
        if (document is null)
        {
            return Fail("document must not be NULL.");
        }
        *document = 0;
        if (!TryPrepare(reportJson, "report_json")
            || !TryReadUtf8(path, "path", out var file)
            || !TryReadOptionalUtf8(optionsJson, "options_json", out var options))
        {
            return (int)IdmlInterop.LastErrorStatus;
        }
        var status = IdmlInterop.PdfImportFile(file, options, Progress(progress, userData), out var handle, out var report);
        *document = handle;
        return Text(status, report, reportJson);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The C callback as a progress function: continue while it returns 0. It is called on the thread that made the
    /// call, so the caller's user_data is used as it was given.
    /// </summary>
    /// <remarks>
    /// The callback arrives as an address rather than a typed function pointer: reflection (the ABI tests, and
    /// anything else that inspects the exports) cannot describe function pointer parameters portably.
    /// </remarks>
    private static Func<int, int, bool>? Progress(nint callback, void* userData)
    {
        if (callback == 0)
        {
            return null;
        }
        var user = (nint)userData;
        return (done, total) => ((delegate* unmanaged<int, int, void*, int>)callback)(done, total, (void*)user) == 0;
    }

    /// <summary>Checks an out parameter for text and clears it.</summary>
    private static bool TryPrepare(byte** text, string name)
    {
        if (text is null)
        {
            Fail($"{name} must not be NULL.");
            return false;
        }
        *text = null;
        return true;
    }

    /// <summary>Hands text to the caller when the call succeeded, and returns the status.</summary>
    private static int Text(IdmlStatus status, string result, byte** text)
    {
        if (status == IdmlStatus.Ok)
        {
            *text = AllocateUtf8(result);
        }
        return (int)status;
    }

    /// <summary>An optional UTF-8 argument: NULL reads as <c>null</c>; anything else must be valid UTF-8.</summary>
    private static bool TryReadOptionalUtf8(byte* value, string name, out string? result)
    {
        if (value is null)
        {
            result = null;
            return true;
        }
        var ok = TryReadUtf8(value, name, out var text);
        result = text;
        return ok;
    }
}
