namespace ArgonSharedLogicTest.Storage;

using System.Buffers.Binary;
using Argon.Features.Storage;

[TestFixture]
public class Mp4ProbeTests
{
    private static (bool Ok, Mp4ProbeResult Result, Mp4ProbeError Error) Probe(byte[] file)
        => Probe(file, file.Length);

    private static (bool Ok, Mp4ProbeResult Result, Mp4ProbeError Error) Probe(ReadOnlySpan<byte> head, long totalSize)
    {
        var ok = Mp4Probe.TryProbe(head, totalSize, out var result, out var error);
        return (ok, result, error);
    }

    /// <summary>Where the first box of the type starts (its size field).</summary>
    private static int BoxAt(byte[] file, ReadOnlySpan<byte> type)
        => file.AsSpan().IndexOf(type) - 4;

    [Test]
    public void A_faststart_file_reports_moov_before_mdat()
    {
        var mp4 = new Mp4Builder { Seed = 1 }.Build();

        var (ok, result, error) = Probe(mp4);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, error.ToString());
            Assert.That(error, Is.EqualTo(Mp4ProbeError.None));
            Assert.That(result.FastStart, Is.True);
            Assert.That(result.MoovEnd, Is.EqualTo(BoxAt(mp4, "mdat"u8)));
            Assert.That((result.Width, result.Height), Is.EqualTo((640, 360)));
            Assert.That(result.DurationMs, Is.EqualTo(3000));
            Assert.That(result.Rotation, Is.Zero);
            Assert.That(result.VideoCodec, Is.EqualTo("avc1.64001f"));
            Assert.That(result.AudioCodec, Is.EqualTo("mp4a.40.2"));
            Assert.That(result.HasAudio, Is.True);
        });
    }

    [Test]
    public void A_moov_after_mdat_inside_the_head_still_parses()
    {
        var mp4 = new Mp4Builder { FastStart = false, Seed = 1 }.Build();

        var (ok, result, error) = Probe(mp4);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, error.ToString());
            Assert.That(result.FastStart, Is.False);
            Assert.That(result.MoovEnd, Is.EqualTo(mp4.Length));
            Assert.That((result.Width, result.Height), Is.EqualTo((640, 360)));
            Assert.That(result.VideoCodec, Is.EqualTo("avc1.64001f"));
        });
    }

    [Test]
    public void A_moov_behind_a_large_mdat_is_not_in_the_head()
    {
        var mp4 = new Mp4Builder { FastStart = false, MdatSize = 1 << 20, Seed = 1 }.Build();

        var (ok, _, error) = Probe(mp4.AsSpan(0, 64 * 1024), mp4.Length);

        Assert.That(ok, Is.False);
        Assert.That(error, Is.EqualTo(Mp4ProbeError.MoovNotInHead));
    }

    [Test]
    public void A_moov_running_past_the_head_is_not_in_the_head()
    {
        var mp4  = new Mp4Builder { Seed = 1 }.Build();
        var moov = BoxAt(mp4, "moov"u8);
        var end  = BoxAt(mp4, "mdat"u8);

        foreach (var cut in new[] { moov + 4, moov + 8, moov + 100, end - 1 })
            Assert.That(Probe(mp4.AsSpan(0, cut), mp4.Length).Error, Is.EqualTo(Mp4ProbeError.MoovNotInHead), $"head of {cut}");

        Assert.That(Probe(mp4.AsSpan(0, end), mp4.Length).Ok, Is.True, "a head ending where moov does");
    }

    [TestCase(0, 640, 360)]
    [TestCase(90, 360, 640)]
    [TestCase(180, 640, 360)]
    [TestCase(270, 360, 640)]
    public void Quarter_turns_are_reported_and_90_and_270_swap_the_size(int rotation, int width, int height)
    {
        var (ok, result, error) = Probe(new Mp4Builder { Rotation = rotation }.Build());

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.Rotation, Is.EqualTo(rotation));
        Assert.That((result.Width, result.Height), Is.EqualTo((width, height)));
    }

    [Test]
    public void A_track_header_without_a_size_falls_back_to_the_sample_entry()
    {
        var mp4 = new Mp4Builder { Width = 1920, Height = 1080, Rotation = 90 }.Build();
        mp4.AsSpan(BoxAt(mp4, "tkhd"u8) + 8 + 76, 8).Clear();

        var (ok, result, error) = Probe(mp4);

        Assert.That(ok, Is.True, error.ToString());
        Assert.That((result.Width, result.Height), Is.EqualTo((1080, 1920)));
    }

    private static TestCaseData Codec(string fourcc, byte[]? config, string expected)
        => new TestCaseData(fourcc, config, expected).SetArgDisplayNames(fourcc, expected);

    private static IEnumerable<TestCaseData> VideoCodecs()
    {
        yield return Codec("avc1", Mp4Builder.AvcC(0x64, 0x00, 0x1f), "avc1.64001f");
        yield return Codec("avc3", Mp4Builder.AvcC(0x64, 0x00, 0x1f), "avc3.64001f");
        yield return Codec("avc1", Mp4Builder.AvcC(0x42, 0xC0, 0x1E), "avc1.42c01e");
        yield return Codec("hvc1", Mp4Builder.HvcC(0, false, 1, 0x60000000, [0xB0, 0, 0, 0, 0, 0], 93), "hvc1.1.6.L93.B0");
        yield return Codec("hev1", Mp4Builder.HvcC(0, true, 2, 0x20000000, [0x90, 0, 0, 0, 0, 0], 153), "hev1.2.4.H153.90");
        yield return Codec("hvc1", Mp4Builder.HvcC(1, false, 1, 0x60000000, [0xB0, 0, 0x08, 0, 0, 0], 120), "hvc1.A1.6.L120.B0.0.8");
        yield return Codec("av01", Mp4Builder.Av1C(0, 4, false, 8), "av01.0.04M.08");
        yield return Codec("av01", Mp4Builder.Av1C(1, 13, true, 10), "av01.1.13H.10");
        yield return Codec("av01", Mp4Builder.Av1C(2, 9, false, 12), "av01.2.09M.12");
        yield return Codec("vp09", Mp4Builder.VpcC(0, 10, 8), "vp09.00.10.08");
        yield return Codec("vp09", Mp4Builder.VpcC(2, 41, 10), "vp09.02.41.10");
        yield return Codec("avc1", null, "avc1");
        yield return Codec("hvc1", Mp4Builder.Box("hvcC", [1, 2, 3]), "hvc1");
        yield return Codec("mp4v", null, "mp4v");
    }

    [TestCaseSource(nameof(VideoCodecs))]
    public void The_video_codec_is_an_rfc_6381_string(string fourcc, byte[]? config, string expected)
    {
        var (ok, result, error) = Probe(new Mp4Builder { VideoFourcc = fourcc, VideoConfig = config }.Build());

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.VideoCodec, Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> AudioCodecs()
    {
        yield return Codec("mp4a", Mp4Builder.Esds(0x40, 2), "mp4a.40.2");
        yield return Codec("mp4a", Mp4Builder.Esds(0x40, 5), "mp4a.40.5");
        yield return Codec("mp4a", Mp4Builder.Esds(0x40, 42), "mp4a.40.42");
        yield return Codec("mp4a", Mp4Builder.Esds(0x6B, 0), "mp4a.6b");
        yield return Codec("mp4a", null, "mp4a");
        yield return Codec("Opus", Mp4Builder.DOps(), "opus");
        yield return Codec("fLaC", null, "flac");
        yield return Codec("ac-3", null, "ac-3");
        yield return Codec("ec-3", null, "ec-3");
        yield return Codec("alac", null, "alac");
    }

    [TestCaseSource(nameof(AudioCodecs))]
    public void The_audio_codec_is_an_rfc_6381_string(string fourcc, byte[]? config, string expected)
    {
        var (ok, result, error) = Probe(new Mp4Builder { AudioFourcc = fourcc, AudioConfig = config }.Build());

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.HasAudio, Is.True);
        Assert.That(result.AudioCodec, Is.EqualTo(expected));
    }

    [Test]
    public void A_quicktime_version_1_sound_entry_keeps_its_esds_in_wave()
    {
        // As ffmpeg's MOV muxer writes it: 16 more fixed bytes, then wave { frma, mp4a, esds, terminator }.
        byte[] tail =
        [
            .. new byte[16],
            .. Mp4Builder.Box("wave", Mp4Builder.Box("frma", "mp4a"u8.ToArray()), Mp4Builder.Box("mp4a", new byte[4]),
                Mp4Builder.Esds(0x40, 2), [0, 0, 0, 8, 0, 0, 0, 0])
        ];
        var mov = new Mp4Builder { AudioConfig = tail }.Build();
        BinaryPrimitives.WriteUInt16BigEndian(mov.AsSpan(mov.AsSpan().IndexOf("mp4a"u8) + 12), 1);

        var (ok, result, error) = Probe(mov);

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.AudioCodec, Is.EqualTo("mp4a.40.2"));
    }

    [Test]
    public void Without_an_audio_track_there_is_no_audio_codec()
    {
        var (ok, result, error) = Probe(new Mp4Builder { Audio = false }.Build());

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.HasAudio, Is.False);
        Assert.That(result.AudioCodec, Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void A_64_bit_mdat_and_version_1_boxes_parse(bool fastStart)
    {
        var mp4 = new Mp4Builder { LargeMdat = true, Version1 = true, FastStart = fastStart, Rotation = 90 }.Build();

        var (ok, result, error) = Probe(mp4);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, error.ToString());
            Assert.That(result.FastStart, Is.EqualTo(fastStart));
            Assert.That(result.MoovEnd, Is.EqualTo(fastStart ? BoxAt(mp4, "mdat"u8) : mp4.Length));
            Assert.That(result.DurationMs, Is.EqualTo(3000));
            Assert.That(result.Rotation, Is.EqualTo(90));
            Assert.That((result.Width, result.Height), Is.EqualTo((360, 640)));
        });
    }

    [Test]
    public void A_90_khz_timescale_is_converted_to_milliseconds()
    {
        var (ok, result, error) = Probe(new Mp4Builder { Timescale = 90000, DurationMs = 5000 }.Build());

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.DurationMs, Is.EqualTo(5000));
    }

    [TestCase(0u)]
    [TestCase(uint.MaxValue)]
    public void An_unknown_movie_duration_falls_back_to_the_longest_track(uint declared)
    {
        var mp4 = new Mp4Builder { DurationMs = 4321 }.Build();
        BinaryPrimitives.WriteUInt32BigEndian(mp4.AsSpan(BoxAt(mp4, "mvhd"u8) + 24), declared);

        Assert.That(Probe(mp4).Result.DurationMs, Is.EqualTo(4321));
    }

    [Test]
    public void A_duration_past_int_max_milliseconds_is_clamped()
    {
        var mp4 = new Mp4Builder { Version1 = true }.Build();
        BinaryPrimitives.WriteUInt64BigEndian(mp4.AsSpan(BoxAt(mp4, "mvhd"u8) + 32), ulong.MaxValue - 1);

        Assert.That(Probe(mp4).Result.DurationMs, Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void A_zero_timescale_or_a_missing_mvhd_is_malformed()
    {
        var zero = new Mp4Builder().Build();
        BinaryPrimitives.WriteUInt32BigEndian(zero.AsSpan(BoxAt(zero, "mvhd"u8) + 20), 0);

        var missing = new Mp4Builder().Build();
        "xvhd"u8.CopyTo(missing.AsSpan(BoxAt(missing, "mvhd"u8) + 4));

        Assert.That(Probe(zero).Error, Is.EqualTo(Mp4ProbeError.Malformed));
        Assert.That(Probe(missing).Error, Is.EqualTo(Mp4ProbeError.Malformed));
    }

    [Test]
    public void A_file_not_starting_with_ftyp_is_not_mp4()
    {
        var mp4 = new Mp4Builder().Build();

        Assert.Multiple(() =>
        {
            Assert.That(Probe([.. Mp4Builder.Box("free", new byte[8]), .. mp4]).Error, Is.EqualTo(Mp4ProbeError.NotMp4));
            Assert.That(Probe("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()).Error, Is.EqualTo(Mp4ProbeError.NotMp4));
            Assert.That(Probe([]).Error, Is.EqualTo(Mp4ProbeError.NotMp4));
            Assert.That(Probe(mp4.AsSpan(0, 7), mp4.Length).Error, Is.EqualTo(Mp4ProbeError.NotMp4), "a head shorter than a box header");
        });
    }

    [Test]
    public void A_file_with_only_an_audio_track_has_no_video_track()
    {
        var mp4 = new Mp4Builder { Seed = 1 }.Build();
        "xxxx"u8.CopyTo(mp4.AsSpan(mp4.AsSpan().IndexOf("vide"u8)));

        var (ok, _, error) = Probe(mp4);

        Assert.That(ok, Is.False);
        Assert.That(error, Is.EqualTo(Mp4ProbeError.NoVideoTrack));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void A_last_box_of_size_zero_runs_to_the_end(bool fastStart)
    {
        var mp4  = new Mp4Builder { FastStart = fastStart, Seed = 1 }.Build();
        var last = fastStart ? BoxAt(mp4, "mdat"u8) : mp4.AsSpan().LastIndexOf("moov"u8) - 4;
        BinaryPrimitives.WriteUInt32BigEndian(mp4.AsSpan(last), 0);

        var (ok, result, error) = Probe(mp4);

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.MoovEnd, Is.EqualTo(fastStart ? last : mp4.Length));
    }

    [Test]
    public void Free_and_uuid_boxes_before_moov_are_skipped()
    {
        var mp4 = new Mp4Builder { Seed = 1 }.Build();
        byte[] padded = [.. mp4[..32], .. Mp4Builder.Box("uuid", new byte[20]), .. Mp4Builder.Box("free"), .. mp4[32..]];

        var (ok, result, error) = Probe(padded);

        Assert.That(ok, Is.True, error.ToString());
        Assert.That(result.MoovEnd, Is.EqualTo(BoxAt(mp4, "mdat"u8) + 36));
    }

    [Test]
    public void Too_many_boxes_in_one_container_is_malformed()
    {
        var mp4   = new Mp4Builder().Build();
        var frees = Enumerable.Repeat(Mp4Builder.Box("free"), 1100).ToArray();

        byte[] topLevel = [.. mp4[..32], .. frees.SelectMany(b => b), .. mp4[32..]];
        byte[] inMoov   = [.. mp4[..32], .. Mp4Builder.Box("moov", frees)];

        Assert.That(Probe(topLevel).Error, Is.EqualTo(Mp4ProbeError.Malformed));
        Assert.That(Probe(inMoov).Error, Is.EqualTo(Mp4ProbeError.Malformed));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Every_truncation_short_of_moov_is_refused_and_none_throws(bool fastStart)
    {
        var mp4     = new Mp4Builder { FastStart = fastStart, MdatSize = 64, Seed = 3 }.Build();
        var moovEnd = Probe(mp4).Result.MoovEnd;
        Assert.That(moovEnd, Is.GreaterThan(0));

        for (var length = 0; length <= mp4.Length; length++)
        {
            var whole  = Probe(mp4.AsSpan(0, length), length);
            var prefix = Probe(mp4.AsSpan(0, length), mp4.Length);

            if (length < moovEnd)
            {
                Assert.That(whole.Ok, Is.False, $"cut at {length} of {mp4.Length}");
                Assert.That(prefix.Error, Is.EqualTo(length < 8 ? Mp4ProbeError.NotMp4 : Mp4ProbeError.MoovNotInHead), $"head of {length}");
            }
            else
                Assert.That(prefix.Ok, Is.True, $"head of {length}");

            // A file cut inside mdat: the mdat header claims more than there is.
            if (length >= moovEnd + 8 && length < mp4.Length)
                Assert.That(whole.Error, Is.EqualTo(Mp4ProbeError.Malformed), $"cut at {length} of {mp4.Length}");
        }
    }

    [Test]
    public void Random_and_corrupted_input_never_throws()
    {
        var random  = new Random(2026);
        var source  = new Mp4Builder { MdatSize = 64, Seed = 5 }.Build();
        var ftyp    = source[..32];
        var moovEnd = BoxAt(source, "mdat"u8);

        for (var n = 0; n < 500; n++)
        {
            var garbage = new byte[random.Next(0, 600)];
            random.NextBytes(garbage);

            Check(garbage, garbage.Length);
            Check([.. ftyp, .. garbage], 32 + garbage.Length);
            Check([.. ftyp, .. garbage], long.MaxValue);
        }

        // Every header byte set to the values that hit size and version edges.
        foreach (var value in new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF })
        {
            for (var at = 0; at < moovEnd; at++)
            {
                var patched = (byte[])source.Clone();
                patched[at] = value;
                Check(patched, patched.Length);
            }
        }

        for (var n = 0; n < 3000; n++)
        {
            var corrupt = (byte[])source.Clone();
            for (var flips = random.Next(1, 8); flips > 0; flips--)
                corrupt[random.Next(moovEnd)] = (byte)random.Next(256);
            Check(corrupt, corrupt.Length);
        }

        Check(source, -1);
        Check(source, 0);

        static void Check(byte[] data, long totalSize)
        {
            var ok = Mp4Probe.TryProbe(data, totalSize, out _, out var error);
            Assert.That(ok, Is.EqualTo(error == Mp4ProbeError.None));
        }
    }

    /// <summary>The first header bytes say where moov ends, so the rest of the read can stop there.</summary>
    [Test]
    public void Moov_is_located_from_the_box_headers_alone()
    {
        var mp4   = new Mp4Builder { Seed = 3, MdatSize = 64 * 1024 }.Build();
        var moov  = BoxAt(mp4, "moov"u8);
        var end   = moov + BinaryPrimitives.ReadInt32BigEndian(mp4.AsSpan(moov));

        Assert.Multiple(() =>
        {
            Assert.That(Mp4Probe.TryLocateMoov(mp4.AsSpan(0, moov + 8), mp4.Length, out var located), Is.True);
            Assert.That(located, Is.EqualTo(end));
            Assert.That(Mp4Probe.TryProbe(mp4.AsSpan(0, (int)located), mp4.Length, out _, out var error), Is.True, error.ToString());
        });
    }

    [Test]
    public void Moov_after_mdat_or_out_of_reach_is_not_located()
    {
        var late = new Mp4Builder { Seed = 4, FastStart = false }.Build();
        var mp4  = new Mp4Builder { Seed = 4 }.Build();

        Assert.Multiple(() =>
        {
            Assert.That(Mp4Probe.TryLocateMoov(late, late.Length, out _), Is.False, "moov after mdat");
            Assert.That(Mp4Probe.TryLocateMoov(mp4.AsSpan(0, BoxAt(mp4, "moov"u8) + 4), mp4.Length, out _), Is.False, "moov header cut");
            Assert.That(Mp4Probe.TryLocateMoov("not an mp4 at all"u8, 17, out _), Is.False);
        });
    }
}
