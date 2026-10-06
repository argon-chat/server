namespace Argon.Grains.Interfaces;

using System.Net.Http.Headers;
using Argon.Features.Expressions;
using Orleans.Concurrency;

/// <summary>
/// Files a bot uploads for later use (Bot API <c>IFiles/Upload</c>), keyed by the bot's user id. An upload
/// starts unreferenced and is collected <see cref="BotFileLimits.Lifetime"/> later: items get their own copy,
/// and a message that attaches one takes a reference to it.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IBotFilesGrain)}")]
public interface IBotFilesGrain : IGrainWithGuidKey
{
    /// <param name="inline">A file sent with the message that uploads it: not counted against <see cref="BotFileLimits.MaxPending"/>.</param>
    [Alias(nameof(UploadAsync)), ResponseTimeout("00:01:00")]
    Task<BotFileUploadResult> UploadAsync(BotFilePurpose purpose, byte[] data, string? fileName = null, string? contentType = null,
        bool inline = false);

    /// <summary>A file the bot owns, or any sticker or emoji file; null for anything else.</summary>
    [Alias(nameof(GetAsync)), AlwaysInterleave]
    Task<BotFileDescription?> GetAsync(Guid fileId);

    /// <summary>A sticker or emoji upload of the bot's that is still usable; null when there is none.</summary>
    [Alias(nameof(ResolveUploadAsync)), AlwaysInterleave]
    Task<BotStoredFile?> ResolveUploadAsync(Guid fileId);

    /// <summary>The upload was used, so it stops counting against <see cref="BotFileLimits.MaxPending"/>.</summary>
    [Alias(nameof(ClaimAsync))]
    Task ClaimAsync(Guid fileId);

    /// <summary>The bot's attachment uploads among <paramref name="fileIds"/> that are still usable; the rest are left out.</summary>
    [Alias(nameof(DescribeAttachmentsAsync)), AlwaysInterleave]
    Task<Dictionary<Guid, BotAttachmentFile>> DescribeAttachmentsAsync(List<Guid> fileIds);

    /// <summary>A message carries these attachments: each takes a reference and stops counting as pending.</summary>
    [Alias(nameof(RetainAttachmentsAsync)), AlwaysInterleave]
    Task RetainAttachmentsAsync(List<Guid> fileIds);
}

[GenerateSerializer]
public enum BotFilePurpose
{
    Sticker    = 0,
    Emoji      = 1,
    Attachment = 2
}

public static class BotFileLimits
{
    /// <summary>The body limit of the expression routes that take files.</summary>
    public const int MaxRequestBytes = 5 * 1024 * 1024;

    /// <summary>The largest file a bot sends to a channel.</summary>
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;

    /// <summary>The body limit of the routes that take an attachment: one at its largest, and the form around it.</summary>
    public const int MaxAttachmentRequestBytes = MaxAttachmentBytes + 256 * 1024;

    /// <summary>Attachments one message carries.</summary>
    public const int MaxAttachmentsPerMessage = 10;

    /// <summary>Unclaimed uploads a bot may hold at once.</summary>
    public const int MaxPending = 100;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static int MaxBytes(BotFilePurpose purpose) => purpose switch
    {
        BotFilePurpose.Attachment => MaxAttachmentBytes,
        BotFilePurpose.Emoji      => ExpressionUploads.MaxUploadBytes(ExpressionKind.Emoji),
        _                         => ExpressionUploads.MaxUploadBytes(ExpressionKind.Sticker)
    };

    /// <summary>The media type the client declared, without parameters; octet-stream when it declared none that parses.</summary>
    public static string AttachmentContentType(string? declared)
    {
        var type = ExpressionUploads.MediaType(declared);
        return type.Contains('/') && MediaTypeHeaderValue.TryParse(type, out _) ? type : ExpressionUploads.OctetStream;
    }

    /// <summary>The last segment of the name, without control characters, at most 255 characters; <c>file</c> when nothing is left.</summary>
    public static string AttachmentFileName(string? name)
    {
        var last  = (name ?? string.Empty).Replace('\\', '/').Split('/')[^1];
        var clean = new string(last.Where(c => !char.IsControl(c)).ToArray()).Trim();

        return clean.Length switch
        {
            0     => "file",
            > 255 => clean[..255],
            _     => clean
        };
    }
}

[GenerateSerializer, Immutable]
public sealed record BotFileDescription(
    [property: Id(0)] Guid    FileId,
    [property: Id(1)] long    Size,
    [property: Id(2)] string  ContentType,
    [property: Id(3)] string  Url,
    [property: Id(4)] string? FileName = null);

/// <summary><see cref="File"/> is set when <see cref="Error"/> is <c>NONE</c>.</summary>
[GenerateSerializer, Immutable]
public sealed record BotFileUploadResult(
    [property: Id(0)] ExpressionError     Error,
    [property: Id(1)] BotFileDescription? File);

[GenerateSerializer, Immutable]
public sealed record BotStoredFile(
    [property: Id(0)] Guid    FileId,
    [property: Id(1)] string  S3Key,
    [property: Id(2)] string? ContentType = null);

/// <summary>
/// What a message needs of an attachment upload. Width and height are set for an image whose header could be read;
/// <see cref="Video"/> for an MP4 that has a media record.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record BotAttachmentFile(
    [property: Id(0)] Guid       FileId,
    [property: Id(1)] string     FileName,
    [property: Id(2)] long       Size,
    [property: Id(3)] string     ContentType,
    [property: Id(4)] int?       Width,
    [property: Id(5)] int?       Height,
    [property: Id(6)] VideoInfo? Video = null);
