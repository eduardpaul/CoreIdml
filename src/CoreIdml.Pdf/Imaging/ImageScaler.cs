namespace CoreIdml.Pdf.Imaging;

/// <summary>Area-averaging downscale (every source pixel contributes by its overlap), for any number of channels.</summary>
internal static class ImageScaler
{
    public static RasterImage Downscale(RasterImage source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        width = Math.Clamp(width, 1, source.Width);
        height = Math.Clamp(height, 1, source.Height);
        if (width == source.Width && height == source.Height)
        {
            return source;
        }
        var channels = source.Channels;
        var columns = new Taps(source.Width, width);
        var rows = new Taps(source.Height, height);
        var pixels = new byte[width * height * channels];
        // Each target row sums the source rows it covers, each averaged horizontally once (a source row shared by two
        // target rows is the last of one and the first of the next).
        var horizontal = new float[width * channels];
        var horizontalRow = -1;
        var sums = new float[width * channels];
        for (var y = 0; y < height; y++)
        {
            Array.Clear(sums);
            for (var tap = rows.Offsets[y]; tap < rows.Offsets[y + 1]; tap++)
            {
                var sourceRow = rows.Indices[tap];
                if (sourceRow != horizontalRow)
                {
                    Horizontal(source.Pixels.AsSpan(sourceRow * source.Width * channels, source.Width * channels), channels, columns, horizontal);
                    horizontalRow = sourceRow;
                }
                var weight = rows.Weights[tap];
                for (var i = 0; i < sums.Length; i++)
                {
                    sums[i] += horizontal[i] * weight;
                }
            }
            var target = pixels.AsSpan(y * width * channels, width * channels);
            for (var i = 0; i < sums.Length; i++)
            {
                target[i] = (byte)Math.Clamp((int)(sums[i] + 0.5f), 0, 255);
            }
        }
        return new RasterImage(width, height, channels, pixels);
    }

    /// <summary>One source row averaged into the target columns.</summary>
    private static void Horizontal(ReadOnlySpan<byte> row, int channels, Taps columns, Span<float> output)
    {
        var offsets = columns.Offsets;
        var indices = columns.Indices;
        var weights = columns.Weights;
        for (var x = 0; x < columns.Count; x++)
        {
            var first = offsets[x];
            var last = offsets[x + 1];
            for (var c = 0; c < channels; c++)
            {
                var sum = 0f;
                for (var tap = first; tap < last; tap++)
                {
                    sum += row[(indices[tap] * channels) + c] * weights[tap];
                }
                output[(x * channels) + c] = sum;
            }
        }
    }

    /// <summary>
    /// For each target sample t, the source samples it covers (<see cref="Indices"/> from <c>Offsets[t]</c> to
    /// <c>Offsets[t + 1]</c>) and their shares (<see cref="Weights"/>, summing to 1).
    /// </summary>
    private sealed class Taps
    {
        public Taps(int sourceSize, int targetSize)
        {
            Count = targetSize;
            var ratio = (double)sourceSize / targetSize;
            var indices = new List<int>(sourceSize + targetSize);
            var weights = new List<float>(sourceSize + targetSize);
            Offsets = new int[targetSize + 1];
            for (var t = 0; t < targetSize; t++)
            {
                var start = t * ratio;
                var end = start + ratio;
                for (var s = (int)start; s < Math.Min(sourceSize, (int)Math.Ceiling(end)); s++)
                {
                    var overlap = Math.Min(end, s + 1) - Math.Max(start, s);
                    if (overlap > 0)
                    {
                        indices.Add(s);
                        weights.Add((float)(overlap / ratio));
                    }
                }
                Offsets[t + 1] = indices.Count;
            }
            Indices = [.. indices];
            Weights = [.. weights];
        }

        public int Count { get; }

        public int[] Offsets { get; }

        public int[] Indices { get; }

        public float[] Weights { get; }
    }
}
