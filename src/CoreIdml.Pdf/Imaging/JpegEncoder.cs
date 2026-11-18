using System.Buffers.Binary;

namespace CoreIdml.Pdf.Imaging;

/// <summary>
/// Baseline JPEG encoder (gray or YCbCr 4:2:0, the standard Annex K tables scaled by quality) for downsampled photos.
/// </summary>
internal static class JpegEncoder
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    ];

    private static readonly byte[] LumaQuantization =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] ChromaQuantization =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // Annex K.3 Huffman tables: counts per length (1-16), then values.
    private static readonly byte[] LumaDcCounts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] LumaDcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] ChromaDcCounts = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] ChromaDcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] LumaAcCounts = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];

    private static readonly byte[] LumaAcValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    private static readonly byte[] ChromaAcCounts = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static readonly byte[] ChromaAcValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    /// <summary>Encodes gray (1 channel) or RGB (3 channels) samples; <paramref name="quality"/> 1-100.</summary>
    public static byte[] Encode(RasterImage image, int quality)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Channels is not (1 or 3))
        {
            throw new ArgumentException("Only gray and RGB images can be encoded.", nameof(image));
        }
        quality = Math.Clamp(quality, 1, 100);
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var luma = Scale(LumaQuantization, scale);
        var chroma = Scale(ChromaQuantization, scale);
        var color = image.Channels == 3;

        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        // JFIF APP0, so readers take the 3 components as YCbCr.
        Segment(output, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(output, 0xDB, [0, .. ZigZagOrder(luma), .. color ? (byte[])[1, .. ZigZagOrder(chroma)] : []]);
        var sampling = color ? (byte)0x22 : (byte)0x11;
        byte[] frame = color
            ? [8, (byte)(image.Height >> 8), (byte)image.Height, (byte)(image.Width >> 8), (byte)image.Width, 3, 1, sampling, 0, 2, 0x11, 1, 3, 0x11, 1]
            : [8, (byte)(image.Height >> 8), (byte)image.Height, (byte)(image.Width >> 8), (byte)image.Width, 1, 1, 0x11, 0];
        Segment(output, 0xC0, frame);
        Segment(output, 0xC4, [0x00, .. LumaDcCounts, .. LumaDcValues, 0x10, .. LumaAcCounts, .. LumaAcValues,
            .. color ? (byte[])[0x01, .. ChromaDcCounts, .. ChromaDcValues, 0x11, .. ChromaAcCounts, .. ChromaAcValues] : []]);
        Segment(output, 0xDA, color ? [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0] : [1, 1, 0x00, 0, 63, 0]);

        var lumaDc = Codes(LumaDcCounts, LumaDcValues);
        var lumaAc = Codes(LumaAcCounts, LumaAcValues);
        var chromaDc = Codes(ChromaDcCounts, ChromaDcValues);
        var chromaAc = Codes(ChromaAcCounts, ChromaAcValues);
        var bits = new BitWriter(output);
        var predictions = new int[3];
        var mcu = color ? 16 : 8;
        Span<float> block = stackalloc float[64];
        for (var y0 = 0; y0 < image.Height; y0 += mcu)
        {
            for (var x0 = 0; x0 < image.Width; x0 += mcu)
            {
                if (!color)
                {
                    Sample(image, x0, y0, 1, 0, block);
                    EncodeBlock(bits, block, luma, lumaDc, lumaAc, ref predictions[0]);
                    continue;
                }
                for (var by = 0; by < 2; by++)
                {
                    for (var bx = 0; bx < 2; bx++)
                    {
                        Sample(image, x0 + (bx * 8), y0 + (by * 8), 1, 0, block);
                        EncodeBlock(bits, block, luma, lumaDc, lumaAc, ref predictions[0]);
                    }
                }
                Sample(image, x0, y0, 2, 1, block);
                EncodeBlock(bits, block, chroma, chromaDc, chromaAc, ref predictions[1]);
                Sample(image, x0, y0, 2, 2, block);
                EncodeBlock(bits, block, chroma, chromaDc, chromaAc, ref predictions[2]);
            }
        }
        bits.Flush();
        output.Write([0xFF, 0xD9]);
        return output.ToArray();
    }

    /// <summary>An 8×8 block of Y (component 0), Cb (1) or Cr (2), averaging step×step pixels, edges repeated.</summary>
    private static void Sample(RasterImage image, int x0, int y0, int step, int component, Span<float> block)
    {
        var pixels = image.Pixels;
        var channels = image.Channels;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var sum = 0.0;
                for (var dy = 0; dy < step; dy++)
                {
                    for (var dx = 0; dx < step; dx++)
                    {
                        var px = Math.Min(x0 + (x * step) + dx, image.Width - 1);
                        var py = Math.Min(y0 + (y * step) + dy, image.Height - 1);
                        var at = ((py * image.Width) + px) * channels;
                        if (channels == 1)
                        {
                            sum += pixels[at];
                            continue;
                        }
                        double r = pixels[at], g = pixels[at + 1], b = pixels[at + 2];
                        sum += component switch
                        {
                            0 => (0.299 * r) + (0.587 * g) + (0.114 * b),
                            1 => (-0.168736 * r) - (0.331264 * g) + (0.5 * b) + 128,
                            _ => (0.5 * r) - (0.418688 * g) - (0.081312 * b) + 128,
                        };
                    }
                }
                block[(y * 8) + x] = (float)((sum / (step * step)) - 128);
            }
        }
    }

    private static readonly float[] Cosines = CreateCosines();

    private static float[] CreateCosines()
    {
        var table = new float[64];
        for (var u = 0; u < 8; u++)
        {
            for (var x = 0; x < 8; x++)
            {
                var scale = u == 0 ? 1 / Math.Sqrt(2) : 1;
                table[(u * 8) + x] = (float)(scale * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) / 2);
            }
        }
        return table;
    }

    private static void EncodeBlock(BitWriter bits, ReadOnlySpan<float> block, ushort[] quantization, (ushort Code, byte Length)[] dc, (ushort Code, byte Length)[] ac, ref int prediction)
    {
        // Forward DCT (rows, then columns), then quantize in zig-zag order.
        Span<float> temp = stackalloc float[64];
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;
                for (var x = 0; x < 8; x++)
                {
                    sum += Cosines[(u * 8) + x] * block[(y * 8) + x];
                }
                temp[(y * 8) + u] = sum;
            }
        }
        Span<int> coefficients = stackalloc int[64];
        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                var sum = 0f;
                for (var y = 0; y < 8; y++)
                {
                    sum += Cosines[(v * 8) + y] * temp[(y * 8) + u];
                }
                var index = (v * 8) + u;
                coefficients[index] = (int)Math.Round(sum / quantization[index]);
            }
        }
        var diff = coefficients[0] - prediction;
        prediction = coefficients[0];
        var size = Magnitude(diff);
        bits.Write(dc[size]);
        bits.Write(Amplitude(diff, size), size);
        var zeros = 0;
        for (var k = 1; k < 64; k++)
        {
            var value = coefficients[ZigZag[k]];
            if (value == 0)
            {
                zeros++;
                continue;
            }
            while (zeros >= 16)
            {
                bits.Write(ac[0xF0]);
                zeros -= 16;
            }
            size = Magnitude(value);
            bits.Write(ac[(zeros << 4) | size]);
            bits.Write(Amplitude(value, size), size);
            zeros = 0;
        }
        if (zeros > 0)
        {
            bits.Write(ac[0x00]);
        }
    }

    private static int Magnitude(int value)
    {
        var size = 0;
        for (var magnitude = Math.Abs(value); magnitude > 0; magnitude >>= 1)
        {
            size++;
        }
        return size;
    }

    private static int Amplitude(int value, int size) => value >= 0 ? value : value + (1 << size) - 1;

    private static ushort[] Scale(byte[] table, int scale) =>
        [.. table.Select(value => (ushort)Math.Clamp(((value * scale) + 50) / 100, 1, 255))];

    private static byte[] ZigZagOrder(ushort[] table) => [.. ZigZag.Select(index => (byte)table[index])];

    private static (ushort Code, byte Length)[] Codes(byte[] counts, byte[] values)
    {
        var codes = new (ushort, byte)[256];
        var code = 0;
        var k = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < counts[length - 1]; i++)
            {
                codes[values[k++]] = ((ushort)code++, (byte)length);
            }
            code <<= 1;
        }
        return codes;
    }

    private static void Segment(Stream output, byte marker, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = [0xFF, marker, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(payload.Length + 2));
        output.Write(header);
        output.Write(payload);
    }

    /// <summary>Entropy-coded bits, with 0xFF bytes stuffed and the last byte padded with ones.</summary>
    private sealed class BitWriter(Stream output)
    {
        private uint buffer;
        private int count;

        public void Write((ushort Code, byte Length) code) => Write(code.Code, code.Length);

        public void Write(int value, int length)
        {
            if (length == 0)
            {
                return;
            }
            buffer = (buffer << length) | (uint)(value & ((1 << length) - 1));
            count += length;
            while (count >= 8)
            {
                var b = (byte)(buffer >> (count - 8));
                output.WriteByte(b);
                if (b == 0xFF)
                {
                    output.WriteByte(0);
                }
                count -= 8;
            }
        }

        public void Flush()
        {
            if (count > 0)
            {
                Write((1 << (8 - count)) - 1, 8 - count);
            }
        }
    }
}
