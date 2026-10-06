namespace ArgonSharedLogicTest.Storage;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>Writes tiny but structurally complete MP4s: ftyp, moov with a video and an optional audio track, and mdat.</summary>
internal sealed class Mp4Builder
{
    public int     Width       { get; init; } = 640;
    public int     Height      { get; init; } = 360;
    public int     DurationMs  { get; init; } = 3000;
    public uint    Timescale   { get; init; } = 1000;
    public int     Rotation    { get; init; }
    public bool    FastStart   { get; init; } = true;
    public bool    Version1    { get; init; }
    public bool    LargeMdat   { get; init; }
    public string  VideoFourcc { get; init; } = "avc1";
    public byte[]? VideoConfig { get; init; } = AvcC(0x64, 0x00, 0x1f);
    public bool    Audio       { get; init; } = true;
    public string  AudioFourcc { get; init; } = "mp4a";
    public byte[]? AudioConfig { get; init; } = Esds(0x40, 2);
    public int     MdatSize    { get; init; } = 1024;

    /// <summary>Seeds the mdat filler; null fills it from <see cref="RandomNumberGenerator"/>, so every build is unique.</summary>
    public int?    Seed        { get; init; }

    private byte Version => Version1 ? (byte)1 : (byte)0;

    public byte[] Build()
    {
        var duration = (ulong)DurationMs * Timescale / 1000;
        var tracks   = new List<byte[]> { Mvhd(duration), Trak(1, video: true, duration) };
        if (Audio)
            tracks.Add(Trak(2, video: false, duration));

        var ftyp = Box("ftyp", Ascii("isom"), U32(0x200), Ascii("isom"), Ascii("iso2"), Ascii("avc1"), Ascii("mp41"));
        var moov = Box("moov", tracks.ToArray());
        var mdat = Mdat();

        return FastStart ? [.. ftyp, .. moov, .. mdat] : [.. ftyp, .. mdat, .. moov];
    }

    private byte[] Mvhd(ulong duration)
        => FullBox("mvhd", Version, 0, Times(duration), U32(0x00010000), U16(0x0100), new byte[10], Matrix(0), new byte[24], U32(3));

    private byte[] Trak(uint id, bool video, ulong duration)
    {
        byte[] times = Version1
            ? [.. U64(0), .. U64(0), .. U32(id), .. U32(0), .. U64(duration)]
            : [.. U32(0), .. U32(0), .. U32(id), .. U32(0), .. U32((uint)duration)];

        var tkhd = FullBox("tkhd", Version, 3, times, new byte[8], U16(0), U16(0), U16(video ? (ushort)0 : (ushort)0x0100), U16(0),
            Matrix(video ? Rotation : 0), U32(video ? (uint)Width << 16 : 0), U32(video ? (uint)Height << 16 : 0));

        var stbl = Box("stbl",
            FullBox("stsd", 0, 0, U32(1), video ? VisualEntry() : AudioEntry()),
            FullBox("stts", 0, 0, U32(0)),
            FullBox("stsc", 0, 0, U32(0)),
            FullBox("stsz", 0, 0, U32(0), U32(0)),
            FullBox("stco", 0, 0, U32(0)));

        var minf = Box("minf",
            video ? FullBox("vmhd", 0, 1, new byte[8]) : FullBox("smhd", 0, 0, new byte[4]),
            Box("dinf", FullBox("dref", 0, 0, U32(1), FullBox("url ", 0, 1))),
            stbl);

        var mdia = Box("mdia",
            FullBox("mdhd", Version, 0, Times(duration), U16(0x55C4), U16(0)),
            FullBox("hdlr", 0, 0, U32(0), Ascii(video ? "vide" : "soun"), new byte[12], Ascii(video ? "VideoHandler\0" : "SoundHandler\0")),
            minf);

        return Box("trak", tkhd, mdia);
    }

    /// <summary>VisualSampleEntry: 78 fixed bytes, then the codec configuration.</summary>
    private byte[] VisualEntry()
        => Box(VideoFourcc, new byte[6], U16(1), new byte[16], U16((ushort)Width), U16((ushort)Height),
            U32(0x00480000), U32(0x00480000), U32(0), U16(1), new byte[32], U16(0x0018), U16(0xFFFF), VideoConfig ?? []);

    /// <summary>AudioSampleEntry version 0: stereo, 16 bit, 48 kHz.</summary>
    private byte[] AudioEntry()
        => Box(AudioFourcc, new byte[6], U16(1), new byte[8], U16(2), U16(16), U16(0), U16(0), U32(48000u << 16), AudioConfig ?? []);

    private byte[] Mdat()
    {
        var payload = new byte[MdatSize];
        if (Seed is { } seed)
            new Random(seed).NextBytes(payload);
        else
            RandomNumberGenerator.Fill(payload);

        return LargeMdat ? [.. U32(1), .. Ascii("mdat"), .. U64((ulong)MdatSize + 16), .. payload] : Box("mdat", payload);
    }

    private byte[] Times(ulong duration)
        => Version1
            ? [.. U64(0), .. U64(0), .. U32(Timescale), .. U64(duration)]
            : [.. U32(0), .. U32(0), .. U32(Timescale), .. U32((uint)duration)];

    private static byte[] Matrix(int rotation)
    {
        var (a, b, c, d) = rotation switch
        {
            90  => (0, 1, -1, 0),
            180 => (-1, 0, 0, -1),
            270 => (0, -1, 1, 0),
            _   => (1, 0, 0, 1)
        };
        return [.. I32(a << 16), .. I32(b << 16), .. I32(0), .. I32(c << 16), .. I32(d << 16), .. I32(0), .. I32(0), .. I32(0), .. I32(0x40000000)];
    }

    public static byte[] AvcC(byte profile, byte compat, byte level)
        => Box("avcC", [1, profile, compat, level, 0xFF, 0xE0, 0]);

    public static byte[] HvcC(byte profileSpace, bool highTier, byte profileIdc, uint compatFlags, ReadOnlySpan<byte> constraints, byte levelIdc)
    {
        var flags = new byte[6];
        constraints[..Math.Min(6, constraints.Length)].CopyTo(flags);

        return Box("hvcC",
        [
            1, (byte)((profileSpace << 6) | (highTier ? 0x20 : 0) | (profileIdc & 0x1F)), .. U32(compatFlags), .. flags, levelIdc,
            0xF0, 0x00, 0xFC, 0xFD, 0xF8, 0xF8, 0x00, 0x00, 0x0F, 0x00
        ]);
    }

    public static byte[] Av1C(byte profile, byte level, bool highTier, int bitDepth)
    {
        var depth = bitDepth switch { 12 => 0x60, 10 => 0x40, _ => 0 };
        return Box("av1C", [0x81, (byte)((profile << 5) | (level & 0x1F)), (byte)((highTier ? 0x80 : 0) | depth | 0x0C), 0]);
    }

    public static byte[] VpcC(byte profile, byte level, byte bitDepth)
        => FullBox("vpcC", 1, 0, [profile, level, (byte)((bitDepth << 4) | 0x02), 1, 1, 1, 0, 0]);

    /// <summary>esds with ES, DecoderConfig and (for 0x40) an AudioSpecificConfig at 48 kHz stereo; lengths in the 4-byte form ffmpeg writes.</summary>
    public static byte[] Esds(byte objectType, byte audioObjectType)
    {
        byte[] config = [objectType, 0x15, 0, 0, 0, .. U32(128_000), .. U32(128_000)];
        if (objectType == 0x40)
            config = [.. config, .. Descriptor(0x05, AudioSpecificConfig(audioObjectType))];

        byte[] es = [.. U16(1), 0, .. Descriptor(0x04, config), .. Descriptor(0x06, [2])];
        return FullBox("esds", 0, 0, Descriptor(0x03, es));
    }

    public static byte[] DOps()
        => Box("dOps", [0, 2, .. U16(312), .. U32(48000), 0, 0, 0]);

    public static byte[] Box(string type, params byte[][] parts)
    {
        var box = new byte[8 + parts.Sum(p => p.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4, 4));

        var at = 8;
        foreach (var part in parts)
        {
            part.CopyTo(box, at);
            at += part.Length;
        }
        return box;
    }

    public static byte[] FullBox(string type, byte version, uint flags, params byte[][] parts)
        => Box(type, [U32(((uint)version << 24) | flags), .. parts]);

    private static byte[] AudioSpecificConfig(byte aot)
    {
        // Object type (escaped past 30), frequency index 3 (48 kHz), channel configuration 2.
        var bits  = aot < 31 ? aot : (31UL << 6) | (ulong)(aot - 32);
        var count = (aot < 31 ? 5 : 11) + 8;
        bits = (bits << 8) | 0x32;

        var bytes = new byte[(count + 7) / 8];
        bits <<= bytes.Length * 8 - count;
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(bits >> (8 * (bytes.Length - 1 - i)));
        return bytes;
    }

    private static byte[] Descriptor(byte tag, byte[] body)
        =>
        [
            tag,
            (byte)(0x80 | ((body.Length >> 21) & 0x7F)), (byte)(0x80 | ((body.Length >> 14) & 0x7F)),
            (byte)(0x80 | ((body.Length >> 7) & 0x7F)), (byte)(body.Length & 0x7F),
            .. body
        ];

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] I32(int value) => U32((uint)value);

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
