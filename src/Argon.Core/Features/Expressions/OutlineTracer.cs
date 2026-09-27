namespace Argon.Features.Expressions;

using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// Traces an alpha channel into a sticker outline: an SVG path in a 512×512 view box, encoded with
/// <see cref="OutlineCodec"/>.
/// </summary>
public static class OutlineTracer
{
    private const int    MaskSide        = 128;
    private const int    PointBudget     = 400;
    private const double BaseTolerance   = 2;
    private const int    Retries         = 4;
    private const double ViewBox         = 512;
    private const double MinContourArea  = 48;
    private const byte   OpaqueThreshold = 127;

    public static byte[]? FromAlpha(Image<Rgba32> image, int maxBytes = ExpressionLimits.OutlineMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(image);

        var width = image.Width;
        var alpha = new byte[width * image.Height];

        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                var dst = alpha.AsSpan(y * width, width);
                for (var x = 0; x < width; x++)
                    dst[x] = row[x].A;
            }
        });

        return FromAlpha(alpha, width, image.Height, maxBytes);
    }

    public static byte[]? FromAlpha(ReadOnlySpan<byte> alpha, int width, int height, int maxBytes = ExpressionLimits.OutlineMaxBytes)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "dimensions must be positive");
        if (alpha.Length < width * height)
            throw new ArgumentException("the alpha buffer is smaller than width × height", nameof(alpha));

        var mask = Downsample(alpha, width, height, out var mw, out var mh, out var any, out var all);
        if (!any)
            return null;

        List<Vertex[]> contours = all
            ? [[new(0, 0), new(mw, 0), new(mw, mh), new(0, mh)]]
            : Trace(mask, mw, mh);

        // Mask coordinates to the 512 view box, aspect kept, centred.
        var scale   = ViewBox / Math.Max(width, height);
        var sx      = width / (double)mw * scale;
        var sy      = height / (double)mh * scale;
        var offsetX = (ViewBox - width * scale) / 2;
        var offsetY = (ViewBox - height * scale) / 2;

        var rings = contours
           .Select(c => Array.ConvertAll(c, p => new Vertex(offsetX + p.X * sx, offsetY + p.Y * sy)))
           .Select(c => (Points: c, Area: Math.Abs(SignedArea(c))))
           .Where(c => c.Area >= MinContourArea)
           .OrderByDescending(c => c.Area)
           .Select(c => c.Points)
           .ToList();

        if (rings.Count == 0)
            return null;

        var tolerance = BaseTolerance;

        for (var attempt = 0; attempt <= Retries; attempt++, tolerance *= 2)
        {
            // Null when even the largest contour is over the point budget at this tolerance.
            var path = BuildPath(rings, tolerance);
            if (path is null)
                continue;

            var bytes = OutlineCodec.Encode(path);
            if (bytes.Length <= maxBytes)
                return bytes;
        }

        return null;
    }

    private readonly record struct Vertex(double X, double Y);

    private static bool[] Downsample(ReadOnlySpan<byte> alpha, int width, int height, out int mw, out int mh, out bool any, out bool all)
    {
        var factor = Math.Max(1, (Math.Max(width, height) + MaskSide - 1) / MaskSide);

        mw  = (width + factor - 1) / factor;
        mh  = (height + factor - 1) / factor;
        any = false;
        all = true;

        var mask = new bool[mw * mh];

        for (var my = 0; my < mh; my++)
        {
            var y0 = my * factor;
            var y1 = Math.Min(y0 + factor, height);

            for (var mx = 0; mx < mw; mx++)
            {
                var x0    = mx * factor;
                var x1    = Math.Min(x0 + factor, width);
                var count = 0;

                for (var y = y0; y < y1; y++)
                {
                    var row = alpha.Slice(y * width + x0, x1 - x0);
                    foreach (var a in row)
                        if (a > OpaqueThreshold)
                            count++;
                }

                var set = count * 2 > (x1 - x0) * (y1 - y0);
                mask[my * mw + mx] =  set;
                any                |= set;
                all                &= set;
            }
        }

        return mask;
    }

    /// <summary>
    /// Marching squares over pixel centres with a transparent border, so every contour closes. Segments
    /// are oriented with the opaque side on the left, which gives holes the opposite winding to their
    /// outer contour; saddles join the opaque corners.
    /// </summary>
    private static List<Vertex[]> Trace(bool[] mask, int mw, int mh)
    {
        var w    = mw + 2;
        var h    = mh + 2;
        var next = new int[w * h * 2];
        Array.Fill(next, -1);

        Span<bool> corner = stackalloc bool[4];
        Span<int>  edge   = stackalloc int[4];

        for (var cy = 0; cy < h - 1; cy++)
        for (var cx = 0; cx < w - 1; cx++)
        {
            corner[0] = Sample(cx, cy);
            corner[1] = Sample(cx + 1, cy);
            corner[2] = Sample(cx + 1, cy + 1);
            corner[3] = Sample(cx, cy + 1);

            if (corner[0] == corner[1] && corner[1] == corner[2] && corner[2] == corner[3])
                continue;

            // Clockwise: top, right, bottom, left; edge k runs from corner k to corner k+1.
            edge[0] = HorizontalKey(cx, cy);
            edge[1] = VerticalKey(cx + 1, cy);
            edge[2] = HorizontalKey(cx, cy + 1);
            edge[3] = VerticalKey(cx, cy);

            var saddle = corner[0] == corner[2] && corner[1] == corner[3];
            var exit   = -1;

            for (var k = 0; k < 4; k++)
                if (corner[k] && !corner[(k + 1) & 3])
                    exit = k;

            for (var k = 0; k < 4; k++)
            {
                if (corner[k] || !corner[(k + 1) & 3])
                    continue;

                next[edge[k]] = edge[saddle ? (k + 3) & 3 : exit];
            }
        }

        var contours = new List<Vertex[]>();
        var ring     = new List<Vertex>();

        for (var start = 0; start < next.Length; start++)
        {
            if (next[start] < 0)
                continue;

            ring.Clear();
            var key = start;

            while (next[key] >= 0)
            {
                ring.Add(KeyToPoint(key));
                var following = next[key];
                next[key] = -1;
                key       = following;
            }

            if (ring.Count >= 3)
                contours.Add(ring.ToArray());
        }

        return contours;

        bool Sample(int px, int py)
            => px >= 1 && px <= mw && py >= 1 && py <= mh && mask[(py - 1) * mw + px - 1];

        int HorizontalKey(int px, int py) => (py * w + px) * 2;
        int VerticalKey(int px, int py)   => (py * w + px) * 2 + 1;

        // Padded sample (px, py) is mask pixel (px - 1, py - 1), centred at (px - 0.5, py - 0.5).
        Vertex KeyToPoint(int k)
        {
            var cell = k >> 1;
            var px   = cell % w;
            var py   = cell / w;
            return (k & 1) == 0 ? new Vertex(px, py - 0.5) : new Vertex(px - 0.5, py);
        }
    }

    private static string? BuildPath(List<Vertex[]> rings, double tolerance)
    {
        var path   = new StringBuilder();
        var budget = PointBudget;
        var xs     = new List<int>();
        var ys     = new List<int>();

        foreach (var ring in rings)
        {
            var simplified = Simplify(ring, tolerance);

            xs.Clear();
            ys.Clear();

            foreach (var p in simplified)
            {
                var x = (int)Math.Round(p.X);
                var y = (int)Math.Round(p.Y);
                if (xs.Count > 0 && xs[^1] == x && ys[^1] == y)
                    continue;
                xs.Add(x);
                ys.Add(y);
            }

            if (xs.Count > 1 && xs[0] == xs[^1] && ys[0] == ys[^1])
            {
                xs.RemoveAt(xs.Count - 1);
                ys.RemoveAt(ys.Count - 1);
            }

            if (xs.Count < 3)
                continue;
            if (xs.Count > budget)
                break;

            budget -= xs.Count;

            path.Append('M').Append(Format(xs[0])).Append(',').Append(Format(ys[0])).Append('l');

            for (var i = 1; i < xs.Count; i++)
            {
                AppendDelta(path, xs[i] - xs[i - 1], first: i == 1);
                AppendDelta(path, ys[i] - ys[i - 1], first: false);
            }

            path.Append('z');
        }

        return path.Length == 0 ? null : path.ToString();

        static void AppendDelta(StringBuilder path, int value, bool first)
        {
            // A minus sign separates on its own; a comma before it would cost a byte.
            if (!first && value >= 0)
                path.Append(',');
            path.Append(Format(value));
        }

        static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Douglas–Peucker on a closed ring, anchored at vertex 0 and the vertex farthest from it.</summary>
    private static List<Vertex> Simplify(Vertex[] ring, double tolerance)
    {
        var n = ring.Length;
        if (n <= 4)
            return [.. ring];

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

        var keep = new bool[n];
        keep[0]   = true;
        keep[far] = true;

        var stack = new Stack<(int From, int To)>();
        stack.Push((0, far));
        stack.Push((far, n));

        while (stack.TryPop(out var span))
        {
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

            keep[index] = true;
            stack.Push((span.From, index));
            stack.Push((index, span.To));
        }

        var result = new List<Vertex>();
        for (var i = 0; i < n; i++)
            if (keep[i])
                result.Add(ring[i]);
        return result;
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

    private static double SignedArea(Vertex[] ring)
    {
        var sum = 0.0;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            sum += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
        return sum / 2;
    }
}
