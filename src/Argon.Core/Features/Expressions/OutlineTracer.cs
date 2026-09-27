namespace Argon.Features.Expressions;

using System.Buffers;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// Traces an alpha channel into a sticker outline: an SVG path in a 512×512 view box, encoded with
/// <see cref="OutlineCodec"/>.
/// </summary>
/// <remarks>The client's <c>outlineTrace.ts</c> reproduces the output byte for byte.</remarks>
public static class OutlineTracer
{
    private const int    MaskSide       = 128;
    private const int    PointBudget    = 400;
    private const double BaseTolerance  = 2;
    private const int    Retries        = 4;
    private const double ViewBox        = 512;
    private const double MinContourArea = 48;

    // OutlineCodec bytes: 192 + table index for a symbol, a flag on the first chunk of a number.
    private const byte MoveTo    = 192 + 12;
    private const byte LineTo    = 192 + 37;
    private const byte ClosePath = 192 + 51;
    private const byte Comma     = 192 + 63;
    private const byte CommaFlag = 128;
    private const byte MinusFlag = 64;

    // 400 points of two numbers of at most four bytes, and three symbols per ring.
    private const int OutputCapacity = 4096;

    private const byte None = 0xFF;

    // Per cell code, up to two (entry edge, exit edge) pairs; edges 0..3 are top, right, bottom, left.
    // Segments keep the opaque side on the left; saddles (5, 10) join the opaque corners.
    private static ReadOnlySpan<byte> CellLinks =>
    [
        None, None, None, None,
        3, 0, None, None,
        0, 1, None, None,
        3, 1, None, None,
        1, 2, None, None,
        1, 0, 3, 2,
        0, 2, None, None,
        3, 2, None, None,
        2, 3, None, None,
        2, 0, None, None,
        0, 3, 2, 1,
        2, 1, None, None,
        1, 3, None, None,
        1, 0, None, None,
        0, 3, None, None,
        None, None, None, None
    ];

    /// <summary>The kernels the public overloads use; the benchmarks pin it.</summary>
    internal static OutlineKernelMode KernelMode { get; set; } = OutlineKernelMode.Auto;

    public static byte[]? FromAlpha(Image<Rgba32> image, int maxBytes = ExpressionLimits.OutlineMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        return FromImage(image, maxBytes, KernelMode);
    }

    public static byte[]? FromAlpha(ReadOnlySpan<byte> alpha, int width, int height, int maxBytes = ExpressionLimits.OutlineMaxBytes)
        => FromAlpha(alpha, width, height, maxBytes, KernelMode);

    internal static byte[]? FromImage(Image<Rgba32> image, int maxBytes, OutlineKernelMode mode)
    {
        mode = OutlineKernels.Resolve(mode);

        var width  = image.Width;
        var height = image.Height;
        var alpha  = ArrayPool<byte>.Shared.Rent(width * height);

        try
        {
            for (var y = 0; y < height; y++)
                OutlineKernels.ExtractAlpha(MemoryMarshal.AsBytes(image.DangerousGetPixelRowMemory(y).Span), alpha.AsSpan(y * width, width), mode);

            return FromAlpha(alpha.AsSpan(0, width * height), width, height, maxBytes, mode);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(alpha);
        }
    }

    internal static byte[]? FromAlpha(ReadOnlySpan<byte> alpha, int width, int height, int maxBytes, OutlineKernelMode mode)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "dimensions must be positive");
        if (alpha.Length < width * height)
            throw new ArgumentException("the alpha buffer is smaller than width × height", nameof(alpha));

        mode = OutlineKernels.Resolve(mode);

        var factor = Math.Max(1, (Math.Max(width, height) + MaskSide - 1) / MaskSide);
        var mw     = (width + factor - 1) / factor;
        var mh     = (height + factor - 1) / factor;
        var cells  = (mw + 2) * (mh + 2);
        var grid   = ArrayPool<byte>.Shared.Rent(cells);

        try
        {
            var mask = grid.AsSpan(0, cells);
            mask.Clear();

            OutlineKernels.Downsample(alpha, width, height, factor, mask, mode, out var any, out var all);

            return any ? Outline(mask, mw, mh, all, width, height, maxBytes, mode) : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grid);
        }
    }

    private readonly record struct Vertex(double X, double Y);

    private readonly record struct Ring(double Area, int Start, int Count, int Order) : IComparable<Ring>
    {
        // Largest first; the order found breaks ties, as a stable sort would.
        public int CompareTo(Ring other)
        {
            var byArea = other.Area.CompareTo(Area);
            return byArea != 0 ? byArea : Order.CompareTo(other.Order);
        }
    }

    private readonly record struct Interval(int From, int To);

    private static byte[]? Outline(Span<byte> mask, int mw, int mh, bool all, int width, int height, int maxBytes, OutlineKernelMode mode)
    {
        var w = mw + 2;
        var h = mh + 2;

        // Mask coordinates to the 512 view box, aspect kept, centred.
        var scale   = ViewBox / Math.Max(width, height);
        var sx      = width / (double)mw * scale;
        var sy      = height / (double)mh * scale;
        var offsetX = (ViewBox - width * scale) / 2;
        var offsetY = (ViewBox - height * scale) / 2;

        int[]?    next     = null;
        Vertex[]? vertices = null;
        Ring[]?   rings    = null;

        try
        {
            var vertexCount = 0;
            var ringCount   = 0;

            if (all)
            {
                vertices    = ArrayPool<Vertex>.Shared.Rent(4);
                rings       = ArrayPool<Ring>.Shared.Rent(1);
                vertices[0] = Map(0, 0);
                vertices[1] = Map(mw, 0);
                vertices[2] = Map(mw, mh);
                vertices[3] = Map(0, mh);
                vertexCount = 4;
                ringCount   = Keep(vertices, rings, 0, ref vertexCount, 0);
            }
            else
            {
                next = ArrayPool<int>.Shared.Rent(w * h * 2);
                var links = next.AsSpan(0, w * h * 2);
                links.Fill(-1);

                var linked = Link(mask, w, h, links, mode);

                vertices = ArrayPool<Vertex>.Shared.Rent(linked);
                rings    = ArrayPool<Ring>.Shared.Rent(linked / 3 + 1);

                for (var start = 0;; start++)
                {
                    var skip = links[start..].IndexOfAnyExcept(-1);
                    if (skip < 0)
                        break;

                    start += skip;

                    var first = vertexCount;
                    var key   = start;

                    while (links[key] >= 0)
                    {
                        // Padded sample (px, py) is mask pixel (px - 1, py - 1), centred at (px - 0.5, py - 0.5).
                        var cell = key >> 1;
                        var px   = cell % w;
                        var py   = cell / w;
                        vertices[vertexCount++] = (key & 1) == 0 ? Map(px, py - 0.5) : Map(px - 0.5, py);

                        var following = links[key];
                        links[key] = -1;
                        key        = following;
                    }

                    ringCount = Keep(vertices, rings, first, ref vertexCount, ringCount);
                }
            }

            if (ringCount == 0)
                return null;

            var kept = rings.AsSpan(0, ringCount);
            kept.Sort();

            return Encode(vertices.AsSpan(0, vertexCount), kept, maxBytes);
        }
        finally
        {
            if (next is not null)
                ArrayPool<int>.Shared.Return(next);
            if (vertices is not null)
                ArrayPool<Vertex>.Shared.Return(vertices);
            if (rings is not null)
                ArrayPool<Ring>.Shared.Return(rings);
        }

        Vertex Map(double x, double y) => new(offsetX + x * sx, offsetY + y * sy);
    }

    /// <summary>Keeps the ring just collected when it is large enough, or gives its vertices back.</summary>
    private static int Keep(Vertex[] vertices, Ring[] rings, int first, ref int vertexCount, int ringCount)
    {
        var count = vertexCount - first;

        if (count >= 3)
        {
            var area = Math.Abs(SignedArea(vertices.AsSpan(first, count)));
            if (area >= MinContourArea)
            {
                rings[ringCount] = new Ring(area, first, count, ringCount);
                return ringCount + 1;
            }
        }

        vertexCount = first;
        return ringCount;
    }

    /// <summary>
    /// Marching squares over pixel centres with a transparent border, so every contour closes. Returns
    /// how many edges were linked.
    /// </summary>
    private static int Link(ReadOnlySpan<byte> mask, int w, int h, Span<int> next, OutlineKernelMode mode)
    {
        Span<byte> codes = stackalloc byte[w - 1];
        Span<int>  edge  = stackalloc int[4];

        var linked = 0;

        for (var cy = 0; cy < h - 1; cy++)
        {
            OutlineKernels.Classify(mask.Slice(cy * w, w), mask.Slice((cy + 1) * w, w), codes, mode);

            for (var cx = 0;; cx++)
            {
                var skip = codes[cx..].IndexOfAnyExcept((byte)0, (byte)15);
                if (skip < 0)
                    break;

                cx += skip;

                edge[0] = (cy * w + cx) * 2;
                edge[1] = (cy * w + cx + 1) * 2 + 1;
                edge[2] = ((cy + 1) * w + cx) * 2;
                edge[3] = (cy * w + cx) * 2 + 1;

                var pairs = CellLinks.Slice(codes[cx] * 4, 4);

                next[edge[pairs[0]]] = edge[pairs[1]];
                linked++;

                if (pairs[2] != None)
                {
                    next[edge[pairs[2]]] = edge[pairs[3]];
                    linked++;
                }
            }
        }

        return linked;
    }

    private static byte[]? Encode(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<Ring> rings, int maxBytes)
    {
        var longest = 0;
        foreach (var ring in rings)
            longest = Math.Max(longest, ring.Count);

        var keep  = ArrayPool<bool>.Shared.Rent(longest);
        var stack = ArrayPool<Interval>.Shared.Rent(longest + 2);
        var xs    = ArrayPool<int>.Shared.Rent(longest);
        var ys    = ArrayPool<int>.Shared.Rent(longest);

        try
        {
            Span<byte> output    = stackalloc byte[OutputCapacity];
            var        tolerance = BaseTolerance;

            for (var attempt = 0; attempt <= Retries; attempt++, tolerance *= 2)
            {
                // Zero when even the largest contour is over the point budget at this tolerance.
                var length = BuildPath(vertices, rings, tolerance, keep, stack, xs, ys, output);
                if (length > 0 && length <= maxBytes)
                    return output[..length].ToArray();
            }

            return null;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(keep);
            ArrayPool<Interval>.Shared.Return(stack);
            ArrayPool<int>.Shared.Return(xs);
            ArrayPool<int>.Shared.Return(ys);
        }
    }

    /// <summary>
    /// Writes <c>M x,y l dx dy … z</c> per ring straight into <see cref="OutlineCodec"/> bytes, without the
    /// leading <c>M</c> and trailing <c>z</c> the format implies. Returns the length, zero for no ring.
    /// </summary>
    private static int BuildPath(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<Ring> rings, double tolerance, Span<bool> keep,
        Span<Interval> stack, Span<int> xs, Span<int> ys, Span<byte> output)
    {
        var length = 0;
        var budget = PointBudget;
        var any    = false;

        foreach (var ring in rings)
        {
            var points = vertices.Slice(ring.Start, ring.Count);
            var kept   = keep[..points.Length];

            Simplify(points, tolerance, kept, stack);

            var count = 0;

            for (var i = 0; i < points.Length; i++)
            {
                if (!kept[i])
                    continue;

                var x = (int)Math.Round(points[i].X);
                var y = (int)Math.Round(points[i].Y);
                if (count > 0 && xs[count - 1] == x && ys[count - 1] == y)
                    continue;

                xs[count] = x;
                ys[count] = y;
                count++;
            }

            if (count > 1 && xs[0] == xs[count - 1] && ys[0] == ys[count - 1])
                count--;

            if (count < 3)
                continue;
            if (count > budget)
                break;

            budget -= count;

            if (any)
                output[length++] = MoveTo;
            any = true;

            length = Number(output, length, xs[0], comma: false);
            length = Number(output, length, ys[0], comma: true);
            output[length++] = LineTo;

            for (var i = 1; i < count; i++)
            {
                // A minus sign separates on its own; a comma before it would cost a byte.
                var dx = xs[i] - xs[i - 1];
                var dy = ys[i] - ys[i - 1];
                length = Number(output, length, dx, comma: i > 1 && dx >= 0);
                length = Number(output, length, dy, comma: dy >= 0);
            }

            output[length++] = ClosePath;
        }

        return any ? length - 1 : 0;
    }

    /// <summary>
    /// One decimal number as <see cref="OutlineCodec.Encode"/> would write it: <paramref name="comma"/>
    /// says whether a ',' precedes it in the path, and the digits go in chunks of 0..63 without leading
    /// zeros, the first carrying the ',' or '-'.
    /// </summary>
    private static int Number(Span<byte> output, int length, int value, bool comma)
    {
        int flag;

        if (value < 0)
        {
            if (comma)
                output[length++] = Comma;
            flag = MinusFlag;
        }
        else
        {
            flag = comma ? CommaFlag : 0;
        }

        Span<byte> digits    = stackalloc byte[10];
        var        magnitude = value < 0 ? (uint)-(long)value : (uint)value;
        var        start     = digits.Length;

        do
        {
            digits[--start] =  (byte)(magnitude % 10);
            magnitude       /= 10;
        } while (magnitude != 0);

        for (var i = start; i < digits.Length;)
        {
            var chunk = (int)digits[i++];

            if (chunk != 0)
                while (i < digits.Length && chunk * 10 + digits[i] <= 63)
                    chunk = chunk * 10 + digits[i++];

            output[length++] = (byte)(flag | chunk);
            flag             = 0;
        }

        return length;
    }

    /// <summary>Douglas–Peucker on a closed ring, anchored at vertex 0 and the vertex farthest from it.</summary>
    private static void Simplify(ReadOnlySpan<Vertex> ring, double tolerance, Span<bool> keep, Span<Interval> stack)
    {
        var n = ring.Length;
        if (n <= 4)
        {
            keep.Fill(true);
            return;
        }

        var far  = 0;
        var best = -1.0;
        for (var i = 1; i < n; i++)
        {
            var d = Distance2(ring[0], ring[i]);
            if (d > best)
            {
                best = d;
                far  = i;
            }
        }

        keep.Clear();
        keep[0]   = true;
        keep[far] = true;

        var top = 0;
        stack[top++] = new Interval(0, far);
        stack[top++] = new Interval(far, n);

        while (top > 0)
        {
            var span = stack[--top];
            if (span.To - span.From < 2)
                continue;

            var a     = ring[span.From];
            var b     = ring[span.To % n];
            var index = -1;
            var max   = tolerance;

            for (var i = span.From + 1; i < span.To; i++)
            {
                var d = SegmentDistance(ring[i], a, b);
                if (d > max)
                {
                    max   = d;
                    index = i;
                }
            }

            if (index < 0)
                continue;

            keep[index]  = true;
            stack[top++] = new Interval(span.From, index);
            stack[top++] = new Interval(index, span.To);
        }
    }

    private static double Distance2(Vertex a, Vertex b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static double SegmentDistance(Vertex p, Vertex a, Vertex b)
    {
        var dx     = b.X - a.X;
        var dy     = b.Y - a.Y;
        var length = dx * dx + dy * dy;

        if (length == 0)
            return Math.Sqrt(Distance2(p, a));

        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        return Math.Sqrt(Distance2(p, new Vertex(a.X + t * dx, a.Y + t * dy)));
    }

    private static double SignedArea(ReadOnlySpan<Vertex> ring)
    {
        var sum = 0.0;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            sum += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
        return sum / 2;
    }
}
