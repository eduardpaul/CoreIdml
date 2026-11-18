using System.Buffers.Binary;

namespace CoreIdml.Dom;

/// <summary>Image file type as InDesign names it in links.</summary>
public sealed record ImageFormat(string TypeName)
{
    public static readonly ImageFormat Jpeg = new("$ID/JPEG");
    public static readonly ImageFormat Png = new("$ID/Portable Network Graphics (PNG)");
}

/// <summary>
/// An image to place: where InDesign will find it (the link) and its pixel size and resolution, which set its
/// size on the page. IDML links images, it does not embed them, so the file must be reachable from the machine
/// that opens the document.
/// </summary>
public sealed record ImageSource(string LinkUri, int PixelWidth, int PixelHeight, double DpiX, double DpiY, ImageFormat Format)
{
    /// <summary>File size in bytes (informational, written to the link).</summary>
    public long FileSize { get; init; }

    /// <summary>
    /// EXIF orientation of a JPEG (1 = upright; 6 = camera held upright, pixels stored sideways; 3, 8 and the mirrored
    /// 2, 4, 5, 7). Placing applies it in the image transform, as InDesign does when it places such a photo.
    /// </summary>
    public int Orientation { get; init; } = 1;

    /// <summary>True when the orientation swaps width and height (5–8).</summary>
    public bool IsSideways => Orientation is >= 5 and <= 8;

    /// <summary>Width on the page at 100% once the orientation is applied.</summary>
    public double UprightWidthPoints => IsSideways ? HeightPoints : WidthPoints;

    /// <summary>Height on the page at 100% once the orientation is applied.</summary>
    public double UprightHeightPoints => IsSideways ? WidthPoints : HeightPoints;

    /// <summary>Maps the stored image (0..<see cref="WidthPoints"/>, 0..<see cref="HeightPoints"/>) onto the upright image.</summary>
    public Matrix OrientationTransform
    {
        get
        {
            double w = WidthPoints, h = HeightPoints;
            return Orientation switch
            {
                2 => new Matrix(-1, 0, 0, 1, w, 0),
                3 => new Matrix(-1, 0, 0, -1, w, h),
                4 => new Matrix(1, 0, 0, -1, 0, h),
                5 => new Matrix(0, 1, 1, 0, 0, 0),
                6 => new Matrix(0, 1, -1, 0, h, 0),
                7 => new Matrix(0, -1, -1, 0, h, w),
                8 => new Matrix(0, -1, 1, 0, 0, w),
                _ => Matrix.Identity,
            };
        }
    }

    public double WidthPoints => PixelWidth * Units.PointsPerInch / DpiX;

    public double HeightPoints => PixelHeight * Units.PointsPerInch / DpiY;

    /// <summary>Reads size and resolution from a local JPEG or PNG; the link points at its absolute path.</summary>
    public static ImageSource FromFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        return FromStream(stream, ToLinkUri(fullPath)) with { FileSize = stream.Length };
    }

    /// <summary>Reads size and resolution from image bytes, linking to <paramref name="linkUri"/>.</summary>
    public static ImageSource FromBytes(ReadOnlySpan<byte> data, string linkUri)
    {
        var info = ImageHeader.Read(data) ?? throw new IdmlException(IdmlErrorKind.Unsupported, "Unsupported image: only JPEG and PNG headers can be read.");
        return new ImageSource(linkUri, info.Width, info.Height, info.DpiX, info.DpiY, info.Format) { FileSize = data.Length, Orientation = info.Orientation };
    }

    public static ImageSource FromStream(Stream stream, string linkUri)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // Headers sit at the start of the file; JPEG metadata (EXIF) can push SOF past 64 KB, so read up to 1 MB.
        var buffer = new byte[Math.Min(stream.CanSeek ? stream.Length - stream.Position : 1 << 20, 1 << 20)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return FromBytes(buffer.AsSpan(0, read), linkUri);
    }

    /// <summary>
    /// The link URI InDesign uses for a local path: <c>file:C:/dir/a.jpg</c> on Windows (as InDesign for Windows writes
    /// it), <c>file:/home/me/a.jpg</c> elsewhere; spaces and other reserved characters percent-encoded.
    /// </summary>
    public static string ToLinkUri(string path)
    {
        var uri = new Uri(Path.GetFullPath(path));
        return "file:" + uri.AbsolutePath;
    }

    private static readonly System.Buffers.SearchValues<char> SchemeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+.-");

    /// <summary>Keeps a value that is already a URI (<c>file:…</c>, <c>http://…</c>); converts a local path.</summary>
    public static string ToLinkUriOrKeep(string uriOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriOrPath);
        // A scheme of 2+ letters followed by ":" (so "C:\x" is a path, "file:/x" and "https://x" are URIs).
        var colon = uriOrPath.IndexOf(':', StringComparison.Ordinal);
        var isUri = colon >= 2 && uriOrPath.AsSpan(0, colon).IndexOfAnyExcept(SchemeCharacters) < 0;
        return isUri ? uriOrPath : ToLinkUri(uriOrPath);
    }
}

internal readonly record struct ImageHeader(int Width, int Height, double DpiX, double DpiY, ImageFormat Format, int Orientation = 1)
{
    public static ImageHeader? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ReadPng(data);
        }
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return ReadJpeg(data);
        }
        return null;
    }

    private static ImageHeader? ReadPng(ReadOnlySpan<byte> data)
    {
        var width = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(data[20..]);
        double dpiX = 72, dpiY = 72;
        // Chunks: length(4) type(4) data crc(4). pHYs: ppu x, ppu y, unit (1 = metre).
        var offset = 8;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            var type = data.Slice(offset + 4, 4);
            if (length < 0 || offset + 12 + length > data.Length)
            {
                break;
            }
            if (type.SequenceEqual("pHYs"u8) && length >= 9 && data[offset + 8 + 8] == 1)
            {
                // Pixels per metre lose precision (144 dpi is stored as 5669 ppm); round like InDesign displays it.
                dpiX = Math.Round(BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 8)..]) * 0.0254, 1);
                dpiY = Math.Round(BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 12)..]) * 0.0254, 1);
            }
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                break;
            }
            offset += 12 + length;
        }
        return new ImageHeader(width, height, Sane(dpiX), Sane(dpiY), ImageFormat.Png);
    }

    private static ImageHeader? ReadJpeg(ReadOnlySpan<byte> data)
    {
        double dpiX = 72, dpiY = 72;
        var orientation = 1;
        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }
            var marker = data[offset + 1];
            if (marker is 0xFF)
            {
                offset++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var segment = data.Slice(offset + 4, Math.Min(length - 2, data.Length - offset - 4));
            if (marker == 0xE0 && segment.Length >= 12 && segment[..5].SequenceEqual("JFIF\0"u8))
            {
                var unit = segment[7];
                var x = BinaryPrimitives.ReadUInt16BigEndian(segment[8..]);
                var y = BinaryPrimitives.ReadUInt16BigEndian(segment[10..]);
                if (unit == 1)
                {
                    (dpiX, dpiY) = (x, y);
                }
                else if (unit == 2)
                {
                    (dpiX, dpiY) = (x * 2.54, y * 2.54);
                }
            }
            if (marker == 0xE1 && segment.Length >= 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                orientation = ExifOrientation(segment[6..]);
            }
            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && segment.Length >= 5)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                return new ImageHeader(width, height, Sane(dpiX), Sane(dpiY), ImageFormat.Jpeg, orientation);
            }
            if (marker == 0xDA)
            {
                break;
            }
            offset += 2 + length;
        }
        return null;
    }

    /// <summary>The Orientation tag (0x0112) of IFD0 in an EXIF TIFF block; 1 when absent or unreadable.</summary>
    private static int ExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return 1;
        }
        var little = tiff[0] == (byte)'I';
        int U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        var ifd = little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]);
        if (ifd + 2 > (uint)tiff.Length)
        {
            return 1;
        }
        var count = U16(tiff[(int)ifd..]);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                break;
            }
            if (U16(tiff[entry..]) == 0x0112)
            {
                var value = U16(tiff[(entry + 8)..]);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }
        return 1;
    }

    private static double Sane(double dpi) => dpi is >= 1 and <= 100_000 ? dpi : 72;
}
