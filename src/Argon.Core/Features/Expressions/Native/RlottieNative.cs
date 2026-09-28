namespace Argon.Features.Expressions.Native;

using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>rlottie's C API (<c>rlottie_capi.h</c>, 0.1 and later).</summary>
internal static unsafe partial class RlottieNative
{
    private static readonly Lazy<bool> Available = new(Probe);

    private static long serial;

    static RlottieNative() => NativeLibraries.EnsureResolver();

    public static bool IsAvailable => Available.Value;

    [LibraryImport(NativeLibraries.Rlottie, EntryPoint = "lottie_animation_from_data")]
    public static partial nint FromData(byte* data, byte* key, byte* resourcePath);

    [LibraryImport(NativeLibraries.Rlottie, EntryPoint = "lottie_animation_destroy")]
    public static partial void Destroy(nint animation);

    [LibraryImport(NativeLibraries.Rlottie, EntryPoint = "lottie_animation_get_size")]
    public static partial void GetSize(nint animation, nuint* width, nuint* height);

    [LibraryImport(NativeLibraries.Rlottie, EntryPoint = "lottie_animation_get_totalframe")]
    public static partial nuint GetTotalFrame(nint animation);

    [LibraryImport(NativeLibraries.Rlottie, EntryPoint = "lottie_animation_get_framerate")]
    public static partial double GetFrameRate(nint animation);

    /// <summary>Renders into <paramref name="buffer"/> as premultiplied ARGB32 (B, G, R, A in little-endian memory).</summary>
    [LibraryImport(NativeLibraries.Rlottie, EntryPoint = "lottie_animation_render")]
    public static partial void Render(nint animation, nuint frame, uint* buffer, nuint width, nuint height, nuint bytesPerLine);

    /// <summary>Frame 0 at <paramref name="width"/>×<paramref name="height"/>; null when rlottie does not take the JSON.</summary>
    public static Image<Rgba32>? RenderFirstFrame(ReadOnlySpan<byte> json, int width, int height)
    {
        var  text  = new byte[json.Length + 1];
        var  key   = Encoding.ASCII.GetBytes($"argon-{Interlocked.Increment(ref serial)}\0");
        byte empty = 0;
        json.CopyTo(text);

        nint animation;

        // rlottie caches models by key, 0.2 the empty key too: a fresh one never hits. A null key crashes it.
        fixed (byte* data = text)
        fixed (byte* name = key)
            animation = FromData(data, name, &empty);

        if (animation == 0)
            return null;

        try
        {
            if (GetTotalFrame(animation) == 0)
                return null;

            var pixels = new uint[width * height];
            fixed (uint* buffer = pixels)
                Render(animation, 0, buffer, (nuint)width, (nuint)height, (nuint)width * 4);

            return ToImage(pixels, width, height);
        }
        finally
        {
            Destroy(animation);
        }
    }

    private static bool Probe()
    {
        if (!NativeLibraries.Exports(NativeLibraries.Rlottie, "lottie_animation_from_data", "lottie_animation_destroy",
                "lottie_animation_get_size", "lottie_animation_get_totalframe", "lottie_animation_get_framerate", "lottie_animation_render"))
            return false;

        var library = NativeLibraries.Load(NativeLibraries.Rlottie);

        // 0.2 asks for it once when loaded with dlopen.
        if (NativeLibrary.TryGetExport(library, "lottie_init", out var init))
            ((delegate* unmanaged<void>)init)();

        // The model cache off, so no model outlives its render: 0.2 has a C switch, 0.1 only the C++ one.
        if (NativeLibrary.TryGetExport(library, "lottie_configure_model_cache_size", out var configure)
         || NativeLibrary.TryGetExport(library, "_ZN7rlottie23configureModelCacheSizeEm", out configure))
            ((delegate* unmanaged<nuint, void>)configure)(0);

        return true;
    }

    internal static Image<Rgba32> ToImage(uint[] argb, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = Unpremultiply(argb[y * width + x]);
            }
        });
        return image;
    }

    internal static Rgba32 Unpremultiply(uint argb)
    {
        var a = argb >> 24;
        if (a == 0)
            return default;

        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;

        return a == 255
            ? new Rgba32((byte)r, (byte)g, (byte)b, 255)
            : new Rgba32(Straight(r, a), Straight(g, a), Straight(b, a), (byte)a);

        static byte Straight(uint c, uint a) => (byte)Math.Min(255, (c * 255 + a / 2) / a);
    }
}
