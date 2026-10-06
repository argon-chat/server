namespace Argon.Api.Grains;

using System.Buffers;
using System.Diagnostics;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Services;
using Argon.Entities;
using Argon.Features.Expressions;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ion.runtime;
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
    IEntitlementChecker entitlements,
    IOptions<StorageOptions> storageOptions,
    IOptions<FileLimitsOptions> limitsOptions,
    ILogger<FileStorageGrain> logger) : Grain, IFileStorageGrain
{
    private readonly StorageOptions    _storage = storageOptions.Value;
    private readonly FileLimitsOptions _limits  = limitsOptions.Value;

    public async Task<FileUploadResponse> RequestUploadAsync(FileUploadRequest request, CancellationToken ct = default)
        => (await CreateUploadAsync(request, ct)).Response;

    public async Task<Either<PreparedUpload, PrepareUploadError>> PrepareUploadAsync(FileUploadRequest request, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.PrepareUpload");
        activity?.SetTag("file.purpose", request.Purpose.ToString());

        var userId = this.GetPrimaryKey();
        var limit  = await ResolveEffectiveSizeLimit(userId, request.Purpose, request.SpaceId, ct);

        if (request.FileSize > limit)
        {
            StorageInstruments.UploadsFailed.Add(1,
                new KeyValuePair<string, object?>("purpose", request.Purpose.ToString()),
                new KeyValuePair<string, object?>("reason", "size_exceeded"));
            return PrepareUploadError.TOO_LARGE;
        }

        if (request.ClaimedSha256 is { Length: 32 } claim && request.Purpose.IsDedupable())
        {
            var type   = BlobHashes.NormalizeContentType(request.ContentType);
            var source = await FindReadableCopyAsync(userId, claim, type, request.SpaceId, ct);

            if (source is { } sourceId)
            {
                var linked = await LinkAsync(sourceId, request, request.FileName, ct);
                if (linked.IsSuccess)
                {
                    StorageInstruments.DedupPrepared.Add(1, new KeyValuePair<string, object?>("purpose", request.Purpose.ToString()));
                    return new PreparedUpload(null, linked.Value);
                }
            }
        }

        var (ticket, _) = await CreateUploadAsync(request, ct, limit);
        return new PreparedUpload(ticket, null);
    }

    /// <summary>
    /// A file with these bytes the account may read, or null. Its own uploads count by the hash it
    /// claimed for them — nobody else is harmed by an account misdescribing its own file — while what
    /// others posted in the space counts only by a hash the server computed, and only where the
    /// account can read the channel.
    /// </summary>
    private async Task<Guid?> FindReadableCopyAsync(Guid userId, byte[] claim, string contentType, Guid? spaceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var own = await (
            from f in db.Files
            join b in db.Blobs on f.BlobId equals b.Id
            where f.OwnerId == userId && f.Finalized
               && b.Dedupable && b.CanonicalId == null && b.DeleteAfter == null && b.ContentType == contentType
               && (b.Sha256 == claim || b.ClaimedSha256 == claim)
            orderby f.CreatedAt descending
            select (Guid?)f.Id
        ).FirstOrDefaultAsync(ct);

        if (own is not null)
            return own;

        if (spaceId is not { } space)
            return null;

        var posted = await (
            from f in db.Files
            join b in db.Blobs on f.BlobId equals b.Id
            where f.SpaceId == space && f.ChannelId != null && (f.Purpose == FilePurpose.ChannelAttachment || f.Purpose == FilePurpose.Video)
               && f.Finalized
               && b.Dedupable && b.CanonicalId == null && b.DeleteAfter == null && b.ContentType == contentType
               && b.Sha256 == claim
            orderby f.CreatedAt descending
            select new { f.Id, ChannelId = f.ChannelId!.Value }
        ).Take(5).ToListAsync(ct);

        foreach (var candidate in posted)
            if (await entitlements.HasChannelAccessAsync(space, candidate.ChannelId, userId,
                    ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory, ct))
                return candidate.Id;

        return null;
    }

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

    public Task<Either<FileInfoResponse, AttachExistingFileError>> LinkAsync(Guid sourceFileId, FileUploadRequest target, string? fileName,
        CancellationToken ct = default)
    {
        if (target.Purpose is not (FilePurpose.ChannelAttachment or FilePurpose.DirectAttachment))
            throw new InvalidOperationException($"Files are not linked into purpose {target.Purpose}");

        return LinkFileAsync(sourceFileId, target, fileName, null, ct);
    }

    /// <param name="probed">
    /// The media record of a copy whose source has none, read off the object's header; saved with the
    /// link. A source's own record is copied instead, and either makes the copy a video.
    /// </param>
    private async Task<Either<FileInfoResponse, AttachExistingFileError>> LinkFileAsync(Guid sourceFileId, FileUploadRequest target,
        string? fileName, FileMediaEntity? probed, CancellationToken ct)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.Link");
        activity?.SetTag("file.purpose", target.Purpose.ToString());

        var userId = this.GetPrimaryKey();
        activity?.SetTag("user.id", userId.ToString());

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var source = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == sourceFileId && f.Finalized, ct);
        if (source is null)
            return AttachExistingFileError.SOURCE_NOT_FOUND;

        var blob = source.BlobId is { } blobId ? await ResolveBlobAsync(db, blobId, ct) : null;
        var now  = DateTimeOffset.UtcNow;

        // No blob: finalized by a silo that predates them and not backfilled yet. Not shareable: an
        // expression's object, or one the sweep is about to take.
        if (blob is null || !blob.Dedupable || blob.DeleteAfter <= now)
        {
            logger.LogInformation("File {SourceId} cannot be linked: its object is not shareable", sourceFileId);
            return AttachExistingFileError.SOURCE_NOT_FOUND;
        }

        // A copy of a video is a video: it keeps the media record and the video limits.
        var media   = await db.FileMedia.AsNoTracking().FirstOrDefaultAsync(m => m.FileId == sourceFileId, ct) ?? probed;
        var purpose = media is null ? target.Purpose : FilePurpose.Video;

        if (target.Purpose == FilePurpose.Video && media is null)
            throw new InvalidOperationException("A video copy needs a media record");

        var limit = await ResolveEffectiveSizeLimit(userId, purpose, target.SpaceId, ct);
        if (blob.Size > limit)
            return AttachExistingFileError.TOO_LARGE;

        if (!AcceptsContentType(purpose, source.ContentType))
            return AttachExistingFileError.CONTENT_TYPE_REJECTED;

        var file = new FileEntity
        {
            Id          = ArgonId.New(),
            OwnerId     = userId,
            Purpose     = purpose,
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
        if (media is not null)
            db.FileMedia.Add(media with { FileId = file.Id, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(ct);

        await db.Blobs
           .Where(b => b.Id == blob.Id)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.Links, b => b.Links + 1)
               .SetProperty(b => b.DeleteAfter, (DateTimeOffset?)null)
               .SetProperty(b => b.UpdatedAt, now), ct);

        StorageInstruments.DedupLinks.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()));
        activity?.SetTag("file.id", file.Id.ToString());

        logger.LogInformation("File linked: source={SourceId}, fileId={FileId}, purpose={Purpose}, userId={UserId}",
            sourceFileId, file.Id, purpose, userId);

        return new FileInfoResponse(file.Id, file.FileName, file.FileSize, file.ContentType, file.Purpose,
            s3.GetFileDownloadUrl(file.Id), blob.S3Key);
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

    private async Task<(FileUploadResponse Response, string S3Key)> CreateUploadAsync(FileUploadRequest request, CancellationToken ct, long? knownLimit = null)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.RequestUpload");
        activity?.SetTag("file.purpose", request.Purpose.ToString());
        activity?.SetTag("file.size", request.FileSize);

        // A video file exists only with a media record, which only PrepareVideoUploadAsync's path writes.
        if (request.Purpose == FilePurpose.Video)
            throw new InvalidOperationException("Videos are uploaded through PrepareVideoUpload, which checks their header");

        var userId = this.GetPrimaryKey();
        activity?.SetTag("user.id", userId.ToString());

        var effectiveLimit = knownLimit ?? await ResolveEffectiveSizeLimit(userId, request.Purpose, request.SpaceId, ct);
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

        // The object's row exists from the moment its key is signed: an upload that never finishes is
        // still an object to delete, and the key has no other home.
        var stored = new BlobEntity
        {
            Id          = ArgonId.New(),
            S3Key       = s3Key,
            Size        = 0,
            ContentType = BlobHashes.NormalizeContentType(request.ContentType),
            Links       = 0,
            Dedupable   = request.Purpose.IsDedupable(),
            CreatedAt   = DateTimeOffset.UtcNow,
            UpdatedAt   = DateTimeOffset.UtcNow
        };

        var file = new FileEntity
        {
            Id          = fileId,
            OwnerId     = userId,
            Purpose     = request.Purpose,
            BucketName  = putData.Url, // store full presigned URL for reference
            FileSize    = 0,
            ContentType = request.ContentType,
            FileName    = request.FileName,
            Finalized   = false,
            SpaceId     = request.SpaceId,
            ChannelId   = request.ChannelId,
            BlobId      = stored.Id,
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

        db.Blobs.Add(stored);
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

        // A video ticket closes only through CompleteVideoUploadAsync, which reads the header.
        var blob = await db.FileBlobs.FirstOrDefaultAsync(x => x.Id == blobId && x.OwnerId == userId && x.Declaration == null, ct);
        if (blob is null)
            throw new KeyNotFoundException("Upload blob not found");

        var file   = await db.Files.FindAsync([blob.FileId], ct);
        var stored = file?.BlobId is { } storedId ? await db.Blobs.FindAsync([storedId], ct) : null;

        if (blob.ExpiresAt < DateTimeOffset.UtcNow)
        {
            // Expired — clean up
            if (file is not null)
            {
                if (stored is not null)
                {
                    await s3.DeleteFileAsync(stored.S3Key, ct);
                    db.Blobs.Remove(stored);
                }
                db.Files.Remove(file);
            }
            db.FileBlobs.Remove(blob);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Upload blob has expired");
        }

        if (file is null || stored is null)
            throw new KeyNotFoundException("File record not found");

        // Validate file exists in S3 via HEAD
        var metadata = await s3.HeadFileAsync(stored.S3Key, ct);
        if (metadata is null)
            throw new InvalidOperationException("File not found in storage — upload may have failed");

        // Validate what was actually stored against what this purpose accepts. Here rather than in
        // the signature because this is the first moment anyone knows: the server never sees the
        // bytes, and the content type the client declared when it asked for the URL is a claim it
        // was never held to.
        if (!AcceptsContentType(file.Purpose, metadata.ContentType))
        {
            await s3.DeleteFileAsync(stored.S3Key, ct);
            db.Files.Remove(file);
            db.Blobs.Remove(stored);
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
            await s3.DeleteFileAsync(stored.S3Key, ct);
            db.Files.Remove(file);
            db.Blobs.Remove(stored);
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

        // The object is now what the store says it is.
        stored.Size          = metadata.ContentLength;
        stored.ContentType   = BlobHashes.NormalizeContentType(metadata.ContentType ?? file.ContentType);
        stored.Md5           = BlobHashes.ParseEtagMd5(metadata.ETag);
        stored.ClaimedSha256 = blob.ClaimedSha256;
        stored.Links         = 1;
        stored.UpdatedAt     = DateTimeOffset.UtcNow;

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
            file.Purpose, downloadUrl, stored.S3Key);
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

        var found = await (
            from f in db.Files.AsNoTracking()
            join b in db.Blobs.AsNoTracking() on f.BlobId equals b.Id
            where f.Id == fileId && f.Finalized
            select new { File = f, Key = b.S3Key }
        ).FirstOrDefaultAsync(ct);

        if (found is null) return null;

        var file        = found.File;
        var downloadUrl = s3.GetFileDownloadUrl(file.Id);

        return new FileInfoResponse(
            file.Id, file.FileName, file.FileSize, file.ContentType,
            file.Purpose, downloadUrl, found.Key);
    }

    public async Task<string?> GetDownloadUrlAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.Finalized, ct);
        if (file is null) return null;

        return s3.GetFileDownloadUrl(file.Id);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Video
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>S3 caps a multipart upload at this many parts.</summary>
    private const int MaxParts = 10_000;

    /// <summary>What is read first to find where <c>moov</c> ends.</summary>
    private const int MoovSearchBytes = 64 * 1024;

    private const int MaxEtagLength     = 256;
    private const int MaxFileNameLength = 255;

    public async Task<IVideoUploadResult> PrepareVideoUploadAsync(VideoUploadRequest request, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.PrepareVideoUpload");

        var userId = this.GetPrimaryKey();
        var d      = request.Declaration;
        activity?.SetTag("user.id", userId.ToString());
        activity?.SetTag("file.size", d.size);

        var limit = await ResolveEffectiveSizeLimit(userId, FilePurpose.Video, request.SpaceId, ct);
        if (d.size > limit)
            return VideoRefused(VideoUploadError.TOO_LARGE, "size_exceeded");

        if (d.durationMs > _limits.VideoMaxDurationMs)
            return VideoRefused(VideoUploadError.TOO_LONG, "too_long");

        if (BlobHashes.NormalizeContentType(d.contentType) != VideoMedia.ContentType || d.codec is not null && !VideoMedia.IsH264(d.codec))
            return VideoRefused(VideoUploadError.CONTENT_TYPE_REJECTED, "content_type_rejected");

        if (d.size <= 0 || d.width <= 0 || d.height <= 0 || d.durationMs <= 0 || d.fileName is null || d.fileName.Length > MaxFileNameLength
         || d.codec?.Length > 64 || d.thumbHash?.Length > 64)
            return VideoRefused(VideoUploadError.DECLARATION_MISMATCH, "declaration_invalid");

        if (!await AcceptsPosterAsync(userId, request.ChannelId, d, ct))
            return VideoRefused(VideoUploadError.POSTER_REJECTED, "poster_rejected");

        var claim = d.sha256?.ToArray();
        if (claim is { Length: 32 }
         && await FindReadableCopyAsync(userId, claim, VideoMedia.ContentType, request.SpaceId, ct) is { } sourceId
         && await CopyVideoAsync(sourceId, request, ct) is { } copied)
            return copied;

        return await CreateVideoTicketAsync(userId, request, claim is { Length: 32 } ? claim : null, ct);
    }

    /// <summary>The poster and the storyboard: finalized images of the caller's in the same chat, and a storyboard within bounds.</summary>
    private async Task<bool> AcceptsPosterAsync(Guid userId, Guid? scopeId, VideoUploadDeclaration d, CancellationToken ct)
    {
        if (d.storyboard is { } board)
        {
            if (d.storyboardFileId is null
             || board.frameWidth is < 1 or > 200 || board.frameHeight is < 1 or > 200
             || board.columns is < 1 or > 32 || board.frameCount is < 1 or > 400 || board.intervalMs < 200)
                return false;
        }
        else if (d.storyboardFileId is not null)
            return false;

        var ids = new List<Guid>(2);
        if (d.posterFileId is { } poster)
            ids.Add(poster);
        if (d.storyboardFileId is { } storyboard && !ids.Contains(storyboard))
            ids.Add(storyboard);

        if (ids.Count == 0)
            return true;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var found = await db.Files.AsNoTracking()
           .CountAsync(f => ids.Contains(f.Id) && f.Finalized && f.OwnerId == userId && f.ChannelId == scopeId
                         && f.ContentType != null && f.ContentType.StartsWith("image/"), ct);

        return found == ids.Count;
    }

    /// <summary>
    /// A copy of a readable file with these bytes, with its media record: the source's own, or one read
    /// off the object's header when the source was posted as a plain file. Null to upload instead,
    /// which is also the answer when that header does not pass.
    /// </summary>
    private async Task<IVideoUploadResult?> CopyVideoAsync(Guid sourceId, VideoUploadRequest request, CancellationToken ct)
    {
        var d = request.Declaration;

        FileMediaEntity? probed = null;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var source = await (
                from f in db.Files.AsNoTracking()
                join b in db.Blobs.AsNoTracking() on f.BlobId equals b.Id
                where f.Id == sourceId
                select new { b.S3Key, b.Size, HasMedia = db.FileMedia.Any(m => m.FileId == f.Id) }
            ).FirstOrDefaultAsync(ct);

            if (source is null)
                return null;

            if (!source.HasMedia)
            {
                var (read, error) = await ProbeAsync(source.S3Key, source.Size, ct);
                var refusal       = read is { } header ? Refusal(d, header) : VideoUploadError.NOT_STREAMABLE;

                if (refusal is not null)
                {
                    logger.LogInformation("Video copy of {SourceId} not made ({Refusal}, {Error}); the client uploads instead", sourceId, refusal, error);
                    return null;
                }

                probed = VideoMedia.Record(Guid.Empty, read!.Value, d, source.Size);
            }
        }

        var linked = await LinkFileAsync(sourceId,
            new FileUploadRequest(FilePurpose.Video, VideoMedia.ContentType, d.size, request.SpaceId, request.ChannelId, d.fileName), d.fileName,
            probed, ct);

        if (!linked.IsSuccess)
            return null;

        StorageInstruments.DedupPrepared.Add(1, new KeyValuePair<string, object?>("purpose", nameof(FilePurpose.Video)));

        return await GetVideoInfoAsync(linked.Value.FileId, ct) is { } info
            ? new VideoStored(info)
            : null;
    }

    /// <summary>
    /// Every video is a multipart upload, a single part up to the threshold: its upload id dies at
    /// completion, so no URL handed out can write over a checked object. Each part's length is signed.
    /// </summary>
    private async Task<IVideoUploadResult> CreateVideoTicketAsync(Guid userId, VideoUploadRequest request, byte[]? claim, CancellationToken ct)
    {
        var d      = request.Declaration;
        var now    = DateTimeOffset.UtcNow;
        var ttl    = _limits.VideoTicketTtlSeconds;
        var fileId = ArgonId.New();
        var key    = BuildS3Key(FilePurpose.Video, fileId, userId, request.SpaceId, request.ChannelId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var open = await db.FileBlobs.CountAsync(b => b.OwnerId == userId && b.Declaration != null && b.ExpiresAt > now, ct);
        if (open >= _limits.VideoMaxOpenTickets)
        {
            logger.LogWarning("Video upload refused for {UserId}: {Open} video tickets already open", userId, open);
            return VideoRefused(VideoUploadError.NOT_AUTHORIZED, "too_many_tickets");
        }

        var (partSize, partCount) = PartLayout(d.size);

        var uploadId = await s3.CreateMultipartUploadAsync(key, VideoMedia.ContentType, VideoMedia.CacheControl, ct);
        if (uploadId is null)
            return VideoRefused(VideoUploadError.INTERNAL_ERROR, "store_refused");

        var partUrls = new List<string>(partCount);
        for (var part = 1; part <= partCount; part++)
        {
            var length = part < partCount ? partSize : d.size - partSize * (partCount - 1);
            partUrls.Add(presignedUrlGenerator.GeneratePresignedUploadPart(key, uploadId, part, length, ttl));
        }

        var stored = new BlobEntity
        {
            Id          = ArgonId.New(),
            S3Key       = key,
            ContentType = VideoMedia.ContentType,
            Dedupable   = FilePurpose.Video.IsDedupable(),
            CreatedAt   = now,
            UpdatedAt   = now
        };

        var file = new FileEntity
        {
            Id          = fileId,
            OwnerId     = userId,
            Purpose     = FilePurpose.Video,
            BucketName  = "video",
            ContentType = VideoMedia.ContentType,
            FileName    = d.fileName,
            SpaceId     = request.SpaceId,
            ChannelId   = request.ChannelId,
            BlobId      = stored.Id,
            CreatedAt   = now,
            UpdatedAt   = now
        };

        var ticket = new FileBlobEntity
        {
            Id            = ArgonId.New(),
            FileId        = fileId,
            OwnerId       = userId,
            Purpose       = FilePurpose.Video,
            SizeLimit     = d.size,
            ExpiresAt     = now.AddSeconds(ttl),
            ClaimedSha256 = claim,
            UploadId      = uploadId,
            PartSize      = partSize,
            PartCount     = partCount,
            Declaration   = VideoMedia.Serialize(d),
            CreatedAt     = now,
            UpdatedAt     = now
        };

        try
        {
            db.Blobs.Add(stored);
            db.Files.Add(file);
            db.FileBlobs.Add(ticket);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await s3.AbortMultipartUploadAsync(key, uploadId, CancellationToken.None);
            throw;
        }

        StorageInstruments.UploadsRequested.Add(1, new KeyValuePair<string, object?>("purpose", nameof(FilePurpose.Video)));
        StorageInstruments.ActiveBlobs.Add(1);

        logger.LogInformation("Video upload requested: fileId={FileId}, size={Size}, parts={Parts}, userId={UserId}",
            fileId, d.size, partCount, userId);

        return new VideoUploadRequired(new VideoUploadTicket(ticket.Id, fileId, null, IonArray<FormField>.Empty, partUrls, partSize, ttl));
    }

    /// <summary>One part up to the threshold, else parts of the configured size, raised so there are at most 10000.</summary>
    private (long Size, int Count) PartLayout(long size)
    {
        if (size <= _limits.VideoMultipartThresholdBytes)
            return (size, 1);

        var part = _limits.VideoPartSizeBytes;
        if ((size + part - 1) / part > MaxParts)
        {
            const long MiB = 1024 * 1024;
            part = ((size + MaxParts - 1) / MaxParts + MiB - 1) / MiB * MiB;
        }

        return (part, (int)((size + part - 1) / part));
    }

    public async Task<IVideoUploadResult> CompleteVideoUploadAsync(Guid ticketId, UploadedPart[] parts, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileStorage.CompleteVideoUpload");
        var sw     = Stopwatch.StartNew();
        var userId = this.GetPrimaryKey();
        activity?.SetTag("blob.id", ticketId.ToString());

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // A completed ticket stays behind soft-deleted, so a retry still finds its file.
        var ticket = await db.FileBlobs.IgnoreQueryFilters()
           .FirstOrDefaultAsync(x => x.Id == ticketId && x.OwnerId == userId && x.Declaration != null, ct);
        if (ticket is null)
            return new FailedVideoUpload(VideoUploadError.NOT_FOUND);

        if (await StoredVideoAsync(db, ticket.FileId, userId, ct) is { } done)
            return done;

        if (ticket.IsDeleted)
            return new FailedVideoUpload(VideoUploadError.NOT_FOUND);

        var file   = await db.Files.FirstOrDefaultAsync(f => f.Id == ticket.FileId, ct);
        var stored = file?.BlobId is { } storedId ? await db.Blobs.FirstOrDefaultAsync(b => b.Id == storedId, ct) : null;

        if (file is null || stored is null)
            return new FailedVideoUpload(VideoUploadError.NOT_FOUND);

        if (ticket.ExpiresAt < DateTimeOffset.UtcNow)
        {
            await DiscardTicketAsync(ticket.Id, userId, ct);
            return VideoRefused(VideoUploadError.TICKET_EXPIRED, "ticket_expired");
        }

        if (VideoMedia.Deserialize(ticket.Declaration) is not { } declaration)
            return new FailedVideoUpload(VideoUploadError.INTERNAL_ERROR);

        if (ticket.UploadId is { } uploadId)
        {
            var ordered = CoveredParts(parts, ticket.PartCount ?? 0);
            if (ordered is null)
                return VideoRefused(VideoUploadError.DECLARATION_MISMATCH, "parts_invalid");

            // A refusal keeps the upload open for a retry with the right ETags; a parallel retry may have completed it already.
            if (!await s3.CompleteMultipartUploadAsync(stored.S3Key, uploadId, ordered, ct)
             && (await s3.HeadFileAsync(stored.S3Key, ct))?.ContentLength != ticket.SizeLimit)
                return VideoRefused(VideoUploadError.DECLARATION_MISMATCH, "parts_rejected");

            await db.FileBlobs.Where(b => b.Id == ticket.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.UploadId, (string?)null), ct);
            ticket.UploadId = null;
        }

        var metadata = await s3.HeadFileAsync(stored.S3Key, ct);
        if (metadata is null)
            return new FailedVideoUpload(VideoUploadError.NOT_FOUND);

        if (metadata.ContentLength != ticket.SizeLimit)
            return await RejectObjectAsync(stored, VideoUploadError.DECLARATION_MISMATCH, "size_mismatch", ct);

        if (!AcceptsContentType(FilePurpose.Video, metadata.ContentType))
            return await RejectObjectAsync(stored, VideoUploadError.CONTENT_TYPE_REJECTED, "content_type_rejected", ct);

        var (probe, probeError) = await ProbeAsync(stored.S3Key, metadata.ContentLength, ct);
        if (probe is not { } header)
        {
            logger.LogInformation("Video {FileId} is not streamable: {Error}", file.Id, probeError);
            return await RejectObjectAsync(stored, VideoUploadError.NOT_STREAMABLE, "not_streamable", ct);
        }

        if (Refusal(declaration, header) is { } refusal)
        {
            logger.LogInformation("Video {FileId} refused ({Refusal}): declared {Width}x{Height} {Duration}ms audio={Audio} {Codec}, "
                + "read {ReadWidth}x{ReadHeight} {ReadDuration}ms audio={ReadAudio} {ReadCodec}", file.Id, refusal,
                declaration.width, declaration.height, declaration.durationMs, declaration.hasAudio, declaration.codec,
                header.Width, header.Height, header.DurationMs, header.HasAudio, header.VideoCodec);
            return await RejectObjectAsync(stored, refusal, refusal.ToString().ToLowerInvariant(), ct);
        }

        var now = DateTimeOffset.UtcNow;

        file.FileSize    = metadata.ContentLength;
        file.ContentType = VideoMedia.ContentType;
        file.Checksum    = metadata.ETag;
        file.Finalized   = true;
        file.UpdatedAt   = now;

        // A multipart ETag is no MD5, and ParseEtagMd5 says so.
        stored.Size          = metadata.ContentLength;
        stored.ContentType   = VideoMedia.ContentType;
        stored.Md5           = BlobHashes.ParseEtagMd5(metadata.ETag);
        stored.ClaimedSha256 = ticket.ClaimedSha256;
        stored.Links         = 1;
        stored.UpdatedAt     = now;

        var media = VideoMedia.Record(file.Id, header, declaration, file.FileSize);

        db.FileCounters.Add(new FileCounterEntity { Id = file.Id, RefCount = 1, CreatedAt = now, UpdatedAt = now });
        db.FileMedia.Add(media);

        // Written with the file, so an abort that took the ticket meanwhile fails this save instead of losing the bytes after it.
        db.Entry(ticket).Property(t => t.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            await using var fresh = await dbFactory.CreateDbContextAsync(ct);
            if (await StoredVideoAsync(fresh, file.Id, userId, ct) is { } raced)
                return raced;

            logger.LogInformation(e, "Video {FileId} was not finalized: its ticket went while it was checked", file.Id);
            return new FailedVideoUpload(VideoUploadError.NOT_FOUND);
        }

        // Last, and soft: a retry finds the finished file through it.
        await db.FileBlobs.Where(b => b.Id == ticket.Id)
           .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDeleted, true).SetProperty(b => b.DeletedAt, now), ct);

        try
        {
            await dedup.OnStoredAsync(stored.Id, ticket.ClaimedSha256, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Dedup step failed for video {FileId}; the object stays its own", file.Id);
        }

        sw.Stop();
        var purpose = new KeyValuePair<string, object?>("purpose", nameof(FilePurpose.Video));
        StorageInstruments.UploadsFinalized.Add(1, purpose);
        StorageInstruments.UploadSizeBytes.Record(file.FileSize, purpose);
        StorageInstruments.UploadFinalizeDuration.Record(sw.Elapsed.TotalMilliseconds, purpose);
        StorageInstruments.TotalStoredBytes.Add(file.FileSize, purpose);
        StorageInstruments.ActiveBlobs.Add(-1);

        logger.LogInformation("Video finalized: fileId={FileId}, size={Size}, {Width}x{Height}, {Duration}ms, codec={Codec}, userId={UserId}",
            file.Id, file.FileSize, header.Width, header.Height, header.DurationMs, header.VideoCodec, userId);

        return new VideoStored(VideoMedia.ToInfo(file, media, s3));
    }

    public async Task AbortVideoUploadAsync(Guid ticketId, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        if (await DiscardTicketAsync(ticketId, userId, ct))
            logger.LogInformation("Video upload aborted: ticket={TicketId}, userId={UserId}", ticketId, userId);
    }

    public async Task<UploadLimits> GetUploadLimitsAsync(Guid? spaceId, CancellationToken ct = default)
    {
        var userId     = this.GetPrimaryKey();
        var attachment = await ResolveEffectiveSizeLimit(userId, spaceId is null ? FilePurpose.DirectAttachment : FilePurpose.ChannelAttachment,
            spaceId, ct);
        var video      = await ResolveEffectiveSizeLimit(userId, FilePurpose.Video, spaceId, ct);

        return new UploadLimits(attachment, video, _limits.VideoMaxDurationMs);
    }

    public async Task<VideoInfo?> GetVideoInfoAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var found = await (
            from f in db.Files.AsNoTracking()
            join m in db.FileMedia.AsNoTracking() on f.Id equals m.FileId
            where f.Id == fileId && f.Finalized
            select new { File = f, Media = m }
        ).FirstOrDefaultAsync(ct);

        return found is null ? null : VideoMedia.ToInfo(found.File, found.Media, s3);
    }

    public async Task<Dictionary<Guid, VideoInfo>> GetSendableVideosAsync(Guid scopeId, List<Guid> fileIds, CancellationToken ct = default)
    {
        if (fileIds is not { Count: > 0 })
            return [];

        var userId = this.GetPrimaryKey();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var found = await (
            from f in db.Files.AsNoTracking()
            join m in db.FileMedia.AsNoTracking() on f.Id equals m.FileId
            where fileIds.Contains(f.Id) && f.Finalized && f.OwnerId == userId && f.ChannelId == scopeId
            select new { File = f, Media = m }
        ).ToListAsync(ct);

        return found.ToDictionary(x => x.File.Id, x => VideoMedia.ToInfo(x.File, x.Media, s3));
    }

    private async Task<IVideoUploadResult?> StoredVideoAsync(ApplicationDbContext db, Guid fileId, Guid userId, CancellationToken ct)
    {
        var found = await (
            from f in db.Files.AsNoTracking()
            join m in db.FileMedia.AsNoTracking() on f.Id equals m.FileId
            where f.Id == fileId && f.OwnerId == userId && f.Finalized
            select new { File = f, Media = m }
        ).FirstOrDefaultAsync(ct);

        return found is null ? null : new VideoStored(VideoMedia.ToInfo(found.File, found.Media, s3));
    }

    /// <summary>
    /// The MP4 header: the first 64 KiB say where <c>moov</c> ends, and the read goes on exactly that far,
    /// never past <see cref="FileLimitsOptions.VideoProbeBytes"/>. Pooled buffers, read once.
    /// </summary>
    private async Task<(Mp4ProbeResult? Result, Mp4ProbeError Error)> ProbeAsync(string key, long size, CancellationToken ct)
    {
        var headLength = (int)Math.Min(size, MoovSearchBytes);
        if (headLength <= 0)
            return (null, Mp4ProbeError.NotMp4);

        var    head  = ArrayPool<byte>.Shared.Rent(headLength);
        byte[]? whole = null;
        try
        {
            var read = await ReadRangeAsync(key, 0, head.AsMemory(0, headLength), ct);

            if (!Mp4Probe.TryLocateMoov(head.AsSpan(0, read), size, out var moovEnd) || moovEnd > _limits.VideoProbeBytes)
                return (null, Mp4ProbeError.MoovNotInHead);

            var buffer = head;
            if (moovEnd > read)
            {
                whole = ArrayPool<byte>.Shared.Rent((int)moovEnd);
                head.AsSpan(0, read).CopyTo(whole);
                read  += await ReadRangeAsync(key, read, whole.AsMemory(read, (int)moovEnd - read), ct);
                buffer = whole;
            }

            return Mp4Probe.TryProbe(buffer.AsSpan(0, (int)Math.Min(read, moovEnd)), size, out var result, out var error)
                ? (result, Mp4ProbeError.None)
                : (null, error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(head);
            if (whole is not null)
                ArrayPool<byte>.Shared.Return(whole);
        }
    }

    /// <summary>Fills <paramref name="into"/> from the object, starting at <paramref name="from"/>; how many bytes came.</summary>
    private async Task<int> ReadRangeAsync(string key, long from, Memory<byte> into, CancellationToken ct)
    {
        await using var stream = await s3.OpenReadRangeAsync(key, from, from + into.Length - 1, ct);
        if (stream is null)
            return 0;

        var read = 0;
        while (read < into.Length)
        {
            var n = await stream.ReadAsync(into[read..], ct);
            if (n == 0)
                break;
            read += n;
        }

        return read;
    }

    /// <summary>
    /// What the header says against what is accepted and what was declared; null when it passes. Not
    /// streamable without moov first or without a duration (a fragmented file), too long past the cap,
    /// and <c>CONTENT_TYPE_REJECTED</c> for any codec but H.264 (<see cref="VideoMedia.IsH264"/>).
    /// </summary>
    private VideoUploadError? Refusal(VideoUploadDeclaration d, Mp4ProbeResult read)
    {
        if (!read.FastStart || read.DurationMs <= 0)
            return VideoUploadError.NOT_STREAMABLE;
        if (read.DurationMs > _limits.VideoMaxDurationMs)
            return VideoUploadError.TOO_LONG;
        if (!VideoMedia.IsH264(read.VideoCodec))
            return VideoUploadError.CONTENT_TYPE_REJECTED;
        return Matches(d, read) ? null : VideoUploadError.DECLARATION_MISMATCH;
    }

    /// <summary>Dimensions exactly, duration within a second or 2 %, audio, and the codec's sample entry when one was declared.</summary>
    private static bool Matches(VideoUploadDeclaration d, Mp4ProbeResult read)
        => d.width == read.Width && d.height == read.Height
        && Math.Abs((long)d.durationMs - read.DurationMs) <= Math.Max(1000, read.DurationMs / 50)
        && d.hasAudio == read.HasAudio
        && (d.codec is null
         || read.VideoCodec is not null && VideoMedia.CodecFamily(d.codec).Equals(VideoMedia.CodecFamily(read.VideoCodec), StringComparison.OrdinalIgnoreCase));

    /// <summary>Parts 1..count, each once and with a bounded ETag, in order; null when the list is not that.</summary>
    private static List<(int PartNumber, string ETag)>? CoveredParts(UploadedPart[]? parts, int count)
    {
        if (parts is null || count <= 0 || parts.Length != count)
            return null;

        var ordered = new (int PartNumber, string ETag)[count];
        foreach (var part in parts)
        {
            if (part.partNumber < 1 || part.partNumber > count || string.IsNullOrWhiteSpace(part.etag) || part.etag.Length > MaxEtagLength
             || ordered[part.partNumber - 1].ETag is not null)
                return null;
            ordered[part.partNumber - 1] = (part.partNumber, part.etag);
        }

        return [..ordered];
    }

    /// <summary>The object goes; the ticket stays until it is aborted or expires.</summary>
    private async Task<IVideoUploadResult> RejectObjectAsync(BlobEntity stored, VideoUploadError error, string reason, CancellationToken ct)
    {
        await s3.DeleteFileAsync(stored.S3Key, ct);
        return VideoRefused(error, reason);
    }

    /// <summary>
    /// Takes the ticket row first and touches the store only after: whoever deletes the row owns the
    /// cleanup, and a file a racing completion already finalized keeps its object.
    /// </summary>
    private async Task<bool> DiscardTicketAsync(Guid ticketId, Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var ticket = await db.FileBlobs.AsNoTracking()
           .FirstOrDefaultAsync(x => x.Id == ticketId && x.OwnerId == userId && x.Declaration != null, ct);
        if (ticket is null)
            return false;

        if (await db.FileBlobs.Where(x => x.Id == ticketId).ExecuteDeleteAsync(ct) != 1)
            return false;

        StorageInstruments.ActiveBlobs.Add(-1);

        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == ticket.FileId, ct);
        if (file is null || file.Finalized)
            return true;

        if (file.BlobId is { } storedId && await db.Blobs.FirstOrDefaultAsync(b => b.Id == storedId, ct) is { } stored)
            await UnfinishedUploads.DiscardAsync(db, s3, logger, stored, ticket.UploadId, ct);

        db.Files.Remove(file);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static FailedVideoUpload VideoRefused(VideoUploadError error, string reason)
    {
        StorageInstruments.UploadsFailed.Add(1,
            new KeyValuePair<string, object?>("purpose", nameof(FilePurpose.Video)),
            new KeyValuePair<string, object?>("reason", reason));
        return new FailedVideoUpload(error);
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
                return await TieredLimitAsync(userId, spaceId, _limits.VideoMaxBytes, _limits.VideoUltimaMaxBytes,
                    _limits.VideoBoostLevel2MaxBytes, _limits.VideoBoostLevel3MaxBytes, ct);

            case FilePurpose.ChannelAttachment:
            case FilePurpose.DirectAttachment:
                return await TieredLimitAsync(userId, spaceId, _limits.AttachmentBaseMaxBytes, _limits.AttachmentUltimaMaxBytes,
                    _limits.AttachmentBoostLevel2MaxBytes, _limits.AttachmentBoostLevel3MaxBytes, ct);

            default:
                return _limits.AttachmentBaseMaxBytes;
        }
    }

    // A direct chat has no space to be boosted, so only the base limit and Ultima apply.
    private async Task<long> TieredLimitAsync(Guid userId, Guid? spaceId, long baseLimit, long ultimaLimit, long level2Limit, long level3Limit,
        CancellationToken ct)
    {
        var limit = baseLimit;

        var sub = await GrainFactory.GetGrain<IUltimaGrain>(userId).GetSubscriptionAsync(ct);
        if (sub is { status: UltimaSubscriptionStatus.Active or UltimaSubscriptionStatus.GracePeriod })
            limit = Math.Max(limit, ultimaLimit);

        if (spaceId.HasValue)
        {
            var spaceInfo = await GrainFactory.GetGrain<ISpaceGrain>(spaceId.Value).GetSpace();
            if (spaceInfo.BoostLevel >= 3)
                limit = Math.Max(limit, level3Limit);
            else if (spaceInfo.BoostLevel >= 2)
                limit = Math.Max(limit, level2Limit);
        }

        return limit;
    }

    private string BuildS3Key(FilePurpose purpose, Guid fileId, Guid userId, Guid? spaceId, Guid? channelId)
    {
        // Flat mode: avatars stored at root as just {fileId}
        if (_storage.FlatAvatarKeys && purpose is FilePurpose.Avatar or FilePurpose.SpaceAvatar)
            return fileId.ToString();

        var category = purpose.S3Prefix();

        // A direct-chat video files under its sender, like a direct attachment: the conversation id is a hash of both users.
        if (purpose == FilePurpose.Video && spaceId is null)
            return $"u/{userId}/{category}/{fileId}";

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
        FilePurpose.Video                                                   => BlobHashes.NormalizeContentType(contentType) == VideoMedia.ContentType,
        // Lottie arrives as gzip or JSON and video as WEBM; AddItem checks the bytes themselves.
        FilePurpose.Emoji or FilePurpose.Sticker => ExpressionUploads.IsExpressionContentType(contentType),
        _                                        => true // any content type for attachments
    };

    private static bool HasPrefix(string? contentType, string prefix)
        => contentType?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false;
}
