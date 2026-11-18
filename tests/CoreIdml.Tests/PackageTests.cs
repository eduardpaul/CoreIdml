using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CoreIdml.Packaging;
using CoreIdml.TestSupport;

namespace CoreIdml.Tests;

public class PackageTests
{
    private const string StoryPath = "Stories/Story_u188.xml";

    [Fact]
    public void Untouched_parts_of_a_real_file_are_saved_byte_for_byte()
    {
        var original = IdmlPackage.Open(TestFiles.Article);
        var package = IdmlPackage.Open(TestFiles.Article);
        foreach (var path in package.PartPaths.Where(path => path.EndsWith(".xml", StringComparison.Ordinal)))
        {
            _ = package.GetXml(path); // parse everything, change nothing
        }

        var reopened = IdmlPackage.Open(package.ToArray());

        Assert.Equal(original.PartPaths, reopened.PartPaths);
        foreach (var path in original.PartPaths)
        {
            Assert.False(package.IsModified(path), $"{path} is marked as modified");
            Assert.True(original.GetBytes(path).Span.SequenceEqual(reopened.GetBytes(path).Span), $"{path} changed");
        }
    }

    [Fact]
    public void Changing_one_part_rewrites_only_that_part()
    {
        var original = IdmlPackage.Open(TestFiles.Article);
        var package = IdmlPackage.Open(TestFiles.Article);

        package.GetXml(StoryPath).Root!.SetAttributeValue("Marker", "edited");

        Assert.True(package.IsModified(StoryPath));
        var reopened = IdmlPackage.Open(package.ToArray());
        var changed = original.PartPaths.Where(path => !original.GetBytes(path).Span.SequenceEqual(reopened.GetBytes(path).Span));
        Assert.Equal([StoryPath], changed);
        Assert.Equal("edited", (string?)reopened.GetXml(StoryPath).Root!.Attribute("Marker"));
    }

    [Fact]
    public void A_saved_package_starts_with_a_stored_mimetype_and_no_data_descriptor()
    {
        var bytes = IdmlPackage.Open(TestFiles.Article).ToArray();

        // Local file header: signature (4), version (2), flags (2), method (2) ... name length at 26, name at 30.
        Assert.Equal("PK\u0003\u0004"u8, bytes.AsSpan(0, 4));
        Assert.Equal(0, bytes[6] & 0x08); // bit 3: sizes follow the data
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 8)); // stored
        Assert.Equal("mimetype"u8, bytes.AsSpan(30, 8));
        Assert.Equal(IdmlNames.MimeType, Encoding.ASCII.GetString(bytes, 38, IdmlNames.MimeType.Length));

        using var archive = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(ZipCompressionMethod.Stored, archive.Entries[0].CompressionMethod);
        Assert.All(archive.Entries.Skip(1), entry => Assert.Equal(ZipCompressionMethod.Deflate, entry.CompressionMethod));
    }

    [Fact]
    public void Saving_to_a_stream_that_cannot_seek_gives_the_same_bytes()
    {
        var package = IdmlPackage.Open(TestFiles.Article);
        using var target = new ForwardOnlyStream();

        package.Save(target);

        Assert.Equal(package.ToArray(), target.ToArray());
    }

    [Fact]
    public void An_unchanged_package_always_saves_to_the_same_bytes_and_keeps_its_timestamps()
    {
        var first = IdmlPackage.Open(TestFiles.Article).ToArray();
        var second = IdmlPackage.Open(TestFiles.Article).ToArray();

        Assert.Equal(first, second);
        using var original = new ZipArchive(File.OpenRead(TestFiles.Article));
        using var saved = new ZipArchive(new MemoryStream(first));
        Assert.Equal(original.Entries.Select(entry => entry.LastWriteTime), saved.Entries.Select(entry => entry.LastWriteTime));
    }

    [Fact]
    public void An_edited_package_saved_with_a_fixed_timestamp_is_reproducible()
    {
        var options = new IdmlSaveOptions { Timestamp = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero) };

        byte[] Save()
        {
            var package = IdmlPackage.Open(TestFiles.Article);
            package.GetXml(StoryPath).Root!.SetAttributeValue("Marker", "edited");
            package.SetBytes("Extra/note.txt", "hello"u8.ToArray());
            return package.ToArray(options);
        }

        var bytes = Save();

        Assert.Equal(bytes, Save());
        using var archive = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(options.Timestamp.Value.DateTime, archive.GetEntry(StoryPath)!.LastWriteTime.DateTime);
        Assert.Equal(2014, archive.GetEntry("designmap.xml")!.LastWriteTime.Year); // untouched: as InDesign wrote it
    }

    [Fact]
    public void A_changed_part_keeps_tabs_and_line_breaks_in_attribute_values_and_its_ending()
    {
        const string Part = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Root Separator=\"a&#x9;b&#xA;c\" Other=\"1\" />\n";
        var package = IdmlPackage.Open(TestFiles.BuildPackage(("Resources/Test.xml", Part)));

        package.GetXml("Resources/Test.xml").Root!.SetAttributeValue("Other", "2");

        var text = Encoding.UTF8.GetString(package.GetBytes("Resources/Test.xml").Span);
        Assert.Equal(Part.Replace("Other=\"1\"", "Other=\"2\"", StringComparison.Ordinal), text);
    }

    [Fact]
    public void Parts_can_be_added_replaced_and_removed()
    {
        var package = IdmlPackage.Open(TestFiles.BuildPackage(("Stories/Story_a.xml", "<Story />")));

        package.SetXml("Stories/Story_b.xml", new XDocument(new XElement("Story", new XAttribute("Self", "b"))));
        package.SetBytes("Stories/Story_a.xml", "<Story Self=\"a\" />"u8.ToArray());
        Assert.True(package.Remove("designmap.xml") && !package.Remove("designmap.xml"));
        package.SetXml("designmap.xml", XDocument.Parse(TestFiles.MinimalDesignMap));

        var reopened = IdmlPackage.Open(package.ToArray());
        Assert.Equal(["mimetype", "Stories/Story_a.xml", "Stories/Story_b.xml", "designmap.xml"], reopened.PartPaths);
        Assert.Equal("a", (string?)reopened.GetXml("Stories/Story_a.xml").Root!.Attribute("Self"));
        Assert.Equal("b", (string?)reopened.GetXml("Stories/Story_b.xml").Root!.Attribute("Self"));
        Assert.Null(reopened.TryGetXml("Stories/Story_c.xml"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Stories/a.xml")]
    [InlineData("Stories/")]
    [InlineData("Stories\\a.xml")]
    public void A_part_path_that_is_not_a_zip_path_is_refused(string path)
    {
        var package = IdmlPackage.Create();

        Assert.ThrowsAny<ArgumentException>(() => package.SetBytes(path, new byte[] { 1 }));
    }

    [Fact]
    public void A_new_package_holds_the_mimetype()
    {
        var package = IdmlPackage.Create();

        Assert.Equal(["mimetype"], package.PartPaths);
        Assert.Equal(IdmlNames.MimeType, Encoding.ASCII.GetString(package.GetBytes("mimetype").Span));
    }

    [Fact]
    public void Asking_for_a_part_that_does_not_exist_is_not_found()
    {
        var package = IdmlPackage.Open(TestFiles.Article);

        var exception = Assert.Throws<IdmlException>(() => package.GetBytes("Stories/Nope.xml"));

        Assert.Equal(IdmlErrorKind.NotFound, exception.Kind);
        Assert.Contains("Stories/Nope.xml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_that_is_not_a_zip_is_an_invalid_format()
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlPackage.Open("this is not a zip file"u8.ToArray()));

        Assert.Equal(IdmlErrorKind.InvalidFormat, exception.Kind);
        Assert.StartsWith("Not an IDML package", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_without_a_designmap_is_an_invalid_format()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("hello.txt");
        }

        var exception = Assert.Throws<IdmlException>(() => IdmlPackage.Open(buffer.ToArray()));

        Assert.Equal(IdmlErrorKind.InvalidFormat, exception.Kind);
        Assert.Contains("designmap.xml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_entry_is_an_invalid_format()
    {
        // .NET 11 checks the CRC of every entry it reads. The mimetype entry is stored, so flipping one of its
        // bytes leaves a zip that is structurally fine and wrong only in its content.
        var bytes = TestFiles.ArticleBytes;
        bytes[38] ^= 0xFF;

        var exception = Assert.Throws<IdmlException>(() => IdmlPackage.Open(bytes));

        Assert.Equal(IdmlErrorKind.InvalidFormat, exception.Kind);
    }

    [Fact]
    public void A_part_that_is_not_well_formed_xml_fails_when_it_is_read_as_xml()
    {
        var package = IdmlPackage.Open(TestFiles.BuildPackage(("Stories/Broken.xml", "<Story><Unclosed></Story>")));

        var exception = Assert.Throws<IdmlException>(() => package.GetXml("Stories/Broken.xml"));

        Assert.Equal(IdmlErrorKind.InvalidFormat, exception.Kind);
        Assert.Contains("Stories/Broken.xml", exception.Message, StringComparison.Ordinal);
        Assert.Equal("<Story><Unclosed></Story>", Encoding.UTF8.GetString(package.GetBytes("Stories/Broken.xml").Span)); // still readable as bytes
    }

    [Fact]
    public void A_part_with_a_document_type_declaration_is_refused()
    {
        const string Bomb = "<!DOCTYPE a [<!ENTITY x \"xxxxxxxxxx\"><!ENTITY y \"&x;&x;&x;&x;\">]><a>&y;</a>";
        var package = IdmlPackage.Open(TestFiles.BuildPackage(("Stories/Dtd.xml", Bomb)));

        var exception = Assert.Throws<IdmlException>(() => package.GetXml("Stories/Dtd.xml"));

        Assert.Equal(IdmlErrorKind.InvalidFormat, exception.Kind);
    }

    [Fact]
    public async Task A_package_opens_and_saves_through_streams_without_blocking_on_them()
    {
        var expected = IdmlPackage.Open(TestFiles.Article);
        await using var source = new ForwardOnlyStream(TestFiles.ArticleBytes);
        await using var target = new ForwardOnlyStream();

        var package = await IdmlPackage.OpenAsync(source, TestContext.Current.CancellationToken);
        await package.SaveAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.ToArray(), target.ToArray());
    }

    /// <summary>A stream like a network stream: it cannot seek and has no length.</summary>
    private sealed class ForwardOnlyStream(byte[]? content = null) : Stream
    {
        private readonly MemoryStream inner = content is null ? new MemoryStream() : new MemoryStream(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => inner.ToArray();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
