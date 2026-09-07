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
    S3PresignedUrlGenerator presignedUrlGenerator,
    IS3StorageService s3,
    IReferenceCountService refCount,
    IOptions<StorageOptions> storageOptions,
    IOptions<FileLimitsOptions> limitsOptions,
    ILogger<FileStorageGrain> logger) : Grain, IFileStorageGrain
{
    private readonly StorageOptions    _storage = storageOptions.Value;
    private readonly FileLimitsOptions _limits  = limitsOptions.Value;

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

        var fileId = ArgonId.New();
        var s3Key  = BuildS3Key(request.Purpose, fileId, userId, request.SpaceId, request.ChannelId);

        // NOT the purpose's prefix. It used to be passed here as the content type, which signed the
        // literal string "image/" into the request and obliged the client to send exactly that — so
        // every avatar was stored, and afterwards served, with a media type that has no subtype and
        // that a browser will not render. It restricted nothing either: "must send image/" is not
        // "must be an image". The prefix is a rule about the bytes, so it is checked against what the
        // store actually received, at FinalizeUploadAsync. Signing nothing here lets the client's own
        // Content-Type through, which is where the real type has been all along.
        var putData = presignedUrlGenerator.GeneratePresignedPut(
            s3Key,
            contentType: null,
            _limits.BlobTtlSeconds);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var file = new FileEntity
        {
            Id          = fileId,
            OwnerId     = userId,
            Purpose     = request.Purpose,
            S3Key       = s3Key,
            BucketName  = putData.Url, // store full presigned URL for reference
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
            Id        = ArgonId.New(),
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

        return new FileUploadResponse(blob.Id, fileId, putData.Url, putData.Headers, _limits.BlobTtlSeconds);
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

        // Validate what was actually stored against what this purpose accepts. Here rather than in
        // the signature because this is the first moment anyone knows: the server never sees the
        // bytes, and the content type the client declared when it asked for the URL is a claim it
        // was never held to.
        var required = GetContentTypePrefix(file.Purpose);

        if (required is not null &&
            !(metadata.ContentType?.StartsWith(required, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            await s3.DeleteFileAsync(file.S3Key, ct);
            db.Files.Remove(file);
            db.FileBlobs.Remove(blob);
            await db.SaveChangesAsync(ct);

            StorageInstruments.UploadsFailed.Add(1,
                new KeyValuePair<string, object?>("purpose", file.Purpose.ToString()),
                new KeyValuePair<string, object?>("reason", "content_type_rejected"));

            throw new InvalidOperationException(
                $"Uploaded content type '{metadata.ContentType}' is not allowed for purpose {file.Purpose}");
        }

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

        var downloadUrl = s3.GetFileDownloadUrl(file.Id);

        return new FileInfoResponse(
            file.Id, file.FileName, file.FileSize, file.ContentType,
            file.Purpose, downloadUrl, file.S3Key);
    }

    /// <inheritdoc cref="IFileStorageGrain.IncrementRefAsync"/>
    public async Task IncrementRefAsync(Guid fileId, CancellationToken ct = default)
    {
        if (!await OwnedByCallerAsync(fileId, "retain", ct))
            return;

        await refCount.IncrementAsync(fileId, 1, ct);
        StorageInstruments.RefIncrements.Add(1);
    }

    /// <summary>
    /// Releases one reference to a file this account owns, never taking the count below zero.
    /// </summary>
    /// <remarks>
    /// <para><b>The ownership test is the security half and it is not a formality.</b> Defect R1:
    /// <c>POST /api/files/{fileId}/decrement</c> takes any file id from any authenticated caller, and
    /// the grain it reaches is keyed by the caller while the id is not checked against anything. One
    /// call took a stranger's file from its single reference to zero, and
    /// <c>FileGcService.SweepOrphanFilesAsync</c> — <c>RefCount &lt;= 0</c> past a grace period —
    /// then deleted the S3 object and the row. Clamping at zero, which is all this method used to do,
    /// stops the count going negative and does nothing whatever about 1 → 0. So the release is scoped
    /// to the file's owner instead: a reference nobody can show they hold is not released.</para>
    ///
    /// <para><b>No holder argument, because every caller in the tree is the owner.</b> A file's
    /// <c>OwnerId</c> is the account that asked for the upload URL (<see cref="RequestUploadAsync"/>),
    /// including a space avatar or a channel attachment — those are space-<em>scoped</em>, not
    /// space-owned — so <c>SpaceGrain.CompleteUploadSpaceFile</c>, <c>UserGrain</c>'s moderation
    /// rejection and its avatar replacement, and <c>AccountDeletionGrain</c>'s walk over the files an
    /// account owns all release their own. The one call that deliberately reached somebody else's file
    /// is <c>AccountDeletionGrain.AnonymizeUserAsync</c>'s targeted avatar release, which existed only
    /// because <c>UserGrain.UpdateProfileAsync</c> would take an <c>avatarId</c> belonging to another
    /// account; that is now refused at the source, and this refuses the release itself.</para>
    ///
    /// <para>A refusal is a no-op with a warning rather than an exception: the callers that could hit
    /// it are erasure steps whose exception budget is spent on real failures, and the endpoint answers
    /// the same either way, so throwing would only turn "the file is not yours" into a probe for
    /// whether a file id exists.</para>
    ///
    /// <para>The clamp stays, for the case ownership cannot decide: a file the owner releases twice.
    /// (Defect ACC-08, pinned by
    /// <c>AccountPeripheralTests.Releasing_a_file_twice_leaves_its_reference_count_at_zero</c> —
    /// <c>AccountDeletionGrain</c> released the avatar by id and then walked every file the account
    /// owns, the avatar included.) It is a compensation rather than a guard because the count is only
    /// knowable after the fact: <c>ReferenceCountService</c> takes the row <c>FOR UPDATE</c> and
    /// returns what it wrote, so reading first and deciding after would be the race this is meant to
    /// survive. The compensation puts back exactly the one reference this call took — not the whole
    /// overshoot — so concurrent releases each undo their own and the row converges on zero instead of
    /// being pushed back up by whichever one saw the deepest negative.</para>
    /// </remarks>
    public async Task DecrementRefAsync(Guid fileId, CancellationToken ct = default)
    {
        if (!await OwnedByCallerAsync(fileId, "release", ct))
            return;

        var remaining = await refCount.DecrementAsync(fileId, 1, ct);
        StorageInstruments.RefDecrements.Add(1);

        if (remaining >= 0)
            return;

        logger.LogWarning(
            "Reference release took file {FileId} to {RefCount}; clamping back to zero. "
          + "Something released a reference it did not hold.", fileId, remaining);

        await refCount.IncrementAsync(fileId, 1, ct);
    }

    /// <summary>
    /// Whether the file exists and belongs to the account this grain is keyed by.
    /// </summary>
    /// <remarks>
    /// <para>Reads with <c>IgnoreQueryFilters</c> on purpose. A soft-deleted row is still a file whose
    /// owner may release its reference — the count is the only thing that tells
    /// <c>FileGcService</c> the bytes are collectable, so hiding the row from its owner would leave
    /// the object in the store for ever — and <c>AccountDeletionGrain</c> reads the same table the
    /// same way when it decides which files an erasure has to release.</para>
    ///
    /// <para>A file with no row at all is refused too: there is nothing to own, and the alternative is
    /// <c>ReferenceCountService</c> throwing <c>KeyNotFoundException</c> at a caller that only wanted
    /// to say "I am done with this".</para>
    /// </remarks>
    private async Task<bool> OwnedByCallerAsync(Guid fileId, string operation, CancellationToken ct)
    {
        var userId = this.GetPrimaryKey();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var owner = await db.Files
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(f => f.Id == fileId)
           .Select(f => (Guid?)f.OwnerId)
           .FirstOrDefaultAsync(ct);

        if (owner == userId)
            return true;

        logger.LogWarning(
            "Refused to {Operation} a reference to file {FileId} for user {UserId}: the file belongs "
          + "to {OwnerId}. A reference count may only be moved by the account that owns the file.",
            operation, fileId, userId, owner);

        return false;
    }

    public async Task<FileInfoResponse?> GetFileInfoAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId && x.Finalized, ct);
        if (file is null) return null;

        var downloadUrl = s3.GetFileDownloadUrl(file.Id);

        return new FileInfoResponse(
            file.Id, file.FileName, file.FileSize, file.ContentType,
            file.Purpose, downloadUrl, file.S3Key);
    }

    public async Task<string?> GetDownloadUrlAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId && x.Finalized, ct);
        if (file is null) return null;

        return s3.GetFileDownloadUrl(file.Id);
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

            // A direct chat has no space to be boosted, so only the base limit and Ultima apply.
            case FilePurpose.ChannelAttachment:
            case FilePurpose.DirectAttachment:
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

    private string BuildS3Key(FilePurpose purpose, Guid fileId, Guid userId, Guid? spaceId, Guid? channelId)
    {
        // Flat mode: avatars stored at root as just {fileId}
        if (_storage.FlatAvatarKeys && purpose is FilePurpose.Avatar or FilePurpose.SpaceAvatar)
            return fileId.ToString();

        var category = purpose.S3Prefix();

        if (purpose.IsSpaceScoped())
        {
            var ownerId = spaceId ?? userId;
            return purpose switch
            {
                FilePurpose.ChannelAttachment => $"s/{ownerId}/{category}/{channelId}/{fileId}",
                FilePurpose.Video             => $"s/{ownerId}/{category}/{channelId}/{fileId}",
                _                             => $"s/{ownerId}/{category}/{fileId}"
            };
        }

        return $"u/{userId}/{category}/{fileId}";
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
