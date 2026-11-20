using CoreIdml.Pdf.Imaging;

namespace CoreIdml.Pdf.Tests;

/// <summary>Rasterizes PDF pages for the visual harness (pdf_oxide; test-only native dependency).</summary>
internal static class Rasterizer
{
    public static int PageCount(byte[] pdf)
    {
        using var document = PdfOxide.Core.PdfDocument.Open(pdf);
        return document.PageCount;
    }

    public static RasterImage Render(byte[] pdf, int pageIndex, int dpi)
    {
        using var document = PdfOxide.Core.PdfDocument.Open(pdf);
        var pixmap = document.RenderToRgba(pageIndex, dpi);
        return new RasterImage(pixmap.Width, pixmap.Height, 4, pixmap.Data.ToArray());
    }
}
