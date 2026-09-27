namespace ArgonSharedLogicTest.Expressions;

using Argon.Features.Expressions;
using ArgonContracts;

[TestFixture]
public class VideoExpressionValidationTests
{
    private readonly ExpressionFileValidator validator = new();

    private ValueTask<ExpressionValidation> Validate(byte[] data, ExpressionKind kind = ExpressionKind.Sticker)
        => validator.ValidateAsync(new MemoryStream(data), kind, ExpressionFormat.Video, CancellationToken.None);

    [Test]
    public async Task A_three_second_vp9_sticker_passes()
    {
        var webm = WebmBuilder.Sticker(3.0).Build();

        var result = await Validate(webm);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Error.ToString());
            Assert.That(result.Format, Is.EqualTo(ExpressionFormat.Video));
            Assert.That((result.Width, result.Height), Is.EqualTo((512, 512)));
            Assert.That(result.ContentType, Is.EqualTo("video/webm"));
            Assert.That(result.FileSize, Is.EqualTo(webm.Length));
            Assert.That(result.Reencoded, Is.Null);
            Assert.That(result.Outline, Is.Null);
            Assert.That(result.DurationSeconds, Is.EqualTo(3.0).Within(0.001));
            Assert.That(result.Fps, Is.EqualTo(30).Within(0.05));
        });
    }

    [Test]
    public async Task An_audio_track_is_refused()
        => Assert.That((await Validate(new WebmBuilder { Audio = true }.Build())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));

    [TestCase("V_VP8")]
    [TestCase("V_AV1")]
    [TestCase("V_VP9X")]
    public async Task Only_vp9_is_accepted(string codec)
        => Assert.That((await Validate(new WebmBuilder { Codec = codec }.Build())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));

    [Test]
    public async Task Three_and_a_half_seconds_is_too_long()
        => Assert.That((await Validate(WebmBuilder.Sticker(3.5).Build())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));

    [TestCase(512, 600)]
    [TestCase(511, 511)]
    [TestCase(700, 512)]
    public async Task Sticker_sizes_other_than_one_side_of_512_are_refused(int width, int height)
        => Assert.That((await Validate(new WebmBuilder { Width = width, Height = height }.Build())).Error,
            Is.EqualTo(ExpressionError.INVALID_FORMAT));

    [Test]
    public async Task A_portrait_sticker_passes()
    {
        var result = await Validate(new WebmBuilder { Width = 300, Height = 512 }.Build());

        Assert.That(result.Ok, Is.True);
        Assert.That((result.Width, result.Height), Is.EqualTo((300, 512)));
    }

    [Test]
    public async Task An_emoji_is_100_square()
    {
        Assert.That((await Validate(new WebmBuilder { Width = 100, Height = 100 }.Build(), ExpressionKind.Emoji)).Ok, Is.True);
        Assert.That((await Validate(new WebmBuilder().Build(), ExpressionKind.Emoji)).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task Sixty_fps_is_refused_whether_declared_or_measured()
    {
        var declared = WebmBuilder.Sticker(2.0, fps: 60).Build();
        var measured = new WebmBuilder { Frames = 120, FrameIntervalMs = 1000.0 / 60, DurationMs = 2000, DefaultDurationNs = null }.Build();

        Assert.That((await Validate(declared)).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
        Assert.That((await Validate(measured)).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task Without_a_duration_the_blocks_are_measured()
    {
        // MediaRecorder writes unknown-size segments and clusters and no Duration.
        var recorded = new WebmBuilder { UnknownSizes = true, DurationMs = null, DefaultDurationNs = null, Frames = 60 }.Build();
        var tooLong  = new WebmBuilder { UnknownSizes = true, DurationMs = null, DefaultDurationNs = null, Frames = 120 }.Build();

        var ok = await Validate(recorded);

        Assert.That(ok.Ok, Is.True, ok.Error.ToString());
        Assert.That(ok.DurationSeconds, Is.EqualTo(2.0).Within(0.01));
        Assert.That((await Validate(tooLong)).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_short_declared_duration_does_not_hide_longer_content()
    {
        var lying = new WebmBuilder { DurationMs = 2000, Frames = 105 }.Build();

        Assert.That((await Validate(lying)).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_file_without_frames_is_refused()
        => Assert.That((await Validate(new WebmBuilder { Frames = 0 }.Build())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));

    [Test]
    public async Task A_matroska_doctype_is_refused()
        => Assert.That((await Validate(new WebmBuilder { DocType = "matroska" }.Build())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));

    [Test]
    public async Task Over_256_kb_is_too_large()
    {
        var big = new WebmBuilder { FramePayload = 4000 }.Build();
        Assert.That(big.Length, Is.GreaterThan(ExpressionLimits.StickerVideoMaxBytes));

        Assert.That((await Validate(big)).Error, Is.EqualTo(ExpressionError.TOO_LARGE));
    }

    [Test]
    public async Task Every_truncation_of_a_sized_file_is_refused()
    {
        var webm = WebmBuilder.Sticker(1.0).Build();

        for (var length = 0; length < webm.Length; length++)
        {
            var result = await Validate(webm[..length]);
            Assert.That(result.Error, Is.EqualTo(ExpressionError.INVALID_FORMAT), $"cut at {length} of {webm.Length}");
        }
    }

    [Test]
    public async Task Truncated_and_corrupted_files_never_throw()
    {
        var sources = new[]
        {
            WebmBuilder.Sticker(3.0).Build(),
            new WebmBuilder { UnknownSizes = true, DurationMs = null, DefaultDurationNs = null }.Build()
        };
        var random = new Random(2026);

        foreach (var webm in sources)
        {
            for (var length = 0; length <= webm.Length; length += 7)
                await Validate(webm[..length]);

            for (var n = 0; n < 2000; n++)
            {
                var corrupt = (byte[])webm.Clone();
                for (var flips = random.Next(1, 8); flips > 0; flips--)
                    corrupt[random.Next(corrupt.Length)] = (byte)random.Next(256);
                await Validate(corrupt);
            }
        }

        Assert.That((await Validate([])).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
        Assert.That((await Validate([0x1A, 0x45, 0xDF, 0xA3, 0xFF])).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }
}
