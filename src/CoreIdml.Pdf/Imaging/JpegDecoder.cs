using System.Buffers.Binary;

namespace CoreIdml.Pdf.Imaging;

/// <summary>
/// Decodes 8-bit baseline, extended and progressive Huffman JPEGs (1, 3 or 4 components, any sampling, restart
/// intervals) into interleaved samples: gray, RGB, or CMYK (Adobe CMYK/YCCK files: inverted, as stored), at full size
/// or scaled down by 2, 4 or 8 in the DCT (as libjpeg's scaled decoding does). Used only to downsample photos for
/// <see cref="PdfExportOptions.MaxImageDpi"/>; arithmetic-coded and 12-bit JPEGs are refused.
/// </summary>
internal sealed class JpegDecoder
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    ];

    private readonly byte[] data;
    private readonly int end;
    private readonly int scale;
    private readonly ushort[][] quantization = new ushort[4][];
    private readonly Huffman?[] dcTables = new Huffman?[4];
    private readonly Huffman?[] acTables = new Huffman?[4];
    private Component[] components = [];
    private int width;
    private int height;
    private int maxH = 1;
    private int maxV = 1;
    private int mcusPerLine;
    private int mcusPerColumn;
    private bool progressive;
    private int restartInterval;
    private int adobeTransform = -1;
    private int eobRun;

    // Bit reader state.
    private int position;
    private uint bitBuffer;
    private int bitCount;
    private bool hitMarker;

    // Per-block scratch: a baseline block's coefficients, then its dequantized frequencies.
    private readonly short[] coefficients = new short[64];
    private readonly float[] frequencies = new float[64];
    private readonly float[] samples = new float[64];

    private JpegDecoder(byte[] data, int length, int scale)
    {
        if (scale is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "The scale must be 1, 2, 4 or 8 (eighths).");
        }
        this.data = data;
        end = length;
        this.scale = scale;
    }

    /// <summary>Decodes a JPEG; throws <see cref="InvalidDataException"/> for unsupported or broken files.</summary>
    public static RasterImage Decode(ReadOnlySpan<byte> jpeg, int scale = 8) => Decode(jpeg.ToArray(), jpeg.Length, scale);

    /// <summary>
    /// Decodes the JPEG in the first <paramref name="length"/> bytes of <paramref name="buffer"/> at
    /// <paramref name="scale"/> eighths of its size (8, 4, 2 or 1; see <see cref="ScaledSize"/>).
    /// </summary>
    public static RasterImage Decode(byte[] buffer, int length, int scale)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, buffer.Length);
        return new JpegDecoder(buffer, length, scale).Run();
    }

    /// <summary>The decoded size of a side of <paramref name="size"/> pixels at <paramref name="scale"/> eighths.</summary>
    public static int ScaledSize(int size, int scale) => ((size * scale) + 7) / 8;

    /// <summary>The smallest scale (1, 2, 4 or 8 eighths) that still decodes at least the target size.</summary>
    public static int ScaleFor(int width, int height, int targetWidth, int targetHeight)
    {
        var scale = 1;
        while (scale < 8 && (ScaledSize(width, scale) < targetWidth || ScaledSize(height, scale) < targetHeight))
        {
            scale *= 2;
        }
        return scale;
    }

    private sealed class Component
    {
        public int Id;
        public int H;
        public int V;
        public int Quantization;
        public int BlocksPerLine;
        public int BlocksPerColumn;
        public int BlocksPerLinePadded;
        public short[] Coefficients = []; // progressive only: every scan refines them
        public int BlockWidth; // samples per block decoded: 8 at full size, fewer when scaled down
        public int BlockHeight;
        public int PlaneWidth;
        public byte[] Plane = [];
        public int DcPrediction;
        public int DcTable;
        public int AcTable;

        public Span<short> Block(int row, int column) => Coefficients.AsSpan(((row * BlocksPerLinePadded) + column) * 64, 64);
    }

    /// <summary>A Huffman table: a 9-bit lookahead for short codes, canonical code ranges for the rest.</summary>
    private sealed class Huffman
    {
        public const int FastBits = 9;
        public readonly ushort[] Fast = new ushort[1 << FastBits]; // (length << 8) | value; 0 = slow path

        // For AC tables: a short code and the coefficient bits after it, decoded at once:
        // (coefficient << 8) | (zero run << 4) | bits used; 0 = decode symbol and bits separately.
        public readonly short[] FastAc = new short[1 << FastBits];
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValueOffset = new int[17];
        public readonly byte[] Values;

        public Huffman(ReadOnlySpan<byte> counts, byte[] values)
        {
            Values = values;
            var code = 0;
            var k = 0;
            for (var length = 1; length <= 16; length++)
            {
                ValueOffset[length] = k - code;
                for (var i = 0; i < counts[length - 1]; i++, k++, code++)
                {
                    if (length <= FastBits)
                    {
                        var shift = FastBits - length;
                        for (var fill = 0; fill < 1 << shift; fill++)
                        {
                            Fast[(code << shift) | fill] = (ushort)((length << 8) | values[k]);
                        }
                    }
                }
                MaxCode[length] = counts[length - 1] == 0 ? -1 : code - 1;
                code <<= 1;
            }
            MaxCode[17] = int.MaxValue;
            for (var bits = 0; bits < Fast.Length; bits++)
            {
                var length = Fast[bits] >> 8;
                var size = Fast[bits] & 15;
                if (length > 0 && size > 0 && length + size <= FastBits)
                {
                    var coefficient = Extend((bits >> (FastBits - length - size)) & ((1 << size) - 1), size);
                    if (coefficient is >= -128 and <= 127)
                    {
                        FastAc[bits] = (short)((coefficient << 8) | (Fast[bits] & 0xF0) | (length + size));
                    }
                }
            }
        }
    }

    private RasterImage Run()
    {
        if (end < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw new InvalidDataException("Not a JPEG.");
        }
        position = 2;
        var frameSeen = false;
        while (position < end)
        {
            var marker = NextMarker();
            if (marker is 0xD9 or -1)
            {
                break;
            }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }
            var length = position + 2 <= end ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2)) : 0;
            if (length < 2 || position + length > end)
            {
                throw new InvalidDataException("Truncated JPEG segment.");
            }
            var segment = data.AsSpan(position + 2, length - 2);
            var next = position + length;
            switch (marker)
            {
                case 0xDB:
                    ReadQuantization(segment);
                    break;
                case 0xC4:
                    ReadHuffman(segment);
                    break;
                case 0xC0 or 0xC1 or 0xC2:
                    progressive = marker == 0xC2;
                    ReadFrame(segment);
                    frameSeen = true;
                    break;
                case >= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC:
                    throw new InvalidDataException("Lossless, hierarchical and arithmetic-coded JPEGs are not supported.");
                case 0xDD:
                    restartInterval = BinaryPrimitives.ReadUInt16BigEndian(segment);
                    break;
                case 0xEE:
                    if (segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8))
                    {
                        adobeTransform = segment[11];
                    }
                    break;
                case 0xDA:
                    if (!frameSeen)
                    {
                        throw new InvalidDataException("JPEG scan before the frame header.");
                    }
                    position = next;
                    ReadScan(segment);
                    continue;
            }
            position = next;
        }
        if (!frameSeen)
        {
            throw new InvalidDataException("JPEG without a frame header.");
        }
        return Output();
    }

    private int NextMarker()
    {
        while (position + 1 < end)
        {
            if (data[position] == 0xFF && data[position + 1] != 0x00 && data[position + 1] != 0xFF)
            {
                var marker = data[position + 1];
                position += 2;
                return marker;
            }
            position++;
        }
        return -1;
    }

    private void ReadQuantization(ReadOnlySpan<byte> segment)
    {
        while (segment.Length > 0)
        {
            var precision = segment[0] >> 4;
            var table = new ushort[64];
            for (var i = 0; i < 64; i++)
            {
                table[ZigZag[i]] = precision == 0 ? segment[1 + i] : BinaryPrimitives.ReadUInt16BigEndian(segment[(1 + (i * 2))..]);
            }
            quantization[segment[0] & 3] = table;
            segment = segment[(precision == 0 ? 65 : 129)..];
        }
    }

    private void ReadHuffman(ReadOnlySpan<byte> segment)
    {
        while (segment.Length > 17)
        {
            var info = segment[0];
            var counts = segment.Slice(1, 16);
            var total = 0;
            foreach (var count in counts)
            {
                total += count;
            }
            var table = new Huffman(counts, segment.Slice(17, total).ToArray());
            ((info >> 4) == 0 ? dcTables : acTables)[info & 3] = table;
            segment = segment[(17 + total)..];
        }
    }

    private void ReadFrame(ReadOnlySpan<byte> segment)
    {
        if (segment[0] != 8)
        {
            throw new InvalidDataException("Only 8-bit JPEGs are supported.");
        }
        height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
        width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
        var count = segment[5];
        if (width == 0 || height == 0 || count is not (1 or 3 or 4))
        {
            throw new InvalidDataException("Unsupported JPEG frame.");
        }
        components = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var spec = segment.Slice(6 + (i * 3), 3);
            components[i] = new Component { Id = spec[0], H = Math.Max(1, spec[1] >> 4), V = Math.Max(1, spec[1] & 15), Quantization = spec[2] & 3 };
        }
        maxH = components.Max(c => c.H);
        maxV = components.Max(c => c.V);
        mcusPerLine = (width + (8 * maxH) - 1) / (8 * maxH);
        mcusPerColumn = (height + (8 * maxV) - 1) / (8 * maxV);
        foreach (var component in components)
        {
            component.BlocksPerLine = ((((width * component.H) + maxH - 1) / maxH) + 7) / 8;
            component.BlocksPerColumn = ((((height * component.V) + maxV - 1) / maxV) + 7) / 8;
            component.BlocksPerLinePadded = mcusPerLine * component.H;
            // Subsampled components scaled less (up to full size), so they come out at the size of the others.
            component.BlockWidth = Math.Clamp(scale * maxH / component.H, 1, 8);
            component.BlockHeight = Math.Clamp(scale * maxV / component.V, 1, 8);
            component.PlaneWidth = component.BlocksPerLinePadded * component.BlockWidth;
            component.Plane = new byte[component.PlaneWidth * mcusPerColumn * component.V * component.BlockHeight];
            // Baseline blocks are transformed as they are decoded (blocks no scan reaches stay mid-gray, as zero
            // coefficients would give); progressive scans refine stored coefficients, transformed at the end.
            if (progressive)
            {
                component.Coefficients = new short[component.BlocksPerLinePadded * mcusPerColumn * component.V * 64];
            }
            else
            {
                component.Plane.AsSpan().Fill(128);
            }
        }
    }

    private void ReadScan(ReadOnlySpan<byte> header)
    {
        var count = header[0];
        var scanComponents = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var id = header[1 + (i * 2)];
            var tables = header[2 + (i * 2)];
            var component = components.FirstOrDefault(c => c.Id == id) ?? throw new InvalidDataException("JPEG scan names an unknown component.");
            component.DcTable = tables >> 4;
            component.AcTable = tables & 15;
            scanComponents[i] = component;
        }
        var spectralStart = header[1 + (count * 2)];
        var spectralEnd = header[2 + (count * 2)];
        var approximation = header[3 + (count * 2)];
        var successiveHigh = approximation >> 4;
        var successiveLow = approximation & 15;

        foreach (var component in scanComponents)
        {
            component.DcPrediction = 0;
        }
        eobRun = 0;
        ResetBits();

        void DecodeBlock(Component component, int row, int column)
        {
            if (!progressive)
            {
                Array.Clear(coefficients);
                DecodeBaseline(component, coefficients);
                StoreBlock(component, row, column, coefficients);
                return;
            }
            var block = component.Block(row, column);
            if (spectralStart == 0)
            {
                if (successiveHigh == 0)
                {
                    DecodeDcFirst(component, block, successiveLow);
                }
                else if (ReadBit() != 0)
                {
                    block[0] |= (short)(1 << successiveLow);
                }
            }
            else if (successiveHigh == 0)
            {
                DecodeAcFirst(component, block, spectralStart, spectralEnd, successiveLow);
            }
            else
            {
                DecodeAcRefine(component, block, spectralStart, spectralEnd, successiveLow);
            }
        }

        var total = scanComponents.Length == 1
            ? scanComponents[0].BlocksPerLine * scanComponents[0].BlocksPerColumn
            : mcusPerLine * mcusPerColumn;
        var interval = restartInterval > 0 ? restartInterval : int.MaxValue;
        for (var unit = 0; unit < total; unit++)
        {
            if (unit > 0 && unit % interval == 0)
            {
                Restart(scanComponents);
            }
            if (scanComponents.Length == 1)
            {
                // Non-interleaved: the component's own blocks, row by row.
                var component = scanComponents[0];
                DecodeBlock(component, unit / component.BlocksPerLine, unit % component.BlocksPerLine);
                continue;
            }
            var mcuRow = unit / mcusPerLine;
            var mcuColumn = unit % mcusPerLine;
            foreach (var component in scanComponents)
            {
                for (var v = 0; v < component.V; v++)
                {
                    for (var h = 0; h < component.H; h++)
                    {
                        DecodeBlock(component, (mcuRow * component.V) + v, (mcuColumn * component.H) + h);
                    }
                }
            }
        }
        // Continue after the entropy-coded data: the next marker.
        position = Math.Max(position - (bitCount / 8), 0);
        while (position + 1 < end && !(data[position] == 0xFF && data[position + 1] != 0x00 && data[position + 1] is < 0xD0 or > 0xD7))
        {
            position++;
        }
    }

    private void Restart(Component[] scanComponents)
    {
        // Byte-align and skip the RSTn marker.
        ResetBits();
        while (position + 1 < end && !(data[position] == 0xFF && data[position + 1] is >= 0xD0 and <= 0xD7))
        {
            position++;
        }
        position += 2;
        foreach (var component in scanComponents)
        {
            component.DcPrediction = 0;
        }
        eobRun = 0;
    }

    private void DecodeBaseline(Component component, Span<short> block)
    {
        var dc = DecodeSymbol(dcTables[component.DcTable]);
        component.DcPrediction += dc == 0 ? 0 : Extend(ReadBits(dc), dc);
        block[0] = (short)component.DcPrediction;
        var ac = acTables[component.AcTable] ?? throw new InvalidDataException("JPEG scan uses a missing Huffman table.");
        for (var k = 1; k < 64;)
        {
            if (bitCount < 16)
            {
                Fill();
            }
            var fast = ac.FastAc[bitBuffer >> (32 - Huffman.FastBits)];
            if (fast != 0)
            {
                // Most coefficients: a short code and a small value.
                k += (fast >> 4) & 15;
                bitBuffer <<= fast & 15;
                bitCount -= fast & 15;
                if (k > 63)
                {
                    break;
                }
                block[ZigZag[k++]] = (short)(fast >> 8);
                continue;
            }
            var symbol = DecodeSymbol(ac);
            var run = symbol >> 4;
            var size = symbol & 15;
            if (size == 0)
            {
                if (run != 15)
                {
                    break;
                }
                k += 16;
                continue;
            }
            k += run;
            if (k > 63)
            {
                break;
            }
            block[ZigZag[k++]] = (short)Extend(ReadBits(size), size);
        }
    }

    private void DecodeDcFirst(Component component, Span<short> block, int low)
    {
        var dc = DecodeSymbol(dcTables[component.DcTable]);
        component.DcPrediction += dc == 0 ? 0 : Extend(ReadBits(dc), dc);
        block[0] = (short)(component.DcPrediction << low);
    }

    private void DecodeAcFirst(Component component, Span<short> block, int start, int end, int low)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }
        var ac = acTables[component.AcTable];
        for (var k = start; k <= end;)
        {
            var symbol = DecodeSymbol(ac);
            var run = symbol >> 4;
            var size = symbol & 15;
            if (size == 0)
            {
                if (run < 15)
                {
                    eobRun = (1 << run) - 1 + (run > 0 ? ReadBits(run) : 0);
                    break;
                }
                k += 16;
                continue;
            }
            k += run;
            if (k > 63)
            {
                break;
            }
            block[ZigZag[k++]] = (short)(Extend(ReadBits(size), size) * (1 << low));
        }
    }

    private void DecodeAcRefine(Component component, Span<short> block, int start, int end, int low)
    {
        var positive = 1 << low;
        var negative = -1 << low;
        var k = start;
        if (eobRun <= 0)
        {
            var ac = acTables[component.AcTable];
            for (; k <= end;)
            {
                var symbol = DecodeSymbol(ac);
                var run = symbol >> 4;
                var size = symbol & 15;
                var value = 0;
                if (size == 0)
                {
                    if (run < 15)
                    {
                        eobRun = (1 << run) + (run > 0 ? ReadBits(run) : 0);
                        break;
                    }
                }
                else
                {
                    value = ReadBit() != 0 ? positive : negative;
                }
                // Skip `run` zero coefficients, refining the non-zero ones met on the way.
                while (k <= end)
                {
                    var index = ZigZag[k];
                    if (block[index] != 0)
                    {
                        Refine(block, index, positive, negative);
                    }
                    else
                    {
                        if (run == 0)
                        {
                            if (value != 0)
                            {
                                block[index] = (short)value;
                            }
                            k++;
                            break;
                        }
                        run--;
                    }
                    k++;
                }
            }
        }
        if (eobRun > 0)
        {
            // The rest of the band: only refinement bits for non-zero coefficients.
            for (; k <= end; k++)
            {
                var index = ZigZag[k];
                if (block[index] != 0)
                {
                    Refine(block, index, positive, negative);
                }
            }
            eobRun--;
        }
    }

    private void Refine(Span<short> block, int index, int positive, int negative)
    {
        if (ReadBit() != 0 && (block[index] & positive) == 0)
        {
            block[index] = (short)(block[index] + (block[index] >= 0 ? positive : negative));
        }
    }

    // ---- bits --------------------------------------------------------------------------------------

    private void ResetBits()
    {
        bitBuffer = 0;
        bitCount = 0;
        hitMarker = false;
    }

    private void Fill()
    {
        while (bitCount <= 24)
        {
            var value = 0;
            if (!hitMarker && position < end)
            {
                value = data[position];
                if (value == 0xFF)
                {
                    var next = position + 1 < end ? data[position + 1] : 0;
                    if (next == 0x00)
                    {
                        position += 2;
                    }
                    else
                    {
                        // A marker: feed zeros from now on.
                        hitMarker = true;
                        value = 0;
                    }
                }
                else
                {
                    position++;
                }
            }
            bitBuffer |= (uint)value << (24 - bitCount);
            bitCount += 8;
        }
    }

    private int ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }
        if (bitCount < count)
        {
            Fill();
        }
        var value = (int)(bitBuffer >> (32 - count));
        bitBuffer <<= count;
        bitCount -= count;
        return value;
    }

    private int ReadBit() => ReadBits(1);

    private int DecodeSymbol(Huffman? table)
    {
        if (table is null)
        {
            throw new InvalidDataException("JPEG scan uses a missing Huffman table.");
        }
        if (bitCount < 16)
        {
            Fill();
        }
        var fast = table.Fast[bitBuffer >> (32 - Huffman.FastBits)];
        if (fast != 0)
        {
            var length = fast >> 8;
            bitBuffer <<= length;
            bitCount -= length;
            return fast & 0xFF;
        }
        for (var length = Huffman.FastBits + 1; length <= 16; length++)
        {
            var code = (int)(bitBuffer >> (32 - length));
            if (code <= table.MaxCode[length])
            {
                bitBuffer <<= length;
                bitCount -= length;
                var index = code + table.ValueOffset[length];
                return index >= 0 && index < table.Values.Length ? table.Values[index] : throw new InvalidDataException("Corrupt JPEG Huffman data.");
            }
        }
        throw new InvalidDataException("Corrupt JPEG Huffman data.");
    }

    private static int Extend(int value, int size) => value < (1 << (size - 1)) ? value - (1 << size) + 1 : value;

    // ---- output ------------------------------------------------------------------------------------

    private RasterImage Output()
    {
        if (progressive)
        {
            foreach (var component in components)
            {
                for (var row = 0; row < component.BlocksPerColumn; row++)
                {
                    for (var column = 0; column < component.BlocksPerLinePadded; column++)
                    {
                        StoreBlock(component, row, column, component.Block(row, column));
                    }
                }
                component.Coefficients = [];
            }
        }
        var outputWidth = ScaledSize(width, scale);
        var outputHeight = ScaledSize(height, scale);
        var channels = components.Length;
        var pixels = new byte[outputWidth * outputHeight * channels];
        var transform = channels == 3 ? adobeTransform != 0 : channels == 4 && adobeTransform == 2;
        // Each component's sample under each output column: the same position, or the nearest one for a subsampled
        // component that could not be scaled to the output size (at full size).
        var columns = new int[channels][];
        for (var c = 0; c < channels; c++)
        {
            var component = components[c];
            columns[c] = new int[outputWidth];
            for (var x = 0; x < outputWidth; x++)
            {
                columns[c][x] = Math.Min(x * component.H * component.BlockWidth / (maxH * scale), component.PlaneWidth - 1);
            }
        }
        for (var y = 0; y < outputHeight; y++)
        {
            var row = pixels.AsSpan(y * outputWidth * channels, outputWidth * channels);
            for (var c = 0; c < channels; c++)
            {
                var component = components[c];
                var sourceRow = Math.Min(y * component.V * component.BlockHeight / (maxV * scale), (component.BlocksPerColumn * component.BlockHeight) - 1);
                var plane = component.Plane.AsSpan(sourceRow * component.PlaneWidth, component.PlaneWidth);
                var map = columns[c];
                for (var x = 0; x < map.Length; x++)
                {
                    row[(x * channels) + c] = plane[map[x]];
                }
            }
            if (transform)
            {
                YccToRgb(row, channels);
            }
        }
        return new RasterImage(outputWidth, outputHeight, channels, pixels);
    }

    // YCbCr → RGB in 16-bit fixed point (JFIF: R = Y + 1.402 Cr, G = Y - 0.344136 Cb - 0.714136 Cr, B = Y + 1.772 Cb).
    private const int FixedBits = 16;
    private static readonly int[] CrRed = ColorTable(1.402, round: true);
    private static readonly int[] CbBlue = ColorTable(1.772, round: true);
    private static readonly int[] CbGreen = ColorTable(-0.344136, round: false);
    private static readonly int[] CrGreen = ColorTable(-0.714136, round: false);

    private static int[] ColorTable(double factor, bool round)
    {
        var table = new int[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = round ? (int)Math.Floor((factor * (i - 128)) + 0.5) : (int)Math.Round(factor * (i - 128) * (1 << FixedBits));
        }
        return table;
    }

    /// <summary>Converts a row of interleaved YCbCr (YCCK: then inverted, K kept) to RGB (CMYK) in place.</summary>
    private static void YccToRgb(Span<byte> row, int channels)
    {
        const int half = 1 << (FixedBits - 1);
        for (var at = 0; at + 2 < row.Length; at += channels)
        {
            int luma = row[at], cb = row[at + 1], cr = row[at + 2];
            var red = (byte)Math.Clamp(luma + CrRed[cr], 0, 255);
            var green = (byte)Math.Clamp(luma + ((CbGreen[cb] + CrGreen[cr] + half) >> FixedBits), 0, 255);
            var blue = (byte)Math.Clamp(luma + CbBlue[cb], 0, 255);
            if (channels == 4)
            {
                // YCCK: the converted values are inverted CMY; K stays.
                (red, green, blue) = ((byte)(255 - red), (byte)(255 - green), (byte)(255 - blue));
            }
            (row[at], row[at + 1], row[at + 2]) = (red, green, blue);
        }
    }

    /// <summary>Dequantizes and inverse-transforms a block into its component's plane (scaled when decoding smaller).</summary>
    private void StoreBlock(Component component, int row, int column, ReadOnlySpan<short> block)
    {
        var table = quantization[component.Quantization] ?? throw new InvalidDataException("JPEG uses a missing quantization table.");
        var blockWidth = component.BlockWidth;
        var blockHeight = component.BlockHeight;
        // Scaled down, only the lowest frequencies are needed.
        for (var v = 0; v < blockHeight; v++)
        {
            for (var u = 0; u < blockWidth; u++)
            {
                frequencies[(v * 8) + u] = block[(v * 8) + u] * table[(v * 8) + u];
            }
        }
        InverseDct(frequencies, blockWidth, blockHeight, samples);
        var target = component.Plane.AsSpan((row * blockHeight * component.PlaneWidth) + (column * blockWidth));
        for (var y = 0; y < blockHeight; y++)
        {
            var line = target.Slice(y * component.PlaneWidth, blockWidth);
            for (var x = 0; x < blockWidth; x++)
            {
                line[x] = Clamp(samples[(y * blockWidth) + x] + 128);
            }
        }
    }

    /// <summary>For each size 1-8: C(u)/2·cos((2x+1)uπ/2N) at [x·N + u], the N-point inverse DCT of JPEG's normalization.</summary>
    private static readonly float[][] Cosines = [.. Enumerable.Range(0, 9).Select(CreateCosines)];

    private static float[] CreateCosines(int size)
    {
        var table = new float[size * size];
        for (var x = 0; x < size; x++)
        {
            for (var u = 0; u < size; u++)
            {
                var scale = u == 0 ? 1 / Math.Sqrt(2) : 1;
                table[(x * size) + u] = (float)(scale * Math.Cos(((2 * x) + 1) * u * Math.PI / (2 * size)) / 2);
            }
        }
        return table;
    }

    /// <summary>
    /// Separable inverse DCT (rows, then columns) of a dequantized 8×8 block in natural order into
    /// <paramref name="width"/>×<paramref name="height"/> samples (row by row). 8×8 is the full transform; a smaller
    /// size transforms only that many of the lowest frequencies, which gives the block scaled down, each sample close
    /// to the average of the pixels it covers (libjpeg's scaled IDCT). Rows and columns of zeros are skipped.
    /// </summary>
    internal static void InverseDct(ReadOnlySpan<float> block, int width, int height, Span<float> output)
    {
        ReadOnlySpan<float> horizontal = Cosines[width];
        ReadOnlySpan<float> vertical = Cosines[height];
        Span<float> rows = stackalloc float[64];
        var usedRows = 0;
        for (var v = 0; v < height; v++)
        {
            var coefficients = block.Slice(v * 8, width);
            var last = coefficients.LastIndexOfAnyExcept(0f);
            var transformed = rows.Slice(v * width, width);
            if (last < 0)
            {
                transformed.Clear();
                continue;
            }
            usedRows = v + 1;
            for (var x = 0; x < width; x++)
            {
                var basis = horizontal.Slice(x * width, last + 1);
                var sum = 0f;
                for (var u = 0; u < basis.Length; u++)
                {
                    sum += basis[u] * coefficients[u];
                }
                transformed[x] = sum;
            }
        }
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var basis = vertical.Slice(y * height, usedRows);
                var sum = 0f;
                for (var v = 0; v < basis.Length; v++)
                {
                    sum += basis[v] * rows[(v * width) + x];
                }
                output[(y * width) + x] = sum;
            }
        }
    }

    private static byte Clamp(double value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5);
}
