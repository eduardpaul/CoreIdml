using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using CoreIdml.Pdf.Fonts;
using CoreIdml.Pdf.Imaging;
using static CoreIdml.Pdf.Writing.PdfObjectWriter;

namespace CoreIdml.Pdf.Writing;

/// <summary>
/// Assembles a PDF: pages with content streams, shared resources (fonts embedded as subsets on finish, images
/// written once per source, opacity states). All pages share one resource dictionary.
/// </summary>
internal sealed class PdfDocumentBuilder : IDisposable
{
    private readonly PdfObjectWriter writer;
    private readonly bool compress;
    private readonly int pagesObject;
    private readonly int resourcesObject;
    private readonly List<int> pageObjects = [];
    private readonly Dictionary<string, FontResource> fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageResource?> images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> graphicStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> graphicStateObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormResource?> forms = new(StringComparer.Ordinal);
    private readonly PdfPageImporter pageImporter;
    private readonly Dictionary<string, Task<PreparedImage?>> prefetched = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource prefetchCancellation = new();

    public PdfDocumentBuilder(Stream output, bool compress)
    {
        writer = new PdfObjectWriter(output);
        this.compress = compress;
        pagesObject = writer.Reserve();
        resourcesObject = writer.Reserve();
        pageImporter = new PdfPageImporter(writer, compress);
    }

    /// <summary>A page of a placed PDF as a Form XObject (null when it cannot be read). Written once per page and crop.</summary>
    public FormResource? PlacedPdf(string path, int pageNumber, string crop, out string? error)
    {
        error = null;
        var key = path + "|" + pageNumber.ToString(CultureInfo.InvariantCulture) + "|" + crop;
        if (forms.TryGetValue(key, out var cached))
        {
            return cached;
        }
        FormResource? form = null;
        try
        {
            form = pageImporter.Import(path, pageNumber, crop, "Fm" + (forms.Count + 1).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException or IdmlException)
        {
            error = exception.Message;
        }
        forms[key] = form;
        return form;
    }

    public int PageCount => pageObjects.Count;

    public void AddPage(double width, double height, ContentBuilder content)
    {
        var page = writer.Reserve();
        var contents = writer.Reserve();
        writer.WriteStream(contents, string.Empty, content.ToBytes(), compress);
        writer.WriteObject(page, $"<</Type /Page /Parent {Ref(pagesObject)} /MediaBox [0 0 {Number(width)} {Number(height)}] /Resources {Ref(resourcesObject)} /Contents {Ref(contents)}>>");
        pageObjects.Add(page);
    }

    /// <summary>The resource name for a font face, registering it on first use.</summary>
    public FontResource Font(FontFace face)
    {
        if (!fonts.TryGetValue(face.Key, out var resource))
        {
            resource = new FontResource(face, "F" + (fonts.Count + 1).ToString(CultureInfo.InvariantCulture), writer.Reserve());
            fonts[face.Key] = resource;
        }
        return resource;
    }

    /// <summary>The resource name for an ExtGState with the given fill/stroke opacity.</summary>
    public string Opacity(double fill, double stroke)
    {
        var key = Number(fill) + "/" + Number(stroke);
        if (!graphicStates.TryGetValue(key, out var name))
        {
            name = "GS" + (graphicStates.Count + 1).ToString(CultureInfo.InvariantCulture);
            var number = writer.Reserve();
            writer.WriteObject(number, $"<</Type /ExtGState /ca {Number(fill)} /CA {Number(stroke)}>>");
            graphicStates[key] = name;
            graphicStateObjects[name] = number;
        }
        return name;
    }

    /// <summary>
    /// Starts decoding and compressing the PNG images at <paramref name="paths"/> on the thread pool, so pages can be
    /// composed meanwhile and several large images use several cores. <see cref="Image"/> still writes every image
    /// when it is first drawn, in drawing order, so the PDF is the same as without prefetching.
    /// </summary>
    public void PrefetchPngs(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!images.ContainsKey(path) && !prefetched.ContainsKey(path))
            {
                var token = prefetchCancellation.Token;
                prefetched[path] = Start(() => ReadFile(path, Prefetch), token);
            }
        }
    }

    /// <summary>
    /// For <see cref="PdfExportOptions.MaxImageDpi"/>: prepares each image of <paramref name="limits"/> (path → the
    /// largest pixel size it needs) on the thread pool, as <see cref="PrefetchPngs"/> does. Images more than 10% larger
    /// are downsampled: JPEGs re-encoded at <paramref name="jpegQuality"/>, PNGs kept lossless; images that cannot be
    /// downsampled (CMYK JPEGs, unsupported JPEG variants) are written as they are.
    /// </summary>
    public void PrefetchLimitedImages(IReadOnlyDictionary<string, (int Width, int Height)> limits, int jpegQuality)
    {
        foreach (var (path, limit) in limits)
        {
            if (!images.ContainsKey(path) && !prefetched.ContainsKey(path))
            {
                var token = prefetchCancellation.Token;
                prefetched[path] = Start(() => ReadFile(path, (buffer, length) => PrepareLimited(buffer, length, limit, jpegQuality, token)), token);
            }
        }
    }

    /// <summary>
    /// Whether images are prepared on the thread pool. Not on WebAssembly (browser, WASI), whose builds run on one
    /// thread: there a queued pool task never runs while <see cref="Image"/> waits for it (the export hung).
    /// </summary>
    private static bool UseThreadPool { get; } = !OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi();

    /// <summary>Starts preparing an image on the thread pool; on WebAssembly, only creates the task (see <see cref="Result"/>).</summary>
    private static Task<PreparedImage?> Start(Func<PreparedImage?> work, CancellationToken token) =>
        UseThreadPool ? Task.Run(work, token) : new Task<PreparedImage?>(work, token);

    /// <summary>A prepared image; a task never started (WebAssembly) runs here, when the image is first drawn.</summary>
    private static PreparedImage? Result(Task<PreparedImage?> task)
    {
        if (task.Status == TaskStatus.Created)
        {
            task.RunSynchronously();
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>An image XObject for a JPEG or PNG file (null when it cannot be read). Written once per path.</summary>
    public ImageResource? Image(string path)
    {
        if (images.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var name = "Im" + (images.Count + 1).ToString(CultureInfo.InvariantCulture);
        ImageResource? resource = null;
        try
        {
            // The object number is taken once the image is known to be readable (prefetched or not), so an image that
            // fails leaves no object behind.
            if (prefetched.Remove(path, out var task) && Result(task) is { } prepared)
            {
                prepared.Error?.Throw();
                var number = writer.Reserve();
                resource = prepared.Jpeg is { } jpeg ? WriteJpeg(number, jpeg, name) : WritePng(number, prepared.Png!, name);
                // Placement geometry uses the source's pixel size, downsampled or not.
                resource = resource with { Width = prepared.SourceWidth, Height = prepared.SourceHeight };
            }
            else
            {
                resource = ReadFile(path, data => WriteImage(data, name));
            }
        }
        // The decoders report every damaged or unsupported file as InvalidDataException.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            resource = null;
        }
        images[path] = resource;
        return resource;
    }

    /// <summary>A prefetched PNG encoded, or the error decoding it; null when the file is not a PNG.</summary>
    private static PreparedImage? Prefetch(ReadOnlySpan<byte> data) => PngCodec.IsPng(data) ? PreparePng(data, limit: null) : null;

    /// <summary>
    /// An image downsampled to <paramref name="limit"/> when it is more than 10% larger (PNGs are always encoded, as
    /// prefetched); null to write the file as it is: a JPEG within the limit, CMYK or not decodable, or not a JPEG or PNG.
    /// </summary>
    private static PreparedImage? PrepareLimited(byte[] buffer, int length, (int Width, int Height) limit, int quality, CancellationToken cancellation)
    {
        var data = buffer.AsSpan(0, length);
        if (PngCodec.IsPng(data))
        {
            return PreparePng(data, limit);
        }
        if (data.Length <= 3 || data[0] != 0xFF || data[1] != 0xD8
            || JpegInfo.Read(data) is not { Components: 1 or 3, BitsPerComponent: 8 } info
            || DownscaleFactor(info.Width, info.Height, limit) is not (var factor and < 0.9))
        {
            return null;
        }
        var width = (int)Math.Round(info.Width * factor);
        var height = (int)Math.Round(info.Height * factor);
        RasterImage raster;
        try
        {
            // Decoded at the smallest DCT scale still at least that large, then area-averaged to it.
            raster = JpegDecoder.Decode(buffer, length, JpegDecoder.ScaleFor(info.Width, info.Height, width, height));
        }
        catch (InvalidDataException)
        {
            return null; // An unusual or damaged JPEG: keep the original.
        }
        // A cancelled or failed export stops work already started between steps (the result is not needed).
        cancellation.ThrowIfCancellationRequested();
        var small = ImageScaler.Downscale(raster, width, height);
        return new PreparedImage(null, new EncodedJpeg(small.Width, small.Height, small.Channels, JpegEncoder.Encode(small, quality)), info.Width, info.Height, null);
    }

    /// <summary>A PNG decoded (downsampled to <paramref name="limit"/> when more than 10% larger) and compressed, or the error.</summary>
    private static PreparedImage PreparePng(ReadOnlySpan<byte> data, (int Width, int Height)? limit)
    {
        try
        {
            var raster = PngCodec.Decode(data);
            var small = limit is { } size && DownscaleFactor(raster.Width, raster.Height, size) is var factor and < 0.9
                ? ImageScaler.Downscale(raster, (int)Math.Round(raster.Width * factor), (int)Math.Round(raster.Height * factor))
                : raster;
            return new PreparedImage(EncodeRaster(small), null, raster.Width, raster.Height, null);
        }
        catch (Exception exception)
        {
            // Not swallowed: Image() rethrows it where the image is written, as without prefetching.
            return new PreparedImage(null, null, 0, 0, ExceptionDispatchInfo.Capture(exception));
        }
    }

    /// <summary>The factor (at most 1) that brings an image down to the size it needs, keeping its proportions.</summary>
    private static double DownscaleFactor(int width, int height, (int Width, int Height) limit) =>
        Math.Min(1, Math.Max((double)limit.Width / width, (double)limit.Height / height));

    /// <summary>Reads a file into a pooled buffer (photos are several MB each) and hands its bytes to <paramref name="use"/>.</summary>
    private static T ReadFile<T>(string path, Func<ReadOnlySpan<byte>, T> use) => ReadFile(path, (buffer, length) => use(buffer.AsSpan(0, length)));

    /// <summary>Reads a file into a pooled buffer and hands it to <paramref name="use"/> with the file's length.</summary>
    private static T ReadFile<T>(string path, Func<byte[], int, T> use)
    {
        using var file = File.OpenRead(path);
        if (file.Length > Array.MaxLength)
        {
            throw new IOException($"{path} is too large.");
        }
        var length = (int)file.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            file.ReadExactly(buffer, 0, length);
            return use(buffer, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes a JPEG as it is, or a PNG encoded; the object number is reserved once the file is known to be usable.</summary>
    private ImageResource WriteImage(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var info = JpegInfo.Read(data) ?? throw new InvalidDataException("Unreadable JPEG.");
            // DCTDecode takes 8-bit gray, RGB (YCbCr) or CMYK data only.
            var colorSpace = info.Components switch
            {
                1 => "/DeviceGray",
                3 => "/DeviceRGB",
                4 => "/DeviceCMYK",
                _ => throw new InvalidDataException($"JPEGs with {info.Components} components are not supported."),
            };
            if (info.BitsPerComponent != 8)
            {
                throw new InvalidDataException($"{info.BitsPerComponent}-bit JPEGs are not supported.");
            }
            // Adobe CMYK JPEGs store inverted values.
            var decode = info.Components == 4 && info.AdobeMarker ? " /Decode [1 0 1 0 1 0 1 0]" : string.Empty;
            var number = writer.Reserve();
            writer.WriteStream(number, $"/Type /XObject /Subtype /Image /Width {info.Width} /Height {info.Height} /ColorSpace {colorSpace} /BitsPerComponent {info.BitsPerComponent}{decode}", data, compress: false, filter: "/DCTDecode");
            return new ImageResource(name, number, info.Width, info.Height);
        }
        if (PngCodec.IsPng(data))
        {
            var png = EncodePng(data);
            return WritePng(writer.Reserve(), png, name);
        }
        throw new InvalidDataException("Only JPEG and PNG images are supported.");
    }

    /// <summary>Decodes a PNG and compresses its colour samples and, unless fully opaque, its alpha (thread-safe).</summary>
    private static EncodedPng EncodePng(ReadOnlySpan<byte> data) => EncodeRaster(PngCodec.Decode(data));

    /// <summary>Compresses decoded samples: colour, and alpha unless fully opaque.</summary>
    private static EncodedPng EncodeRaster(RasterImage raster)
    {
        var color = raster.Channels is 1 or 2 ? 1 : 3;
        var hasAlpha = raster.Channels is 2 or 4;
        // Without alpha the decoded samples are the image data as is; with alpha, split colour and alpha.
        var pixels = raster.Pixels;
        byte[]? alpha = null;
        var opaque = true;
        if (hasAlpha)
        {
            var count = raster.Width * raster.Height;
            pixels = new byte[count * color];
            alpha = new byte[count];
            var source = raster.Pixels.AsSpan();
            for (var i = 0; i < count; i++)
            {
                var pixel = source.Slice(i * raster.Channels, raster.Channels);
                pixel[..color].CopyTo(pixels.AsSpan(i * color, color));
                alpha[i] = pixel[color];
                opaque &= pixel[color] == 255;
            }
        }
        // Alpha first: it was always compressed (and written) before the colour samples.
        var mask = alpha is not null && !opaque ? Compress(alpha) : null;
        return new EncodedPng(raster.Width, raster.Height, color == 1 ? "/DeviceGray" : "/DeviceRGB", Compress(pixels), mask);
    }

    private ImageResource WritePng(int number, EncodedPng png, string name)
    {
        using var pixels = png.Pixels;
        using var alpha = png.Alpha;
        var smask = string.Empty;
        if (alpha is not null)
        {
            var mask = writer.Reserve();
            writer.WriteCompressedStream(mask, $"/Type /XObject /Subtype /Image /Width {png.Width} /Height {png.Height} /ColorSpace /DeviceGray /BitsPerComponent 8", alpha);
            smask = " /SMask " + Ref(mask);
        }
        writer.WriteCompressedStream(number, $"/Type /XObject /Subtype /Image /Width {png.Width} /Height {png.Height} /ColorSpace {png.ColorSpace} /BitsPerComponent 8{smask}", pixels);
        return new ImageResource(name, number, png.Width, png.Height);
    }

    private ImageResource WriteJpeg(int number, EncodedJpeg jpeg, string name)
    {
        writer.WriteStream(number, $"/Type /XObject /Subtype /Image /Width {jpeg.Width} /Height {jpeg.Height} /ColorSpace {(jpeg.Channels == 1 ? "/DeviceGray" : "/DeviceRGB")} /BitsPerComponent 8", jpeg.Data, compress: false, filter: "/DCTDecode");
        return new ImageResource(name, number, jpeg.Width, jpeg.Height);
    }

    /// <summary>A PNG ready to write: its size, colour space and compressed samples.</summary>
    private sealed record EncodedPng(int Width, int Height, string ColorSpace, MemoryStream Pixels, MemoryStream? Alpha);

    /// <summary>A re-encoded (downsampled) JPEG: its size, gray or RGB, and the file.</summary>
    private sealed record EncodedJpeg(int Width, int Height, int Channels, byte[] Data);

    /// <summary>
    /// An image prepared on the thread pool (a PNG or a downsampled JPEG, with the source's pixel size), or the error
    /// preparing it.
    /// </summary>
    private sealed record PreparedImage(EncodedPng? Png, EncodedJpeg? Jpeg, int SourceWidth, int SourceHeight, ExceptionDispatchInfo? Error);

    public void Finish(string? title)
    {
        foreach (var font in fonts.Values)
        {
            WriteFont(font);
        }
        var resources = new StringBuilder("<</ProcSet [/PDF /Text /ImageB /ImageC]");
        if (fonts.Count > 0)
        {
            resources.Append(" /Font <<");
            foreach (var font in fonts.Values)
            {
                resources.Append(CultureInfo.InvariantCulture, $" /{font.Name} {Ref(font.ObjectNumber)}");
            }
            resources.Append(">>");
        }
        var usedImages = images.Values.OfType<ImageResource>().ToList();
        var usedForms = forms.Values.OfType<FormResource>().ToList();
        if (usedImages.Count > 0 || usedForms.Count > 0)
        {
            resources.Append(" /XObject <<");
            foreach (var image in usedImages)
            {
                resources.Append(CultureInfo.InvariantCulture, $" /{image.Name} {Ref(image.ObjectNumber)}");
            }
            foreach (var form in usedForms)
            {
                resources.Append(CultureInfo.InvariantCulture, $" /{form.Name} {Ref(form.ObjectNumber)}");
            }
            resources.Append(">>");
        }
        if (graphicStateObjects.Count > 0)
        {
            resources.Append(" /ExtGState <<");
            foreach (var (name, number) in graphicStateObjects)
            {
                resources.Append(CultureInfo.InvariantCulture, $" /{name} {Ref(number)}");
            }
            resources.Append(">>");
        }
        resources.Append(">>");
        writer.WriteObject(resourcesObject, resources.ToString());
        writer.WriteObject(pagesObject, $"<</Type /Pages /Count {pageObjects.Count} /Kids [{string.Join(' ', pageObjects.Select(Ref))}]>>");
        var catalog = writer.Reserve();
        writer.WriteObject(catalog, $"<</Type /Catalog /Pages {Ref(pagesObject)}>>");
        var info = writer.Reserve();
        var titleEntry = string.IsNullOrEmpty(title) ? string.Empty : " /Title " + TextString(title);
        writer.WriteObject(info, $"<</Producer {TextString("CoreIdml " + IdmlDocument.LibraryVersion)}{titleEntry}>>");
        writer.Finish(catalog, info);
        Dispose();
    }

    public void Dispose()
    {
        // Images prefetched but never drawn (e.g. the export failed): skip the ones not started yet.
        prefetchCancellation.Cancel();
        prefetched.Clear();
        pageImporter.Dispose();
    }

    private void WriteFont(FontResource font)
    {
        var face = font.Face;
        if (face.Standard is not null)
        {
            var encoding = face.StandardName is "Symbol" or "ZapfDingbats" ? string.Empty : " /Encoding /WinAnsiEncoding";
            writer.WriteObject(font.ObjectNumber, $"<</Type /Font /Subtype /Type1 /BaseFont /{face.StandardName}{encoding}>>");
            return;
        }
        var otf = face.OpenType!;
        var glyphs = font.Glyphs;
        var tag = SubsetTag(otf.PostScriptName, glyphs.Keys);
        var baseFont = Name((otf.IsCff ? string.Empty : tag + "+") + SafePostScript(otf.PostScriptName));
        var scale = 1000.0 / otf.UnitsPerEm;

        var fontFile = writer.Reserve();
        if (otf.IsCff)
        {
            var whole = otf.RawData;
            if (otf.FaceIndex != 0 || OpenTypeFont.FaceCount(whole) > 1)
            {
                throw new InvalidDataException($"CFF fonts inside collections cannot be embedded ({otf.FamilyName}).");
            }
            writer.WriteStream(fontFile, "/Subtype /OpenType", whole, compress: true);
        }
        else
        {
            // A licence that forbids subsetting gets the whole font (only possible outside collections).
            var subset = face.SubsettingForbidden && OpenTypeFont.FaceCount(otf.RawData) == 1 ? otf.RawData : FontSubsetter.Subset(otf, glyphs.Keys);
            writer.WriteStream(fontFile, $"/Length1 {subset.Length}", subset, compress: true);
        }

        var flags = 4; // symbolic: glyphs are addressed by id, not by a standard encoding
        if (otf.ItalicAngle != 0 || otf.IsItalic)
        {
            flags |= 64;
        }
        var descriptor = writer.Reserve();
        var bbox = otf.BBox;
        writer.WriteObject(descriptor, string.Create(CultureInfo.InvariantCulture,
            $"<</Type /FontDescriptor /FontName {baseFont} /Flags {flags} /FontBBox [{Number(bbox.XMin * scale)} {Number(bbox.YMin * scale)} {Number(bbox.XMax * scale)} {Number(bbox.YMax * scale)}] /ItalicAngle {Number(otf.ItalicAngle)} /Ascent {Number(otf.TypoAscender * scale)} /Descent {Number(otf.TypoDescender * scale)} /CapHeight {Number(otf.CapHeight * scale)} /StemV {(otf.WeightClass >= 600 ? 120 : 80)} {(otf.IsCff ? "/FontFile3" : "/FontFile2")} {Ref(fontFile)}>>"));

        var widths = new StringBuilder();
        foreach (var glyph in glyphs.Keys)
        {
            widths.Append(CultureInfo.InvariantCulture, $"{glyph} [{Number(otf.Advance(glyph) * scale)}] ");
        }
        var cid = writer.Reserve();
        var cidMap = otf.IsCff ? string.Empty : " /CIDToGIDMap /Identity";
        writer.WriteObject(cid, $"<</Type /Font /Subtype {(otf.IsCff ? "/CIDFontType0" : "/CIDFontType2")} /BaseFont {baseFont} /CIDSystemInfo <</Registry (Adobe) /Ordering (Identity) /Supplement 0>> /FontDescriptor {Ref(descriptor)} /DW 0 /W [{widths.ToString().TrimEnd()}]{cidMap}>>");

        var toUnicode = writer.Reserve();
        writer.WriteStream(toUnicode, string.Empty, Encoding.ASCII.GetBytes(ToUnicodeCMap(glyphs)), compress);
        writer.WriteObject(font.ObjectNumber, $"<</Type /Font /Subtype /Type0 /BaseFont {baseFont} /Encoding /Identity-H /DescendantFonts [{Ref(cid)}] /ToUnicode {Ref(toUnicode)}>>");
    }

    private static string SafePostScript(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name)
        {
            if (c is > ' ' and < (char)0x7F and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%'))
            {
                builder.Append(c);
            }
        }
        return builder.Length == 0 ? "Font" : builder.ToString();
    }

    /// <summary>
    /// Six uppercase letters derived from the font and its glyph set (deterministic). Uses FNV-1a, not a
    /// System.Security.Cryptography hash: on the linux-bionic Native AOT build (Android via Flutter) .NET's crypto
    /// needs OpenSSL, which Android does not have, and the runtime aborts the process instead of throwing.
    /// </summary>
    private static string SubsetTag(string postScript, IEnumerable<ushort> glyphs)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        void Mix(byte value)
        {
            hash ^= value;
            hash *= prime;
        }
        foreach (var value in Encoding.UTF8.GetBytes(postScript))
        {
            Mix(value);
        }
        foreach (var glyph in glyphs)
        {
            Mix((byte)(glyph >> 8));
            Mix((byte)glyph);
        }
        var tag = new char[6];
        for (var i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + (hash % 26));
            hash /= 26;
        }
        return new string(tag);
    }

    private static string ToUnicodeCMap(SortedDictionary<ushort, string> glyphs)
    {
        var builder = new StringBuilder();
        builder.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo <</Registry (Adobe) /Ordering (UCS) /Supplement 0>> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = glyphs.Where(pair => pair.Value.Length > 0).ToList();
        for (var start = 0; start < entries.Count; start += 100)
        {
            var chunk = entries.Skip(start).Take(100).ToList();
            builder.Append(CultureInfo.InvariantCulture, $"{chunk.Count} beginbfchar\n");
            foreach (var (glyph, text) in chunk)
            {
                builder.Append(CultureInfo.InvariantCulture, $"<{glyph:X4}> <");
                foreach (var b in Encoding.BigEndianUnicode.GetBytes(text))
                {
                    builder.Append(CultureInfo.InvariantCulture, $"{b:X2}");
                }
                builder.Append(">\n");
            }
            builder.Append("endbfchar\n");
        }
        builder.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return builder.ToString();
    }
}

/// <summary>A font used in the document, with the glyphs used (glyph id → text it stands for).</summary>
internal sealed class FontResource(FontFace face, string name, int objectNumber)
{
    public FontFace Face { get; } = face;

    public string Name { get; } = name;

    public int ObjectNumber { get; } = objectNumber;

    public SortedDictionary<ushort, string> Glyphs { get; } = [];

    public void Use(ushort glyph, string text) => Glyphs.TryAdd(glyph, text);

    /// <summary>The string operand for glyph codes (2-byte hex for embedded fonts, 1-byte for standard fonts).</summary>
    public string Hex(IEnumerable<ushort> glyphs)
    {
        var builder = new StringBuilder("<");
        foreach (var glyph in glyphs)
        {
            builder.Append(Face.IsStandard
                ? ((byte)glyph).ToString("X2", CultureInfo.InvariantCulture)
                : glyph.ToString("X4", CultureInfo.InvariantCulture));
        }
        return builder.Append('>').ToString();
    }
}

internal sealed record ImageResource(string Name, int ObjectNumber, int Width, int Height);

/// <summary>JPEG header facts the PDF needs.</summary>
internal readonly record struct JpegInfo(int Width, int Height, int Components, int BitsPerComponent, bool AdobeMarker)
{
    public static JpegInfo? Read(ReadOnlySpan<byte> data)
    {
        var adobe = false;
        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }
            var marker = data[offset + 1];
            if (marker is 0xFF or 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                offset += marker == 0xFF ? 1 : 2;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var segment = data.Slice(offset + 4, Math.Max(0, Math.Min(length - 2, data.Length - offset - 4)));
            if (marker == 0xEE && segment.Length >= 5 && segment[..5].SequenceEqual("Adobe"u8))
            {
                adobe = true;
            }
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && segment.Length >= 6)
            {
                return new JpegInfo(BinaryPrimitives.ReadUInt16BigEndian(segment[3..]), BinaryPrimitives.ReadUInt16BigEndian(segment[1..]), segment[5], segment[0], adobe);
            }
            if (marker == 0xDA)
            {
                break;
            }
            offset += 2 + length;
        }
        return null;
    }
}
