using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace CoreIdml.Pdf.Imaging;

/// <summary>A decoded raster: 8 bits per sample, interleaved, top row first.</summary>
internal sealed record RasterImage(int Width, int Height, int Channels, byte[] Pixels)
{
    /// <summary>RGBA copy (gray and RGB expanded, alpha 255 when absent).</summary>
    public byte[] ToRgba()
    {
        if (Channels == 4)
        {
            return Pixels;
        }
        var result = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            switch (Channels)
            {
                case 1:
                    result[(4 * i) + 0] = result[(4 * i) + 1] = result[(4 * i) + 2] = Pixels[i];
                    result[(4 * i) + 3] = 255;
                    break;
                case 2:
                    result[(4 * i) + 0] = result[(4 * i) + 1] = result[(4 * i) + 2] = Pixels[2 * i];
                    result[(4 * i) + 3] = Pixels[(2 * i) + 1];
                    break;
                default:
                    result[(4 * i) + 0] = Pixels[3 * i];
                    result[(4 * i) + 1] = Pixels[(3 * i) + 1];
                    result[(4 * i) + 2] = Pixels[(3 * i) + 2];
                    result[(4 * i) + 3] = 255;
                    break;
            }
        }
        return result;
    }
}

/// <summary>
/// Minimal PNG decoder (all colour types, bit depths 1-16, Adam7 interlacing; samples reduced to 8 bits) and an
/// encoder for 8-bit gray/RGB/RGBA. BCL only (zlib through <see cref="ZLibStream"/>).
/// </summary>
internal static class PngCodec
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(Signature);

    public static RasterImage Decode(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data))
        {
            throw new InvalidDataException("Not a PNG file.");
        }
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        // The compressed stream is the concatenation of the IDAT chunks: find them first, then copy them once.
        var idatChunks = new List<(int Offset, int Length)>();
        var idatLength = 0;
        var offset = 8;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            var type = data.Slice(offset + 4, 4);
            if (length < 0 || offset + 12 + length > data.Length)
            {
                break;
            }
            var chunk = data.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                bitDepth = chunk[8];
                colorType = chunk[9];
                interlace = chunk[12];
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = chunk.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = chunk.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idatChunks.Add((offset + 8, length));
                idatLength += length;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
            offset += 12 + length;
        }
        if (width <= 0 || height <= 0 || (long)width * height > 1L << 28)
        {
            throw new InvalidDataException("Invalid PNG size.");
        }
        var samplesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("Unsupported PNG colour type."),
        };
        var idat = new byte[idatLength];
        var at = 0;
        foreach (var (chunkOffset, chunkLength) in idatChunks)
        {
            data.Slice(chunkOffset, chunkLength).CopyTo(idat.AsSpan(at));
            at += chunkLength;
        }

        // Samples at 8 bits, palette not yet expanded. Scanlines are inflated and unfiltered one row at a time.
        var samples = new byte[width * height * samplesPerPixel];
        var bitsPerPixel = samplesPerPixel * bitDepth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        using (var inflater = new ZLibStream(new MemoryStream(idat, writable: false), CompressionMode.Decompress))
        {
            var decoder = new ScanlineDecoder(inflater, samples, width, samplesPerPixel, bitDepth, bytesPerPixel, colorType == 3);
            if (interlace == 0)
            {
                decoder.DecodePass(width, height, 0, 0, 1, 1);
            }
            else
            {
                int[] startX = [0, 4, 0, 2, 0, 1, 0], startY = [0, 0, 4, 0, 2, 0, 1], stepX = [8, 8, 4, 4, 2, 2, 1], stepY = [8, 8, 8, 4, 4, 2, 2];
                for (var pass = 0; pass < 7; pass++)
                {
                    var passWidth = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
                    var passHeight = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
                    if (passWidth > 0 && passHeight > 0)
                    {
                        decoder.DecodePass(passWidth, passHeight, startX[pass], startY[pass], stepX[pass], stepY[pass]);
                    }
                }
            }
            // Read to the end, so damaged data after the last row is still reported.
            inflater.CopyTo(Stream.Null);
        }

        if (colorType == 3)
        {
            if (palette is null)
            {
                throw new InvalidDataException("PNG palette missing.");
            }
            var hasAlpha = transparency is { Length: > 0 };
            var channels = hasAlpha ? 4 : 3;
            var pixels = new byte[width * height * channels];
            for (var i = 0; i < width * height; i++)
            {
                var index = samples[i];
                for (var c = 0; c < 3; c++)
                {
                    pixels[(i * channels) + c] = (3 * index) + c < palette.Length ? palette[(3 * index) + c] : (byte)0;
                }
                if (hasAlpha)
                {
                    pixels[(i * channels) + 3] = index < transparency!.Length ? transparency[index] : (byte)255;
                }
            }
            return new RasterImage(width, height, channels, pixels);
        }
        if (transparency is not null && colorType is 0 or 2)
        {
            // A single transparent colour key: expand to an alpha channel.
            var channels = samplesPerPixel + 1;
            var pixels = new byte[width * height * channels];
            var key = new byte[samplesPerPixel];
            for (var c = 0; c < samplesPerPixel && (2 * c) + 1 < transparency.Length; c++)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2 * c));
                key[c] = bitDepth == 16 ? (byte)(value >> 8) : ScaleSample(value, bitDepth);
            }
            for (var i = 0; i < width * height; i++)
            {
                var transparent = true;
                for (var c = 0; c < samplesPerPixel; c++)
                {
                    var value = samples[(i * samplesPerPixel) + c];
                    pixels[(i * channels) + c] = value;
                    transparent &= value == key[c];
                }
                pixels[(i * channels) + samplesPerPixel] = transparent ? (byte)0 : (byte)255;
            }
            return new RasterImage(width, height, channels, pixels);
        }
        return new RasterImage(width, height, samplesPerPixel, samples);
    }

    private static byte ScaleSample(int value, int bitDepth) => bitDepth switch
    {
        1 => (byte)(value * 255),
        2 => (byte)(value * 85),
        4 => (byte)(value * 17),
        16 => (byte)(value >> 8),
        _ => (byte)value,
    };

    /// <summary>Reads filtered scanlines from the inflated stream into 8-bit samples (one image or one Adam7 pass).</summary>
    private sealed class ScanlineDecoder(Stream scanlines, byte[] samples, int imageWidth, int samplesPerPixel, int bitDepth, int bytesPerPixel, bool indexed)
    {
        public void DecodePass(int width, int height, int startX, int startY, int stepX, int stepY)
        {
            var stride = ((width * samplesPerPixel * bitDepth) + 7) / 8;
            // Filter type byte plus the row; previous row starts as zeros.
            var previous = new byte[stride];
            var current = new byte[stride + 1];
            var row = width * samplesPerPixel;
            var unpacked = bitDepth == 8 ? null : new byte[row];
            for (var y = 0; y < height; y++)
            {
                try
                {
                    scanlines.ReadExactly(current);
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException("PNG data is truncated.");
                }
                var line = current.AsSpan(1);
                Unfilter(current[0], line, previous, bytesPerPixel);
                ReadOnlySpan<byte> values = unpacked is null ? line : Unpack(line, unpacked);
                var target = startY + (y * stepY);
                if (stepX == 1)
                {
                    values.CopyTo(samples.AsSpan(target * imageWidth * samplesPerPixel, row));
                }
                else
                {
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = ((target * imageWidth) + startX + (x * stepX)) * samplesPerPixel;
                        values.Slice(x * samplesPerPixel, samplesPerPixel).CopyTo(samples.AsSpan(pixel, samplesPerPixel));
                    }
                }
                line.CopyTo(previous);
            }
        }

        /// <summary>Samples of a 1, 2, 4 or 16-bit row as 8-bit values (palette indices are not scaled).</summary>
        private Span<byte> Unpack(ReadOnlySpan<byte> line, byte[] output)
        {
            if (bitDepth == 16)
            {
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = line[2 * i];
                }
                return output;
            }
            var mask = (1 << bitDepth) - 1;
            for (var i = 0; i < output.Length; i++)
            {
                var bit = i * bitDepth;
                var sample = (line[bit / 8] >> (8 - bitDepth - (bit % 8))) & mask;
                output[i] = indexed ? (byte)sample : ScaleSample(sample, bitDepth);
            }
            return output;
        }
    }

    private static void Unfilter(byte filter, Span<byte> current, ReadOnlySpan<byte> previous, int bpp)
    {
        var start = Math.Min(bpp, current.Length);
        switch (filter)
        {
            case 1: // Sub
                for (var i = bpp; i < current.Length; i++)
                {
                    current[i] += current[i - bpp];
                }
                break;
            case 2: // Up
                var up = 0;
                for (; up <= current.Length - Vector<byte>.Count; up += Vector<byte>.Count)
                {
                    (new Vector<byte>(current[up..]) + new Vector<byte>(previous[up..])).CopyTo(current[up..]);
                }
                for (; up < current.Length; up++)
                {
                    current[up] += previous[up];
                }
                break;
            case 3: // Average
                for (var i = 0; i < start; i++)
                {
                    current[i] += (byte)(previous[i] / 2);
                }
                for (var i = bpp; i < current.Length; i++)
                {
                    current[i] += (byte)((current[i - bpp] + previous[i]) / 2);
                }
                break;
            case 4: // Paeth
                for (var i = 0; i < start; i++)
                {
                    current[i] += (byte)Paeth(0, previous[i], 0);
                }
                for (var i = bpp; i < current.Length; i++)
                {
                    // Paeth predictor with p = a + b - c folded in: |p - a| = |b - c|, |p - b| = |a - c|, |p - c| = |a + b - 2c|.
                    int a = current[i - bpp], b = previous[i], c = previous[i - bpp];
                    int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - (2 * c));
                    current[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Encodes 8-bit samples (1 = gray, 2 = gray+alpha, 3 = RGB, 4 = RGBA) as PNG.</summary>
    public static byte[] Encode(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var colorType = image.Channels switch
        {
            1 => 0,
            2 => 4,
            3 => 2,
            4 => 6,
            _ => throw new ArgumentException("Unsupported channel count.", nameof(image)),
        };
        var stride = image.Width * image.Channels;
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[stride + 1];
            for (var y = 0; y < image.Height; y++)
            {
                row[0] = 0;
                Array.Copy(image.Pixels, y * stride, row, 1, stride);
                zlib.Write(row);
            }
        }
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = (byte)colorType;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
