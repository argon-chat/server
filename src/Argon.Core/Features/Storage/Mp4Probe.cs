namespace Argon.Features.Storage;

using System.Buffers.Binary;

public enum Mp4ProbeError { None, NotMp4, Malformed, MoovNotInHead, NoVideoTrack }

public readonly record struct Mp4ProbeResult(
    int Width, int Height, int DurationMs, int Rotation,
    string? VideoCodec, string? AudioCodec, bool HasAudio, bool FastStart, long MoovEnd);

/// <summary>
/// Reads an MP4/MOV from its first bytes: where <c>moov</c> lies, the displayed video size and rotation, the length,
/// and RFC 6381 codec strings. Only moov/trak/mdia/minf/stbl/stsd is walked, so nesting is bounded by construction;
/// nothing is allocated but the codec strings.
/// </summary>
public static class Mp4Probe
{
    private const int MaxBoxes = 1024;

    private const uint Ftyp = 0x66747970;
    private const uint Moov = 0x6D6F6F76;
    private const uint Mdat = 0x6D646174;
    private const uint Mvhd = 0x6D766864;
    private const uint Trak = 0x7472616B;
    private const uint Tkhd = 0x746B6864;
    private const uint Mdia = 0x6D646961;
    private const uint Mdhd = 0x6D646864;
    private const uint Hdlr = 0x68646C72;
    private const uint Minf = 0x6D696E66;
    private const uint Stbl = 0x7374626C;
    private const uint Stsd = 0x73747364;
    private const uint Vide = 0x76696465;
    private const uint Soun = 0x736F756E;
    private const uint Avc1 = 0x61766331;
    private const uint Avc3 = 0x61766333;
    private const uint AvcC = 0x61766343;
    private const uint Hvc1 = 0x68766331;
    private const uint Hev1 = 0x68657631;
    private const uint HvcC = 0x68766343;
    private const uint Av01 = 0x61763031;
    private const uint Av1C = 0x61763143;
    private const uint Vp09 = 0x76703039;
    private const uint VpcC = 0x76706343;
    private const uint Mp4a = 0x6D703461;
    private const uint Esds = 0x65736473;
    private const uint Wave = 0x77617665;
    private const uint Opus = 0x4F707573;
    private const uint FLaC = 0x664C6143;
    private const uint Ac3  = 0x61632D33;
    private const uint Ec3  = 0x65632D33;

    private ref struct Track
    {
        public uint               Handler;
        public int                Width;
        public int                Height;
        public int                Rotation;
        public int                DurationMs;
        public uint               EntryType;
        public ReadOnlySpan<byte> Entry;
    }

    public static bool TryProbe(ReadOnlySpan<byte> head, long totalSize, out Mp4ProbeResult result, out Mp4ProbeError error)
    {
        result = default;
        if (totalSize < head.Length)
            head = head[..(int)Math.Max(totalSize, 0)];

        if (head.Length < 8 || U32(head, 4) != Ftyp)
            return Fail(Mp4ProbeError.NotMp4, out error);

        var  parsed    = default(Mp4ProbeResult);
        long pos       = 0;
        long moovEnd   = -1;
        var  mdatFirst = false;
        var  stop      = Mp4ProbeError.Malformed;

        // A tail shorter than a header is padding. Past moov the walk goes on only while it can, to catch an overrun.
        for (var count = 0; totalSize - pos >= 8; count++)
        {
            if (count == MaxBoxes)
                break;
            if (head.Length - pos < 8)
            {
                stop = Mp4ProbeError.MoovNotInHead;
                break;
            }

            var   at     = (int)pos;
            ulong size   = U32(head, at);
            var   type   = U32(head, at + 4);
            var   header = 8;

            if (size == 1)
            {
                if (totalSize - pos < 16)
                    return Fail(Mp4ProbeError.Malformed, out error);
                if (head.Length - pos < 16)
                {
                    stop = Mp4ProbeError.MoovNotInHead;
                    break;
                }

                size   = U64(head, at + 8);
                header = 16;
            }
            else if (size == 0)
                size = (ulong)(totalSize - pos);

            if (size < (ulong)header || size > (ulong)(totalSize - pos))
                return Fail(Mp4ProbeError.Malformed, out error);

            if (type == Moov && moovEnd < 0)
            {
                var end = pos + (long)size;
                if (end > head.Length)
                    return Fail(Mp4ProbeError.MoovNotInHead, out error);

                var moovError = ReadMoov(head[(at + header)..(int)end], out parsed);
                if (moovError != Mp4ProbeError.None)
                    return Fail(moovError, out error);

                moovEnd = end;
            }
            else if (type == Mdat && moovEnd < 0)
                mdatFirst = true;

            pos += (long)size;
        }

        if (moovEnd < 0)
            return Fail(stop, out error);

        result = parsed with { FastStart = !mdatFirst, MoovEnd = moovEnd };
        error  = Mp4ProbeError.None;
        return true;
    }

    /// <summary>
    /// Where <c>moov</c> ends, from the top-level box headers in <paramref name="head"/> alone; false when
    /// an <c>mdat</c> comes first, the file is not an MP4, or no <c>moov</c> header lies within the head.
    /// </summary>
    public static bool TryLocateMoov(ReadOnlySpan<byte> head, long totalSize, out long moovEnd)
    {
        moovEnd = -1;
        if (totalSize < head.Length)
            head = head[..(int)Math.Max(totalSize, 0)];

        if (head.Length < 8 || U32(head, 4) != Ftyp)
            return false;

        long pos = 0;
        for (var count = 0; count < MaxBoxes && head.Length - pos >= 8; count++)
        {
            var   at     = (int)pos;
            ulong size   = U32(head, at);
            var   type   = U32(head, at + 4);
            var   header = 8;

            if (size == 1)
            {
                if (head.Length - pos < 16)
                    return false;
                size   = U64(head, at + 8);
                header = 16;
            }
            else if (size == 0)
                size = (ulong)(totalSize - pos);

            if (size < (ulong)header || size > (ulong)(totalSize - pos) || type == Mdat)
                return false;

            if (type == Moov)
            {
                moovEnd = pos + (long)size;
                return true;
            }

            pos += (long)size;
        }

        return false;
    }

    private static Mp4ProbeError ReadMoov(ReadOnlySpan<byte> moov, out Mp4ProbeResult result)
    {
        result = default;

        var   boxes     = new BoxReader(moov);
        var   video     = default(Track);
        var   audio     = default(Track);
        var   haveMvhd  = false;
        uint  timescale = 0;
        ulong duration  = 0;
        var   longest   = 0;

        while (boxes.Next())
        {
            if (boxes.Type == Mvhd && !haveMvhd)
            {
                if (!ReadTimes(boxes.Payload, out timescale, out duration) || timescale == 0)
                    return Mp4ProbeError.Malformed;
                haveMvhd = true;
            }
            else if (boxes.Type == Trak)
            {
                var track = default(Track);
                if (!ReadTrak(boxes.Payload, ref track))
                    return Mp4ProbeError.Malformed;

                longest = Math.Max(longest, track.DurationMs);
                if (track.Handler == Vide && video.Handler == 0)
                    video = track;
                else if (track.Handler == Soun && audio.Handler == 0)
                    audio = track;
            }
        }

        if (boxes.Malformed || !haveMvhd)
            return Mp4ProbeError.Malformed;
        if (video.Handler == 0)
            return Mp4ProbeError.NoVideoTrack;

        int width = video.Width, height = video.Height;
        if ((width == 0 || height == 0) && video.Entry.Length >= 28)
        {
            width  = U16(video.Entry, 24);
            height = U16(video.Entry, 26);
        }
        if (video.Rotation is 90 or 270)
            (width, height) = (height, width);

        var hasAudio = audio.Handler == Soun;
        result = new Mp4ProbeResult(width, height, duration > 0 ? Ms(duration, timescale) : longest, video.Rotation,
            VideoCodec(video.EntryType, video.Entry), hasAudio ? AudioCodec(audio.EntryType, audio.Entry) : null, hasAudio,
            FastStart: false, MoovEnd: 0);
        return Mp4ProbeError.None;
    }

    private static bool ReadTrak(ReadOnlySpan<byte> trak, ref Track track)
    {
        var boxes = new BoxReader(trak);
        while (boxes.Next())
        {
            var ok = boxes.Type switch
            {
                Tkhd => ReadTkhd(boxes.Payload, ref track),
                Mdia => ReadMdia(boxes.Payload, ref track),
                _    => true
            };
            if (!ok)
                return false;
        }
        return !boxes.Malformed;
    }

    private static bool ReadTkhd(ReadOnlySpan<byte> tkhd, ref Track track)
    {
        if (tkhd.Length < 4 || tkhd[0] > 1)
            return false;

        // The matrix follows the times and 16 reserved/layer/volume bytes; width and height follow it.
        var matrix = tkhd[0] == 1 ? 52 : 40;
        if (tkhd.Length < matrix + 44)
            return false;

        track.Rotation = Rotation(I32(tkhd, matrix), I32(tkhd, matrix + 4));
        track.Width    = (int)(U32(tkhd, matrix + 36) >> 16);
        track.Height   = (int)(U32(tkhd, matrix + 40) >> 16);
        return true;
    }

    private static bool ReadMdia(ReadOnlySpan<byte> mdia, ref Track track)
    {
        var boxes = new BoxReader(mdia);
        while (boxes.Next())
        {
            switch (boxes.Type)
            {
                case Mdhd:
                    if (ReadTimes(boxes.Payload, out var timescale, out var duration) && timescale > 0 && duration > 0)
                        track.DurationMs = Ms(duration, timescale);
                    break;

                case Hdlr:
                    if (boxes.Payload.Length < 12)
                        return false;
                    track.Handler = U32(boxes.Payload, 8);
                    break;

                case Minf:
                {
                    var stsd = Child(Child(boxes.Payload, Stbl), Stsd);
                    if (stsd.Length < 8 || U32(stsd, 4) == 0)
                        break;

                    var entries = new BoxReader(stsd[8..]);
                    if (entries.Next())
                    {
                        track.EntryType = entries.Type;
                        track.Entry     = entries.Payload;
                    }
                    break;
                }
            }
        }
        return !boxes.Malformed;
    }

    /// <summary>Timescale and duration of an mvhd/mdhd; an all-ones (unknown) duration reads as 0.</summary>
    private static bool ReadTimes(ReadOnlySpan<byte> box, out uint timescale, out ulong duration)
    {
        timescale = 0;
        duration  = 0;

        if (box.Length >= 20 && box[0] == 0)
        {
            timescale = U32(box, 12);
            var value = U32(box, 16);
            duration = value == uint.MaxValue ? 0 : value;
            return true;
        }

        if (box.Length >= 32 && box[0] == 1)
        {
            timescale = U32(box, 20);
            var value = U64(box, 24);
            duration = value == ulong.MaxValue ? 0 : value;
            return true;
        }

        return false;
    }

    private static int Ms(ulong duration, uint timescale)
    {
        var ms = (UInt128)duration * 1000 / timescale;
        return ms > int.MaxValue ? int.MaxValue : (int)ms;
    }

    /// <summary>Quarter turns from the matrix's first row (16.16), snapped to the nearest one.</summary>
    private static int Rotation(int a, int b)
    {
        long absA = Math.Abs((long)a), absB = Math.Abs((long)b);
        if (absA > absB)
            return a > 0 ? 0 : 180;
        if (absB > absA)
            return b > 0 ? 90 : 270;
        return 0;
    }

    private static string? VideoCodec(uint type, ReadOnlySpan<byte> entry)
    {
        // VisualSampleEntry children start after its 78 fixed bytes.
        var children = entry.Length >= 78 ? entry[78..] : default;

        switch (type)
        {
            case Avc1 or Avc3:
            {
                var prefix = type == Avc1 ? "avc1" : "avc3";
                var c      = Child(children, AvcC);
                return c.Length >= 4 ? $"{prefix}.{c[1]:x2}{c[2]:x2}{c[3]:x2}" : prefix;
            }

            case Hvc1 or Hev1:
            {
                var prefix = type == Hvc1 ? "hvc1" : "hev1";
                var c      = Child(children, HvcC);
                return c.Length >= 13 ? Hevc(prefix, c) : prefix;
            }

            case Av01:
            {
                var c = Child(children, Av1C);
                if (c.Length < 3)
                    return "av01";

                var depth = (c[2] & 0x20) != 0 ? 12 : (c[2] & 0x40) != 0 ? 10 : 8;
                return $"av01.{c[1] >> 5}.{c[1] & 0x1F:D2}{((c[2] & 0x80) != 0 ? 'H' : 'M')}.{depth:D2}";
            }

            case Vp09:
            {
                var c = Child(children, VpcC);
                return c.Length >= 7 ? $"vp09.{c[4]:D2}.{c[5]:D2}.{c[6] >> 4:D2}" : "vp09";
            }

            default:
                return Printable(type);
        }
    }

    /// <summary>ISO 14496-15 E.3: profile space and idc, reversed compatibility flags, tier and level, then the constraint bytes.</summary>
    private static string Hevc(string prefix, ReadOnlySpan<byte> c)
    {
        var space = (c[1] >> 6) switch { 1 => "A", 2 => "B", 3 => "C", _ => "" };
        var tier  = (c[1] & 0x20) != 0 ? 'H' : 'L';

        var flags    = U32(c, 2);
        var reversed = 0u;
        for (var i = 0; i < 32; i++, flags >>= 1)
            reversed = (reversed << 1) | (flags & 1);

        Span<char> text = stackalloc char[64];
        text.TryWrite($"{prefix}.{space}{c[1] & 0x1F}.{reversed:X}.{tier}{c[12]}", out var length);

        var last = 11;
        while (last >= 6 && c[last] == 0)
            last--;

        for (var i = 6; i <= last; i++)
        {
            text[length..].TryWrite($".{c[i]:X}", out var written);
            length += written;
        }

        return new string(text[..length]);
    }

    private static string? AudioCodec(uint type, ReadOnlySpan<byte> entry)
    {
        switch (type)
        {
            case Mp4a:
            {
                // Children follow the 28-byte AudioSampleEntry, longer in QuickTime sound versions 1 and 2.
                var skip     = entry.Length < 10 ? 28 : U16(entry, 8) switch { 1 => 44, 2 => 64, _ => 28 };
                var children = entry.Length >= skip ? entry[skip..] : default;
                var esds     = Child(children, Esds);
                if (esds.IsEmpty)
                    esds = Child(Child(children, Wave), Esds);
                return Mp4aCodec(esds);
            }

            case Opus: return "opus";
            case FLaC: return "flac";
            case Ac3:  return "ac-3";
            case Ec3:  return "ec-3";
            default:   return Printable(type);
        }
    }

    /// <summary>ES_Descriptor → DecoderConfigDescriptor → DecoderSpecificInfo; as much of mp4a.40.{aot} as can be read.</summary>
    private static string Mp4aCodec(ReadOnlySpan<byte> esds)
    {
        if (esds.Length < 4 || !Descriptor(esds[4..], 0x03, out var es) || es.Length < 3)
            return "mp4a";

        // ES_ID, then flags for an optional dependency, URL and OCR stream.
        var flags = es[2];
        var pos   = 3;
        if ((flags & 0x80) != 0)
            pos += 2;
        if ((flags & 0x40) != 0)
        {
            if (pos >= es.Length)
                return "mp4a";
            pos += 1 + es[pos];
        }
        if ((flags & 0x20) != 0)
            pos += 2;

        if (pos > es.Length || !Descriptor(es[pos..], 0x04, out var config) || config.Length < 1)
            return "mp4a";

        var oti = config[0];
        if (oti != 0x40)
            return $"mp4a.{oti:x2}";

        if (config.Length < 13 || !Descriptor(config[13..], 0x05, out var info) || info.Length < 1)
            return "mp4a";

        var aot = info[0] >> 3;
        if (aot == 31)
        {
            if (info.Length < 2)
                return "mp4a";
            aot = 32 + (((info[0] & 0x07) << 3) | (info[1] >> 5));
        }

        return $"mp4a.40.{aot}";
    }

    /// <summary>The first MPEG-4 descriptor with the tag; lengths are 1–4 bytes of 7 bits each.</summary>
    private static bool Descriptor(ReadOnlySpan<byte> data, byte tag, out ReadOnlySpan<byte> body)
    {
        body = default;
        var pos = 0;

        for (var count = 0; count < MaxBoxes && pos < data.Length; count++)
        {
            var current = data[pos++];
            var length  = 0;
            var bytes   = 0;
            byte next;

            do
            {
                if (bytes++ == 4 || pos >= data.Length)
                    return false;
                next   = data[pos++];
                length = (length << 7) | (next & 0x7F);
            } while ((next & 0x80) != 0);

            if (length > data.Length - pos)
                return false;

            if (current == tag)
            {
                body = data.Slice(pos, length);
                return true;
            }

            pos += length;
        }

        return false;
    }

    /// <summary>The payload of the first child of the type; empty when absent or unreadable.</summary>
    private static ReadOnlySpan<byte> Child(ReadOnlySpan<byte> container, uint type)
    {
        var boxes = new BoxReader(container);
        while (boxes.Next())
            if (boxes.Type == type)
                return boxes.Payload;
        return default;
    }

    private static string? Printable(uint type)
    {
        for (var shift = 0; shift < 32; shift += 8)
            if ((byte)(type >> shift) is < 0x20 or > 0x7E)
                return null;

        return string.Create(4, type, static (chars, value) =>
        {
            for (var i = 0; i < 4; i++)
                chars[i] = (char)(byte)(value >> (24 - 8 * i));
        });
    }

    private static bool Fail(Mp4ProbeError reason, out Mp4ProbeError error)
    {
        error = reason;
        return false;
    }

    private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    private static int I32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt32BigEndian(data[at..]);

    private static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64BigEndian(data[at..]);

    /// <summary>Child boxes of a container, each bounded by it. A tail too short for a header ends it (QuickTime pads with zeros).</summary>
    private ref struct BoxReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private          int                pos;
        private          int                count;

        public uint               Type;
        public ReadOnlySpan<byte> Payload;
        public bool               Malformed;

        public bool Next()
        {
            if (data.Length - pos < 8)
                return false;
            if (++count > MaxBoxes)
                return Stop();

            ulong size   = U32(data, pos);
            var   header = 8;

            if (size == 1)
            {
                if (data.Length - pos < 16)
                    return Stop();
                size   = U64(data, pos + 8);
                header = 16;
            }
            else if (size == 0)
                size = (ulong)(data.Length - pos);

            if (size < (ulong)header || size > (ulong)(data.Length - pos))
                return Stop();

            Type    =  U32(data, pos + 4);
            Payload =  data.Slice(pos + header, (int)size - header);
            pos     += (int)size;
            return true;
        }

        private bool Stop()
        {
            Malformed = true;
            return false;
        }
    }
}
