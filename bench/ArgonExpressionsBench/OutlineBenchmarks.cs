namespace Argon.Expressions.Bench;

using System.Runtime.InteropServices;
using Argon.Features.Expressions;
using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

public enum Kernels
{
    Vector256,
    Vector128,
    Scalar
}

[ShortRunJob]
[MemoryDiagnoser]
public class OutlineBenchmarks
{
    private byte[]          sticker = [];
    private byte[]          emoji   = [];
    private byte[]          alpha   = [];
    private Image<Rgba32>   image   = null!;

    [Params(Kernels.Vector256, Kernels.Vector128, Kernels.Scalar)]
    public Kernels Mode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        OutlineTracer.KernelMode = Mode switch
        {
            Kernels.Vector256 => OutlineKernelMode.Vector256,
            Kernels.Vector128 => OutlineKernelMode.Vector128,
            _                 => OutlineKernelMode.Scalar
        };

        image   = Sticker(512);
        sticker = new byte[512 * 512];
        alpha   = new byte[512 * 512];
        for (var y = 0; y < 512; y++)
            for (var x = 0; x < 512; x++)
                sticker[y * 512 + x] = image[x, y].A;

        emoji = new byte[100 * 100];
        using var small = Sticker(100);
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                emoji[y * 100 + x] = small[x, y].A;
    }

    [GlobalCleanup]
    public void Cleanup() => image.Dispose();

    /// <summary>A disc with two holes and a detached dot, soft-edged, over opaque colour.</summary>
    private static Image<Rgba32> Sticker(int side)
    {
        var image = new Image<Rgba32>(side, side);
        var c     = side / 2.0;

        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var px    = x + 0.5;
            var py    = y + 0.5;
            var outer = Math.Sqrt((px - c) * (px - c) + (py - c) * (py - c)) - side * 0.42;
            var hole1 = side * 0.09 - Math.Sqrt((px - c * 0.7) * (px - c * 0.7) + (py - c * 0.8) * (py - c * 0.8));
            var hole2 = side * 0.07 - Math.Sqrt((px - c * 1.3) * (px - c * 1.3) + (py - c * 1.2) * (py - c * 1.2));
            var dot   = Math.Sqrt((px - side * 0.9) * (px - side * 0.9) + (py - side * 0.1) * (py - side * 0.1)) - side * 0.05;
            var d     = Math.Min(Math.Max(Math.Max(outer, hole1), hole2), dot);
            var a     = (byte)Math.Clamp(128 - d * 64, 0, 255);
            image[x, y] = new Rgba32((byte)(x * 255 / side), (byte)(y * 255 / side), 180, a);
        }

        return image;
    }

    [Benchmark]
    public byte[]? Sticker512() => OutlineTracer.FromAlpha(sticker, 512, 512);

    [Benchmark]
    public byte[]? Emoji100() => OutlineTracer.FromAlpha(emoji, 100, 100);

    /// <summary>What <c>FromAlpha(Image)</c> does before tracing: every row's alpha bytes.</summary>
    [Benchmark]
    public void AlphaExtraction512()
    {
        var mode = OutlineKernels.Resolve(OutlineTracer.KernelMode);
        for (var y = 0; y < 512; y++)
            OutlineKernels.ExtractAlpha(MemoryMarshal.AsBytes(image.DangerousGetPixelRowMemory(y).Span), alpha.AsSpan(y * 512, 512), mode);
    }

    [Benchmark]
    public byte[]? StickerImage512() => OutlineTracer.FromAlpha(image);
}
