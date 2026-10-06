namespace Argon.Features.Storage;

using ion.runtime;

/// <summary>Shared rules of video files: the stored type, the declaration on a ticket, and the message entity.</summary>
public static class VideoMedia
{
    public const string ContentType  = "video/mp4";
    public const string CacheControl = "public, max-age=31536000, immutable";

    /// <summary>The declaration as the ticket keeps it; the hash already rides on the ticket.</summary>
    public static string Serialize(VideoUploadDeclaration declaration)
        => JsonConvert.SerializeObject(declaration with { sha256 = null });

    public static VideoUploadDeclaration? Deserialize(string? json)
        => json is null ? null : JsonConvert.DeserializeObject<VideoUploadDeclaration>(json);

    /// <summary>The sample entry part of an RFC 6381 codec string: <c>avc1</c> of <c>avc1.64001f</c>.</summary>
    public static ReadOnlySpan<char> CodecFamily(string codec)
    {
        var dot = codec.IndexOf('.');
        return dot < 0 ? codec : codec.AsSpan(0, dot);
    }

    /// <summary>
    /// H.264, the one video codec accepted: it decodes everywhere a client runs. Any other is refused
    /// with <c>CONTENT_TYPE_REJECTED</c>, whatever the declaration said.
    /// </summary>
    public static bool IsH264(string? codec)
        => codec is not null
        && (CodecFamily(codec).Equals("avc1", StringComparison.OrdinalIgnoreCase)
         || CodecFamily(codec).Equals("avc3", StringComparison.OrdinalIgnoreCase));

    /// <summary>A header that may become a media record without a declaration to check it against (a bot's upload).</summary>
    public static bool IsPlayable(Mp4ProbeResult read, int maxDurationMs)
        => read.FastStart && IsH264(read.VideoCodec)
        && read.Width is >= 2 and <= 8192 && read.Height is >= 2 and <= 8192
        && read.DurationMs > 0 && read.DurationMs <= maxDurationMs;

    /// <summary>What was read off the header, and what only the uploader could say: thumbhash, poster, storyboard, preload.</summary>
    public static FileMediaEntity Record(Guid fileId, Mp4ProbeResult read, VideoUploadDeclaration? declared, long size)
    {
        var now = DateTimeOffset.UtcNow;

        return new FileMediaEntity
        {
            FileId                = fileId,
            Width                 = read.Width,
            Height                = read.Height,
            DurationMs            = read.DurationMs,
            HasAudio              = read.HasAudio,
            VideoCodec            = Truncate(read.VideoCodec),
            AudioCodec            = Truncate(read.AudioCodec),
            PosterFileId          = declared?.posterFileId,
            StoryboardFileId      = declared?.storyboardFileId,
            StoryboardFrameWidth  = declared?.storyboard?.frameWidth,
            StoryboardFrameHeight = declared?.storyboard?.frameHeight,
            StoryboardColumns     = declared?.storyboard?.columns,
            StoryboardFrameCount  = declared?.storyboard?.frameCount,
            StoryboardIntervalMs  = declared?.storyboard?.intervalMs,
            ThumbHash             = declared?.thumbHash,
            PreloadPrefixSize     = declared?.preloadPrefixSize is { } prefix && prefix > 0 ? Math.Min(prefix, size) : null,
            CreatedAt             = now,
            UpdatedAt             = now
        };

        static string? Truncate(string? codec) => codec is { Length: > 64 } ? codec[..64] : codec;
    }

    public static VideoStoryboard? Storyboard(FileMediaEntity media)
        => media is
        {
            StoryboardFrameWidth: { } w, StoryboardFrameHeight: { } h, StoryboardColumns: { } columns,
            StoryboardFrameCount: { } count, StoryboardIntervalMs: { } interval
        }
            ? new VideoStoryboard(w, h, columns, count, interval)
            : null;

    public static VideoInfo ToInfo(FileEntity file, FileMediaEntity media, IS3StorageService s3)
        => new(file.Id, file.FileName ?? "", file.FileSize, file.ContentType ?? ContentType,
            media.Width, media.Height, media.DurationMs, media.HasAudio, media.VideoCodec, media.ThumbHash,
            media.PosterFileId, media.StoryboardFileId, Storyboard(media), media.PreloadPrefixSize,
            s3.GetFileDownloadUrl(file.Id),
            media.PosterFileId is { } poster ? s3.GetFileDownloadUrl(poster) : null,
            media.StoryboardFileId is { } board ? s3.GetFileDownloadUrl(board) : null);

    /// <summary>The entity as it is stored: every field from the file's media record, no URLs, no variants.</summary>
    public static MessageEntityVideo FromRecord(MessageEntityVideo entity, VideoInfo info)
        => entity with
        {
            fileName          = info.fileName,
            fileSize          = info.fileSize,
            contentType       = info.contentType,
            width             = info.width,
            height            = info.height,
            durationMs        = info.durationMs,
            hasAudio          = info.hasAudio,
            codec             = info.codec,
            thumbHash         = info.thumbHash,
            posterFileId      = info.posterFileId,
            storyboardFileId  = info.storyboardFileId,
            storyboard        = info.storyboard,
            preloadPrefixSize = info.preloadPrefixSize,
            variants          = IonArray<VideoVariant>.Empty,
            downloadUrl       = null,
            posterUrl         = null,
            storyboardUrl     = null
        };

    public static MessageEntityVideo WithoutUrls(MessageEntityVideo entity)
        => entity with { downloadUrl = null, posterUrl = null, storyboardUrl = null };

    public static MessageEntityVideo WithUrls(MessageEntityVideo entity, IS3StorageService s3)
        => entity with
        {
            downloadUrl   = s3.GetFileDownloadUrl(entity.fileId),
            posterUrl     = entity.posterFileId is { } poster ? s3.GetFileDownloadUrl(poster) : null,
            storyboardUrl = entity.storyboardFileId is { } board ? s3.GetFileDownloadUrl(board) : null
        };

    /// <summary>A minimal video entity a bot message or a test can start from; <see cref="FromRecord"/> fills it.</summary>
    public static MessageEntityVideo Placeholder(Guid fileId)
        => new(EntityType.Video, 0, 0, 1, fileId, "", 0, ContentType, 0, 0, 0, false, null, null, null, null, null, null,
            IonArray<VideoVariant>.Empty, null, null, null);
}
