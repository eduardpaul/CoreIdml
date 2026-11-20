using CoreIdml.Pdf.Fonts;
using CoreIdml.Pdf.Imaging;

namespace CoreIdml.Pdf.Tests;

internal static class TestSupport
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string FontDirectory => TestFiles.Fonts;

    public static string TestData(string name) => TestFiles.Fixture(name);

    /// <summary>Reproducible fonts: only the bundled Liberation faces, InDesign's defaults mapped onto them.</summary>
    public static PdfFontOptions Fonts()
    {
        var options = new PdfFontOptions { UseSystemFonts = false };
        options.FontDirectories.Add(FontDirectory);
        options.Substitutions["Minion Pro"] = "Liberation Serif";
        options.Substitutions["Myriad Pro"] = "Liberation Sans";
        return options;
    }

    public static PdfExportOptions ExportOptions() => new() { Fonts = Fonts() };

    public static byte[] Export(IdmlDocument document, PdfExportOptions? options = null)
    {
        using var stream = new MemoryStream();
        IdmlPdf.Export(document, stream, options ?? ExportOptions());
        return stream.ToArray();
    }

    public static FontLibrary FontLibrary() => new(Fonts());

    /// <summary>A scratch folder under the test output, emptied first.</summary>
    public static string OutputDirectory(string name)
    {
        var path = Path.Combine(BaseDirectory, "out", name);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A small RGB JPEG-free test image (PNG gradient) written to <paramref name="path"/>.</summary>
    public static string WriteTestPng(string path, int width = 64, int height = 48)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                pixels[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[i + 2] = 128;
            }
        }
        File.WriteAllBytes(path, PngCodec.Encode(new RasterImage(width, height, 3, pixels)));
        return path;
    }
}
