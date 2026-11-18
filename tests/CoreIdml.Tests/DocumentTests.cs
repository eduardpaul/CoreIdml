using CoreIdml.TestSupport;

namespace CoreIdml.Tests;

public class DocumentTests
{
    [Fact]
    public void The_manifest_of_a_real_file_is_read()
    {
        var document = IdmlDocument.Open(TestFiles.Article);

        Assert.Equal("10.0", document.DomVersion);
        Assert.Equal(["Spreads/Spread_ud6.xml"], document.SpreadPaths);
        Assert.Equal(["MasterSpreads/MasterSpread_ua5.xml"], document.MasterSpreadPaths);
        Assert.Equal(["Stories/Story_u1db.xml", "Stories/Story_u19f.xml", "Stories/Story_u188.xml"], document.StoryPaths);
        Assert.All(document.SpreadPaths.Concat(document.MasterSpreadPaths).Concat(document.StoryPaths), path => Assert.True(document.Package.Contains(path)));
    }

    [Fact]
    public void Reading_the_manifest_does_not_mark_the_document_as_changed()
    {
        var document = IdmlDocument.Open(TestFiles.ArticleBytes);

        _ = document.StoryPaths;

        Assert.False(document.Package.IsModified(IdmlNames.DesignMapPath));
        Assert.Equal(IdmlDocument.Open(TestFiles.Article).ToArray(), document.ToArray());
    }

    [Fact]
    public async Task A_document_opens_from_a_path_bytes_and_streams_alike()
    {
        var fromPath = IdmlDocument.Open(TestFiles.Article).ToArray();
        using var stream = File.OpenRead(TestFiles.Article);
        using var asyncStream = File.OpenRead(TestFiles.Article);

        Assert.Equal(fromPath, IdmlDocument.Open(TestFiles.ArticleBytes).ToArray());
        Assert.Equal(fromPath, IdmlDocument.Open(stream).ToArray());
        Assert.Equal(fromPath, (await IdmlDocument.OpenAsync(asyncStream, TestContext.Current.CancellationToken)).ToArray());
    }

    [Fact]
    public void A_document_saves_to_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"coreidml-{Guid.NewGuid():N}.idml");
        try
        {
            var document = IdmlDocument.Open(TestFiles.Article);

            document.Save(path);

            Assert.Equal(document.StoryPaths, IdmlDocument.Open(path).StoryPaths);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_library_reports_its_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", IdmlDocument.LibraryVersion);
    }
}
