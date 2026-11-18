using System.IO.Compression;
using CoreIdml.Packaging;

namespace CoreIdml.Tests;

public class PackagePreservationTests
{
    [Fact]
    public void Saved_package_starts_with_an_uncompressed_mimetype_entry()
    {
        using var document = IdmlDocument.Create();

        var bytes = document.ToArray();

        var check = IdmlValidator.ValidatePackageBytes(bytes);
        Assert.True(check.IsValid, check.ToString());
        using var archive = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal("mimetype", archive.Entries[0].FullName);
    }

    [Fact]
    public void Untouched_parts_of_a_real_file_are_saved_byte_for_byte()
    {
        var original = IdmlPackage.Open(TestFiles.Editorial);
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        _ = document.Stories.Select(story => story.Text).ToList(); // read everything, change nothing

        var reopened = IdmlPackage.Open(document.ToArray());

        Assert.Equal(original.PartPaths, reopened.PartPaths);
        foreach (var path in original.PartPaths)
        {
            Assert.True(original.GetBytes(path).Span.SequenceEqual(reopened.GetBytes(path).Span), $"{path} changed");
        }
    }

    [Fact]
    public void Editing_one_story_rewrites_only_that_part()
    {
        var original = IdmlPackage.Open(TestFiles.Editorial);
        using var document = IdmlDocument.Open(TestFiles.Editorial);
        var story = document.Stories.First(s => s.Text.Contains("Némard", StringComparison.Ordinal));

        Assert.Equal(1, story.ReplaceText("Jean Némard", "Ana Pérez"));

        var reopened = IdmlPackage.Open(document.ToArray());
        var changed = original.PartPaths.Where(path => !original.GetBytes(path).Span.SequenceEqual(reopened.GetBytes(path).Span)).ToList();
        Assert.Equal([story.PartPath], changed);
    }

    [Fact]
    public void Opening_something_that_is_not_idml_fails_clearly()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("hello.txt");
        }

        var exception = Assert.Throws<IdmlException>(() => IdmlDocument.Open(buffer.ToArray()));
        Assert.Contains("designmap.xml", exception.Message, StringComparison.Ordinal);
    }
}
