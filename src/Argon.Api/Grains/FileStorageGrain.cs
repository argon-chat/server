namespace Argon.Api.Grains;

using System.Diagnostics;
using Argon.Api.Grains.Interfaces;
using Argon.Entities;
using Argon.Features.Expressions;
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
    IBlobDedupService dedup,
    IOptions<StorageOptions> storageOptions,
    IOptions<FileLimitsOptions> limitsOptions,
    ILogger<FileStorageGrain> logger) : Grain, IFileStorageGrain
{
    private readonly StorageOptions    _storage = storageOptions.Value;
    private readonly FileLimitsOptions _limits  = limitsOptions.Value;

    public async Task<FileUploadResponse> RequestUploadAsync(FileUploadRequest request, CancellationToken ct = default)
        => (await CreateUploadAsync(request, ct)).Response;

    public async Task<FileInfoResponse> StoreAsync(FileUploadRequest request, byte[] data, string? cacheControl, TimeSpan? unclaimedFor,
        CancellationToken ct = default)
    {
        var (upload, key) = await CreateUploadAsync(request with { FileSize = data.Length }, ct);

        using (var content = new MemoryStream(data, writable: false))
            if (!await s3.PutObjectAsync(key, content, request.ContentType, cacheControl, ct))
                throw new InvalidOperationException($"could not store file {upload.FileId}");

        var file = await FinalizeUploadAsync(upload.BlobId, ct);

        if (unclaimedFor is { } lifetime)
        {
            // FileGcService collects RefCount <= 0 once UpdatedAt is older than its grace period, so stamping
            // UpdatedAt ahead keeps an unclaimed file for at least the lifetime.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.FileCounters
               .Where(c => c.Id == file.FileId)
               .ExecuteUpdateAsync(s => s
                   .SetProperty(c => c.RefCount, 0L)
                   .SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow + lifetime), ct);
        }

        return file;
    }

    public async Task<FileInfoResponse> LinkAsync(Guid sourceFileId, FileUploadRequest target, string? fileName, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.Link");
        activity?.SetTag("file.purpose", target.Purpose.ToString());

        if (target.Purpose is not (FilePurpose.ChannelAttachment or FilePurpose.DirectAttachment))
            throw new InvalidOperationException($"Files are not linked into purpose {target.Purpose}");

        var userId = this.GetPrimaryKey();
        activity?.SetTag("user.id", userId.ToString());

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var source = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == sourceFileId && f.Finalized, ct)
            ?? throw new KeyNotFoundException("Source file not found");

        if (source.BlobId is not { } blobId)
            throw new InvalidOperationException("Source file has no shared object");

        var blob = await ResolveBlobAsync(db, blobId, ct);
        var now  = DateTimeOffset.UtcNow;

        if (blob is null || !blob.Dedupable || blob.DeleteAfter <= now)
            throw new InvalidOperationException("Source file cannot be shared");

        var limit = await ResolveEffectiveSizeLimit(userId, target.Purpose, target.SpaceId, ct);
        if (blob.Size > limit)
            throw new InvalidOperationException($"File size {blob.Size} exceeds limit {limit} for purpose {target.Purpose}");

        if (!AcceptsContentType(target.Purpose, source.ContentType))
            throw new InvalidOperationException($"Content type '{source.ContentType}' is not allowed for purpose {target.Purpose}");

        var file = new FileEntity
        {
            Id          = ArgonId.New(),
            OwnerId     = userId,
            Purpose     = target.Purpose,
            S3Key       = blob.S3Key,
            BucketName  = "link",
            FileSize    = blob.Size,
            ContentType = source.ContentType,
            Checksum    = source.Checksum,
            FileName    = fileName ?? source.FileName,
            Finalized   = true,
            SpaceId     = target.SpaceId,
            ChannelId   = target.ChannelId,
            BlobId      = blob.Id,
            CreatedAt   = now,
            UpdatedAt   = now
        };

        db.Files.Add(file);
        db.FileCounters.Add(new FileCounterEntity { Id = file.Id, RefCount = 1, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(ct);

        await db.Blobs
           .Where(b => b.Id == blob.Id)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.Links, b => b.Links + 1)
               .SetProperty(b => b.DeleteAfter, (DateTimeOffset?)null)
               .SetProperty(b => b.UpdatedAt, now), ct);

        StorageInstruments.DedupLinks.Add(1, new KeyValuePair<string, object?>("purpose", target.Purpose.ToString()));
        activity?.SetTag("file.id", file.Id.ToString());

        logger.LogInformation("File linked: source={SourceId}, fileId={FileId}, purpose={Purpose}, userId={UserId}",
            sourceFileId, file.Id, target.Purpose, userId);

        return new FileInfoResponse(file.Id, file.FileName, file.FileSize, file.ContentType, file.Purpose,
            s3.GetFileDownloadUrl(file.Id), file.S3Key);
    }

    private static async Task<BlobEntity?> ResolveBlobAsync(ApplicationDbContext db, Guid id, CancellationToken ct)
    {
        for (var hop = 0; hop < 8; hop++)
        {
            var blob = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
            if (blob?.CanonicalId is not { } next)
                return blob;

            id = next;
        }

        return null;
    }

    private async Task<(FileUploadResponse Response, string S3Key)> CreateUploadAsync(FileUploadRequest request, CancellationToken ct)
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
            FileName    = request.FileName,
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
            ClaimedSha256 = request.ClaimedSha256,
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

        return (new FileUploadResponse(blob.Id, fileId, putData.Url, putData.Headers, _limits.BlobTtlSeconds), s3Key);
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
        if (!AcceptsContentType(file.Purpose, metadata.ContentType))
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

        var stored = new BlobEntity
        {
            Id          = ArgonId.New(),
            S3Key       = file.S3Key,
            Size        = metadata.ContentLength,
            ContentType = BlobHashes.NormalizeContentType(metadata.ContentType ?? file.ContentType),
            Md5         = BlobHashes.ParseEtagMd5(metadata.ETag),
            Links       = 1,
            Dedupable   = file.Purpose.IsDedupable(),
            CreatedAt   = DateTimeOffset.UtcNow,
            UpdatedAt   = DateTimeOffset.UtcNow
        };

        file.BlobId = stored.Id;

        // Create ref counter
        var counter = new FileCounterEntity
        {
            Id        = file.Id,
            RefCount  = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Blobs.Add(stored);
        db.FileCounters.Add(counter);
        db.FileBlobs.Remove(blob);
        await db.SaveChangesAsync(ct);

        try
        {
            await dedup.OnStoredAsync(stored.Id, blob.ClaimedSha256, ct);
        }
        catch (Exception e)
        {
            // The upload is done; sharing its object is an economy, not a condition.
            logger.LogWarning(e, "Dedup step failed for file {FileId}; the object stays its own", file.Id);
        }

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
    /// <para>A file the owner releases twice stays at zero (ACC-08, pinned by
    /// <c>AccountPeripheralTests.Releasing_a_file_twice_leaves_its_reference_count_at_zero</c>):
    /// <c>ReferenceCountService</c> only decrements a count that can afford it.</para>
    /// </remarks>
    public async Task DecrementRefAsync(Guid fileId, CancellationToken ct = default)
    {
        if (!await OwnedByCallerAsync(fileId, "release", ct))
            return;

        var released = await refCount.DecrementAsync(fileId, 1, ct);
        StorageInstruments.RefDecrements.Add(1);

        if (!released)
            logger.LogWarning(
                "Reference release for file {FileId} found no reference to release; the count stays at zero. "
              + "Something released a reference it did not hold.", fileId);
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
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.Finalized, ct);
        if (file is null) return null;

        var downloadUrl = s3.GetFileDownloadUrl(file.Id);

        return new FileInfoResponse(
            file.Id, file.FileName, file.FileSize, file.ContentType,
            file.Purpose, downloadUrl, file.S3Key);
    }

    public async Task<string?> GetDownloadUrlAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.Finalized, ct);
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

            // The largest file of the kind; the exact cap for the format is checked when the item is added.
            case FilePurpose.Emoji:
                return ExpressionUploads.MaxUploadBytes(ExpressionKind.Emoji);

            case FilePurpose.Sticker:
                return ExpressionUploads.MaxUploadBytes(ExpressionKind.Sticker);

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
                FilePurpose.ChannelAttachment when channelId is not null => $"s/{ownerId}/{category}/{channelId}/{fileId}",
                FilePurpose.Video             => $"s/{ownerId}/{category}/{channelId}/{fileId}",
                _                             => $"s/{ownerId}/{category}/{fileId}"
            };
        }

        return $"u/{userId}/{category}/{fileId}";
    }

    internal static bool AcceptsContentType(FilePurpose purpose, string? contentType) => purpose switch
    {
        FilePurpose.Avatar or FilePurpose.SpaceAvatar or FilePurpose.Banner => HasPrefix(contentType, "image/"),
        FilePurpose.Video                                                   => HasPrefix(contentType, "video/"),
        // Lottie arrives as gzip or JSON and video as WEBM; AddItem checks the bytes themselves.
        FilePurpose.Emoji or FilePurpose.Sticker => ExpressionUploads.IsExpressionContentType(contentType),
        _                                        => true // any content type for attachments
    };

    private static bool HasPrefix(string? contentType, string prefix)
        => contentType?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false;
}
