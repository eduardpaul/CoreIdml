using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CoreIdml.Cli.Rendering;

/// <summary>
/// Rasterizes PDF pages with PDFium (the bblanchon.PDFium native packages, Apache-2.0/BSD; CLI only, so the libraries
/// themselves stay free of native dependencies) and encodes them as PNG. PDFium is not thread-safe: calls are serialized.
/// </summary>
internal static partial class PageRasterizer
{
    private const string Library = "pdfium";
    private static readonly Lock Gate = new();
    private static bool initialized;

    /// <summary>A rendered page: PNG bytes and pixel size.</summary>
    public sealed record RenderedPage(byte[] Png, int Width, int Height);

    /// <summary>True when the PDFium library can be loaded (it ships next to coreidml).</summary>
    public static bool IsAvailable(out string? reason)
    {
        try
        {
            lock (Gate)
            {
                Initialize();
            }
            reason = null;
            return true;
        }
        catch (DllNotFoundException exception)
        {
            reason = exception.Message;
            return false;
        }
        catch (EntryPointNotFoundException exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    /// <summary>Renders page <paramref name="pageIndex"/> (0-based) of a PDF at <paramref name="dpi"/>, on white.</summary>
    public static unsafe RenderedPage Render(byte[] pdf, int pageIndex, double dpi)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        lock (Gate)
        {
            Initialize();
            fixed (byte* data = pdf)
            {
                var document = FPDF_LoadMemDocument(data, pdf.Length, null);
                if (document == 0)
                {
                    throw new InvalidOperationException($"PDFium could not open the PDF (error {FPDF_GetLastError()}).");
                }
                try
                {
                    var page = FPDF_LoadPage(document, pageIndex);
                    if (page == 0)
                    {
                        throw new InvalidOperationException($"PDFium could not load page {pageIndex} (the PDF has {FPDF_GetPageCount(document)} pages).");
                    }
                    try
                    {
                        var width = Math.Max(1, (int)Math.Round(FPDF_GetPageWidth(page) * dpi / 72));
                        var height = Math.Max(1, (int)Math.Round(FPDF_GetPageHeight(page) * dpi / 72));
                        var bitmap = FPDFBitmap_Create(width, height, 0);
                        if (bitmap == 0)
                        {
                            throw new InvalidOperationException($"PDFium could not allocate a {width}x{height} bitmap.");
                        }
                        try
                        {
                            if (FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF) == 0)
                            {
                                throw new InvalidOperationException("PDFium could not clear the bitmap.");
                            }
                            FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, RenderAnnotations);
                            var stride = FPDFBitmap_GetStride(bitmap);
                            var pixels = new ReadOnlySpan<byte>((void*)FPDFBitmap_GetBuffer(bitmap), stride * height);
                            return new RenderedPage(EncodePng(pixels, width, height, stride), width, height);
                        }
                        finally
                        {
                            FPDFBitmap_Destroy(bitmap);
                        }
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }
                }
                finally
                {
                    FPDF_CloseDocument(document);
                }
            }
        }
    }

    private const int RenderAnnotations = 0x01;

    private static void Initialize()
    {
        if (!initialized)
        {
            FPDF_InitLibrary();
            initialized = true;
        }
    }

    /// <summary>A PNG of BGRx pixels (PDFium's layout) as 8-bit RGB, each row filtered with "Up".</summary>
    internal static byte[] EncodePng(ReadOnlySpan<byte> bgrx, int width, int height, int stride)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var previous = new byte[width * 3];
            var current = new byte[width * 3];
            var row = new byte[1 + (width * 3)];
            row[0] = 2; // Up
            for (var y = 0; y < height; y++)
            {
                var source = bgrx.Slice(y * stride, width * 4);
                for (var x = 0; x < width; x++)
                {
                    current[(x * 3) + 0] = source[(x * 4) + 2];
                    current[(x * 3) + 1] = source[(x * 4) + 1];
                    current[(x * 3) + 2] = source[(x * 4) + 0];
                }
                for (var i = 0; i < current.Length; i++)
                {
                    row[1 + i] = (byte)(current[i] - previous[i]);
                }
                zlib.Write(row);
                (previous, current) = (current, previous);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }
    }

    [LibraryImport(Library)]
    private static partial void FPDF_InitLibrary();

    [LibraryImport(Library)]
    private static unsafe partial nint FPDF_LoadMemDocument(byte* data, int size, byte* password);

    [LibraryImport(Library)]
    private static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    private static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    private static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    private static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    private static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    private static partial double FPDF_GetPageWidth(nint page);

    [LibraryImport(Library)]
    private static partial double FPDF_GetPageHeight(nint page);

    [LibraryImport(Library)]
    private static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(Library)]
    private static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    private static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Library)]
    private static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Library)]
    private static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Library)]
    private static partial void FPDFBitmap_Destroy(nint bitmap);
}
