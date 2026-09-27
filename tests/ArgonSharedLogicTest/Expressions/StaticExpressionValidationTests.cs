namespace ArgonSharedLogicTest.Expressions;

using Argon.Features.Expressions;
using ArgonContracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

[TestFixture]
public class StaticExpressionValidationTests
{
    private readonly ExpressionFileValidator validator = new();

    private ValueTask<ExpressionValidation> Validate(byte[] data, ExpressionKind kind)
        => validator.ValidateAsync(new MemoryStream(data), kind, ExpressionFormat.Static, CancellationToken.None);

    [Test]
    public async Task A_512_png_sticker_is_reencoded_as_lossless_webp()
    {
        using var image = TestImages.Disc(512, 512);
        var png = TestImages.Png(image);

        var result = await Validate(png, ExpressionKind.Sticker);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Error, Is.EqualTo(ExpressionError.NONE));
            Assert.That(result.Format, Is.EqualTo(ExpressionFormat.Static));
            Assert.That((result.Width, result.Height), Is.EqualTo((512, 512)));
            Assert.That(result.ContentType, Is.EqualTo("image/png"));
            Assert.That(result.ReencodedContentType, Is.EqualTo("image/webp"));
            Assert.That(result.StoredContentType, Is.EqualTo("image/webp"));
            Assert.That(result.Reencoded, Is.Not.Null);
            Assert.That(result.FileSize, Is.EqualTo(result.Reencoded!.Length));
            Assert.That(result.Outline, Is.Not.Null.And.Length.InRange(1, 1024));
        });

        using var decoded = Image.Load<Rgba32>(result.Reencoded!);

        Assert.That(decoded.Metadata.DecodedImageFormat?.Name, Is.EqualTo("Webp"));
        Assert.That((decoded.Width, decoded.Height), Is.EqualTo((512, 512)));

        for (var y = 0; y < 512; y += 7)
        for (var x = 0; x < 512; x += 7)
            Assert.That(decoded[x, y], Is.EqualTo(image[x, y]), $"pixel ({x}, {y})");
    }

    [Test]
    public async Task A_webp_sticker_is_kept_as_uploaded()
    {
        using var image = TestImages.Disc(512, 512);
        var webp = TestImages.Webp(image, lossless: false);

        var result = await Validate(webp, ExpressionKind.Sticker);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Reencoded, Is.Null);
            Assert.That(result.ReencodedContentType, Is.Null);
            Assert.That(result.ContentType, Is.EqualTo("image/webp"));
            Assert.That(result.FileSize, Is.EqualTo(webp.Length));
            Assert.That(result.Outline, Is.Not.Null);
        });
    }

    [TestCase(512, 512)]
    [TestCase(512, 300)]
    [TestCase(200, 512)]
    [TestCase(512, 1)]
    public async Task Sticker_sizes_with_one_side_of_512_pass(int width, int height)
    {
        using var image = TestImages.Disc(width, height);

        var result = await Validate(TestImages.Png(image), ExpressionKind.Sticker);

        Assert.That(result.Ok, Is.True);
        Assert.That((result.Width, result.Height), Is.EqualTo((width, height)));
    }

    [TestCase(511, 511)]
    [TestCase(512, 513)]
    [TestCase(600, 512)]
    [TestCase(100, 100)]
    public async Task Other_sticker_sizes_are_refused(int width, int height)
    {
        using var image = TestImages.Disc(width, height);

        var result = await Validate(TestImages.Png(image), ExpressionKind.Sticker);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_100_emoji_passes_without_an_outline()
    {
        using var image = TestImages.Disc(100, 100);

        var result = await Validate(TestImages.Png(image), ExpressionKind.Emoji);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That((result.Width, result.Height), Is.EqualTo((100, 100)));
            Assert.That(result.Outline, Is.Null);
            Assert.That(result.Reencoded, Is.Not.Null);
        });
    }

    [TestCase(101, 100)]
    [TestCase(100, 99)]
    [TestCase(512, 512)]
    public async Task Emoji_that_are_not_100_square_are_refused(int width, int height)
    {
        using var image = TestImages.Disc(width, height);

        var result = await Validate(TestImages.Png(image), ExpressionKind.Emoji);

        Assert.That(result.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task An_upload_over_the_cap_is_too_large()
    {
        using var noise = TestImages.Noise(512, 512, seed: 7);
        var png = TestImages.Png(noise);
        Assert.That(png.Length, Is.GreaterThan(ExpressionLimits.StickerStaticMaxBytes), "the fixture must be over the cap");

        var seekable = await Validate(png, ExpressionKind.Sticker);
        var streamed = await validator.ValidateAsync(new ForwardOnlyStream(png), ExpressionKind.Sticker, ExpressionFormat.Static,
            CancellationToken.None);

        Assert.That(seekable.Error, Is.EqualTo(ExpressionError.TOO_LARGE));
        Assert.That(streamed.Error, Is.EqualTo(ExpressionError.TOO_LARGE));
    }

    [Test]
    public async Task An_emoji_over_128_kb_is_too_large()
    {
        using var image = TestImages.Disc(100, 100);
        var padded = TestImages.Png(image).Concat(new byte[ExpressionLimits.EmojiStaticMaxBytes]).ToArray();

        var result = await Validate(padded, ExpressionKind.Emoji);

        Assert.That(result.Error, Is.EqualTo(ExpressionError.TOO_LARGE));
    }

    [Test]
    public async Task A_non_seekable_upload_within_the_cap_is_read_whole()
    {
        using var image = TestImages.Disc(512, 512);

        var result = await validator.ValidateAsync(new ForwardOnlyStream(TestImages.Png(image)), ExpressionKind.Sticker,
            ExpressionFormat.Static, CancellationToken.None);

        Assert.That(result.Ok, Is.True);
    }

    [Test]
    public async Task Jpeg_is_refused_even_at_the_right_size()
    {
        using var image = TestImages.Disc(512, 512);

        var result = await Validate(TestImages.Jpeg(image), ExpressionKind.Sticker);

        Assert.That(result.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task Garbage_behind_a_png_signature_is_refused()
    {
        byte[] data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Repeat((byte)0x42, 500)];

        var result = await Validate(data, ExpressionKind.Sticker);

        Assert.That(result.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_truncated_png_is_refused()
    {
        using var image = TestImages.Disc(512, 512);
        var png = TestImages.Png(image);

        var result = await Validate(png[..(png.Length / 2)], ExpressionKind.Sticker);

        Assert.That(result.Ok, Is.False);
    }

    [Test]
    public async Task An_animated_webp_is_not_a_static_sticker()
    {
        using var image = TestImages.Disc(512, 512);
        using var second = TestImages.Disc(512, 512, 200, 200, 100);
        image.Frames.AddFrame(second.Frames.RootFrame);

        var webp = TestImages.Webp(image);
        using (var check = Image.Load(webp))
            Assume.That(check.Frames.Count, Is.EqualTo(2), "this ImageSharp does not write animated WEBP");

        var result = await Validate(webp, ExpressionKind.Sticker);

        Assert.That(result.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_fully_opaque_sticker_outlines_as_the_whole_box()
    {
        using var image = new Image<Rgba32>(512, 512, TestImages.Ink);

        var result = await Validate(TestImages.Png(image), ExpressionKind.Sticker);

        Assert.That(OutlineCodec.Decode(result.Outline!), Is.EqualTo("M0,0l512,0,0,512-512,0z"));
    }

    [Test]
    public async Task A_thumbnail_must_be_webp_at_the_items_size()
    {
        using var image = TestImages.Disc(512, 300);
        var webp = TestImages.Webp(image, lossless: false);

        var ok       = await validator.ValidateThumbAsync(new MemoryStream(webp), 512, 300, CancellationToken.None);
        var wrongDim = await validator.ValidateThumbAsync(new MemoryStream(webp), 512, 512, CancellationToken.None);
        var png      = await validator.ValidateThumbAsync(new MemoryStream(TestImages.Png(image)), 512, 300, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ok.Ok, Is.True);
            Assert.That(ok.ContentType, Is.EqualTo("image/webp"));
            Assert.That(ok.FileSize, Is.EqualTo(webp.Length));
            Assert.That(wrongDim.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
            Assert.That(png.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
        });
    }

    [Test]
    public async Task A_thumbnail_over_128_kb_is_too_large()
    {
        using var noise = TestImages.Noise(512, 512, seed: 3);
        var webp = TestImages.Webp(noise);
        Assert.That(webp.Length, Is.GreaterThan(ExpressionLimits.ThumbMaxBytes));

        var result = await validator.ValidateThumbAsync(new MemoryStream(webp), 512, 512, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(ExpressionError.TOO_LARGE));
    }
}
