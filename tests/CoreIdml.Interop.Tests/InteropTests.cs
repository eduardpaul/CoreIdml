using System.Text;
using System.Text.Json;
using CoreIdml.TestSupport;

// The open-document count is process-wide state, so these tests do not run next to each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoreIdml.Interop.Tests;

/// <summary>
/// The behaviour every language binding inherits. What passes here holds for C, Node, Python, Dart and the
/// browser alike, because their transports add no behaviour of their own.
/// </summary>
public class InteropTests
{
    private const string StoryPath = "Stories/Story_u188.xml";

    [Fact]
    public void A_document_is_opened_described_and_closed()
    {
        var before = IdmlInterop.OpenDocumentCount;

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentOpen(new MemoryStream(TestFiles.ArticleBytes), out var document));
        Assert.NotEqual(0, document);
        Assert.Equal(before + 1, IdmlInterop.OpenDocumentCount);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentInfo(document, out var json));
        using var info = JsonDocument.Parse(json);
        var root = info.RootElement;
        Assert.Equal("10.0", root.GetProperty("domVersion").GetString());
        Assert.Equal(["Spreads/Spread_ud6.xml"], root.GetProperty("spreads").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, root.GetProperty("masterSpreads").GetArrayLength());
        Assert.Equal(3, root.GetProperty("stories").GetArrayLength());
        var parts = root.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(16, parts.Count);
        Assert.Equal("mimetype", parts[0].GetProperty("path").GetString());
        Assert.Equal(43, parts[0].GetProperty("length").GetInt64());
        Assert.All(parts, part => Assert.False(part.GetProperty("modified").GetBoolean()));

        IdmlInterop.DocumentClose(document);
        Assert.Equal(before, IdmlInterop.OpenDocumentCount);
    }

    [Fact]
    public void A_document_opens_from_a_file_and_saves_to_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"coreidml-{Guid.NewGuid():N}.idml");
        try
        {
            Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentOpenFile(TestFiles.Article, out var document));
            Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSaveFile(document, path));
            IdmlInterop.DocumentClose(document);

            Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentOpenFile(path, out var saved));
            IdmlInterop.DocumentClose(saved);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_part_is_read_replaced_and_saved()
    {
        using var opened = Open();
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentReadPart(opened.Handle, StoryPath, out var original));
        var edited = Encoding.UTF8.GetString(original.Span).Replace("<Story ", "<Story Marker=\"edited\" ", StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetString(original.Span), edited);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentWritePart(opened.Handle, StoryPath, Encoding.UTF8.GetBytes(edited)));
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSave(opened.Handle, out var saved));

        var reopened = IdmlDocument.Open(saved);
        Assert.Equal(edited, Encoding.UTF8.GetString(reopened.Package.GetBytes(StoryPath).Span));
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentInfo(opened.Handle, out var json));
        using var info = JsonDocument.Parse(json);
        var modified = info.RootElement.GetProperty("parts").EnumerateArray().Where(p => p.GetProperty("modified").GetBoolean()).Select(p => p.GetProperty("path").GetString());
        Assert.Equal([StoryPath], modified);
    }

    [Fact]
    public void The_bytes_given_to_write_part_are_copied()
    {
        using var opened = Open();
        var data = "<Story />"u8.ToArray();

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentWritePart(opened.Handle, "Stories/New.xml", data));
        data.AsSpan().Fill((byte)'x'); // the caller reuses its buffer

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentReadPart(opened.Handle, "Stories/New.xml", out var stored));
        Assert.Equal("<Story />", Encoding.UTF8.GetString(stored.Span));
    }

    [Fact]
    public void A_handle_that_is_not_a_document_is_an_invalid_handle()
    {
        Assert.Equal(IdmlStatus.InvalidHandle, IdmlInterop.DocumentInfo(0, out var json));
        Assert.Empty(json);
        Assert.Equal(IdmlStatus.InvalidHandle, IdmlInterop.LastErrorStatus);
        Assert.NotNull(IdmlInterop.LastErrorMessage);

        Assert.Equal(IdmlStatus.InvalidHandle, IdmlInterop.DocumentSave(123_456_789, out _));
        Assert.Equal(IdmlStatus.InvalidHandle, IdmlInterop.DocumentReadPart(-1, StoryPath, out _));
    }

    [Fact]
    public void A_closed_handle_is_never_reused_and_closing_twice_is_harmless()
    {
        var first = Open();
        first.Dispose();
        IdmlInterop.DocumentClose(first.Handle); // a finalizer running after an explicit close
        IdmlInterop.DocumentClose(0);

        using var second = Open();

        Assert.NotEqual(first.Handle, second.Handle);
        Assert.Equal(IdmlStatus.InvalidHandle, IdmlInterop.DocumentInfo(first.Handle, out _));
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentInfo(second.Handle, out _));
    }

    [Fact]
    public void Failures_carry_a_status_and_a_message_and_a_success_clears_them()
    {
        Assert.Equal(IdmlStatus.InvalidFormat, IdmlInterop.DocumentOpen(new MemoryStream("not a zip"u8.ToArray()), out var none));
        Assert.Equal(0, none);
        Assert.Equal(IdmlStatus.InvalidFormat, IdmlInterop.LastErrorStatus);
        Assert.StartsWith("Not an IDML package", IdmlInterop.LastErrorMessage, StringComparison.Ordinal);

        using var opened = Open();
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.LastErrorStatus);
        Assert.Null(IdmlInterop.LastErrorMessage);

        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.DocumentReadPart(opened.Handle, "Stories/Nope.xml", out var data));
        Assert.True(data.IsEmpty);
        Assert.Contains("Stories/Nope.xml", IdmlInterop.LastErrorMessage, StringComparison.Ordinal);

        Assert.Equal(IdmlStatus.InvalidArgument, IdmlInterop.DocumentWritePart(opened.Handle, "bad\\path.xml", [1]));
        Assert.Equal(IdmlStatus.Io, IdmlInterop.DocumentOpenFile(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.idml"), out _));
    }

    [Fact]
    public void Exceptions_map_onto_status_codes_the_same_way_everywhere()
    {
        Assert.Equal(IdmlStatus.InvalidFormat, IdmlInterop.Fail(new IdmlException(IdmlErrorKind.InvalidFormat, "x")));
        Assert.Equal(IdmlStatus.NotFound, IdmlInterop.Fail(new IdmlException(IdmlErrorKind.NotFound, "x")));
        Assert.Equal(IdmlStatus.Unsupported, IdmlInterop.Fail(new IdmlException(IdmlErrorKind.Unsupported, "x")));
        Assert.Equal(IdmlStatus.InvalidOperation, IdmlInterop.Fail(new IdmlException(IdmlErrorKind.InvalidOperation, "x")));
        Assert.Equal(IdmlStatus.InvalidArgument, IdmlInterop.Fail(new ArgumentNullException("path")));
        Assert.Equal(IdmlStatus.Cancelled, IdmlInterop.Fail(new OperationCanceledException()));
        Assert.Equal(IdmlStatus.Io, IdmlInterop.Fail(new FileNotFoundException("gone")));
        Assert.Equal(IdmlStatus.Io, IdmlInterop.Fail(new UnauthorizedAccessException("no")));

        // Something nobody foresaw names its type, so a bug report says what happened.
        Assert.Equal(IdmlStatus.Failed, IdmlInterop.Fail(new NotImplementedException("later")));
        Assert.Equal("NotImplementedException: later", IdmlInterop.LastErrorMessage);
    }

    [Fact]
    public void Every_error_kind_of_the_library_has_a_status_code()
    {
        foreach (var kind in Enum.GetValues<IdmlErrorKind>())
        {
            Assert.Equal(kind.ToString(), IdmlInterop.Fail(new IdmlException(kind, "x")).ToString());
        }
    }

    [Fact]
    public async Task Documents_are_usable_from_several_threads()
    {
        using var opened = Open();
        var handle = opened.Handle;

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentWritePart(handle, $"Extra/{i}.txt", Encoding.UTF8.GetBytes($"{i}:{n}")));
                Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentInfo(handle, out _));
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentSave(handle, out var saved));
        Assert.Equal(16 + 8, IdmlDocument.Open(saved).Package.PartPaths.Count);
    }

    private static OpenDocument Open()
    {
        Assert.Equal(IdmlStatus.Ok, IdmlInterop.DocumentOpen(new MemoryStream(TestFiles.ArticleBytes), out var handle));
        return new OpenDocument(handle);
    }

    private sealed class OpenDocument(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;

        public void Dispose() => IdmlInterop.DocumentClose(Handle);
    }
}
