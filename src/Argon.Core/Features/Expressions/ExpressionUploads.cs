namespace Argon.Features.Expressions;

using Argon.Features.Storage;

/// <summary>What an expression upload may be, before and after the bytes are looked at.</summary>
public static class ExpressionUploads
{
    public const string Gzip        = "application/gzip";
    public const string OctetStream = "application/octet-stream";

    /// <summary>An expression file never changes once its item exists.</summary>
    public const string CacheControl = "public, max-age=31536000, immutable";

    private static readonly ExpressionFormat[] Formats = [ExpressionFormat.Static, ExpressionFormat.Lottie, ExpressionFormat.Video];

    /// <summary>Every content type an emoji or sticker file may be stored as.</summary>
    public static readonly IReadOnlySet<string> ContentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        ExpressionContentTypes.Png,
        ExpressionContentTypes.Webp,
        ExpressionContentTypes.Webm,
        ExpressionContentTypes.Tgs,
        ExpressionContentTypes.Json,
        Gzip,
        OctetStream
    };

    public static FilePurpose PurposeFor(ExpressionKind kind)
        => kind == ExpressionKind.Emoji ? FilePurpose.Emoji : FilePurpose.Sticker;

    /// <summary>The media type without parameters, lower-cased.</summary>
    public static string MediaType(string? contentType)
        => (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

    public static bool IsExpressionContentType(string? contentType)
        => ContentTypes.Contains(MediaType(contentType));

    public static bool Accepts(ExpressionFormat format, string? contentType)
        => (format, MediaType(contentType)) switch
        {
            (ExpressionFormat.Static, ExpressionContentTypes.Png or ExpressionContentTypes.Webp)                     => true,
            (ExpressionFormat.Lottie, ExpressionContentTypes.Tgs or ExpressionContentTypes.Json or Gzip or OctetStream) => true,
            (ExpressionFormat.Video, ExpressionContentTypes.Webm)                                                    => true,
            _                                                                                                          => false
        };

    /// <summary>The largest upload for a format: a plain-JSON Lottie is judged by its gzipped size, so it may arrive larger.</summary>
    public static int MaxUploadBytes(ExpressionKind kind, ExpressionFormat format)
        => format == ExpressionFormat.Lottie ? ExpressionLimits.LottieMaxJsonBytes : ExpressionLimits.MaxBytes(kind, format);

    /// <summary>The largest upload of any file of this kind, thumbnails included.</summary>
    public static int MaxUploadBytes(ExpressionKind kind)
        => Math.Max(ExpressionLimits.ThumbMaxBytes, Formats.Max(f => MaxUploadBytes(kind, f)));

    /// <summary>The format the bytes are in, or null when they are none of the three.</summary>
    public static ExpressionFormat? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
         || (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)))
            return ExpressionFormat.Static;

        if (data.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
            return ExpressionFormat.Lottie;

        if (data.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
            return ExpressionFormat.Video;

        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            data = data[3..];

        var json = data.TrimStart(" \t\r\n"u8);
        return json.Length > 0 && json[0] == (byte)'{' ? ExpressionFormat.Lottie : null;
    }

    /// <summary>A client's outline if it is one — at most 1 KiB and decodable — and none otherwise.</summary>
    public static byte[]? AcceptOutline(byte[]? outline)
    {
        if (outline is not { Length: > 0 and <= ExpressionLimits.OutlineMaxBytes })
            return null;

        try
        {
            OutlineCodec.Decode(outline);
            return outline;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
