namespace ArgonSharedLogicTest.Storage;

using System.ComponentModel.DataAnnotations;
using Argon.Features.Storage;

/// <summary>The rules a video's header is held to without a declaration, and the limits that must bind sanely.</summary>
[TestFixture]
public class VideoMediaTests
{
    private static Mp4ProbeResult Read(int width = 640, int height = 360, int durationMs = 3000, string? codec = "avc1.64001f", bool fastStart = true)
        => new(width, height, durationMs, 0, codec, null, false, fastStart, 0);

    [TestCase("avc1.64001f", true)]
    [TestCase("avc3.42e01e", true)]
    [TestCase("AVC1", true)]
    [TestCase("hvc1.1.6.L93.B0", false)]
    [TestCase("av01.0.04M.08", false)]
    [TestCase(null, false)]
    public void Only_h264_is_accepted(string? codec, bool accepted)
        => Assert.That(VideoMedia.IsH264(codec), Is.EqualTo(accepted));

    [Test]
    public void A_header_is_playable_only_within_its_bounds()
    {
        const int max = 60_000;

        Assert.Multiple(() =>
        {
            Assert.That(VideoMedia.IsPlayable(Read(), max), Is.True);
            Assert.That(VideoMedia.IsPlayable(Read(width: 1), max), Is.False, "a one-pixel video");
            Assert.That(VideoMedia.IsPlayable(Read(height: 8193), max), Is.False, "taller than 8192");
            Assert.That(VideoMedia.IsPlayable(Read(durationMs: 0), max), Is.False, "no duration (a fragmented file)");
            Assert.That(VideoMedia.IsPlayable(Read(durationMs: max + 1), max), Is.False, "over the cap");
            Assert.That(VideoMedia.IsPlayable(Read(codec: "hvc1"), max), Is.False, "not H.264");
            Assert.That(VideoMedia.IsPlayable(Read(fastStart: false), max), Is.False, "moov after mdat");
        });
    }

    [TestCase(5L * 1024 * 1024, true)]
    [TestCase(5L * 1024 * 1024 - 1, false)]
    [TestCase(1024L, false)]
    public void A_part_size_under_the_s3_minimum_does_not_bind(long partSize, bool valid)
    {
        var options = new FileLimitsOptions { VideoPartSizeBytes = partSize };

        Assert.That(Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true), Is.EqualTo(valid));
    }
}
