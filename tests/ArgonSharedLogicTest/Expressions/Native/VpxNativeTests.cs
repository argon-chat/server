namespace ArgonSharedLogicTest.Expressions.Native;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Argon.Features.Expressions;
using Argon.Features.Expressions.Native;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

[TestFixture]
public class VpxNativeTests
{
    private const int Tolerance = 4;

    private static Image<Rgba32>? Decode(byte[] file)
    {
        Assert.That(WebmProbe.TryReadFirstFrame(file, out var frame), Is.True, "the fixture has no first frame");
        return VpxNative.DecodeFrame(frame.Colour(file), frame.Alpha(file), 512);
    }

    private static void AssertRed(Rgba32 pixel, string where)
        => Assert.That(Math.Max(255 - pixel.R, Math.Max(pixel.G, pixel.B)), Is.LessThanOrEqualTo(Tolerance), $"{where}: {pixel}");

    [Test]
    public void The_image_struct_is_vpx_image_t_at_image_abi_5()
    {
        Assume.That(Environment.Is64BitProcess);

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.Range)), Is.EqualTo((nint)8));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.BitDepth)), Is.EqualTo((nint)20));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.DisplayWidth)), Is.EqualTo((nint)24));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.XChromaShift)), Is.EqualTo((nint)40));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.PlaneY)), Is.EqualTo((nint)48));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.PlaneAlpha)), Is.EqualTo((nint)72));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.StrideY)), Is.EqualTo((nint)80));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.BitsPerSample)), Is.EqualTo((nint)96));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.UserPriv)), Is.EqualTo((nint)104));
            Assert.That(Marshal.OffsetOf<VpxNative.VpxImage>(nameof(VpxNative.VpxImage.FrameBufferPriv)), Is.EqualTo((nint)128));
            Assert.That(Unsafe.SizeOf<VpxNative.VpxImage>(), Is.EqualTo(136));
        });
    }

    [Test]
    public void An_opaque_red_frame_decodes_red_and_opaque()
    {
        Assume.That(VpxNative.IsAvailable, "libvpx is not installed");

        using var frame = Decode(NativeFixtures.Red64);

        Assert.That(frame, Is.Not.Null);
        Assert.That((frame!.Width, frame.Height), Is.EqualTo((64, 64)));
        Assert.Multiple(() =>
        {
            for (var y = 0; y < 64; y += 7)
            for (var x = 0; x < 64; x += 7)
            {
                AssertRed(frame[x, y], $"({x},{y})");
                Assert.That(frame[x, y].A, Is.EqualTo(255), $"({x},{y}) is not opaque");
            }
        });
    }

    [Test]
    public void The_alpha_stream_becomes_the_alpha_channel()
    {
        Assume.That(VpxNative.IsAvailable, "libvpx is not installed");

        using var frame = Decode(NativeFixtures.HalfAlpha64);

        Assert.That(frame, Is.Not.Null);
        Assert.Multiple(() =>
        {
            for (var y = 4; y < 64; y += 12)
            {
                Assert.That(frame![8, y].A, Is.GreaterThanOrEqualTo(255 - Tolerance), $"(8,{y}) is not opaque");
                Assert.That(frame[24, y].A, Is.GreaterThanOrEqualTo(255 - Tolerance), $"(24,{y}) is not opaque");
                Assert.That(frame[40, y].A, Is.LessThanOrEqualTo(Tolerance), $"(40,{y}) is not clear");
                Assert.That(frame[56, y].A, Is.LessThanOrEqualTo(Tolerance), $"(56,{y}) is not clear");
                AssertRed(frame[8, y], $"(8,{y})");
            }
        });
    }

    [Test]
    public void A_sticker_sized_frame_keeps_its_square()
    {
        Assume.That(VpxNative.IsAvailable, "libvpx is not installed");

        using var frame = Decode(NativeFixtures.Square512);

        Assert.That(frame, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That((frame!.Width, frame.Height), Is.EqualTo((512, 512)));
            AssertRed(frame[256, 256], "centre");
            Assert.That(frame[256, 256].A, Is.GreaterThanOrEqualTo(255 - Tolerance));
            Assert.That(frame[20, 20].A, Is.LessThanOrEqualTo(Tolerance), "the corner is not clear");
        });
    }

    [Test]
    public void A_packet_that_is_not_vp9_decodes_to_nothing()
    {
        Assume.That(VpxNative.IsAvailable, "libvpx is not installed");

        Assert.Multiple(() =>
        {
            Assert.That(VpxNative.DecodeFrame(Enumerable.Repeat((byte)0x5A, 64).ToArray(), [], 512), Is.Null);
            Assert.That(VpxNative.DecodeFrame([], [], 512), Is.Null);
        });
    }

    [Test]
    public void A_frame_over_the_size_cap_is_not_decoded()
    {
        Assume.That(VpxNative.IsAvailable, "libvpx is not installed");

        Assert.That(WebmProbe.TryReadFirstFrame(NativeFixtures.Square512, out var frame), Is.True);
        Assert.That(VpxNative.DecodeFrame(frame.Colour(NativeFixtures.Square512), [], 256), Is.Null);
    }
}
