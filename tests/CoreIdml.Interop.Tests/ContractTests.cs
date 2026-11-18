using System.Text.Json;
using CoreIdml.TestSupport;

namespace CoreIdml.Interop.Tests;

/// <summary>
/// The document, XML, composition and PDF functions of the contract: their status codes, JSON results and the rules
/// every binding relies on (failures leave out parameters empty; a callback can cancel; two documents lock safely).
/// </summary>
public class ContractTests
{
    private const string Spec = """
        { "unit": "mm", "page": { "size": "A5" },
          "paragraphStyles": [ { "name": "Title", "size": 18 } ],
          "pages": [ { "items": [
            { "type": "text", "name": "title", "x": 10, "y": 10, "width": 128, "height": 12, "text": "Sheet {{code}}", "style": "Title" },
            { "type": "text", "name": "client", "tag": "client", "x": 10, "y": 30, "width": 128, "height": 12, "text": "-" }
          ] } ] }
        """;

    private static PdfOptions Fonts => new(
        "{\"useSystemFonts\":false,\"fontDirectories\":[" + JsonSerializer.Serialize(TestFiles.Fonts) + "]," +
        "\"substitutions\":{\"Minion Pro\":\"Liberation Serif\",\"Myriad Pro\":\"Liberation Sans\"}}");

    [Fact]
    public void A_document_is_created_from_a_spec_filled_and_summarized()
    {
        using var document = Create(Spec);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentFill(document.Handle, """{"text":{"code":"F-001","client":"ACME"}}""", out var report));
        Assert.Empty(JsonElement.Parse(report).GetProperty("missing").EnumerateArray());
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentReplaceText(document.Handle, "ACME", "ACME Inc.", out var count));
        Assert.Equal(1, count);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSummary(document.Handle, null, out var summary));
        Assert.Contains("Sheet F-001", summary, StringComparison.Ordinal);
        Assert.Contains("ACME Inc.", summary, StringComparison.Ordinal);
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSummary(document.Handle, """{"sections":["stories"],"includeRuns":true}""", out var stories));
        Assert.False(JsonElement.Parse(stories).TryGetProperty("paragraphStyles", out _));

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentValidate(document.Handle, out var validation));
        Assert.True(JsonElement.Parse(validation).GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public void A_blank_document_is_created_without_a_spec()
    {
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentCreate(null, out var blank));
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSummary(blank, null, out var summary));
        Assert.Equal(1, JsonElement.Parse(summary).GetProperty("pageCount").GetInt32());
        IdmlInterop.DocumentClose(blank);
    }

    [Fact]
    public void Runs_are_listed_set_and_refused_with_the_right_status()
    {
        using var document = Create(Spec);
        var storyId = FirstStoryId(document.Handle);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentStory(document.Handle, storyId, out var story));
        Assert.Equal(0, JsonElement.Parse(story).GetProperty("runs")[0].GetProperty("index").GetInt32());
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSetRunText(document.Handle, storyId, 0, "Edited", force: false));

        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.DocumentSetRunText(document.Handle, storyId, 99, "x", force: false));
        Assert.Contains("run index 99", IdmlInterop.LastErrorMessage, StringComparison.Ordinal);
        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.DocumentStory(document.Handle, "u-none", out var none));
        Assert.Equal(string.Empty, none);
        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.DocumentRelink(document.Handle, "u-none", "/x.jpg", refit: false));
        Assert.Equal(IdmlStatus.InvalidArgument, IdmlInterop.DocumentReplaceText(document.Handle, "", "x", out _));
    }

    [Fact]
    public void Xml_is_imported_exported_and_described()
    {
        using var document = Create(Spec);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentImportXml(document.Handle, "<Root><client>Imported</client></Root>", null, null, out var report));
        Assert.Empty(JsonElement.Parse(report).GetProperty("unmatched").EnumerateArray());
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentExportXml(document.Handle, "/Root/client", out var xml));
        Assert.Equal("<client>Imported</client>", xml);
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentXmlStructure(document.Handle, out var structure));
        Assert.Contains("\"path\":\"/Root/client\"", structure, StringComparison.Ordinal);

        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.DocumentExportXml(document.Handle, "/Root/nothing", out _));
        Assert.Equal(IdmlStatus.InvalidFormat, IdmlInterop.DocumentImportXml(document.Handle, "<Root><unclosed></Root>", null, null, out _));
    }

    [Fact]
    public void Pages_are_imported_from_another_document_and_from_itself_and_the_diff_sees_them()
    {
        using var target = Create(Spec);
        using var source = Create(Spec);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentImportPages(target.Handle, source.Handle, [], out var added));
        Assert.Equal(1, added);
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentImportPages(target.Handle, target.Handle, [0], out added));
        Assert.Equal(1, added);
        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.DocumentImportPages(target.Handle, source.Handle, [7], out added));
        Assert.Equal(0, added);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentDiff(source.Handle, target.Handle, out var diff));
        Assert.Contains("page-count", diff, StringComparison.Ordinal);
        Assert.Equal(IdmlStatus.InvalidHandle, IdmlInterop.DocumentDiff(source.Handle, 987654, out _));
    }

    [Fact]
    public async Task Two_threads_importing_pages_between_two_documents_in_opposite_directions_do_not_deadlock()
    {
        using var a = Create(Spec);
        using var b = Create(Spec);

        var forward = Task.Run(() => Repeat(() => IdmlInterop.DocumentImportPages(a.Handle, b.Handle, [0], out _)), TestContext.Current.CancellationToken);
        var backward = Task.Run(() => Repeat(() => IdmlInterop.DocumentImportPages(b.Handle, a.Handle, [0], out _)), TestContext.Current.CancellationToken);

        var statuses = await Task.WhenAll(forward, backward).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.All(statuses, status => Assert.Equal(IdmlStatus.Ok, status));

        static IdmlStatus Repeat(Func<IdmlStatus> call)
        {
            for (var i = 0; i < 5; i++)
            {
                if (call() is not IdmlStatus.Ok and var failed)
                {
                    return failed;
                }
            }
            return IdmlStatus.Ok;
        }
    }

    [Fact]
    public void A_pdf_is_exported_with_progress_and_imported_back()
    {
        using var document = Create(Spec);
        List<(int Done, int Total)> calls = [];

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentExportPdfBytes(document.Handle, Fonts.Json, (done, total) =>
        {
            calls.Add((done, total));
            return true;
        }, out var pdf, out var report));
        Assert.Equal("%PDF"u8, pdf.AsSpan(0, 4));
        Assert.Equal(1, JsonElement.Parse(report).GetProperty("pageCount").GetInt32());
        Assert.Equal([(1, 1)], calls);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.PdfImport(new MemoryStream(pdf), null, null, out var imported, out var importReport));
        Assert.NotEqual(0, imported);
        Assert.True(JsonElement.Parse(importReport).GetProperty("fonts").GetArrayLength() > 0);
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSummary(imported, null, out var summary));
        Assert.Contains("Sheet", summary, StringComparison.Ordinal);
        IdmlInterop.DocumentClose(imported);
    }

    [Fact]
    public void A_progress_callback_that_returns_false_cancels_and_nothing_is_returned()
    {
        using var document = Create(Spec);

        Assert.Equal(IdmlStatus.Cancelled, IdmlInterop.DocumentExportPdfBytes(document.Handle, Fonts.Json, (_, _) => false, out var pdf, out var report));
        Assert.Empty(pdf);
        Assert.Equal(string.Empty, report);
        Assert.Equal(IdmlStatus.Cancelled, IdmlInterop.LastErrorStatus);
    }

    [Fact]
    public void Pdf_files_are_written_and_read_and_bad_input_has_its_own_status()
    {
        using var document = Create(Spec);
        var path = Path.Combine(Path.GetTempPath(), $"coreidml-{Guid.NewGuid():N}.pdf");
        try
        {
            Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentExportPdf(document.Handle, path, Fonts.Json, null, out _));
            Assert.Equal(IdmlStatus.Ok, IdmlInterop.PdfImportFile(path, null, null, out var imported, out _));
            IdmlInterop.DocumentClose(imported);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(IdmlStatus.InvalidArgument, IdmlInterop.DocumentExportPdf(document.Handle, "", null, null, out _));
        Assert.Equal(IdmlStatus.Io, IdmlInterop.PdfImportFile(Path.Combine(Path.GetTempPath(), "coreidml-none", "a.pdf"), null, null, out var none, out _));
        Assert.Equal(0, none);
        Assert.Equal(IdmlStatus.InvalidArgument, IdmlInterop.DocumentExportPdfBytes(document.Handle, "{ not json", null, out _, out _));
    }

    [Fact]
    public void A_cancelled_export_leaves_no_file_and_keeps_an_earlier_one()
    {
        using var document = Create(Spec);
        var folder = Directory.CreateTempSubdirectory("coreidml-").FullName;
        var path = Path.Combine(folder, "out.pdf");
        try
        {
            Assert.Equal(IdmlStatus.Cancelled, IdmlInterop.DocumentExportPdf(document.Handle, path, Fonts.Json, (_, _) => false, out _));
            Assert.Empty(Directory.GetFiles(folder));

            File.WriteAllText(path, "earlier");
            Assert.Equal(IdmlStatus.Cancelled, IdmlInterop.DocumentExportPdf(document.Handle, path, Fonts.Json, (_, _) => false, out _));
            Assert.Equal("earlier", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Data_that_is_not_a_pdf_is_invalid_format_and_missing_pages_are_reported()
    {
        Assert.Equal(IdmlStatus.InvalidFormat, IdmlInterop.PdfImport(new MemoryStream("not a pdf"u8.ToArray()), null, null, out var none, out _));
        Assert.Equal(0, none);
        Assert.StartsWith("The PDF is not a readable PDF", IdmlInterop.LastErrorMessage, StringComparison.Ordinal);

        using var document = Create(Spec);
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentExportPdfBytes(document.Handle, "{\"pages\":[1,5]," + Fonts.Json[1..], null, out _, out var report));
        var parsed = JsonElement.Parse(report);
        Assert.Equal(1, parsed.GetProperty("pageCount").GetInt32());
        Assert.Contains(parsed.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.StartsWith("Page 5 does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_or_inconsistent_specs_have_their_own_status()
    {
        Assert.Equal(IdmlStatus.InvalidArgument, IdmlInterop.DocumentCreate("{ not json", out var none));
        Assert.Equal(0, none);

        using var document = Create(Spec);
        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.DocumentApply(document.Handle, """{"pages":[{"items":[{"type":"text","text":"x","style":"Nope"}]}]}"""));
        Assert.Contains("Nope", IdmlInterop.LastErrorMessage, StringComparison.Ordinal);
    }

    private static string FirstStoryId(nint document)
    {
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSummary(document, null, out var summary));
        return Find(JsonElement.Parse(summary)) ?? throw new InvalidOperationException("The summary names no story.");

        static string? Find(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object when element.TryGetProperty("storyId", out var id) && id.ValueKind == JsonValueKind.String => id.GetString(),
            JsonValueKind.Object => element.EnumerateObject().Select(property => Find(property.Value)).FirstOrDefault(found => found is not null),
            JsonValueKind.Array => element.EnumerateArray().Select(Find).FirstOrDefault(found => found is not null),
            _ => null,
        };
    }

    private static OpenHandle Create(string spec)
    {
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentCreate(spec, out var document));
        return new OpenHandle(document);
    }

    private sealed record PdfOptions(string Json);

    private sealed class OpenHandle(nint value) : IDisposable
    {
        public nint Handle { get; } = value;

        public void Dispose() => IdmlInterop.DocumentClose(Handle);
    }
}
