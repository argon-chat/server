namespace Argon.Features.Expressions;

using System.Buffers.Binary;
using System.Numerics;

internal struct WebmInfo
{
    public int    TrackCount;
    public ulong  TrackNumber;
    public ulong  TrackType;
    public bool   IsVp9;
    public int    Width;
    public int    Height;
    public long   Frames;
    public bool   ForeignBlocks;
    public double DurationSeconds;

    /// <summary>One timestamp tick (at most 1 ms): how far a quantised duration may overshoot.</summary>
    public double SlackSeconds;

    /// <summary>The higher of the declared (<c>DefaultDuration</c>) and the measured rate, measured leniently.</summary>
    public double Fps;

    /// <summary>One frame: the declared <c>DefaultDuration</c>, else the mean block interval.</summary>
    public double FrameSeconds;
}

/// <summary>Where the first video frame lies in the file, and its alpha packet (<c>BlockAddID</c> 1) if it has one.</summary>
internal readonly record struct WebmFrame(int Offset, int Length, int AlphaOffset, int AlphaLength)
{
    public ReadOnlySpan<byte> Colour(ReadOnlySpan<byte> file) => file.Slice(Offset, Length);

    public ReadOnlySpan<byte> Alpha(ReadOnlySpan<byte> file) => file.Slice(AlphaOffset, AlphaLength);
}

/// <summary>
/// Minimal EBML/Matroska reader for WebM stickers. Reads the header, <c>Info</c> and <c>Tracks</c>, then
/// scans cluster and block timestamps: the measured length is compared with the declared
/// <c>Duration</c> and the longer wins, so a file without <c>Duration</c> (MediaRecorder output) is
/// still measured. Allocates nothing proportional to the input.
/// </summary>
internal static class WebmProbe
{
    private const uint EbmlHeader      = 0x1A45DFA3;
    private const uint DocType         = 0x4282;
    private const uint Segment         = 0x18538067;
    private const uint SeekHead        = 0x114D9B74;
    private const uint Info            = 0x1549A966;
    private const uint TimestampScale  = 0x2AD7B1;
    private const uint Duration        = 0x4489;
    private const uint Tracks          = 0x1654AE6B;
    private const uint TrackEntry      = 0xAE;
    private const uint TrackNumber     = 0xD7;
    private const uint TrackType       = 0x83;
    private const uint CodecId         = 0x86;
    private const uint DefaultDuration = 0x23E383;
    private const uint Video           = 0xE0;
    private const uint PixelWidth      = 0xB0;
    private const uint PixelHeight     = 0xBA;
    private const uint Cluster         = 0x1F43B675;
    private const uint Cues            = 0x1C53BB6B;
    private const uint Tags            = 0x1254C367;
    private const uint Chapters        = 0x1043A770;
    private const uint Attachments     = 0x1941A469;
    private const uint ClusterTime     = 0xE7;
    private const uint SimpleBlock     = 0xA3;
    private const uint BlockGroup      = 0xA0;
    private const uint Block           = 0xA1;
    private const uint BlockDuration   = 0x9B;
    private const uint BlockAdditions  = 0x75A1;
    private const uint BlockMore       = 0xA6;
    private const uint BlockAddId      = 0xEE;
    private const uint BlockAdditional = 0xA5;

    private const long UnknownSize = -1;

    private const double MaxSlackNs = 1_000_000;

    private struct Blocks
    {
        public long  Count;
        public long  First;
        public long  Last;
        public long  LastEnd;
        public ulong Track;
        public bool  Mixed;
    }

    public static bool TryRead(ReadOnlySpan<byte> file, out WebmInfo info)
    {
        info = default;
        var pos = 0;

        if (!TryHeader(file, ref pos, file.Length, out var id, out var size) || id != EbmlHeader || size < 0)
            return false;
        if (!ReadsDocType(file.Slice(pos, (int)size), "webm"u8))
            return false;
        pos += (int)size;

        while (true)
        {
            if (!TryHeader(file, ref pos, file.Length, out id, out size))
                return false;
            if (id == Segment)
                break;
            if (size < 0)
                return false;
            pos += (int)size;
        }

        var end = size == UnknownSize ? file.Length : pos + (int)size;
        if (end > file.Length)
            return false;

        var    scale      = 1_000_000UL;
        double declared   = -1;
        var    frameNs    = 0UL;
        var    haveInfo   = false;
        var    haveTracks = false;
        var    blocks     = new Blocks { First = long.MaxValue, Last = long.MinValue, LastEnd = long.MinValue };

        while (pos < end)
        {
            if (!TryHeader(file, ref pos, end, out id, out size))
                return false;

            if (size == UnknownSize)
            {
                if (id != Cluster || !ScanCluster(file, ref pos, end, unknownSize: true, ref blocks))
                    return false;
                continue;
            }

            var payload = file.Slice(pos, (int)size);

            switch (id)
            {
                case Info:
                    if (!ReadInfo(payload, ref scale, ref declared))
                        return false;
                    haveInfo = true;
                    break;

                case Tracks:
                    if (!ReadTracks(payload, ref info, ref frameNs))
                        return false;
                    haveTracks = true;
                    break;

                case Cluster:
                {
                    var inner = 0;
                    if (!ScanCluster(payload, ref inner, payload.Length, unknownSize: false, ref blocks))
                        return false;
                    break;
                }
            }

            pos += (int)size;
        }

        // Neither a Duration nor a single block: the length cannot be told.
        if (!haveInfo || !haveTracks || scale == 0 || (declared < 0 && blocks.Count == 0))
            return false;

        info.Frames        = blocks.Count;
        info.ForeignBlocks = blocks.Mixed || (blocks.Count > 0 && blocks.Track != info.TrackNumber);

        var slackNs    = Math.Min(scale, MaxSlackNs);
        var measuredNs = 0.0;
        var spanNs     = blocks.Count > 0 ? (double)(blocks.Last - blocks.First) * scale : 0;

        if (blocks.Count > 0)
        {
            var tailNs = frameNs > 0 ? frameNs
                : blocks.LastEnd > blocks.Last ? (double)(blocks.LastEnd - blocks.Last) * scale
                : blocks.Count > 1 ? spanNs / (blocks.Count - 1)
                : 0;

            measuredNs = spanNs + tailNs;

            if (blocks.Count > 1)
                info.Fps = (blocks.Count - 1) / ((spanNs + slackNs) / 1e9);
        }

        if (frameNs > 0)
            info.Fps = Math.Max(info.Fps, 1e9 / frameNs);

        info.FrameSeconds = frameNs > 0 ? frameNs / 1e9 : blocks.Count > 1 ? spanNs / (blocks.Count - 1) / 1e9 : 0;

        info.DurationSeconds = Math.Max(declared * scale, measuredNs) / 1e9;
        info.SlackSeconds    = slackNs / 1e9;
        return true;
    }

    /// <summary>
    /// The first block of the video track: its frame and, for VP9 with alpha, the packet in its
    /// <c>BlockAdditional</c>. A laced block is not taken.
    /// </summary>
    public static bool TryReadFirstFrame(ReadOnlySpan<byte> file, out WebmFrame frame)
    {
        frame = default;
        var pos = 0;

        if (!TryHeader(file, ref pos, file.Length, out var id, out var size) || id != EbmlHeader || size < 0)
            return false;
        pos += (int)size;

        while (true)
        {
            if (!TryHeader(file, ref pos, file.Length, out id, out size))
                return false;
            if (id == Segment)
                break;
            if (size < 0)
                return false;
            pos += (int)size;
        }

        var   end   = size == UnknownSize ? file.Length : pos + (int)size;
        ulong track = 0;

        while (pos < end)
        {
            if (!TryHeader(file, ref pos, end, out id, out size))
                return false;

            if (id == Cluster)
            {
                // A block before Tracks names a track nobody declared.
                if (track == 0)
                    return false;

                var clusterEnd = size == UnknownSize ? end : pos + (int)size;
                if (!FindFrame(file, ref pos, clusterEnd, size == UnknownSize, track, out frame, out var found))
                    return false;
                if (found)
                    return true;
                continue;
            }

            if (size < 0)
                return false;
            if (id == Tracks && !VideoTrack(file.Slice(pos, (int)size), out track))
                return false;

            pos += (int)size;
        }

        return false;
    }

    private static bool VideoTrack(ReadOnlySpan<byte> tracks, out ulong track)
    {
        track = 0;
        var pos = 0;

        while (pos < tracks.Length)
        {
            if (!TryHeader(tracks, ref pos, tracks.Length, out var id, out var size) || size < 0)
                return false;

            if (id == TrackEntry)
            {
                var entry   = default(WebmInfo);
                var frameNs = 0UL;
                if (!ReadTrackEntry(tracks.Slice(pos, (int)size), ref entry, ref frameNs))
                    return false;
                if (entry.TrackType == 1 && track == 0)
                    track = entry.TrackNumber;
            }

            pos += (int)size;
        }

        return track != 0;
    }

    /// <summary>Walks a cluster for the track's first block; false only on malformed data.</summary>
    private static bool FindFrame(ReadOnlySpan<byte> file, ref int pos, int end, bool unknownSize, ulong track, out WebmFrame frame,
        out bool found)
    {
        frame = default;
        found = false;

        while (pos < end)
        {
            var start = pos;
            if (!TryHeader(file, ref pos, end, out var id, out var size))
                return false;

            if (unknownSize && id is Cluster or Cues or Tags or SeekHead or Info or Tracks or Chapters or Attachments or EbmlHeader)
            {
                pos = start;
                return true;
            }

            if (size < 0)
                return false;

            var at = pos;
            pos += (int)size;

            switch (id)
            {
                case SimpleBlock:
                    if (!BlockFrame(file, at, at + (int)size, track, out var offset, out var length, out found))
                        return false;
                    if (found)
                    {
                        frame = new WebmFrame(offset, length, 0, 0);
                        return true;
                    }
                    break;

                case BlockGroup:
                    if (!GroupFrame(file, at, at + (int)size, track, out frame, out found))
                        return false;
                    if (found)
                        return true;
                    break;
            }
        }

        return true;
    }

    private static bool GroupFrame(ReadOnlySpan<byte> file, int pos, int end, ulong track, out WebmFrame frame, out bool found)
    {
        frame = default;
        found = false;

        int offset = 0, length = 0, alphaOffset = 0, alphaLength = 0;

        while (pos < end)
        {
            if (!TryHeader(file, ref pos, end, out var id, out var size) || size < 0)
                return false;

            if (id == Block && !BlockFrame(file, pos, pos + (int)size, track, out offset, out length, out found))
                return false;
            if (id == BlockAdditions && !AlphaPacket(file, pos, pos + (int)size, out alphaOffset, out alphaLength))
                return false;

            pos += (int)size;
        }

        if (found)
            frame = new WebmFrame(offset, length, alphaOffset, alphaLength);
        return true;
    }

    /// <summary>A block's frame when it is the track's; false on a malformed or laced one.</summary>
    private static bool BlockFrame(ReadOnlySpan<byte> file, int pos, int end, ulong track, out int offset, out int length, out bool found)
    {
        offset = 0;
        length = 0;
        found  = false;

        // Track number, a 16-bit relative timestamp, then the flags.
        if (!TryVint(file, ref pos, end, out var number) || number < 0 || pos + 3 > end)
            return false;
        if ((ulong)number != track)
            return true;
        if ((file[pos + 2] & 0x06) != 0 || pos + 3 == end)
            return false;

        offset = pos + 3;
        length = end - offset;
        found  = true;
        return true;
    }

    /// <summary>The <c>BlockAdditional</c> of <c>BlockAddID</c> 1 (its default), where VP9 alpha is kept.</summary>
    private static bool AlphaPacket(ReadOnlySpan<byte> file, int pos, int end, out int offset, out int length)
    {
        offset = 0;
        length = 0;

        while (pos < end)
        {
            if (!TryHeader(file, ref pos, end, out var id, out var size) || size < 0)
                return false;

            if (id == BlockMore)
            {
                var inner   = pos;
                var moreEnd = pos + (int)size;
                var addId   = 1UL;
                int at      = 0, bytes = 0;

                while (inner < moreEnd)
                {
                    if (!TryHeader(file, ref inner, moreEnd, out var child, out var childSize) || childSize < 0)
                        return false;
                    if (child == BlockAddId && !TryUInt(file.Slice(inner, (int)childSize), out addId))
                        return false;
                    if (child == BlockAdditional)
                        (at, bytes) = (inner, (int)childSize);
                    inner += (int)childSize;
                }

                if (addId == 1 && bytes > 0 && length == 0)
                    (offset, length) = (at, bytes);
            }

            pos += (int)size;
        }

        return true;
    }

    private static bool ReadsDocType(ReadOnlySpan<byte> header, ReadOnlySpan<byte> expected)
    {
        var pos = 0;
        while (pos < header.Length)
        {
            if (!TryHeader(header, ref pos, header.Length, out var id, out var size) || size < 0)
                return false;
            if (id == DocType)
                return header.Slice(pos, (int)size).TrimEnd((byte)0).SequenceEqual(expected);
            pos += (int)size;
        }
        return false;
    }

    private static bool ReadInfo(ReadOnlySpan<byte> info, ref ulong scale, ref double duration)
    {
        var pos = 0;
        while (pos < info.Length)
        {
            if (!TryHeader(info, ref pos, info.Length, out var id, out var size) || size < 0)
                return false;

            var payload = info.Slice(pos, (int)size);

            if (id == TimestampScale && !TryUInt(payload, out scale))
                return false;
            if (id == Duration && (!TryFloat(payload, out duration) || !(duration >= 0) || double.IsInfinity(duration)))
                return false;

            pos += (int)size;
        }
        return true;
    }

    private static bool ReadTracks(ReadOnlySpan<byte> tracks, ref WebmInfo info, ref ulong frameNs)
    {
        var pos = 0;
        while (pos < tracks.Length)
        {
            if (!TryHeader(tracks, ref pos, tracks.Length, out var id, out var size) || size < 0)
                return false;

            if (id == TrackEntry)
            {
                info.TrackCount++;
                if (!ReadTrackEntry(tracks.Slice(pos, (int)size), ref info, ref frameNs))
                    return false;
            }

            pos += (int)size;
        }
        return true;
    }

    private static bool ReadTrackEntry(ReadOnlySpan<byte> entry, ref WebmInfo info, ref ulong frameNs)
    {
        var pos = 0;
        while (pos < entry.Length)
        {
            if (!TryHeader(entry, ref pos, entry.Length, out var id, out var size) || size < 0)
                return false;

            var payload = entry.Slice(pos, (int)size);
            var ok = id switch
            {
                TrackNumber     => TryUInt(payload, out info.TrackNumber),
                TrackType       => TryUInt(payload, out info.TrackType),
                CodecId         => Codec(payload, ref info),
                DefaultDuration => TryUInt(payload, out frameNs),
                Video           => ReadVideo(payload, ref info),
                _               => true
            };

            if (!ok)
                return false;

            pos += (int)size;
        }
        return true;

        static bool Codec(ReadOnlySpan<byte> payload, ref WebmInfo info)
        {
            info.IsVp9 = payload.TrimEnd((byte)0).SequenceEqual("V_VP9"u8);
            return true;
        }
    }

    private static bool ReadVideo(ReadOnlySpan<byte> video, ref WebmInfo info)
    {
        var pos = 0;
        while (pos < video.Length)
        {
            if (!TryHeader(video, ref pos, video.Length, out var id, out var size) || size < 0)
                return false;

            var payload = video.Slice(pos, (int)size);

            if (id is PixelWidth or PixelHeight)
            {
                if (!TryUInt(payload, out var value) || value > int.MaxValue)
                    return false;
                if (id == PixelWidth)
                    info.Width = (int)value;
                else
                    info.Height = (int)value;
            }

            pos += (int)size;
        }
        return true;
    }

    /// <summary>
    /// Walks a cluster's blocks. An unknown-size cluster ends where a top-level element begins.
    /// </summary>
    private static bool ScanCluster(ReadOnlySpan<byte> data, ref int pos, int end, bool unknownSize, ref Blocks blocks)
    {
        var clusterTime = 0L;

        while (pos < end)
        {
            var start = pos;
            if (!TryHeader(data, ref pos, end, out var id, out var size))
                return false;

            if (unknownSize && id is Cluster or Cues or Tags or SeekHead or Info or Tracks or Chapters or Attachments or EbmlHeader)
            {
                pos = start;
                return true;
            }

            if (size < 0)
                return false;

            var payload = data.Slice(pos, (int)size);

            switch (id)
            {
                case ClusterTime:
                    if (!TryUInt(payload, out var time) || time > long.MaxValue / 2)
                        return false;
                    clusterTime = (long)time;
                    break;

                case SimpleBlock:
                    if (!AddBlock(payload, clusterTime, 0, ref blocks))
                        return false;
                    break;

                case BlockGroup:
                    if (!ScanBlockGroup(payload, clusterTime, ref blocks))
                        return false;
                    break;
            }

            pos += (int)size;
        }

        return true;
    }

    private static bool ScanBlockGroup(ReadOnlySpan<byte> group, long clusterTime, ref Blocks blocks)
    {
        var pos      = 0;
        var block    = ReadOnlySpan<byte>.Empty;
        var duration = 0UL;

        while (pos < group.Length)
        {
            if (!TryHeader(group, ref pos, group.Length, out var id, out var size) || size < 0)
                return false;

            var payload = group.Slice(pos, (int)size);

            if (id == Block)
                block = payload;
            else if (id == BlockDuration && !TryUInt(payload, out duration))
                return false;

            pos += (int)size;
        }

        return block.IsEmpty || AddBlock(block, clusterTime, duration, ref blocks);
    }

    private static bool AddBlock(ReadOnlySpan<byte> block, long clusterTime, ulong duration, ref Blocks blocks)
    {
        // Track number (a size-style vint), then a signed 16-bit timestamp relative to the cluster.
        var pos = 0;
        if (!TryVint(block, ref pos, block.Length, out var track) || track < 0 || pos + 3 > block.Length)
            return false;

        var time = clusterTime + BinaryPrimitives.ReadInt16BigEndian(block.Slice(pos, 2));

        if (blocks.Count == 0)
            blocks.Track = (ulong)track;
        else if (blocks.Track != (ulong)track)
            blocks.Mixed = true;

        blocks.Count++;
        blocks.First   = Math.Min(blocks.First, time);
        blocks.Last    = Math.Max(blocks.Last, time);
        blocks.LastEnd = Math.Max(blocks.LastEnd, time + (long)Math.Min(duration, int.MaxValue));
        return true;
    }

    /// <summary>Reads an element ID (marker kept) and size; size is <see cref="UnknownSize"/> for all-ones.</summary>
    private static bool TryHeader(ReadOnlySpan<byte> data, ref int pos, int end, out uint id, out long size)
    {
        id   = 0;
        size = 0;

        if (pos >= end || data[pos] == 0)
            return false;

        var length = BitOperations.LeadingZeroCount((uint)data[pos]) - 23;
        if (length > 4 || pos + length > end)
            return false;

        for (var i = 0; i < length; i++)
            id = (id << 8) | data[pos + i];
        pos += length;

        if (!TryVint(data, ref pos, end, out size))
            return false;

        return size == UnknownSize || size <= end - pos;
    }

    private static bool TryVint(ReadOnlySpan<byte> data, ref int pos, int end, out long value)
    {
        value = 0;

        if (pos >= end || data[pos] == 0)
            return false;

        var length = BitOperations.LeadingZeroCount((uint)data[pos]) - 23;
        if (pos + length > end)
            return false;

        var mask    = 0xFF >> length;
        var bits    = (ulong)(data[pos] & mask);
        var allOnes = bits == (ulong)mask;

        for (var i = 1; i < length; i++)
        {
            bits    =  (bits << 8) | data[pos + i];
            allOnes &= data[pos + i] == 0xFF;
        }

        pos   += length;
        value =  allOnes ? UnknownSize : bits > int.MaxValue ? long.MaxValue : (long)bits;
        return true;
    }

    private static bool TryUInt(ReadOnlySpan<byte> payload, out ulong value)
    {
        value = 0;
        if (payload.Length > 8)
            return false;
        foreach (var b in payload)
            value = (value << 8) | b;
        return true;
    }

    private static bool TryFloat(ReadOnlySpan<byte> payload, out double value)
    {
        switch (payload.Length)
        {
            case 0:
                value = 0;
                return true;
            case 4:
                value = BinaryPrimitives.ReadSingleBigEndian(payload);
                return true;
            case 8:
                value = BinaryPrimitives.ReadDoubleBigEndian(payload);
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
