namespace CoreIdml.Pdf.Rendering;

/// <summary>
/// The shape an effect is computed from, in user space: filled areas (flattened outlines) and stroke bands. A band on
/// the inside or outside of a closed path keeps only its part inside or outside <see cref="StrokeBand.Region"/>.
/// </summary>
internal sealed class EffectShape
{
    public List<FilledArea> Fills { get; } = [];

    public List<StrokeBand> Strokes { get; } = [];

    /// <summary>True when the item has text, whose glyph outlines are not available: the shape leaves it out.</summary>
    public bool LeavesOutText { get; set; }

    public bool IsEmpty => Fills.Count == 0 && Strokes.Count == 0;

    /// <summary>The bounding box of the areas and bands (null when empty).</summary>
    public Rect? Bounds
    {
        get
        {
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            void Include(IEnumerable<Point[]> outlines, double outset)
            {
                foreach (var point in outlines.SelectMany(outline => outline))
                {
                    left = Math.Min(left, point.X - outset);
                    top = Math.Min(top, point.Y - outset);
                    right = Math.Max(right, point.X + outset);
                    bottom = Math.Max(bottom, point.Y + outset);
                }
            }
            foreach (var fill in Fills)
            {
                Include(fill.Outlines, 0);
            }
            foreach (var stroke in Strokes)
            {
                Include(stroke.Lines, stroke.HalfWidth);
            }
            return left <= right && top <= bottom ? Rect.FromEdges(left, top, right, bottom) : null;
        }
    }

    public void Add(EffectShape other)
    {
        Fills.AddRange(other.Fills);
        Strokes.AddRange(other.Strokes);
        LeavesOutText |= other.LeavesOutText;
    }
}

/// <summary>Closed outlines filled with the non-zero or even-odd rule.</summary>
internal sealed record FilledArea(IReadOnlyList<Point[]> Outlines, bool EvenOdd);

/// <summary>
/// Polylines stroked <see cref="HalfWidth"/> to either side (round joins and caps), kept inside (<see cref="Side"/> 1)
/// or outside (−1) of <see cref="Region"/>, or whole (0).
/// </summary>
internal sealed record StrokeBand(IReadOnlyList<Point[]> Lines, double HalfWidth, int Side, FilledArea? Region);

/// <summary>
/// An alpha raster (0..1 per pixel) over an axis-aligned area of user space, at <see cref="Scale"/> pixels per unit:
/// the anti-aliased shape of an item and the soft effects made from it (blur, spread, choke, offsets, feathers).
/// Everything is deterministic and linear in the pixel count (box blurs, a separable distance transform), so effects
/// stay cheap on WebAssembly and in long documents.
/// </summary>
internal sealed class AlphaMask
{
    private const int SubRows = 4;
    private const float Far = 1e20f;

    public AlphaMask(double x, double y, int width, int height, double scale)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Scale = scale;
        Alpha = new float[width * height];
    }

    /// <summary>User-space position of the top-left corner of pixel (0, 0).</summary>
    public double X { get; }

    public double Y { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Pixels per user-space unit.</summary>
    public double Scale { get; }

    public float[] Alpha { get; }

    /// <summary>The user-space area the pixels cover.</summary>
    public Rect Area => new(X, Y, Width / Scale, Height / Scale);

    /// <summary>
    /// A raster of <paramref name="area"/> at <paramref name="scale"/> pixels per unit, lowered so that it has at most
    /// <paramref name="maxPixels"/> pixels.
    /// </summary>
    public static AlphaMask Create(Rect area, double scale, int maxPixels)
    {
        var pixels = area.Width * area.Height * scale * scale;
        if (pixels > maxPixels)
        {
            scale *= Math.Sqrt(maxPixels / pixels);
        }
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
        return new AlphaMask(area.X, area.Y, width, height, scale);
    }

    /// <summary>Paints the coverage of <paramref name="shape"/> (the union of its areas and bands).</summary>
    public void Draw(EffectShape shape)
    {
        var layer = new float[Alpha.Length];
        foreach (var fill in shape.Fills)
        {
            Array.Clear(layer);
            Fill(fill, layer);
            Union(layer);
        }
        foreach (var stroke in shape.Strokes)
        {
            Array.Clear(layer);
            Stroke(stroke, layer);
            if (stroke.Side != 0 && stroke.Region is { } region)
            {
                var inside = new float[Alpha.Length];
                Fill(region, inside);
                for (var i = 0; i < layer.Length; i++)
                {
                    layer[i] *= stroke.Side > 0 ? inside[i] : 1 - inside[i];
                }
            }
            Union(layer);
        }
    }

    public AlphaMask Clone()
    {
        var copy = new AlphaMask(X, Y, Width, Height, Scale);
        Alpha.CopyTo(copy.Alpha, 0);
        return copy;
    }

    /// <summary>1 − alpha: the outside of the shape.</summary>
    public AlphaMask Inverted()
    {
        var copy = new AlphaMask(X, Y, Width, Height, Scale);
        for (var i = 0; i < Alpha.Length; i++)
        {
            copy.Alpha[i] = 1 - Alpha[i];
        }
        return copy;
    }

    /// <summary>Multiplies by another raster of the same grid.</summary>
    public void Multiply(AlphaMask other)
    {
        for (var i = 0; i < Alpha.Length; i++)
        {
            Alpha[i] *= other.Alpha[i];
        }
    }

    public bool IsEmpty => !Alpha.Any(value => value >= 0.5f / 255);

    /// <summary>The alpha as 8-bit gray samples, row 0 at the top.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Alpha.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp((int)MathF.Round(Alpha[i] * 255), 0, 255);
        }
        return bytes;
    }

    /// <summary>
    /// The raster moved by (<paramref name="dx"/>, <paramref name="dy"/>) user units on the same grid (bilinear), the
    /// pixels at the border extended outwards.
    /// </summary>
    public AlphaMask Shifted(double dx, double dy)
    {
        var copy = new AlphaMask(X, Y, Width, Height, Scale);
        var (sx, sy) = (dx * Scale, dy * Scale);
        for (var y = 0; y < Height; y++)
        {
            var fy = y - sy;
            var y0 = (int)Math.Floor(fy);
            var wy = (float)(fy - y0);
            for (var x = 0; x < Width; x++)
            {
                var fx = x - sx;
                var x0 = (int)Math.Floor(fx);
                var wx = (float)(fx - x0);
                var top = (At(x0, y0) * (1 - wx)) + (At(x0 + 1, y0) * wx);
                var bottom = (At(x0, y0 + 1) * (1 - wx)) + (At(x0 + 1, y0 + 1) * wx);
                copy.Alpha[(y * Width) + x] = (top * (1 - wy)) + (bottom * wy);
            }
        }
        return copy;
    }

    private float At(int x, int y) => Alpha[(Math.Clamp(y, 0, Height - 1) * Width) + Math.Clamp(x, 0, Width - 1)];

    /// <summary>
    /// Gaussian blur with standard deviation <paramref name="sigma"/> user units, approximated by three box blurs per
    /// axis (the pixels at the border extended outwards).
    /// </summary>
    public void Blur(double sigma)
    {
        var pixels = sigma * Scale;
        if (pixels < 0.3)
        {
            return;
        }
        var line = new float[Width];
        var output = new float[Width];
        var copy = new float[Alpha.Length];
        var sums = new float[Width];
        foreach (var radius in BoxRadii(pixels))
        {
            for (var y = 0; y < Height; y++)
            {
                Array.Copy(Alpha, y * Width, line, 0, Width);
                BoxBlur(line, output, Width, radius);
                Array.Copy(output, 0, Alpha, y * Width, Width);
            }
            VerticalBoxBlur(copy, sums, radius);
        }
    }

    /// <summary>
    /// A box blur down the columns, all columns at once: a running sum of whole rows (contiguous and vectorized,
    /// where a column at a time would stride through memory).
    /// </summary>
    private void VerticalBoxBlur(float[] copy, float[] sums, int radius)
    {
        if (radius <= 0)
        {
            return;
        }
        Alpha.CopyTo(copy, 0);
        Span<float> Row(int y) => copy.AsSpan(Math.Clamp(y, 0, Height - 1) * Width, Width);
        Array.Clear(sums);
        for (var y = -radius; y <= radius; y++)
        {
            Add(sums, Row(y), 1);
        }
        var scale = 1f / ((2 * radius) + 1);
        for (var y = 0; y < Height; y++)
        {
            var target = Alpha.AsSpan(y * Width, Width);
            for (var x = 0; x < Width; x++)
            {
                target[x] = sums[x] * scale;
            }
            Add(sums, Row(y + radius + 1), 1);
            Add(sums, Row(y - radius), -1);
        }
    }

    /// <summary><paramref name="sums"/> += <paramref name="sign"/> × <paramref name="row"/>.</summary>
    private static void Add(Span<float> sums, ReadOnlySpan<float> row, float sign)
    {
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var width = System.Numerics.Vector<float>.Count;
            var factor = new System.Numerics.Vector<float>(sign);
            for (; i + width <= sums.Length; i += width)
            {
                var sum = new System.Numerics.Vector<float>(sums[i..]) + (new System.Numerics.Vector<float>(row[i..]) * factor);
                sum.CopyTo(sums[i..]);
            }
        }
        for (; i < sums.Length; i++)
        {
            sums[i] += row[i] * sign;
        }
    }

    /// <summary>The radii of three box blurs whose combination has standard deviation <paramref name="sigma"/> pixels.</summary>
    internal static int[] BoxRadii(double sigma)
    {
        const int boxes = 3;
        var ideal = Math.Sqrt((12 * sigma * sigma / boxes) + 1);
        var lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0)
        {
            lower--;
        }
        var upper = lower + 2;
        var count = (int)Math.Round(((12 * sigma * sigma) - (boxes * lower * lower) - (4 * boxes * lower) - (3 * boxes)) / ((-4 * lower) - 4));
        return Enumerable.Range(0, boxes).Select(i => ((i < count ? lower : upper) - 1) / 2).ToArray();
    }

    private static void BoxBlur(float[] input, float[] output, int length, int radius)
    {
        if (radius <= 0)
        {
            Array.Copy(input, output, length);
            return;
        }
        var scale = 1f / ((2 * radius) + 1);
        float Sample(int i) => input[Math.Clamp(i, 0, length - 1)];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            sum += Sample(i);
        }
        for (var i = 0; i < length; i++)
        {
            output[i] = sum * scale;
            sum += Sample(i + radius + 1) - Sample(i - radius);
        }
    }

    /// <summary>Grows the shape by <paramref name="distance"/> user units (round corners).</summary>
    public void Dilate(double distance)
    {
        var pixels = (float)(distance * Scale);
        if (pixels <= 0)
        {
            return;
        }
        var outside = Distances(i => Alpha[i] >= 0.5f);
        for (var i = 0; i < Alpha.Length; i++)
        {
            Alpha[i] = Math.Max(Alpha[i], Math.Clamp(pixels + 0.5f - outside[i], 0, 1));
        }
    }

    /// <summary>Shrinks the shape by <paramref name="distance"/> user units.</summary>
    public void Erode(double distance)
    {
        var pixels = (float)(distance * Scale);
        if (pixels <= 0)
        {
            return;
        }
        var inside = Distances(i => Alpha[i] < 0.5f);
        for (var i = 0; i < Alpha.Length; i++)
        {
            Alpha[i] = Math.Min(Alpha[i], Math.Clamp(inside[i] - pixels + 0.5f, 0, 1));
        }
    }

    /// <summary>
    /// For every pixel, the distance in pixels from its centre to the centre of the nearest pixel whose index
    /// <paramref name="seed"/> accepts (0 on seeds): the exact Euclidean distance transform of Felzenszwalb and
    /// Huttenlocher, two linear passes. Far away (1e20) when there is no seed.
    /// </summary>
    public float[] Distances(Func<int, bool> seed)
    {
        var squared = new float[Alpha.Length];
        for (var i = 0; i < Alpha.Length; i++)
        {
            squared[i] = seed(i) ? 0 : Far;
        }
        var length = Math.Max(Width, Height);
        var f = new float[length];
        var d = new float[length];
        var v = new int[length];
        var z = new float[length + 1];
        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                f[y] = squared[(y * Width) + x];
            }
            Transform(f, d, v, z, Height);
            for (var y = 0; y < Height; y++)
            {
                squared[(y * Width) + x] = d[y];
            }
        }
        for (var y = 0; y < Height; y++)
        {
            Array.Copy(squared, y * Width, f, 0, Width);
            Transform(f, d, v, z, Width);
            Array.Copy(d, 0, squared, y * Width, Width);
        }
        for (var i = 0; i < squared.Length; i++)
        {
            squared[i] = squared[i] >= Far ? Far : MathF.Sqrt(squared[i]);
        }
        return squared;
    }

    /// <summary>One dimension of the squared distance transform (lower envelope of parabolas).</summary>
    private static void Transform(float[] f, float[] d, int[] v, float[] z, int n)
    {
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        float Intersection(int q, int p) => ((f[q] + ((float)q * q)) - (f[p] + ((float)p * p))) / (2f * (q - p));
        for (var q = 1; q < n; q++)
        {
            var s = Intersection(q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersection(q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }
            var offset = q - v[k];
            d[q] = Math.Min(Far, ((float)offset * offset) + f[v[k]]);
        }
    }

    private void Union(float[] layer)
    {
        for (var i = 0; i < Alpha.Length; i++)
        {
            Alpha[i] = Math.Max(Alpha[i], Math.Min(1, layer[i]));
        }
    }

    /// <summary>
    /// Scan-converts closed outlines into <paramref name="target"/>: four sub-rows per pixel row, each span's horizontal
    /// coverage exact.
    /// </summary>
    private void Fill(FilledArea area, float[] target)
    {
        var edges = new List<(double X0, double Y0, double X1, double Y1, int Direction)>();
        foreach (var outline in area.Outlines)
        {
            for (var i = 0; i < outline.Length; i++)
            {
                var (ax, ay) = ToPixels(outline[i]);
                var (bx, by) = ToPixels(outline[(i + 1) % outline.Length]);
                if (ay != by)
                {
                    edges.Add(ay < by ? (ax, ay, bx, by, 1) : (bx, by, ax, ay, -1));
                }
            }
        }
        edges.Sort((a, b) => a.Y0.CompareTo(b.Y0));
        var active = new List<(double X0, double Y0, double X1, double Y1, int Direction)>();
        var crossings = new List<(double X, int Direction)>();
        var next = 0;
        for (var row = 0; row < Height && (next < edges.Count || active.Count > 0); row++)
        {
            for (var sub = 0; sub < SubRows; sub++)
            {
                var y = row + ((sub + 0.5) / SubRows);
                while (next < edges.Count && edges[next].Y0 <= y)
                {
                    active.Add(edges[next++]);
                }
                active.RemoveAll(edge => edge.Y1 <= y);
                crossings.Clear();
                foreach (var edge in active)
                {
                    if (edge.Y0 <= y)
                    {
                        crossings.Add((edge.X0 + ((y - edge.Y0) * (edge.X1 - edge.X0) / (edge.Y1 - edge.Y0)), edge.Direction));
                    }
                }
                crossings.Sort((a, b) => a.X.CompareTo(b.X));
                var winding = 0;
                for (var i = 0; i < crossings.Count - 1; i++)
                {
                    winding += area.EvenOdd ? 1 : crossings[i].Direction;
                    if (area.EvenOdd ? winding % 2 != 0 : winding != 0)
                    {
                        AddSpan(target, row * Width, crossings[i].X, crossings[i + 1].X, 1f / SubRows);
                    }
                }
            }
        }
    }

    private void AddSpan(float[] target, int rowStart, double from, double to, float weight)
    {
        from = Math.Max(0, from);
        to = Math.Min(Width, to);
        if (to <= from)
        {
            return;
        }
        var first = (int)from;
        var last = (int)to;
        if (first == last)
        {
            target[rowStart + first] += (float)(to - from) * weight;
            return;
        }
        target[rowStart + first] += (float)(first + 1 - from) * weight;
        for (var x = first + 1; x < last; x++)
        {
            target[rowStart + x] += weight;
        }
        if (last < Width)
        {
            target[rowStart + last] += (float)(to - last) * weight;
        }
    }

    /// <summary>Coverage of a stroke band: each pixel's distance to the nearest segment against the half width.</summary>
    private void Stroke(StrokeBand band, float[] target)
    {
        var half = band.HalfWidth * Scale;
        foreach (var line in band.Lines)
        {
            for (var i = 0; i + 1 < line.Length; i++)
            {
                var (ax, ay) = ToPixels(line[i]);
                var (bx, by) = ToPixels(line[i + 1]);
                // Long segments in pieces, so each piece only visits the pixels near it.
                var pieces = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay))) / 32));
                for (var piece = 0; piece < pieces; piece++)
                {
                    var t0 = (double)piece / pieces;
                    var t1 = (double)(piece + 1) / pieces;
                    Segment(target, ax + ((bx - ax) * t0), ay + ((by - ay) * t0), ax + ((bx - ax) * t1), ay + ((by - ay) * t1), half);
                }
            }
        }
    }

    private void Segment(float[] target, double ax, double ay, double bx, double by, double half)
    {
        var x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - half - 1));
        var x1 = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + half + 1));
        var y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - half - 1));
        var y1 = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(ay, by) + half + 1));
        var (dx, dy) = (bx - ax, by - ay);
        var lengthSquared = (dx * dx) + (dy * dy);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var (px, py) = (x + 0.5 - ax, y + 0.5 - ay);
                var t = lengthSquared > 0 ? Math.Clamp(((px * dx) + (py * dy)) / lengthSquared, 0, 1) : 0;
                var (ex, ey) = (px - (t * dx), py - (t * dy));
                var coverage = (float)Math.Clamp(half - Math.Sqrt((ex * ex) + (ey * ey)) + 0.5, 0, 1);
                var index = (y * Width) + x;
                target[index] = Math.Max(target[index], coverage);
            }
        }
    }

    private (double X, double Y) ToPixels(Point point) => ((point.X - X) * Scale, (point.Y - Y) * Scale);
}
