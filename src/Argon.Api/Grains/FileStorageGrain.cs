namespace Argon.Api.Grains;

using System.Diagnostics;
using Argon.Api.Grains.Interfaces;
using Argon.Entities;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;

[StatelessWorker]
public class FileStorageGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    S3PostPolicyGenerator policyGenerator,
    IS3StorageService s3,
    IReferenceCountService refCount,
    IOptions<FileLimitsOptions> limitsOptions,
    ILogger<FileStorageGrain> logger) : Grain, IFileStorageGrain
{
    private readonly FileLimitsOptions _limits = limitsOptions.Value;

    public async Task<FileUploadResponse> RequestUploadAsync(FileUploadRequest request, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.RequestUpload");
        activity?.SetTag("file.purpose", request.Purpose.ToString());
        activity?.SetTag("file.size", request.FileSize);

        var userId = this.GetPrimaryKey();
        activity?.SetTag("user.id", userId.ToString());

        var effectiveLimit = await ResolveEffectiveSizeLimit(userId, request.Purpose, request.SpaceId, ct);
        activity?.SetTag("file.size_limit", effectiveLimit);

        if (request.FileSize > 0 && request.FileSize > effectiveLimit)
        {
            StorageInstruments.UploadsFailed.Add(1,
                new KeyValuePair<string, object?>("purpose", request.Purpose.ToString()),
                new KeyValuePair<string, object?>("reason", "size_exceeded"));
            throw new InvalidOperationException($"File size {request.FileSize} exceeds limit {effectiveLimit} for purpose {request.Purpose}");
        }

        var fileId = Guid.NewGuid();
        var s3Key  = BuildS3Key(request.Purpose, fileId, userId, request.SpaceId, request.ChannelId);
        var opts   = policyGenerator;

        var contentTypePrefix = GetContentTypePrefix(request.Purpose);

        var postData = policyGenerator.GeneratePresignedPost(
            s3Key,
            effectiveLimit,
            contentTypePrefix,
            _limits.BlobTtlSeconds);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var file = new FileEntity
        {
            Id          = fileId,
            OwnerId     = userId,
            Purpose     = request.Purpose,
            S3Key       = s3Key,
            BucketName  = postData.Url, // store full bucket URL for reference
            FileSize    = 0,
            ContentType = request.ContentType,
            FileName    = null,
            Finalized   = false,
            SpaceId     = request.SpaceId,
            ChannelId   = request.ChannelId,
            CreatedAt   = DateTimeOffset.UtcNow,
            UpdatedAt   = DateTimeOffset.UtcNow
        };

        var blob = new FileBlobEntity
        {
            Id        = Guid.NewGuid(),
            FileId    = fileId,
            OwnerId   = userId,
            Purpose   = request.Purpose,
            SizeLimit = effectiveLimit,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(_limits.BlobTtlSeconds),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Files.Add(file);
        db.FileBlobs.Add(blob);
        await db.SaveChangesAsync(ct);

        StorageInstruments.UploadsRequested.Add(1, new KeyValuePair<string, object?>("purpose", request.Purpose.ToString()));
        StorageInstruments.ActiveBlobs.Add(1);
        activity?.SetTag("file.id", fileId.ToString());
        activity?.SetTag("blob.id", blob.Id.ToString());

        logger.LogInformation("Upload requested: fileId={FileId}, purpose={Purpose}, sizeLimit={Limit}, userId={UserId}",
            fileId, request.Purpose, effectiveLimit, userId);

        return new FileUploadResponse(blob.Id, fileId, postData.Url, postData.Fields, _limits.BlobTtlSeconds);
    }

    public async Task<FileInfoResponse> FinalizeUploadAsync(Guid blobId, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.FinalizeUpload");
        var sw = Stopwatch.StartNew();
        activity?.SetTag("blob.id", blobId.ToString());

        var userId = this.GetPrimaryKey();
        activity?.SetTag("user.id", userId.ToString());

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var blob = await db.FileBlobs.FirstOrDefaultAsync(x => x.Id == blobId && x.OwnerId == userId, ct);
        if (blob is null)
            throw new KeyNotFoundException("Upload blob not found");

        if (blob.ExpiresAt < DateTimeOffset.UtcNow)
        {
            // Expired — clean up
            var expiredFile = await db.Files.FindAsync([blob.FileId], ct);
            if (expiredFile is not null)
            {
                await s3.DeleteFileAsync(expiredFile.S3Key, ct);
                db.Files.Remove(expiredFile);
            }
            db.FileBlobs.Remove(blob);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Upload blob has expired");
        }

        var file = await db.Files.FindAsync([blob.FileId], ct);
        if (file is null)
            throw new KeyNotFoundException("File record not found");

        // Validate file exists in S3 via HEAD
        var metadata = await s3.HeadFileAsync(file.S3Key, ct);
        if (metadata is null)
            throw new InvalidOperationException("File not found in storage — upload may have failed");

        // Validate size
        if (metadata.ContentLength > blob.SizeLimit)
        {
            await s3.DeleteFileAsync(file.S3Key, ct);
            db.Files.Remove(file);
            db.FileBlobs.Remove(blob);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Uploaded file size {metadata.ContentLength} exceeds limit {blob.SizeLimit}");
        }

        // Update file metadata
        file.FileSize    = metadata.ContentLength;
        file.ContentType = metadata.ContentType ?? file.ContentType;
        file.Checksum    = metadata.ETag;
        file.Finalized   = true;
        file.UpdatedAt   = DateTimeOffset.UtcNow;

        // Create ref counter
        var counter = new FileCounterEntity
        {
            Id        = file.Id,
            RefCount  = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.FileCounters.Add(counter);
        db.FileBlobs.Remove(blob);
        await db.SaveChangesAsync(ct);

        sw.Stop();
        StorageInstruments.UploadsFinalized.Add(1, new KeyValuePair<string, object?>("purpose", file.Purpose.ToString()));
        StorageInstruments.UploadSizeBytes.Record(file.FileSize, new KeyValuePair<string, object?>("purpose", file.Purpose.ToString()));
        StorageInstruments.UploadFinalizeDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("purpose", file.Purpose.ToString()));
        StorageInstruments.TotalStoredBytes.Add(file.FileSize, new KeyValuePair<string, object?>("purpose", file.Purpose.ToString()));
        StorageInstruments.ActiveBlobs.Add(-1);

        activity?.SetTag("file.id", file.Id.ToString());
        activity?.SetTag("file.size", file.FileSize);

        logger.LogInformation("Upload finalized: fileId={FileId}, purpose={Purpose}, size={Size}, userId={UserId}, elapsed={ElapsedMs}ms",
            file.Id, file.Purpose, file.FileSize, userId, sw.Elapsed.TotalMilliseconds);

        var downloadUrl = file.Purpose.IsPublic()
            ? s3.GetPublicUrl(file.S3Key)
            : s3.GeneratePresignedGetUrl(file.S3Key);

        return new FileInfoResponse(
            file.Id, file.FileName, file.FileSize, file.ContentType,
            file.Purpose, downloadUrl, file.Purpose.IsPublic());
    }

    public async Task IncrementRefAsync(Guid fileId, CancellationToken ct = default)
    {
        await refCount.IncrementAsync(fileId, 1, ct);
        StorageInstruments.RefIncrements.Add(1);
    }

    public async Task DecrementRefAsync(Guid fileId, CancellationToken ct = default)
    {
        await refCount.DecrementAsync(fileId, 1, ct);
        StorageInstruments.RefDecrements.Add(1);
    }

    public async Task<FileInfoResponse?> GetFileInfoAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId && x.Finalized, ct);
        if (file is null) return null;

        var downloadUrl = file.Purpose.IsPublic()
            ? s3.GetPublicUrl(file.S3Key)
            : s3.GeneratePresignedGetUrl(file.S3Key);

        return new FileInfoResponse(
            file.Id, file.FileName, file.FileSize, file.ContentType,
            file.Purpose, downloadUrl, file.Purpose.IsPublic());
    }

    public async Task<string?> GetDownloadUrlAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId && x.Finalized, ct);
        if (file is null) return null;

        return file.Purpose.IsPublic()
            ? s3.GetPublicUrl(file.S3Key)
            : s3.GeneratePresignedGetUrl(file.S3Key);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<long> ResolveEffectiveSizeLimit(Guid userId, FilePurpose purpose, Guid? spaceId, CancellationToken ct)
    {
        switch (purpose)
        {
            case FilePurpose.Avatar:
            case FilePurpose.SpaceAvatar:
                return _limits.AvatarMaxBytes;

            case FilePurpose.Emoji:
                return _limits.EmojiMaxBytes;

            case FilePurpose.Sticker:
                return _limits.StickerMaxBytes;

            case FilePurpose.Banner:
                return _limits.BannerMaxBytes;

            case FilePurpose.Video:
                return _limits.VideoMaxBytes;

            case FilePurpose.ChannelAttachment:
            {
                var limit = _limits.AttachmentBaseMaxBytes;

                // Check Ultima subscription
                var ultima = GrainFactory.GetGrain<IUltimaGrain>(userId);
                var sub = await ultima.GetSubscriptionAsync(ct);
                if (sub is { status: UltimaSubscriptionStatus.Active or UltimaSubscriptionStatus.GracePeriod })
                    limit = Math.Max(limit, _limits.AttachmentUltimaMaxBytes);

                // Check space boost level
                if (spaceId.HasValue)
                {
                    var space = GrainFactory.GetGrain<ISpaceGrain>(spaceId.Value);
                    var spaceInfo = await space.GetSpace();
                    if (spaceInfo.BoostLevel >= 3)
                        limit = Math.Max(limit, _limits.AttachmentBoostLevel3MaxBytes);
                    else if (spaceInfo.BoostLevel >= 2)
                        limit = Math.Max(limit, _limits.AttachmentBoostLevel2MaxBytes);
                }

                return limit;
            }

            default:
                return _limits.AttachmentBaseMaxBytes;
        }
    }

    private static string BuildS3Key(FilePurpose purpose, Guid fileId, Guid userId, Guid? spaceId, Guid? channelId)
    {
        // Layout: {visibility}/{scope_prefix}/{ownerId}/{category}/[channelId/]{fileId}
        // User content:  public/u/{userId}/avatars/{fileId}
        // Space content:  public/s/{spaceId}/avatar/{fileId}
        // Private space:  private/s/{spaceId}/channels/{channelId}/{fileId}
        // Bulk delete:    prefix "u/{userId}/" or "s/{spaceId}/"

        var visibility = purpose.IsPublic() ? "public" : "private";
        var category   = purpose.S3Prefix();

        if (purpose.IsSpaceScoped())
        {
            var ownerId = spaceId ?? userId;
            return purpose switch
            {
                FilePurpose.ChannelAttachment => $"{visibility}/s/{ownerId}/{category}/{channelId}/{fileId}",
                FilePurpose.Video             => $"{visibility}/s/{ownerId}/{category}/{channelId}/{fileId}",
                _                             => $"{visibility}/s/{ownerId}/{category}/{fileId}"
            };
        }

        return $"{visibility}/u/{userId}/{category}/{fileId}";
    }

    private static string? GetContentTypePrefix(FilePurpose purpose) => purpose switch
    {
        FilePurpose.Avatar      => "image/",
        FilePurpose.SpaceAvatar => "image/",
        FilePurpose.Emoji       => "image/",
        FilePurpose.Sticker     => "image/",
        FilePurpose.Banner      => "image/",
        FilePurpose.Video       => "video/",
        _                       => null // any content type for attachments
    };
}
