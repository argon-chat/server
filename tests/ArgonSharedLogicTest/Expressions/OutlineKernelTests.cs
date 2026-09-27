namespace ArgonSharedLogicTest.Expressions;

using System.Numerics;
using System.Runtime.Intrinsics.X86;
using Argon.Features.Expressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// The vectorised tracer against its scalar kernels and against the tracer it replaced, which the client's
/// <c>outlineTrace.ts</c> reproduces: every mode has to give the same bytes.
/// </summary>
[TestFixture]
public class OutlineKernelTests
{
    // Resolve falls back when a mode is missing, so only what this CPU runs is listed.
    private static readonly OutlineKernelMode[] VectorModes =
    [
        .. Avx2.IsSupported ? [OutlineKernelMode.Vector256] : Array.Empty<OutlineKernelMode>(),
        .. Sse2.IsSupported ? [OutlineKernelMode.Vector128] : Array.Empty<OutlineKernelMode>(),
        .. Vector.IsHardwareAccelerated ? [OutlineKernelMode.VectorT] : Array.Empty<OutlineKernelMode>()
    ];

    private static readonly OutlineKernelMode[] AllModes = [.. VectorModes, OutlineKernelMode.Scalar, OutlineKernelMode.Auto];

    private static readonly int[] Lengths =
    [
        .. Enumerable.Range(0, 70), 95, 96, 97, 127, 128, 129, 255, 256, 257, 511, 512, 513, 1000, 4099
    ];

    /// <summary>Alpha values that sit on the threshold as often as they sit far from it.</summary>
    private static byte[] RandomAlpha(Random random, int length)
    {
        var alpha = new byte[length];
        for (var i = 0; i < length; i++)
            alpha[i] = random.Next(3) switch
            {
                0 => (byte)random.Next(126, 130),
                1 => random.Next(2) == 0 ? (byte)0 : (byte)255,
                _ => (byte)random.Next(256)
            };
        return alpha;
    }

    private static byte[] RandomBits(Random random, int length)
    {
        var bits = new byte[length];
        for (var i = 0; i < length; i++)
            bits[i] = (byte)random.Next(2);
        return bits;
    }

    [Test]
    public void The_modes_under_test_include_every_vector_width_this_cpu_has()
        => Assert.That(VectorModes.Select(OutlineKernels.Resolve), Is.EqualTo(VectorModes), "a mode resolved to another");

    // ── kernels ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Threshold_matches_its_scalar_reference()
    {
        var random = new Random(1);

        foreach (var mode in VectorModes)
        foreach (var length in Lengths)
        {
            var alpha    = RandomAlpha(random, length);
            var expected = new byte[length];
            var actual   = new byte[length];

            OutlineKernels.ThresholdScalar(alpha, expected);
            OutlineKernels.Threshold(alpha, actual, mode);

            Assert.That(actual, Is.EqualTo(expected), $"{mode}, {length} bytes");
        }
    }

    [Test]
    public void Accumulating_opaque_counts_matches_its_scalar_reference()
    {
        var random = new Random(2);

        foreach (var mode in VectorModes)
        foreach (var length in Lengths)
        {
            var alpha    = RandomAlpha(random, length);
            var expected = new byte[length + 5];
            random.NextBytes(expected);
            var actual = expected.ToArray();

            OutlineKernels.AccumulateOpaqueScalar(alpha, expected);
            OutlineKernels.AccumulateOpaque(alpha, actual, mode);

            Assert.That(actual, Is.EqualTo(expected), $"{mode}, {length} bytes");
        }
    }

    [Test]
    public void Box_majority_matches_its_scalar_reference_for_every_box_width()
    {
        var random  = new Random(3);
        int[] factors = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 24, 31, 32, 33, 40, 63, 64, 65, 100, 255];

        foreach (var mode in VectorModes)
        foreach (var factor in factors)
        foreach (var width in new[] { 1, 2, 3, factor - 1, factor, factor + 1, 5 * factor + 3, 128 * factor - 1, 128 * factor }.Where(x => x > 0).Distinct())
        {
            var cells  = (width + factor - 1) / factor;
            var rows   = random.Next(1, factor + 1);
            var counts = new byte[cells * factor + 32];

            // Past the width is noise: a box must not read it.
            random.NextBytes(counts);

            var expected = new byte[cells];
            var actual   = new byte[cells];

            OutlineKernels.BoxMajorityScalar(counts, width, factor, rows, expected);
            OutlineKernels.BoxMajority(counts, width, factor, rows, actual, mode);

            Assert.That(actual, Is.EqualTo(expected), $"{mode}, factor {factor}, width {width}, rows {rows}");
        }
    }

    [Test]
    public void Cell_classification_matches_its_scalar_reference()
    {
        var random = new Random(4);

        foreach (var mode in VectorModes)
        foreach (var length in Lengths.Where(x => x >= 2))
        {
            var top      = RandomBits(random, length);
            var bottom   = RandomBits(random, length);
            var expected = new byte[length - 1];
            var actual   = new byte[length - 1];

            OutlineKernels.ClassifyScalar(top, bottom, expected);
            OutlineKernels.Classify(top, bottom, actual, mode);

            Assert.That(actual, Is.EqualTo(expected), $"{mode}, {length} samples");
        }
    }

    [Test]
    public void Alpha_extraction_matches_its_scalar_reference()
    {
        var random = new Random(5);

        foreach (var mode in VectorModes)
        foreach (var pixels in Lengths)
        {
            var rgba = new byte[pixels * 4];
            random.NextBytes(rgba);

            var expected = new byte[pixels];
            var actual   = new byte[pixels];

            OutlineKernels.ExtractAlphaScalar(rgba, expected);
            OutlineKernels.ExtractAlpha(rgba, actual, mode);

            Assert.That(actual, Is.EqualTo(expected), $"{mode}, {pixels} pixels");
        }
    }

    private static readonly (int Width, int Height)[] Sizes =
    [
        (1, 1), (1, 300), (300, 1), (2, 2), (7, 5), (31, 33), (100, 100), (128, 128), (129, 129), (255, 17), (256, 256),
        (257, 3), (300, 512), (512, 512), (513, 511), (1000, 37), (4100, 9), (9000, 40)
    ];

    [Test]
    public void Downsampling_matches_its_scalar_reference()
    {
        var random = new Random(6);

        foreach (var mode in VectorModes)
        foreach (var (width, height) in Sizes)
        foreach (var fill in new[] { -1, 0, 255 })
        {
            var alpha = fill < 0 ? RandomAlpha(random, width * height) : Enumerable.Repeat((byte)fill, width * height).ToArray();

            var factor = Math.Max(1, (Math.Max(width, height) + 127) / 128);
            var cells  = ((width + factor - 1) / factor + 2) * ((height + factor - 1) / factor + 2);

            var expected = new byte[cells];
            var actual   = new byte[cells];

            OutlineKernels.DownsampleScalar(alpha, width, height, factor, expected, out var expectedAny, out var expectedAll);
            OutlineKernels.Downsample(alpha, width, height, factor, actual, mode, out var any, out var all);

            Assert.That((actual, any, all), Is.EqualTo((expected, expectedAny, expectedAll)), $"{mode}, {width}×{height}, fill {fill}");
        }
    }

    // ── the whole trace against the tracer it replaced ─────────────────────────────────────────

    private static IEnumerable<(string Name, int Width, int Height, byte[] Alpha)> Shapes()
    {
        var random = new Random(7);

        foreach (var (width, height) in Sizes.Concat([(64, 64), (200, 120), (511, 512), (777, 300), (2048, 2048)]))
        {
            yield return ($"disc {width}×{height}", width, height, Disc(width, height, 0.4, 0));
            yield return ($"ring {width}×{height}", width, height, Disc(width, height, 0.45, 0.2));
            yield return ($"rectangles {width}×{height}", width, height, Rectangles(random, width, height));
            yield return ($"blocks {width}×{height}", width, height, Blocks(random, width, height));
            yield return ($"checkerboard {width}×{height}", width, height, Checkerboard(width, height));
            yield return ($"noise {width}×{height}", width, height, RandomAlpha(random, width * height));
        }

        yield return ("opaque 512×512", 512, 512, Enumerable.Repeat((byte)255, 512 * 512).ToArray());
        yield return ("opaque 512×256", 512, 256, Enumerable.Repeat((byte)128, 512 * 256).ToArray());
        yield return ("threshold 64×64", 64, 64, Enumerable.Repeat((byte)127, 64 * 64).ToArray());
        yield return ("wide band 40000×3", 40000, 3, Blocks(random, 40000, 3));
    }

    private static byte[] Disc(int width, int height, double radius, double hole)
    {
        var alpha = new byte[width * height];
        var r     = Math.Min(width, height) * radius;
        var inner = Math.Min(width, height) * hole;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var d = Math.Sqrt((x + 0.5 - width / 2.0) * (x + 0.5 - width / 2.0) + (y + 0.5 - height / 2.0) * (y + 0.5 - height / 2.0));
            alpha[y * width + x] = d <= r && d >= inner ? (byte)255 : (byte)0;
        }
        return alpha;
    }

    private static byte[] Rectangles(Random random, int width, int height)
    {
        var alpha = new byte[width * height];
        for (var n = random.Next(1, 9); n > 0; n--)
        {
            int x0 = random.Next(width), x1 = random.Next(x0, width + 1), y0 = random.Next(height), y1 = random.Next(y0, height + 1);
            var value = (byte)random.Next(120, 256);
            for (var y = y0; y < y1; y++)
                alpha.AsSpan(y * width + x0, x1 - x0).Fill(value);
        }
        return alpha;
    }

    private static byte[] Blocks(Random random, int width, int height)
    {
        var alpha = new byte[width * height];
        var side  = random.Next(1, 17);
        for (var by = 0; by < height; by += side)
        for (var bx = 0; bx < width; bx += side)
        {
            if (random.Next(3) != 0)
                continue;
            for (var y = by; y < Math.Min(by + side, height); y++)
                alpha.AsSpan(y * width + bx, Math.Min(side, width - bx)).Fill(255);
        }
        return alpha;
    }

    /// <summary>Alternating mask cells: saddles everywhere.</summary>
    private static byte[] Checkerboard(int width, int height)
    {
        var factor = Math.Max(1, (Math.Max(width, height) + 127) / 128);
        var alpha  = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            alpha[y * width + x] = (x / factor + y / factor) % 2 == 0 ? (byte)255 : (byte)0;
        return alpha;
    }

    [Test]
    public void Every_mode_traces_the_same_bytes_as_the_tracer_it_replaced()
    {
        var traced = 0;

        foreach (var (name, width, height, alpha) in Shapes())
        foreach (var maxBytes in new[] { 1024, 300, 60, 4 })
        {
            var expected = LegacyOutlineTracer.FromAlpha(alpha, width, height, maxBytes);
            traced += expected is null ? 0 : 1;

            foreach (var mode in AllModes)
                Assert.That(OutlineTracer.FromAlpha(alpha, width, height, maxBytes, mode), Is.EqualTo(expected), $"{name}, {maxBytes} bytes, {mode}");
        }

        Assert.That(traced, Is.GreaterThan(100), "the corpus hardly produced an outline");
    }

    [Test]
    public void The_image_overload_reads_the_same_alpha_in_every_mode()
    {
        var random = new Random(8);

        foreach (var (width, height) in new[] { (1, 1), (77, 45), (300, 512), (512, 512), (1023, 3) })
        {
            using var image = new Image<Rgba32>(width, height);
            var       alpha = RandomAlpha(random, width * height);
            var       rgb   = new byte[3];

            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                random.NextBytes(rgb);
                image[x, y] = new Rgba32(rgb[0], rgb[1], rgb[2], alpha[y * width + x]);
            }

            var expected = LegacyOutlineTracer.FromAlpha(image);

            foreach (var mode in AllModes)
                Assert.That(OutlineTracer.FromImage(image, ExpressionLimits.OutlineMaxBytes, mode), Is.EqualTo(expected), $"{width}×{height}, {mode}");
        }
    }

    // ── allocations ─────────────────────────────────────────────────────────────────────────────

    // A byte[] on a 64-bit runtime: header, length, then the data rounded up to 8.
    private static long ArrayBytes(int length) => 24 + ((length + 7L) & ~7L);

    private static long Allocated(Func<byte[]?> trace, out byte[]? result)
    {
        for (var i = 0; i < 3; i++)
            trace();

        var before = GC.GetAllocatedBytesForCurrentThread();
        result = trace();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public void A_warm_sticker_trace_allocates_only_its_result()
    {
        using var image = TestImages.Disc(512, 512, 256, 256, 220, hole: 100);
        var alpha = TestImages.Alpha(image);

        foreach (var mode in AllModes)
        {
            var allocated = Allocated(() => OutlineTracer.FromAlpha(alpha, 512, 512, 1024, mode), out var bytes);

            Assert.That(bytes, Is.Not.Null);
            Assert.That(allocated, Is.EqualTo(ArrayBytes(bytes!.Length)), mode.ToString());
        }
    }

    [Test]
    public void A_warm_emoji_trace_allocates_only_its_result()
    {
        using var image = TestImages.Disc(100, 100);
        var alpha = TestImages.Alpha(image);

        var allocated = Allocated(() => OutlineTracer.FromAlpha(alpha, 100, 100), out var bytes);

        Assert.That(bytes, Is.Not.Null);
        Assert.That(allocated, Is.EqualTo(ArrayBytes(bytes!.Length)));
    }

    [Test]
    public void A_warm_trace_of_an_image_allocates_only_its_result()
    {
        using var image = TestImages.Disc(512, 512, 256, 256, 220, hole: 100);

        var allocated = Allocated(() => OutlineTracer.FromAlpha(image), out var bytes);

        Assert.That(bytes, Is.Not.Null);
        Assert.That(allocated, Is.EqualTo(ArrayBytes(bytes!.Length)));
    }

    [Test]
    public void A_warm_trace_with_nothing_opaque_allocates_nothing()
    {
        var alpha = new byte[512 * 512];

        Assert.That(Allocated(() => OutlineTracer.FromAlpha(alpha, 512, 512), out _), Is.Zero);
    }
}
