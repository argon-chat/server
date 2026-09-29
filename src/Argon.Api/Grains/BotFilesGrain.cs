namespace Argon.Grains;

using Argon.Api.Grains.Interfaces;
using Argon.Entities;
using Argon.Features.Expressions;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using Argon.Services;
using SixLabors.ImageSharp;

/// <inheritdoc cref="IBotFilesGrain"/>
/// <remarks>
/// An upload is stored unreferenced and stamped <see cref="BotFileLimits.Lifetime"/> ahead (see
/// <see cref="IFileStorageGrain.StoreAsync"/>), so <c>FileGcService</c> collects it once that has passed. The pending
/// ones are a sorted set per bot, scored by upload time; an item or a message made from an upload claims it.
/// An image attachment's size is read from its header while the bytes are at hand and kept beside the set.
/// </remarks>
public sealed class BotFilesGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IArgonCacheDatabase counters,
    IS3StorageService s3,
    ILogger<BotFilesGrain> logger) : Grain, IBotFilesGrain
{
    private Guid   BotUserId  => this.GetPrimaryKey();
    private string PendingKey => $"bot:files:pending:{BotUserId}";

    private static string SizeKey(Guid fileId) => $"bot:files:dims:{fileId:N}";

    public async Task<BotFileUploadResult> UploadAsync(BotFilePurpose purpose, byte[] data, string? fileName = null,
        string? contentType = null, bool inline = false)
    {
        if (this.GetUserId() != BotUserId)
            return Failed(ExpressionError.FORBIDDEN);

        var attachment = purpose == BotFilePurpose.Attachment;
        var type       = attachment ? BotFileLimits.AttachmentContentType(contentType) : ExpressionUploads.ContentTypeOf(data ?? []);

        if (data is not { Length: > 0 } || type is null)
            return Failed(ExpressionError.INVALID_FORMAT);

        if (data.Length > BotFileLimits.MaxBytes(purpose))
            return Failed(ExpressionError.TOO_LARGE);

        var now = DateTimeOffset.UtcNow;

        if (!inline && await PendingAsync(now) >= BotFileLimits.MaxPending)
            return Failed(ExpressionError.QUOTA_EXCEEDED);

        var name = attachment ? BotFileLimits.AttachmentFileName(fileName) : null;
        var file = await GrainFactory.GetGrain<IFileStorageGrain>(BotUserId)
           .StoreAsync(new FileUploadRequest(StoredAs(purpose), type, data.Length, FileName: name), data, null, BotFileLimits.Lifetime);

        if (!inline)
            await CountAsync(file.FileId, now);

        if (attachment && type.StartsWith("image/", StringComparison.Ordinal))
            await RememberSizeAsync(file.FileId, data);

        return new BotFileUploadResult(ExpressionError.NONE,
            new BotFileDescription(file.FileId, file.FileSize, file.ContentType ?? type, s3.GetFileDownloadUrl(file.FileId), name));
    }

    public async Task<BotFileDescription?> GetAsync(Guid fileId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var file = await ctx.Files
           .AsNoTracking()
           .Where(f => f.Id == fileId && f.Finalized
                    && (f.OwnerId == BotUserId || f.Purpose == FilePurpose.Sticker || f.Purpose == FilePurpose.Emoji))
           .Select(f => new { f.FileSize, f.ContentType, f.FileName })
           .FirstOrDefaultAsync();

        return file is null
            ? null
            : new BotFileDescription(fileId, file.FileSize, file.ContentType ?? ExpressionUploads.OctetStream, s3.GetFileDownloadUrl(fileId),
                file.FileName);
    }

    public async Task<BotStoredFile?> ResolveUploadAsync(Guid fileId)
    {
        if (this.GetUserId() != BotUserId)
            return null;

        var cutoff = DateTimeOffset.UtcNow - BotFileLimits.Lifetime;

        await using var ctx = await context.CreateDbContextAsync();

        return await (
            from f in ctx.Files.AsNoTracking()
            join b in ctx.Blobs.AsNoTracking() on f.BlobId equals b.Id
            where f.Id == fileId && f.OwnerId == BotUserId && f.Finalized && f.CreatedAt > cutoff
               && (f.Purpose == FilePurpose.Sticker || f.Purpose == FilePurpose.Emoji)
            select new BotStoredFile(f.Id, b.S3Key, f.ContentType)
        ).FirstOrDefaultAsync();
    }

    public async Task ClaimAsync(Guid fileId)
        => await counters.SortedSetRemoveAsync(PendingKey, fileId.ToString("N"));

    public async Task<Dictionary<Guid, BotAttachmentFile>> DescribeAttachmentsAsync(List<Guid> fileIds)
    {
        if (this.GetUserId() != BotUserId || fileIds is not { Count: > 0 })
            return [];

        var cutoff = DateTimeOffset.UtcNow - BotFileLimits.Lifetime;

        await using var ctx = await context.CreateDbContextAsync();

        var files = await ctx.Files
           .AsNoTracking()
           .Where(f => fileIds.Contains(f.Id) && f.OwnerId == BotUserId && f.Finalized && f.CreatedAt > cutoff
                    && f.Purpose == FilePurpose.ChannelAttachment)
           .Select(f => new { f.Id, f.FileName, f.FileSize, f.ContentType })
           .ToListAsync();

        var found = new Dictionary<Guid, BotAttachmentFile>(files.Count);

        foreach (var file in files)
        {
            var type = file.ContentType ?? ExpressionUploads.OctetStream;
            var size = type.StartsWith("image/", StringComparison.Ordinal) ? await SizeOfAsync(file.Id) : null;

            found[file.Id] = new BotAttachmentFile(file.Id, file.FileName ?? "file", file.FileSize, type, size?.Width, size?.Height);
        }

        return found;
    }

    public async Task RetainAttachmentsAsync(List<Guid> fileIds)
    {
        if (this.GetUserId() != BotUserId)
            return;

        var storage = GrainFactory.GetGrain<IFileStorageGrain>(BotUserId);

        foreach (var fileId in fileIds)
        {
            try
            {
                await storage.IncrementRefAsync(fileId);
                await counters.SortedSetRemoveAsync(PendingKey, fileId.ToString("N"));
            }
            catch (Exception e)
            {
                logger.LogError(e, "attachment {FileId} of bot {BotUserId} was not retained; it is collected with the unclaimed uploads",
                    fileId, BotUserId);
            }
        }
    }

    private static FilePurpose StoredAs(BotFilePurpose purpose) => purpose switch
    {
        BotFilePurpose.Attachment => FilePurpose.ChannelAttachment,
        BotFilePurpose.Emoji      => FilePurpose.Emoji,
        _                         => FilePurpose.Sticker
    };

    private async Task CountAsync(Guid fileId, DateTimeOffset now)
    {
        try
        {
            await counters.SortedSetAddAsync(PendingKey, fileId.ToString("N"), now.ToUnixTimeMilliseconds());
            await counters.UpdateStringExpirationAsync(PendingKey, BotFileLimits.Lifetime);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "could not count upload {FileId} of bot {BotUserId}", fileId, BotUserId);
        }
    }

    /// <summary>The header only; a file ImageSharp cannot identify is sent without a size.</summary>
    private async Task RememberSizeAsync(Guid fileId, byte[] data)
    {
        ImageInfo info;
        try
        {
            info = Image.Identify(data);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            await counters.StringSetAsync(SizeKey(fileId), $"{info.Width}x{info.Height}", BotFileLimits.Lifetime);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "could not keep the size of attachment {FileId}", fileId);
        }
    }

    private async Task<(int Width, int Height)?> SizeOfAsync(Guid fileId)
    {
        try
        {
            var parts = (await counters.StringGetAsync(SizeKey(fileId)))?.Split('x');
            return parts is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height) ? (width, height) : null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "size of attachment {FileId} unavailable", fileId);
            return null;
        }
    }

    /// <summary>Unclaimed uploads younger than the lifetime; the older ones are dropped on the way.</summary>
    private async Task<long> PendingAsync(DateTimeOffset now)
    {
        try
        {
            await counters.SortedSetRemoveRangeByScoreAsync(PendingKey, double.NegativeInfinity,
                (now - BotFileLimits.Lifetime).ToUnixTimeMilliseconds());
            return await counters.SortedSetLengthAsync(PendingKey);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "pending uploads of bot {BotUserId} unavailable; letting the upload through", BotUserId);
            return 0;
        }
    }

    private static BotFileUploadResult Failed(ExpressionError error) => new(error, null);
}
