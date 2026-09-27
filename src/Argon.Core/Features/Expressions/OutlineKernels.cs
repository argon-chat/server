namespace Argon.Features.Expressions;

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

/// <summary>Which instructions the outline kernels use; <see cref="Auto"/> takes the widest the CPU has.</summary>
internal enum OutlineKernelMode
{
    Auto,
    Vector256,
    Vector128,
    VectorT,
    Scalar
}

/// <summary>
/// The per-pixel passes of <see cref="OutlineTracer"/>. Each vector kernel has a scalar reference it must
/// match byte for byte; the scalar one also finishes the tail a vector does not cover.
/// </summary>
internal static class OutlineKernels
{
    public const byte OpaqueThreshold = 127;

    // Loaded at 32 - n: the first n bytes set.
    private static ReadOnlySpan<byte> PrefixMask =>
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    public static OutlineKernelMode Resolve(OutlineKernelMode mode)
    {
        if (mode is OutlineKernelMode.Auto or OutlineKernelMode.Vector256 && Avx2.IsSupported)
            return OutlineKernelMode.Vector256;
        if (mode is OutlineKernelMode.Auto or OutlineKernelMode.Vector256 or OutlineKernelMode.Vector128 && Sse2.IsSupported)
            return OutlineKernelMode.Vector128;
        if (mode is not OutlineKernelMode.Scalar && Vector.IsHardwareAccelerated)
            return OutlineKernelMode.VectorT;
        return OutlineKernelMode.Scalar;
    }

    // ── alpha > threshold → 0/1 ────────────────────────────────────────────────────────────────

    public static void Threshold(ReadOnlySpan<byte> alpha, Span<byte> mask, OutlineKernelMode mode)
    {
        var n = alpha.Length;
        if (mask.Length < n)
            throw new ArgumentException("the mask is shorter than the alpha row", nameof(mask));

        ref var src = ref MemoryMarshal.GetReference(alpha);
        ref var dst = ref MemoryMarshal.GetReference(mask);
        nuint   i   = 0;

        switch (mode)
        {
            case OutlineKernelMode.Vector256:
            {
                // No unsigned compare on x86: flip the sign bit and compare signed.
                var bias  = Vector256.Create((byte)0x80);
                var limit = Vector256.Create(unchecked((sbyte)(OpaqueThreshold ^ 0x80)));
                var one   = Vector256.Create((byte)1);
                for (; i + 32 <= (nuint)n; i += 32)
                {
                    var above = Avx2.CompareGreaterThan((Vector256.LoadUnsafe(ref src, i) ^ bias).AsSByte(), limit).AsByte();
                    (above & one).StoreUnsafe(ref dst, i);
                }
                break;
            }
            case OutlineKernelMode.Vector128:
            {
                var bias  = Vector128.Create((byte)0x80);
                var limit = Vector128.Create(unchecked((sbyte)(OpaqueThreshold ^ 0x80)));
                var one   = Vector128.Create((byte)1);
                for (; i + 16 <= (nuint)n; i += 16)
                {
                    var above = Sse2.CompareGreaterThan((Vector128.LoadUnsafe(ref src, i) ^ bias).AsSByte(), limit).AsByte();
                    (above & one).StoreUnsafe(ref dst, i);
                }
                break;
            }
            case OutlineKernelMode.VectorT:
            {
                var limit = new Vector<byte>(OpaqueThreshold);
                var step  = (nuint)Vector<byte>.Count;
                for (; i + step <= (nuint)n; i += step)
                    (Vector.GreaterThan(Vector.LoadUnsafe(ref src, i), limit) & Vector<byte>.One).StoreUnsafe(ref dst, i);
                break;
            }
        }

        ThresholdScalar(alpha[(int)i..], mask[(int)i..n]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThresholdScalar(ReadOnlySpan<byte> alpha, Span<byte> mask)
    {
        for (var i = 0; i < alpha.Length; i++)
            mask[i] = alpha[i] > OpaqueThreshold ? (byte)1 : (byte)0;
    }

    // ── counts[x] += alpha[x] > threshold ──────────────────────────────────────────────────────

    /// <summary>Adds one opaque row into per-column counts; a band of more than 255 rows wraps.</summary>
    public static void AccumulateOpaque(ReadOnlySpan<byte> alpha, Span<byte> counts, OutlineKernelMode mode)
    {
        var n = alpha.Length;
        if (counts.Length < n)
            throw new ArgumentException("the counts are shorter than the alpha row", nameof(counts));

        ref var src = ref MemoryMarshal.GetReference(alpha);
        ref var dst = ref MemoryMarshal.GetReference(counts);
        nuint   i   = 0;

        // A true lane of a compare is 0xFF, so subtracting it adds one.
        switch (mode)
        {
            case OutlineKernelMode.Vector256:
            {
                var bias  = Vector256.Create((byte)0x80);
                var limit = Vector256.Create(unchecked((sbyte)(OpaqueThreshold ^ 0x80)));
                for (; i + 32 <= (nuint)n; i += 32)
                {
                    var above = Avx2.CompareGreaterThan((Vector256.LoadUnsafe(ref src, i) ^ bias).AsSByte(), limit).AsByte();
                    (Vector256.LoadUnsafe(ref dst, i) - above).StoreUnsafe(ref dst, i);
                }
                break;
            }
            case OutlineKernelMode.Vector128:
            {
                var bias  = Vector128.Create((byte)0x80);
                var limit = Vector128.Create(unchecked((sbyte)(OpaqueThreshold ^ 0x80)));
                for (; i + 16 <= (nuint)n; i += 16)
                {
                    var above = Sse2.CompareGreaterThan((Vector128.LoadUnsafe(ref src, i) ^ bias).AsSByte(), limit).AsByte();
                    (Vector128.LoadUnsafe(ref dst, i) - above).StoreUnsafe(ref dst, i);
                }
                break;
            }
            case OutlineKernelMode.VectorT:
            {
                var limit = new Vector<byte>(OpaqueThreshold);
                var step  = (nuint)Vector<byte>.Count;
                for (; i + step <= (nuint)n; i += step)
                    (Vector.LoadUnsafe(ref dst, i) - Vector.GreaterThan(Vector.LoadUnsafe(ref src, i), limit)).StoreUnsafe(ref dst, i);
                break;
            }
        }

        AccumulateOpaqueScalar(alpha[(int)i..], counts[(int)i..n]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AccumulateOpaqueScalar(ReadOnlySpan<byte> alpha, Span<byte> counts)
    {
        for (var i = 0; i < alpha.Length; i++)
            if (alpha[i] > OpaqueThreshold)
                counts[i]++;
    }

    // ── box sums → majority ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One mask row from the column counts of a band of <paramref name="rows"/> rows: cell x is set when
    /// more than half of its <paramref name="factor"/>-wide box is opaque. The vector path reads up to 32
    /// bytes past <paramref name="width"/> (masked off), so it needs <c>counts.Length ≥ width + 32</c>.
    /// </summary>
    public static void BoxMajority(ReadOnlySpan<byte> counts, int width, int factor, int rows, Span<byte> mask, OutlineKernelMode mode)
    {
        if (mode is not (OutlineKernelMode.Vector256 or OutlineKernelMode.Vector128) || counts.Length < width + 32
         || (long)(mask.Length - 1) * factor >= width)
        {
            BoxMajorityScalar(counts, width, factor, rows, mask);
            return;
        }

        ref var src  = ref MemoryMarshal.GetReference(counts);
        ref var bits = ref MemoryMarshal.GetReference(PrefixMask);

        for (var x = 0; x < mask.Length; x++)
        {
            var x0    = x * factor;
            var span  = Math.Min(factor, width - x0);
            var count = mode == OutlineKernelMode.Vector256 ? SumAvx2(ref src, x0, span, ref bits) : SumSse2(ref src, x0, span, ref bits);
            mask[x] = count * 2 > span * rows ? (byte)1 : (byte)0;
        }
    }

    private static int SumSse2(ref byte src, int start, int length, ref byte bits)
    {
        var total = 0;

        for (; length > 16; start += 16, length -= 16)
            total += Sad(Vector128.LoadUnsafe(ref src, (nuint)start));

        return total + Sad(Vector128.LoadUnsafe(ref src, (nuint)start) & Vector128.LoadUnsafe(ref bits, (nuint)(32 - length)));

        static int Sad(Vector128<byte> v)
        {
            var sums = Sse2.SumAbsoluteDifferences(v, Vector128<byte>.Zero);
            return sums.GetElement(0) + sums.GetElement(4);
        }
    }

    private static int SumAvx2(ref byte src, int start, int length, ref byte bits)
    {
        var total = 0;

        for (; length > 32; start += 32, length -= 32)
            total += Sad(Vector256.LoadUnsafe(ref src, (nuint)start));

        if (length > 16)
            return total + Sad(Vector256.LoadUnsafe(ref src, (nuint)start) & Vector256.LoadUnsafe(ref bits, (nuint)(32 - length)));

        var tail = Sse2.SumAbsoluteDifferences(
            Vector128.LoadUnsafe(ref src, (nuint)start) & Vector128.LoadUnsafe(ref bits, (nuint)(32 - length)), Vector128<byte>.Zero);
        return total + tail.GetElement(0) + tail.GetElement(4);

        static int Sad(Vector256<byte> v)
        {
            var sums  = Avx2.SumAbsoluteDifferences(v, Vector256<byte>.Zero).AsUInt64();
            var halves = sums.GetLower() + sums.GetUpper();
            return (int)(halves.GetElement(0) + halves.GetElement(1));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void BoxMajorityScalar(ReadOnlySpan<byte> counts, int width, int factor, int rows, Span<byte> mask)
    {
        for (var x = 0; x < mask.Length; x++)
        {
            var x0    = x * factor;
            var x1    = Math.Min(x0 + factor, width);
            var count = 0;

            for (var i = x0; i < x1; i++)
                count += counts[i];

            mask[x] = count * 2 > (x1 - x0) * rows ? (byte)1 : (byte)0;
        }
    }

    // ── downsample ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Majority-downsamples the alpha channel by <paramref name="factor"/> into the interior of a
    /// zeroed (mw + 2) × (mh + 2) mask of 0/1, leaving its one-cell border clear.
    /// </summary>
    public static void Downsample(ReadOnlySpan<byte> alpha, int width, int height, int factor, Span<byte> padded, OutlineKernelMode mode,
        out bool any, out bool all)
    {
        // Byte counters hold a band of at most 255 rows.
        if (mode == OutlineKernelMode.Scalar || factor > byte.MaxValue)
        {
            DownsampleScalar(alpha, width, height, factor, padded, out any, out all);
            return;
        }

        var mw     = (width + factor - 1) / factor;
        var mh     = (height + factor - 1) / factor;
        var stride = mw + 2;
        var counts = factor == 1 ? null : ArrayPool<byte>.Shared.Rent(mw * factor + 32);

        any = false;
        all = true;

        try
        {
            for (var my = 0; my < mh; my++)
            {
                var row = padded.Slice((my + 1) * stride + 1, mw);
                var y0  = my * factor;
                var y1  = Math.Min(y0 + factor, height);

                if (counts is null)
                {
                    Threshold(alpha.Slice(y0 * width, width), row, mode);
                }
                else
                {
                    var sums = counts.AsSpan(0, mw * factor + 32);
                    sums.Clear();

                    for (var y = y0; y < y1; y++)
                        AccumulateOpaque(alpha.Slice(y * width, width), sums, mode);

                    BoxMajority(sums, width, factor, y1 - y0, row, mode);
                }

                any |= row.Contains((byte)1);
                all &= !row.Contains((byte)0);
            }
        }
        finally
        {
            if (counts is not null)
                ArrayPool<byte>.Shared.Return(counts);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void DownsampleScalar(ReadOnlySpan<byte> alpha, int width, int height, int factor, Span<byte> padded,
        out bool any, out bool all)
    {
        var mw     = (width + factor - 1) / factor;
        var mh     = (height + factor - 1) / factor;
        var stride = mw + 2;

        any = false;
        all = true;

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
                padded[(my + 1) * stride + mx + 1] =  set ? (byte)1 : (byte)0;
                any                                |= set;
                all                                &= set;
            }
        }
    }

    // ── marching-squares cell codes ────────────────────────────────────────────────────────────

    /// <summary>
    /// The 4-bit code of each cell between two padded mask rows of 0/1: bit 0 top-left, 1 top-right,
    /// 2 bottom-right, 3 bottom-left. <paramref name="codes"/> is one shorter than the rows.
    /// </summary>
    public static void Classify(ReadOnlySpan<byte> top, ReadOnlySpan<byte> bottom, Span<byte> codes, OutlineKernelMode mode)
    {
        var n = codes.Length;
        if (top.Length <= n || bottom.Length <= n)
            throw new ArgumentException("a row must be one longer than the codes", nameof(codes));

        ref var t = ref MemoryMarshal.GetReference(top);
        ref var b = ref MemoryMarshal.GetReference(bottom);
        ref var c = ref MemoryMarshal.GetReference(codes);
        nuint   i = 0;

        // Lanes hold 0 or 1, so a 16-bit shift by at most 3 never carries into the next byte.
        switch (mode)
        {
            case OutlineKernelMode.Vector256:
                for (; i + 32 <= (nuint)n; i += 32)
                {
                    var code = Vector256.LoadUnsafe(ref t, i)
                             | Avx2.ShiftLeftLogical(Vector256.LoadUnsafe(ref t, i + 1).AsUInt16(), 1).AsByte()
                             | Avx2.ShiftLeftLogical(Vector256.LoadUnsafe(ref b, i + 1).AsUInt16(), 2).AsByte()
                             | Avx2.ShiftLeftLogical(Vector256.LoadUnsafe(ref b, i).AsUInt16(), 3).AsByte();
                    code.StoreUnsafe(ref c, i);
                }
                break;
            case OutlineKernelMode.Vector128:
                for (; i + 16 <= (nuint)n; i += 16)
                {
                    var code = Vector128.LoadUnsafe(ref t, i)
                             | Sse2.ShiftLeftLogical(Vector128.LoadUnsafe(ref t, i + 1).AsUInt16(), 1).AsByte()
                             | Sse2.ShiftLeftLogical(Vector128.LoadUnsafe(ref b, i + 1).AsUInt16(), 2).AsByte()
                             | Sse2.ShiftLeftLogical(Vector128.LoadUnsafe(ref b, i).AsUInt16(), 3).AsByte();
                    code.StoreUnsafe(ref c, i);
                }
                break;
            case OutlineKernelMode.VectorT:
            {
                var step = (nuint)Vector<byte>.Count;
                for (; i + step <= (nuint)n; i += step)
                {
                    var code = Vector.LoadUnsafe(ref t, i)
                             | Vector.ShiftLeft(Vector.LoadUnsafe(ref t, i + 1), 1)
                             | Vector.ShiftLeft(Vector.LoadUnsafe(ref b, i + 1), 2)
                             | Vector.ShiftLeft(Vector.LoadUnsafe(ref b, i), 3);
                    code.StoreUnsafe(ref c, i);
                }
                break;
            }
        }

        ClassifyScalar(top[(int)i..], bottom[(int)i..], codes[(int)i..]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ClassifyScalar(ReadOnlySpan<byte> top, ReadOnlySpan<byte> bottom, Span<byte> codes)
    {
        for (var x = 0; x < codes.Length; x++)
            codes[x] = (byte)(top[x] | top[x + 1] << 1 | bottom[x + 1] << 2 | bottom[x] << 3);
    }

    // ── RGBA → alpha ───────────────────────────────────────────────────────────────────────────

    /// <summary>Every fourth byte of an RGBA row, the alpha of each pixel.</summary>
    public static void ExtractAlpha(ReadOnlySpan<byte> rgba, Span<byte> alpha, OutlineKernelMode mode)
    {
        var n = alpha.Length;
        if (rgba.Length < n * 4)
            throw new ArgumentException("the pixel row is shorter than the alpha row", nameof(rgba));

        ref var src = ref MemoryMarshal.GetReference(rgba);
        ref var dst = ref MemoryMarshal.GetReference(alpha);
        nuint   i   = 0;

        // A little-endian Rgba32 read as a uint has alpha in its top byte; shift it down, then pack.
        switch (mode)
        {
            case OutlineKernelMode.Vector256:
            {
                // The packs work per 128-bit lane; this puts the dwords back in pixel order.
                var order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);
                for (; i + 32 <= (nuint)n; i += 32)
                {
                    ref var p  = ref Unsafe.Add(ref src, i * 4);
                    var     a0 = Avx2.ShiftRightLogical(Vector256.LoadUnsafe(ref p, 0).AsUInt32(), 24).AsInt32();
                    var     a1 = Avx2.ShiftRightLogical(Vector256.LoadUnsafe(ref p, 32).AsUInt32(), 24).AsInt32();
                    var     a2 = Avx2.ShiftRightLogical(Vector256.LoadUnsafe(ref p, 64).AsUInt32(), 24).AsInt32();
                    var     a3 = Avx2.ShiftRightLogical(Vector256.LoadUnsafe(ref p, 96).AsUInt32(), 24).AsInt32();
                    var packed = Avx2.PackUnsignedSaturate(Avx2.PackSignedSaturate(a0, a1), Avx2.PackSignedSaturate(a2, a3));
                    Avx2.PermuteVar8x32(packed.AsInt32(), order).AsByte().StoreUnsafe(ref dst, i);
                }
                break;
            }
            case OutlineKernelMode.Vector128:
                for (; i + 16 <= (nuint)n; i += 16)
                {
                    ref var p  = ref Unsafe.Add(ref src, i * 4);
                    var     a0 = Sse2.ShiftRightLogical(Vector128.LoadUnsafe(ref p, 0).AsUInt32(), 24).AsInt32();
                    var     a1 = Sse2.ShiftRightLogical(Vector128.LoadUnsafe(ref p, 16).AsUInt32(), 24).AsInt32();
                    var     a2 = Sse2.ShiftRightLogical(Vector128.LoadUnsafe(ref p, 32).AsUInt32(), 24).AsInt32();
                    var     a3 = Sse2.ShiftRightLogical(Vector128.LoadUnsafe(ref p, 48).AsUInt32(), 24).AsInt32();
                    Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(a0, a1), Sse2.PackSignedSaturate(a2, a3)).StoreUnsafe(ref dst, i);
                }
                break;
            case OutlineKernelMode.VectorT:
            {
                var step  = (nuint)Vector<byte>.Count;
                var words = (nuint)Vector<uint>.Count;
                for (; i + step <= (nuint)n; i += step)
                {
                    ref var p  = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref src, i * 4));
                    var     a0 = Vector.ShiftRightLogical(Vector.LoadUnsafe(ref p, 0), 24);
                    var     a1 = Vector.ShiftRightLogical(Vector.LoadUnsafe(ref p, words), 24);
                    var     a2 = Vector.ShiftRightLogical(Vector.LoadUnsafe(ref p, words * 2), 24);
                    var     a3 = Vector.ShiftRightLogical(Vector.LoadUnsafe(ref p, words * 3), 24);
                    Vector.Narrow(Vector.Narrow(a0, a1), Vector.Narrow(a2, a3)).StoreUnsafe(ref dst, i);
                }
                break;
            }
        }

        ExtractAlphaScalar(rgba[(int)(i * 4)..], alpha[(int)i..]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ExtractAlphaScalar(ReadOnlySpan<byte> rgba, Span<byte> alpha)
    {
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = rgba[i * 4 + 3];
    }
}
