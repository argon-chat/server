namespace Argon.Features.Expressions.Native;

using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// libvpx's VP9 decoder (<c>vpx_decoder.h</c>). Pinned to decoder ABI 12, which libvpx 1.12 (Debian 12),
/// 1.14 (Ubuntu 24.04) and 1.17 declare alike; a library of another ABI refuses the init and counts as absent.
/// </summary>
internal static unsafe partial class VpxNative
{
    /// <summary><c>VPX_DECODER_ABI_VERSION</c> = 3 + <c>VPX_CODEC_ABI_VERSION</c> (4 + <c>VPX_IMAGE_ABI_VERSION</c> (5)).</summary>
    public const int DecoderAbiVersion = 12;

    // vpx_codec_ctx_t is 56 bytes on 64-bit targets; the rest is headroom.
    private const int ContextBytes = 256;

    private const int FormatPlanar       = 0x100;
    private const int FormatHighBitDepth = 0x800;

    private const int SpaceBt709    = 2;
    private const int SpaceSmpte240 = 4;
    private const int SpaceBt2020   = 5;
    private const int SpaceSrgb     = 7;
    private const int RangeFull     = 1;

    private static readonly Lazy<bool> Available = new(Probe);

    static VpxNative() => NativeLibraries.EnsureResolver();

    public static bool IsAvailable => Available.Value;

    /// <summary>The library's version string, when it loads.</summary>
    public static string? Version => IsAvailable ? Marshal.PtrToStringUTF8((nint)VersionString()) : null;

    /// <summary><c>vpx_image_t</c> at image ABI 5.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VpxImage
    {
        public int   Format;
        public int   ColorSpace;
        public int   Range;
        public uint  Width;
        public uint  Height;
        public uint  BitDepth;
        public uint  DisplayWidth;
        public uint  DisplayHeight;
        public uint  RenderWidth;
        public uint  RenderHeight;
        public uint  XChromaShift;
        public uint  YChromaShift;
        public byte* PlaneY;
        public byte* PlaneU;
        public byte* PlaneV;
        public byte* PlaneAlpha;
        public int   StrideY;
        public int   StrideU;
        public int   StrideV;
        public int   StrideAlpha;
        public int   BitsPerSample;
        public void* UserPriv;
        public byte* ImgData;
        public int   ImgDataOwner;
        public int   SelfAllocated;
        public void* FrameBufferPriv;
    }

    /// <summary><c>vpx_codec_dec_cfg_t</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DecoderConfig
    {
        public uint Threads;
        public uint Width;
        public uint Height;
    }

    [LibraryImport(NativeLibraries.Vpx, EntryPoint = "vpx_codec_vp9_dx")]
    private static partial void* Vp9Decoder();

    [LibraryImport(NativeLibraries.Vpx, EntryPoint = "vpx_codec_dec_init_ver")]
    private static partial int DecoderInit(void* context, void* iface, DecoderConfig* config, CLong flags, int abiVersion);

    [LibraryImport(NativeLibraries.Vpx, EntryPoint = "vpx_codec_decode")]
    private static partial int Decode(void* context, byte* data, uint size, void* userPriv, CLong deadline);

    [LibraryImport(NativeLibraries.Vpx, EntryPoint = "vpx_codec_get_frame")]
    private static partial VpxImage* GetFrame(void* context, void** iterator);

    [LibraryImport(NativeLibraries.Vpx, EntryPoint = "vpx_codec_destroy")]
    private static partial int Destroy(void* context);

    [LibraryImport(NativeLibraries.Vpx, EntryPoint = "vpx_codec_version_str")]
    private static partial byte* VersionString();

    /// <summary>
    /// The frame a VP9 packet shows, as straight RGBA. <paramref name="alpha"/> is the packet of the alpha
    /// stream (a VP9 stream of its own whose Y plane is A); without one the frame is opaque.
    /// </summary>
    public static Image<Rgba32>? DecodeFrame(ReadOnlySpan<byte> colour, ReadOnlySpan<byte> alpha, int maxSide)
    {
        using var decoder = Decoder.TryOpen();
        if (decoder is null)
            return null;

        var image = decoder.Decode(colour);
        if (image is null || !Fits(image, maxSide))
            return null;

        var pixels = ToRgba(image);

        if (!alpha.IsEmpty)
        {
            using var alphaDecoder = Decoder.TryOpen();
            var       plane        = alphaDecoder is null ? null : alphaDecoder.Decode(alpha);

            if (plane is null || !Fits(plane, maxSide) || plane->DisplayWidth != image->DisplayWidth || plane->DisplayHeight != image->DisplayHeight)
                return null;

            ApplyAlpha(plane, pixels);
        }

        return Image.LoadPixelData<Rgba32>(pixels, (int)image->DisplayWidth, (int)image->DisplayHeight);
    }

    private static bool Probe()
    {
        if (!NativeLibraries.Exports(NativeLibraries.Vpx, "vpx_codec_vp9_dx", "vpx_codec_dec_init_ver", "vpx_codec_decode",
                "vpx_codec_get_frame", "vpx_codec_destroy", "vpx_codec_version_str"))
            return false;

        using var decoder = Decoder.TryOpen();
        return decoder is not null;
    }

    private static bool Fits(VpxImage* image, int maxSide)
        => (image->Format & FormatPlanar) != 0
        && image->DisplayWidth >= 1 && image->DisplayWidth <= (uint)maxSide
        && image->DisplayHeight >= 1 && image->DisplayHeight <= (uint)maxSide
        && image->BitDepth is >= 8 and <= 16
        && image->XChromaShift <= 1 && image->YChromaShift <= 1;

    private static int Sample(byte* plane, int stride, int x, int y, bool high, int shift)
        => high ? ((ushort*)(plane + (nint)y * stride))[x] >> shift : plane[(nint)y * stride + x];

    private static Rgba32[] ToRgba(VpxImage* image)
    {
        var width  = (int)image->DisplayWidth;
        var height = (int)image->DisplayHeight;
        var high   = (image->Format & FormatHighBitDepth) != 0;
        var shift  = high ? (int)image->BitDepth - 8 : 0;
        var xs     = (int)image->XChromaShift;
        var ys     = (int)image->YChromaShift;
        var srgb   = image->ColorSpace == SpaceSrgb;
        var full   = image->Range == RangeFull;

        // Unknown and unlisted spaces are taken as BT.601, the default of VP9 encoders.
        var (kr, kb) = image->ColorSpace switch
        {
            SpaceBt709    => (0.2126, 0.0722),
            SpaceSmpte240 => (0.212, 0.087),
            SpaceBt2020   => (0.2627, 0.0593),
            _             => (0.299, 0.114)
        };
        var kg = 1 - kr - kb;

        var lumaScale   = full ? 1.0 : 255.0 / 219;
        var chromaScale = full ? 1.0 : 255.0 / 224;
        var lumaOffset  = full ? 0 : 16;

        var pixels = new Rgba32[width * height];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var luma = Sample(image->PlaneY, image->StrideY, x, y, high, shift);
            var cb   = Sample(image->PlaneU, image->StrideU, x >> xs, y >> ys, high, shift);
            var cr   = Sample(image->PlaneV, image->StrideV, x >> xs, y >> ys, high, shift);

            double r, g, b;

            if (srgb)
            {
                // RGB in the Y, U and V planes as G, B and R.
                (r, g, b) = (cr, luma, cb);
            }
            else
            {
                var l = (luma - lumaOffset) * lumaScale;
                var u = (cb - 128) * chromaScale;
                var v = (cr - 128) * chromaScale;

                r = l + 2 * (1 - kr) * v;
                b = l + 2 * (1 - kb) * u;
                g = (l - kr * r - kb * b) / kg;
            }

            pixels[y * width + x] = new Rgba32(Clamp(r), Clamp(g), Clamp(b), 255);
        }

        return pixels;

        static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static void ApplyAlpha(VpxImage* plane, Rgba32[] pixels)
    {
        var width  = (int)plane->DisplayWidth;
        var height = (int)plane->DisplayHeight;
        var high   = (plane->Format & FormatHighBitDepth) != 0;
        var shift  = high ? (int)plane->BitDepth - 8 : 0;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            pixels[y * width + x].A = (byte)Sample(plane->PlaneY, plane->StrideY, x, y, high, shift);
    }

    /// <summary>One VP9 decoder instance over an opaque <c>vpx_codec_ctx_t</c>.</summary>
    private sealed class Decoder : IDisposable
    {
        private void* context;

        private Decoder(void* context) => this.context = context;

        public static Decoder? TryOpen()
        {
            var context = NativeMemory.AllocZeroed(ContextBytes);
            var config  = new DecoderConfig { Threads = 1 };

            // A failed init destroys what it built; only the blob is left to free.
            if (DecoderInit(context, Vp9Decoder(), &config, default, DecoderAbiVersion) == 0)
                return new Decoder(context);

            NativeMemory.Free(context);
            return null;
        }

        /// <summary>The frame the packet shows, valid until the next call or disposal.</summary>
        public VpxImage* Decode(ReadOnlySpan<byte> packet)
        {
            if (packet.IsEmpty)
                return null;

            fixed (byte* data = packet)
                if (VpxNative.Decode(context, data, (uint)packet.Length, null, default) != 0)
                    return null;

            void* iterator = null;
            return GetFrame(context, &iterator);
        }

        public void Dispose()
        {
            if (context is null)
                return;

            Destroy(context);
            NativeMemory.Free(context);
            context = null;
        }
    }
}
