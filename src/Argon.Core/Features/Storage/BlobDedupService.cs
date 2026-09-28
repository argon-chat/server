namespace Argon.Features.Storage;

using System.Buffers;
using Argon.Features.Clustering.Regions;
using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// Shared objects under files: finds what a stored object duplicates, verifies by hashing, merges, and
/// keeps the object alive while any file points at it.
/// </summary>
public interface IBlobDedupService
{
    /// <summary>A blob just finalized: merge it into a verified twin when the claim allows, else queue it for hashing.</summary>
    Task OnStoredAsync(Guid blobId, byte[]? claimedSha256, CancellationToken ct = default);

    /// <summary>Hashes queued blobs and merges the duplicates. Returns how many objects were read.</summary>
    Task<int> VerifyQueuedAsync(int batch, CancellationToken ct = default);

    /// <summary>One live file fewer points at the blob; at zero the object becomes collectable.</summary>
    Task ReleaseAsync(Guid blobId, CancellationToken ct = default);

    /// <summary>Deletes the objects of blobs past <see cref="BlobEntity.DeleteAfter"/> that nothing points at.</summary>
    Task<int> SweepDeletableAsync(int batch, CancellationToken ct = default);

    /// <summary>Queues every dedupable blob that has an unverified twin by (md5, size, type).</summary>
    Task<int> RequestBackfillAsync(CancellationToken ct = default);

    /// <summary>Gives finalized files that predate blobs a blob each, a batch at a time. Returns how many it did.</summary>
    Task<int> BackfillBlobsAsync(int batch, CancellationToken ct = default);

    /// <summary>Points every file of <paramref name="duplicateId"/> at <paramref name="canonicalId"/> and schedules the duplicate's object.</summary>
    Task MergeAsync(Guid duplicateId, Guid canonicalId, CancellationToken ct = default);
}

public sealed class BlobDedupService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IS3StorageService s3,
    IOptions<DedupOptions> options,
    ILogger<BlobDedupService> logger) : IBlobDedupService
{
    private const string UniqueViolation = "23505";

    private DedupOptions Options => options.Value;

    public async Task OnStoredAsync(Guid blobId, byte[]? claimedSha256, CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var blob = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == blobId, ct);
        if (blob is null || !blob.Dedupable)
            return;

        if (blob.Md5 is { } md5)
        {
            var canonical = await db.Blobs.AsNoTracking()
               .Where(b => b.Id != blob.Id && b.Md5 == md5 && b.Size == blob.Size && b.ContentType == blob.ContentType
                        && b.Sha256 != null && b.CanonicalId == null && b.DeleteAfter == null && b.Dedupable)
               .OrderBy(b => b.CreatedAt)
               .FirstOrDefaultAsync(ct);

            if (canonical is not null)
            {
                // The claim is trusted only where an MD5 second preimage would be needed to abuse it.
                if (claimedSha256 is not null && canonical.Sha256 is not null && claimedSha256.AsSpan().SequenceEqual(canonical.Sha256))
                {
                    StorageInstruments.DedupClaimsHonoured.Add(1);
                    await MergeAsync(blob.Id, canonical.Id, ct);
                    return;
                }

                await RequestAsync(db, [blob.Id], ct);
                return;
            }

            var sibling = await db.Blobs.AsNoTracking()
               .Where(b => b.Id != blob.Id && b.Md5 == md5 && b.Size == blob.Size && b.ContentType == blob.ContentType
                        && b.Sha256 == null && b.CanonicalId == null && b.DeleteAfter == null && b.Dedupable)
               .OrderBy(b => b.CreatedAt)
               .Select(b => b.Id)
               .FirstOrDefaultAsync(ct);

            // The older one first, so it is the one that stays.
            if (sibling != Guid.Empty)
                await RequestAsync(db, [sibling, blob.Id], ct);

            return;
        }

        if (claimedSha256 is null)
            return;

        var claimedExists = await db.Blobs.AnyAsync(b => b.Id != blob.Id && b.Sha256 == claimedSha256 && b.ContentType == blob.ContentType
                                                      && b.CanonicalId == null && b.DeleteAfter == null && b.Dedupable, ct);
        if (claimedExists)
            await RequestAsync(db, [blob.Id], ct);
    }

    public async Task<int> VerifyQueuedAsync(int batch, CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return 0;

        List<BlobEntity> queued;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            queued = await db.Blobs.AsNoTracking()
               .Where(b => b.VerifyRequestedAt != null && b.CanonicalId == null && b.DeleteAfter == null)
               .OrderBy(b => b.VerifyRequestedAt)
               .Take(batch)
               .ToListAsync(ct);

        var  read  = 0;
        long bytes = 0;

        foreach (var blob in queued)
        {
            if (bytes >= Options.VerifyBytesPerMinute)
                break;

            if (blob.Size > Options.MaxVerifyBytes)
            {
                await ClearRequestAsync(blob.Id, ct);
                continue;
            }

            var sha256 = await HashAsync(blob.S3Key, ct);
            bytes += blob.Size;
            read++;

            if (sha256 is null)
            {
                logger.LogWarning("Dedup: object {Key} of blob {BlobId} could not be read for hashing", blob.S3Key, blob.Id);
                await ClearRequestAsync(blob.Id, ct);
                continue;
            }

            await RecordHashAsync(blob, sha256, ct);
        }

        return read;
    }

    public async Task ReleaseAsync(Guid blobId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        await db.Blobs
           .Where(b => b.Id == blobId && b.Links > 0)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.Links, b => b.Links - 1)
               .SetProperty(b => b.UpdatedAt, now), ct);

        await db.Blobs
           .Where(b => b.Id == blobId && b.Links <= 0 && b.DeleteAfter == null)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.DeleteAfter, now)
               .SetProperty(b => b.UpdatedAt, now), ct);
    }

    public async Task<int> SweepDeletableAsync(int batch, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var due = await db.Blobs
           .Where(b => b.DeleteAfter != null && b.DeleteAfter < now)
           .OrderBy(b => b.DeleteAfter)
           .Take(batch)
           .ToListAsync(ct);

        var removed = 0;

        foreach (var blob in due)
        {
            // The count is the guard; Links only says when to look.
            var live = await db.Files.CountAsync(f => f.BlobId == blob.Id, ct);
            if (live > 0)
            {
                if (blob.CanonicalId is { } canonicalId)
                {
                    await MergeAsync(blob.Id, canonicalId, ct);
                    continue;
                }

                blob.Links       = live;
                blob.DeleteAfter = null;
                continue;
            }

            try
            {
                await s3.DeleteFileAsync(blob.S3Key, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Dedup: failed to delete object {Key} of blob {BlobId}", blob.S3Key, blob.Id);
                StorageInstruments.GcErrors.Add(1, new KeyValuePair<string, object?>("sweep_type", "objects"));
                continue;
            }

            db.Blobs.Remove(blob);
            removed++;
        }

        await db.SaveChangesAsync(ct);
        return removed;
    }

    public async Task<int> RequestBackfillAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        return await db.Blobs
           .Where(b => b.Dedupable && b.Md5 != null && b.Sha256 == null && b.CanonicalId == null && b.DeleteAfter == null
                    && b.VerifyRequestedAt == null
                    && db.Blobs.Any(o => o.Id != b.Id && o.Md5 == b.Md5 && o.Size == b.Size && o.ContentType == b.ContentType
                                      && o.CanonicalId == null && o.DeleteAfter == null && o.Dedupable))
           .ExecuteUpdateAsync(s => s.SetProperty(b => b.VerifyRequestedAt, now), ct);
    }

    public async Task<int> BackfillBlobsAsync(int batch, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var files = await db.Files
           .Where(f => f.Finalized && f.BlobId == null)
           .OrderBy(f => f.CreatedAt)
           .Take(batch)
           .ToListAsync(ct);

        if (files.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;

        foreach (var file in files)
        {
            var blob = new BlobEntity
            {
                Id          = ArgonId.New(),
                S3Key       = file.S3Key,
                Size        = file.FileSize,
                ContentType = BlobHashes.NormalizeContentType(file.ContentType),
                Md5         = BlobHashes.ParseEtagMd5(file.Checksum),
                Links       = 1,
                Dedupable   = file.Purpose.IsDedupable(),
                CreatedAt   = file.CreatedAt,
                UpdatedAt   = now
            };

            db.Blobs.Add(blob);
            file.BlobId    = blob.Id;
            file.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return files.Count;
    }

    public async Task MergeAsync(Guid duplicateId, Guid canonicalId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var canonical = await ResolveCanonicalAsync(db, canonicalId, ct);
        var duplicate = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == duplicateId, ct);

        if (canonical is null || duplicate is null || canonical.Id == duplicate.Id || !canonical.Dedupable || !duplicate.Dedupable)
            return;

        var now = DateTimeOffset.UtcNow;

        var live = await db.Files.CountAsync(f => f.BlobId == duplicate.Id, ct);

        // Tombstones move too, so nothing is left naming a blob that is going away.
        await db.Files
           .IgnoreQueryFilters()
           .Where(f => f.BlobId == duplicate.Id)
           .ExecuteUpdateAsync(s => s
               .SetProperty(f => f.BlobId, canonical.Id)
               .SetProperty(f => f.S3Key, canonical.S3Key)
               .SetProperty(f => f.UpdatedAt, now), ct);

        await db.Blobs
           .Where(b => b.Id == canonical.Id)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.Links, b => b.Links + live)
               .SetProperty(b => b.DeleteAfter, (DateTimeOffset?)null)
               .SetProperty(b => b.UpdatedAt, now), ct);

        var deleteAfter = now + Options.PhysicalDeleteDelay;

        await db.Blobs
           .Where(b => b.Id == duplicate.Id)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.Links, 0L)
               .SetProperty(b => b.CanonicalId, canonical.Id)
               .SetProperty(b => b.VerifyRequestedAt, (DateTimeOffset?)null)
               .SetProperty(b => b.DeleteAfter, deleteAfter)
               .SetProperty(b => b.UpdatedAt, now), ct);

        StorageInstruments.DedupMerged.Add(1);
        StorageInstruments.DedupBytesSaved.Add(duplicate.Size);

        logger.LogInformation("Dedup: blob {Duplicate} ({Files} files, {Size} bytes) merged into {Canonical}",
            duplicate.Id, live, duplicate.Size, canonical.Id);
    }

    private async Task RecordHashAsync(BlobEntity blob, byte[] sha256, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await FindCanonicalAsync(db, blob.Id, sha256, blob.ContentType, ct) is { } canonical)
        {
            await MergeAsync(blob.Id, canonical, ct);
            return;
        }

        try
        {
            await db.Blobs
               .Where(b => b.Id == blob.Id && b.CanonicalId == null)
               .ExecuteUpdateAsync(s => s
                   .SetProperty(b => b.Sha256, sha256)
                   .SetProperty(b => b.VerifyRequestedAt, (DateTimeOffset?)null)
                   .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct);

            StorageInstruments.DedupVerified.Add(1);
        }
        catch (Exception e) when (IsUniqueViolation(e))
        {
            // Somebody became canonical for these bytes between the lookup and the write.
            if (await FindCanonicalAsync(db, blob.Id, sha256, blob.ContentType, ct) is { } raced)
                await MergeAsync(blob.Id, raced, ct);
            else
                throw;
        }
    }

    private static async Task<Guid?> FindCanonicalAsync(ApplicationDbContext db, Guid self, byte[] sha256, string? contentType, CancellationToken ct)
        => await db.Blobs
           .Where(b => b.Id != self && b.Sha256 == sha256 && b.ContentType == contentType && b.CanonicalId == null && b.Dedupable)
           .OrderBy(b => b.CreatedAt)
           .Select(b => (Guid?)b.Id)
           .FirstOrDefaultAsync(ct);

    private static async Task<BlobEntity?> ResolveCanonicalAsync(ApplicationDbContext db, Guid id, CancellationToken ct)
    {
        for (var hop = 0; hop < 8; hop++)
        {
            var blob = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
            if (blob is null || blob.CanonicalId is null)
                return blob;

            id = blob.CanonicalId.Value;
        }

        return null;
    }

    private static async Task RequestAsync(ApplicationDbContext db, Guid[] ids, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var at = now.AddMilliseconds(i);

            await db.Blobs
               .Where(b => b.Id == id && b.VerifyRequestedAt == null && b.CanonicalId == null)
               .ExecuteUpdateAsync(s => s.SetProperty(b => b.VerifyRequestedAt, at), ct);
        }

        StorageInstruments.DedupVerifyRequested.Add(ids.Length);
    }

    private async Task ClearRequestAsync(Guid blobId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await db.Blobs
           .Where(b => b.Id == blobId)
           .ExecuteUpdateAsync(s => s.SetProperty(b => b.VerifyRequestedAt, (DateTimeOffset?)null), ct);
    }

    private async Task<byte[]?> HashAsync(string key, CancellationToken ct)
    {
        await using var stream = await s3.OpenReadAsync(key, ct);
        if (stream is null)
            return null;

        using var hash   = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var       buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hash.GetHashAndReset();
    }

    private static bool IsUniqueViolation(Exception e)
        => e is PostgresException { SqlState: UniqueViolation }
        || e.InnerException is PostgresException { SqlState: UniqueViolation };
}
