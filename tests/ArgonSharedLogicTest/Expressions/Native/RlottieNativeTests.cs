namespace ArgonSharedLogicTest.Expressions.Native;

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Argon.Features.Expressions.Native;
using SixLabors.ImageSharp.PixelFormats;

[TestFixture]
public class RlottieNativeTests
{
    private static readonly Rgba32 Red = new(255, 0, 0, 255);

    [Test]
    public void Premultiplied_argb_becomes_straight_rgba()
        => Assert.Multiple(() =>
        {
            Assert.That(RlottieNative.Unpremultiply(0xFFFF0000), Is.EqualTo(Red));
            Assert.That(RlottieNative.Unpremultiply(0x80800000), Is.EqualTo(new Rgba32(255, 0, 0, 128)));
            Assert.That(RlottieNative.Unpremultiply(0x40102030), Is.EqualTo(new Rgba32(64, 128, 191, 64)));
            Assert.That(RlottieNative.Unpremultiply(0x00FFFFFF), Is.EqualTo(new Rgba32(0, 0, 0, 0)), "clear pixels carry no colour");
        });

    [Test]
    public void Blue_green_red_alpha_in_memory_is_red()
    {
        Assume.That(BitConverter.IsLittleEndian);

        var pixels = MemoryMarshal.Cast<byte, uint>((byte[])[0, 0, 255, 255, 0, 0, 128, 128]).ToArray();

        using var image = RlottieNative.ToImage(pixels, 2, 1);

        Assert.Multiple(() =>
        {
            Assert.That(image[0, 0], Is.EqualTo(Red));
            Assert.That(image[1, 0], Is.EqualTo(new Rgba32(255, 0, 0, 128)));
        });
    }

    [Test]
    public void The_first_frame_is_red_where_the_square_is_and_clear_around_it()
    {
        Assume.That(RlottieNative.IsAvailable, "rlottie is not installed");

        using var frame = RlottieNative.RenderFirstFrame(TestLottie.Json(TestLottie.Document()), 512, 512);

        Assert.That(frame, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That((frame!.Width, frame.Height), Is.EqualTo((512, 512)));
            Assert.That(frame[256, 256], Is.EqualTo(Red));
            Assert.That(frame[160, 350], Is.EqualTo(Red));
            Assert.That(frame[100, 100].A, Is.Zero);
            Assert.That(frame[420, 256].A, Is.Zero);
        });
    }

    [Test]
    public void The_frame_is_drawn_at_the_size_asked_for()
    {
        Assume.That(RlottieNative.IsAvailable, "rlottie is not installed");

        using var frame = RlottieNative.RenderFirstFrame(TestLottie.Json(TestLottie.Document()), 100, 100);

        Assert.That(frame, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That((frame!.Width, frame.Height), Is.EqualTo((100, 100)));
            Assert.That(frame[50, 50], Is.EqualTo(Red));
            Assert.That(frame[5, 5].A, Is.Zero);
        });
    }

    [Test]
    public void Every_call_parses_its_own_json()
    {
        Assume.That(RlottieNative.IsAvailable, "rlottie is not installed");

        var blue = TestLottie.Document();
        TestLottie.GroupItems(blue)[1]!["c"]!["k"] = new JsonArray(0, 0, 1, 1);

        using var first  = RlottieNative.RenderFirstFrame(TestLottie.Json(TestLottie.Document()), 64, 64);
        using var second = RlottieNative.RenderFirstFrame(TestLottie.Json(blue), 64, 64);
        using var none   = RlottieNative.RenderFirstFrame("not a lottie"u8, 64, 64);

        Assert.Multiple(() =>
        {
            Assert.That(first?[32, 32], Is.EqualTo(Red));
            Assert.That(second?[32, 32], Is.EqualTo(new Rgba32(0, 0, 255, 255)), "rlottie served a cached model");
            Assert.That(none, Is.Null, "rlottie served a cached model for what is not JSON");
        });
    }

    [Test]
    public void What_is_not_json_is_no_animation()
    {
        Assume.That(RlottieNative.IsAvailable, "rlottie is not installed");

        Assert.That(RlottieNative.RenderFirstFrame("not a lottie"u8, 64, 64), Is.Null);
    }
}
