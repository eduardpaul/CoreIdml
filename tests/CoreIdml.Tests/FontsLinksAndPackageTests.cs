using System.IO.Compression;
using System.Text.Json.Nodes;
using CoreIdml.Dom;
using CoreIdml.Packaging;

namespace CoreIdml.Tests;

/// <summary>Fonts report, relinking from a folder and packaging, through the library and the MCP tools.</summary>
public class FontsLinksAndPackageTests
{
    /// <summary>A document in its own folder with two placed PNGs that live elsewhere (so their links break when moved).</summary>
    private static (string Path, string ImagesFolder) WithImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "idml-tests", Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "fotos");
        Directory.CreateDirectory(images);
        File.WriteAllBytes(Path.Combine(images, "uno.png"), TestFiles.Png(40, 30));
        File.WriteAllBytes(Path.Combine(images, "dos.png"), TestFiles.Png(30, 40));
        var path = Path.Combine(root, "doc.idml");
        using var document = IdmlDocument.Create();
        document.Pages[0].AddImage(new Rect(36, 36, 100, 75), Path.Combine(images, "uno.png"), name: "foto1");
        document.Pages[0].AddImage(new Rect(36, 200, 75, 100), Path.Combine(images, "dos.png"), name: "foto2");
        document.Pages[0].AddText(new Rect(200, 36, 200, 40), "Texto", document.Styles.AddParagraphStyle("Rara", new ParagraphFormat { FontFamily = "Fuente Inexistente" }));
        document.Save(path);
        return (path, images);
    }

    [Fact]
    public void Links_resolve_by_path_then_by_name_in_folders()
    {
        var (path, images) = WithImages();
        var moved = Path.Combine(Path.GetDirectoryName(images)!, "movidas");
        Directory.Move(images, moved);
        using var document = IdmlDocument.Open(path);

        var lost = DocumentPackager.ResolveLinks(document, path, []);
        var found = DocumentPackager.ResolveLinks(document, path, [moved]);

        Assert.All(lost, link => Assert.Null(link.Path));
        Assert.All(found, link => Assert.StartsWith(moved, link.Path, StringComparison.Ordinal));
        Assert.Equal("/srv/x.jpg", DocumentPackager.LocalPath("file:/srv/x.jpg"));
        Assert.Equal("C:/x y.jpg", DocumentPackager.LocalPath("file:///C:/x%20y.jpg"));
        Assert.Null(DocumentPackager.LocalPath("https://example.com/x.jpg"));
    }

    [Fact]
    public void Relink_folder_tool_fixes_missing_links()
    {
        var (path, images) = WithImages();
        var moved = Path.Combine(Path.GetDirectoryName(images)!, "nuevas");
        Directory.Move(images, moved);
        var client = new McpTestClient();

        var result = client.CallJson("idml_relink_folder", new JsonObject { ["path"] = path, ["folders"] = new JsonArray(moved), ["inPlace"] = true });
        var again = client.CallJson("idml_relink_folder", new JsonObject { ["path"] = path, ["folders"] = new JsonArray(moved), ["dryRun"] = true });

        Assert.Equal(2, result.GetProperty("relinked").GetArrayLength());
        Assert.Empty(result.GetProperty("missing").EnumerateArray());
        Assert.Empty(again.GetProperty("result").GetProperty("relinked").EnumerateArray());
        using var document = IdmlDocument.Open(path);
        Assert.All(DocumentPackager.ResolveLinks(document, path, []), link => Assert.NotNull(link.Path));
    }

    [Fact]
    public void Package_tool_zips_the_document_and_its_links()
    {
        var (path, _) = WithImages();
        var zipPath = TestFiles.TempPath(".zip");

        var result = new McpTestClient().CallJson("idml_package", new JsonObject { ["path"] = path, ["outPath"] = zipPath, ["fontDirectories"] = new JsonArray(TestFiles.Fonts) });

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("doc.idml", names);
        Assert.Contains("Links/uno.png", names);
        Assert.Contains("Links/dos.png", names);
        Assert.Equal(2, result.GetProperty("links").EnumerateObject().Count());
        Assert.Empty(result.GetProperty("missingLinks").EnumerateObject());
        using var packaged = IdmlDocument.Open(zip.GetEntry("doc.idml")!.Open());
        Assert.Equal(3, packaged.Pages[0].PageItems.Count);
    }

    [Fact]
    public void Fonts_tool_reports_substituted_fonts_with_pages()
    {
        var (path, _) = WithImages();

        var result = new McpTestClient().CallJson("idml_fonts", new JsonObject { ["path"] = path, ["fonts"] = new JsonArray(TestFiles.Fonts), ["useSystemFonts"] = false });

        var font = result.GetProperty("fonts").EnumerateArray().Single(entry => entry.GetProperty("family").GetString() == "Fuente Inexistente");
        Assert.Equal("substituted", font.GetProperty("status").GetString());
        var substitution = result.GetProperty("substitutions").EnumerateArray().Single(entry => entry.GetProperty("requestedFamily").GetString() == "Fuente Inexistente");
        Assert.Equal(["1"], substitution.GetProperty("pages").EnumerateArray().Select(page => page.GetString()));
        Assert.Single(substitution.GetProperty("stories").EnumerateArray());
    }
}
