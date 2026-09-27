namespace ArgonSharedLogicTest.Expressions;

using System.Buffers.Binary;
using System.Text;

/// <summary>Writes small WebM files: EBML header, Info, Tracks and clusters of tiny SimpleBlocks.</summary>
internal sealed class WebmBuilder
{
    public string  DocType           { get; init; } = "webm";
    public string  Codec             { get; init; } = "V_VP9";
    public int     Width             { get; init; } = 512;
    public int     Height            { get; init; } = 512;
    public double? DurationMs        { get; init; } = 3000;
    public int     Frames            { get; init; } = 90;
    public double  FrameIntervalMs   { get; init; } = 1000.0 / 30;
    public ulong?  DefaultDurationNs { get; init; } = 33_333_333;
    public bool    Audio             { get; init; }
    public bool    UnknownSizes      { get; init; }
    public int     FramesPerCluster  { get; init; } = 30;
    public int     FramePayload      { get; init; } = 16;

    public static WebmBuilder Sticker(double seconds, double fps = 30)
        => new()
        {
            DurationMs        = seconds * 1000,
            Frames            = (int)Math.Round(seconds * fps),
            FrameIntervalMs   = 1000 / fps,
            DefaultDurationNs = (ulong)Math.Round(1e9 / fps)
        };

    public byte[] Build()
    {
        var header = Master(0x1A45DFA3,
            UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
            Str(0x4282, DocType), UInt(0x4287, 4), UInt(0x4285, 2));

        var info = new List<byte[]> { UInt(0x2AD7B1, 1_000_000), Str(0x4D80, "argon-test"), Str(0x5741, "argon-test") };
        if (DurationMs is { } duration)
            info.Add(Float(0x4489, duration));

        var video = new List<byte[]>
        {
            UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 1), Str(0x86, Codec),
            Master(0xE0, UInt(0xB0, (ulong)Width), UInt(0xBA, (ulong)Height))
        };
        if (DefaultDurationNs is { } frameNs)
            video.Add(UInt(0x23E383, frameNs));

        var tracks = new List<byte[]> { Master(0xAE, video.ToArray()) };
        if (Audio)
            tracks.Add(Master(0xAE, UInt(0xD7, 2), UInt(0x73C5, 2), UInt(0x83, 2), Str(0x86, "A_OPUS"),
                Master(0xE1, Float(0xB5, 48000), UInt(0x9F, 2))));

        var segment = new List<byte[]>
        {
            Master(0xEC, new byte[8]), // Void
            Master(0x1549A966, info.ToArray()),
            Master(0x1654AE6B, tracks.ToArray())
        };

        for (var first = 0; first < Frames; first += FramesPerCluster)
        {
            var clusterTime = (long)Math.Round(first * FrameIntervalMs);
            var children    = new List<byte[]> { UInt(0xE7, (ulong)clusterTime) };

            for (var i = first; i < Math.Min(first + FramesPerCluster, Frames); i++)
                children.Add(Block((short)(Math.Round(i * FrameIntervalMs) - clusterTime), keyframe: i == first));

            if (Audio)
                children.Add(Block(0, keyframe: true, track: 2));

            segment.Add(UnknownSizes ? Unknown(0x1F43B675, children.ToArray()) : Master(0x1F43B675, children.ToArray()));
        }

        var body = UnknownSizes ? Unknown(0x18538067, segment.ToArray()) : Master(0x18538067, segment.ToArray());
        return [.. header, .. body];
    }

    private byte[] Block(short relative, bool keyframe, byte track = 1)
    {
        var payload = new byte[4 + FramePayload];
        payload[0] = (byte)(0x80 | track);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(1), relative);
        payload[3] = keyframe ? (byte)0x80 : (byte)0;
        payload.AsSpan(4).Fill(0x5A);
        return Element(0xA3, payload);
    }

    public static byte[] Master(uint id, params byte[][] children)
        => Element(id, children.SelectMany(c => c).ToArray());

    public static byte[] Unknown(uint id, params byte[][] children)
        => [.. Id(id), 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. children.SelectMany(c => c)];

    public static byte[] Element(uint id, byte[] payload)
    {
        var size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)payload.Length);
        size[0] = 0x01;
        return [.. Id(id), .. size, .. payload];
    }

    public static byte[] UInt(uint id, ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        var skip = 0;
        while (skip < 7 && bytes[skip] == 0)
            skip++;
        return Element(id, bytes[skip..]);
    }

    public static byte[] Float(uint id, double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        return Element(id, bytes);
    }

    public static byte[] Str(uint id, string value)
        => Element(id, Encoding.ASCII.GetBytes(value));

    private static byte[] Id(uint id)
        => id switch
        {
            > 0xFFFFFF => [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id],
            > 0xFFFF   => [(byte)(id >> 16), (byte)(id >> 8), (byte)id],
            > 0xFF     => [(byte)(id >> 8), (byte)id],
            _          => [(byte)id]
        };
}
