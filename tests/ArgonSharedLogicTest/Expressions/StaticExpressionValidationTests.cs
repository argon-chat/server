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

    [Test]
    public async Task A_webp_carrying_exif_and_an_icc_profile_passes()
    {
        using var image = TestImages.Disc(512, 512);
        var webp = ExtendedWebp(TestImages.Webp(image), 512, 512, MinimalIccProfile(), Exif());

        Assert.That(System.Text.Encoding.ASCII.GetString(webp, 12, 4), Is.EqualTo("VP8X"), "setup: not the extended format");

        var result = await Validate(webp, ExpressionKind.Sticker);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Error.ToString());
            Assert.That((result.Width, result.Height), Is.EqualTo((512, 512)));
            Assert.That(result.ContentType, Is.EqualTo("image/webp"));
            Assert.That(result.Outline, Is.Not.Null);
        });
    }

    /// <summary>Rewraps a simple (VP8L) WEBP as VP8X with ICCP before the image and EXIF after it.</summary>
    private static byte[] ExtendedWebp(byte[] simple, int width, int height, byte[] icc, byte[] exif)
    {
        Assert.That(System.Text.Encoding.ASCII.GetString(simple, 12, 4), Is.EqualTo("VP8L"), "setup: expected a lossless WEBP");

        var image = simple[12..];
        var vp8x  = new byte[10];
        vp8x[0] = 0x20 | 0x10 | 0x08; // ICC, alpha, EXIF
        WriteUInt24(vp8x.AsSpan(4), width - 1);
        WriteUInt24(vp8x.AsSpan(7), height - 1);

        byte[] body = [.. "WEBP"u8, .. Chunk("VP8X", vp8x), .. Chunk("ICCP", icc), .. image, .. Chunk("EXIF", exif)];

        var size = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        return [.. "RIFF"u8, .. size, .. body];

        static byte[] Chunk(string fourCc, byte[] payload)
        {
            var header = new byte[8];
            System.Text.Encoding.ASCII.GetBytes(fourCc, header);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)payload.Length);
            return payload.Length % 2 == 0 ? [.. header, .. payload] : [.. header, .. payload, 0];
        }

        static void WriteUInt24(Span<byte> target, int value)
        {
            target[0] = (byte)value;
            target[1] = (byte)(value >> 8);
            target[2] = (byte)(value >> 16);
        }
    }

    /// <summary>A structurally valid display profile: the 128-byte header and an empty tag table.</summary>
    private static byte[] MinimalIccProfile()
    {
        var icc = new byte[132];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(icc, 132);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(8), 0x04300000);
        "mntr"u8.CopyTo(icc.AsSpan(12));
        "RGB "u8.CopyTo(icc.AsSpan(16));
        "XYZ "u8.CopyTo(icc.AsSpan(20));
        "acsp"u8.CopyTo(icc.AsSpan(36));
        return icc;
    }

    private static byte[] Exif()
    {
        var exif = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Software, "Telegram Desktop");
        return exif.ToByteArray()!;
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
