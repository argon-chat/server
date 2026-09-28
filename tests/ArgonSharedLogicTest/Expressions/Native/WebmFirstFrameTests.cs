namespace ArgonSharedLogicTest.Expressions.Native;

using Argon.Features.Expressions;
using static ArgonSharedLogicTest.Expressions.WebmBuilder;

[TestFixture]
public class WebmFirstFrameTests
{
    private static readonly byte[] Frame = Enumerable.Repeat((byte)0x5A, 16).ToArray();
    private static readonly byte[] Alpha = Enumerable.Repeat((byte)0xA5, 16).ToArray();

    /// <summary>A WebM with a video track 1 and an audio track 2, whose single cluster holds <paramref name="blocks"/>.</summary>
    private static byte[] File(params byte[][] blocks)
    {
        var header = Master(0x1A45DFA3, Str(0x4282, "webm"));
        var tracks = Master(0x1654AE6B,
            Master(0xAE, UInt(0xD7, 1), UInt(0x83, 1), Str(0x86, "V_VP9")),
            Master(0xAE, UInt(0xD7, 2), UInt(0x83, 2), Str(0x86, "A_OPUS")));
        var cluster = Master(0x1F43B675, [UInt(0xE7, 0), .. blocks]);

        return [.. header, .. Master(0x18538067, tracks, cluster)];
    }

    private static byte[] SimpleBlock(byte track, byte flags, byte[] payload)
        => Element(0xA3, [(byte)(0x80 | track), 0, 0, flags, .. payload]);

    [Test]
    public void A_simple_block_gives_its_frame_and_no_alpha()
    {
        var file = new WebmBuilder().Build();

        Assert.That(WebmProbe.TryReadFirstFrame(file, out var frame), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(frame.Colour(file).ToArray(), Is.EqualTo(Frame));
            Assert.That(frame.AlphaLength, Is.Zero);
        });
    }

    [Test]
    public void A_block_group_gives_its_frame_and_the_alpha_in_its_additions()
    {
        var file = new WebmBuilder { Alpha = true }.Build();

        Assert.That(WebmProbe.TryReadFirstFrame(file, out var frame), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(frame.Colour(file).ToArray(), Is.EqualTo(Frame));
            Assert.That(frame.Alpha(file).ToArray(), Is.EqualTo(Alpha));
        });
    }

    [Test]
    public void Unknown_sized_segments_and_clusters_are_walked_too()
    {
        var file = new WebmBuilder { UnknownSizes = true, Alpha = true }.Build();

        Assert.That(WebmProbe.TryReadFirstFrame(file, out var frame), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(frame.Colour(file).ToArray(), Is.EqualTo(Frame));
            Assert.That(frame.Alpha(file).ToArray(), Is.EqualTo(Alpha));
        });
    }

    [Test]
    public void A_block_of_another_track_is_passed_over()
    {
        var file = File(SimpleBlock(2, 0x80, [1, 2, 3]), SimpleBlock(1, 0x80, Frame));

        Assert.That(WebmProbe.TryReadFirstFrame(file, out var frame), Is.True);
        Assert.That(frame.Colour(file).ToArray(), Is.EqualTo(Frame));
    }

    [Test]
    public void An_addition_of_another_id_is_no_alpha()
    {
        var group = Master(0xA0, Element(0xA1, [0x81, 0, 0, 0, .. Frame]),
            Master(0x75A1, Master(0xA6, UInt(0xEE, 4), Element(0xA5, Alpha))));
        var file = File(group);

        Assert.That(WebmProbe.TryReadFirstFrame(file, out var frame), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(frame.Colour(file).ToArray(), Is.EqualTo(Frame));
            Assert.That(frame.AlphaLength, Is.Zero);
        });
    }

    [Test]
    public void Without_a_video_block_there_is_no_frame()
        => Assert.Multiple(() =>
        {
            Assert.That(WebmProbe.TryReadFirstFrame(new WebmBuilder { Frames = 0, DurationMs = 3000 }.Build(), out _), Is.False, "no clusters");
            Assert.That(WebmProbe.TryReadFirstFrame(File(SimpleBlock(2, 0x80, [1, 2, 3])), out _), Is.False, "audio only");
            Assert.That(WebmProbe.TryReadFirstFrame(File(SimpleBlock(1, 0x80, [])), out _), Is.False, "an empty frame");
            Assert.That(WebmProbe.TryReadFirstFrame(File(SimpleBlock(1, 0x82, Frame)), out _), Is.False, "a laced block");
            Assert.That(WebmProbe.TryReadFirstFrame("not a webm"u8, out _), Is.False, "not EBML");
        });

    [Test]
    public void The_ffmpeg_fixtures_give_their_first_frames()
    {
        var opaque = NativeFixtures.Red64;
        var alpha  = NativeFixtures.HalfAlpha64;

        Assert.Multiple(() =>
        {
            Assert.That(WebmProbe.TryReadFirstFrame(opaque, out var plain), Is.True);
            Assert.That(plain.Length, Is.GreaterThan(0));
            Assert.That(plain.AlphaLength, Is.Zero);

            Assert.That(WebmProbe.TryReadFirstFrame(alpha, out var layered), Is.True);
            Assert.That(layered.Length, Is.GreaterThan(0));
            Assert.That(layered.AlphaLength, Is.GreaterThan(0));

            // A VP9 key frame starts with the frame marker 0b10 and the key-frame sync code 49 83 42.
            Assert.That(opaque[plain.Offset] >> 6, Is.EqualTo(2));
            Assert.That(opaque.AsSpan(plain.Offset + 1, 3).ToArray(), Is.EqualTo(new byte[] { 0x49, 0x83, 0x42 }));
        });
    }
}
