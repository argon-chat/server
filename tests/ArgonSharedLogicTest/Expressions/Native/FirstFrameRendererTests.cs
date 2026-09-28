namespace ArgonSharedLogicTest.Expressions.Native;

using Argon.Features.Expressions;
using ArgonContracts;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

[TestFixture]
public class FirstFrameRendererTests
{
    private readonly FirstFrameRenderer      renderer  = new(NullLogger<FirstFrameRenderer>.Instance);
    private readonly ExpressionFileValidator validator = new();

    [Test]
    public async Task A_noisy_frame_is_encoded_within_the_thumbnail_cap_and_passes_as_a_thumbnail()
    {
        using var noise = TestImages.Noise(512, 512, seed: 7);
        for (var x = 0; x < 512; x++)
            noise[x, 0] = new Rgba32(0, 0, 0, 0);

        var webp = FirstFrameRenderer.EncodeThumb(noise);

        Assert.That(webp, Is.Not.Null, "no quality fits");
        Assert.That(webp!.Length, Is.LessThanOrEqualTo(ExpressionLimits.ThumbMaxBytes));

        var thumb = await validator.ValidateThumbAsync(new MemoryStream(webp), 512, 512, CancellationToken.None);
        Assert.That(thumb.Ok, Is.True, $"refused as a thumbnail: {thumb.Error}");
    }

    [Test]
    public async Task A_rendered_frame_keeps_its_transparency_through_the_encoder()
    {
        using var disc = TestImages.Disc(512, 512);

        var webp = FirstFrameRenderer.EncodeThumb(disc);
        Assert.That(webp, Is.Not.Null);

        using var decoded = Image.Load<Rgba32>(webp!);
        Assert.Multiple(() =>
        {
            Assert.That(decoded[5, 5].A, Is.Zero);
            Assert.That(decoded[256, 256].A, Is.EqualTo(255));
        });

        var thumb = await validator.ValidateThumbAsync(new MemoryStream(webp!), 512, 512, CancellationToken.None);
        Assert.That(thumb.Ok, Is.True);
    }

    [TestCase(ExpressionFormat.Lottie)]
    [TestCase(ExpressionFormat.Video)]
    [TestCase(ExpressionFormat.Static)]
    public async Task Unreadable_input_renders_nothing_and_throws_nothing(ExpressionFormat format)
    {
        var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray();

        using var raw     = await renderer.RenderAsync(garbage, format, 512, 512, CancellationToken.None);
        using var empty   = await renderer.RenderAsync(ReadOnlyMemory<byte>.Empty, format, 512, 512, CancellationToken.None);
        using var gzipped = await renderer.RenderAsync(TestLottie.Gzip(garbage), format, 512, 512, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(raw, Is.Null);
            Assert.That(empty, Is.Null);
            Assert.That(gzipped, Is.Null);
        });
    }

    [Test]
    public async Task Nothing_over_512_pixels_a_side_is_rendered()
    {
        var tgs = TestLottie.Gzip(TestLottie.Json(TestLottie.Document()));

        using var wide = await renderer.RenderAsync(tgs, ExpressionFormat.Lottie, 513, 512, CancellationToken.None);
        using var flat = await renderer.RenderAsync(tgs, ExpressionFormat.Lottie, 512, 0, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(wide, Is.Null);
            Assert.That(flat, Is.Null);
            Assert.That(renderer.IsAvailable(ExpressionFormat.Static), Is.False);
        });
    }

    [Test]
    public async Task A_tgs_is_gunzipped_and_rendered()
    {
        Assume.That(renderer.IsAvailable(ExpressionFormat.Lottie), "rlottie is not installed");

        var json = TestLottie.Json(TestLottie.Document());

        using var fromTgs  = await renderer.RenderAsync(TestLottie.Gzip(json), ExpressionFormat.Lottie, 512, 512, CancellationToken.None);
        using var fromJson = await renderer.RenderAsync((byte[])[0xEF, 0xBB, 0xBF, .. json], ExpressionFormat.Lottie, 512, 512,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fromTgs?[256, 256], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(fromJson?[256, 256], Is.EqualTo(new Rgba32(255, 0, 0, 255)), "a BOM is skipped");
        });
    }

    [Test]
    public async Task A_video_sticker_is_rendered_from_its_first_block()
    {
        Assume.That(renderer.IsAvailable(ExpressionFormat.Video), "libvpx is not installed");

        using var frame = await renderer.RenderAsync(NativeFixtures.Square512, ExpressionFormat.Video, 512, 512, CancellationToken.None);

        Assert.That(frame, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(frame![256, 256].R, Is.GreaterThan(250));
            Assert.That(frame[256, 256].A, Is.GreaterThan(250));
            Assert.That(frame[20, 20].A, Is.LessThan(5));
        });
    }

    [Test]
    public async Task The_video_fixture_is_a_valid_sticker()
    {
        var checkedFile = await validator.ValidateAsync(new MemoryStream(NativeFixtures.Square512), ExpressionKind.Sticker, ExpressionFormat.Video,
            CancellationToken.None);

        Assert.That(checkedFile.Ok, Is.True, $"refused: {checkedFile.Error}");
        Assert.That((checkedFile.Width, checkedFile.Height), Is.EqualTo((512, 512)));
    }
}
