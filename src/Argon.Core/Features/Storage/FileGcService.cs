namespace Argon.Features.Storage;

using Argon.Features.Clustering;
using Argon.Features.EF;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
///     Background service for garbage collecting expired blobs and orphaned files.
///     - Every 5 minutes: deletes expired upload blobs + their S3 objects
///     - Every hour: releases finalized files with ref_count ≤ 0 (1-hour grace period) and deletes the
///       objects nothing points at any more
///     - Every minute: hashes the objects the dedup step queued and merges the duplicates
/// </summary>
/// <remarks>
///     Every media replica runs this loop, so each sweep first takes the <see cref="LockTable"/> lease — the
///     one <see cref="SchemaReconcileLease"/> the TTL sweeper also uses — and a replica that does not get it
///     skips the sweep instead of deleting the same batch as the others.
/// </remarks>
public class FileGcService(
    IServiceScopeFactory scopeFactory,
    IS3StorageService s3,
    IBlobDedupService dedup,
    IOptions<FileLimitsOptions> limitsOptions,
    IOptions<Argon.Features.Logic.FileGcOptions> gcOptions,
    IOptions<DedupOptions> dedupOptions,
    RoleDescriptor role,
    ILogger<FileGcService> logger) : BackgroundService
{
    /// <summary>The lease row that makes one replica the collector for the whole database.</summary>
    public const string LockTable = "__FileGcLock";

    /// <summary>Longer than a sweep of a full batch, S3 round trips included.</summary>
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(5);

    private const int VerifyBatch  = 50;
    private const int ObjectsBatch = 50;

    private TimeSpan BlobSweepInterval   => gcOptions.Value.BlobSweepInterval;
    private TimeSpan OrphanSweepInterval => gcOptions.Value.OrphanSweepInterval;
    private TimeSpan OrphanGracePeriod   => gcOptions.Value.OrphanGracePeriod;
    private TimeSpan VerifyInterval      => dedupOptions.Value.VerifyInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastBlobSweep   = DateTimeOffset.MinValue;
        var lastOrphanSweep = DateTimeOffset.MinValue;
        var lastVerify      = DateTimeOffset.MinValue;
        var backfilled      = !dedupOptions.Value.Backfill;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                if (!backfilled)
                    backfilled = await RequestBackfillAsync(stoppingToken);

                if (now - lastBlobSweep >= BlobSweepInterval)
                {
                    await SweepExpiredBlobsAsync(stoppingToken);
                    lastBlobSweep = now;
                }

                if (now - lastOrphanSweep >= OrphanSweepInterval)
                {
                    await SweepOrphanFilesAsync(stoppingToken);
                    await SweepObjectsAsync(stoppingToken);
                    lastOrphanSweep = now;
                }

                if (now - lastVerify >= VerifyInterval)
                {
                    await VerifyBlobsAsync(stoppingToken);
                    lastVerify = now;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "FileGcService sweep iteration failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>One pass over expired upload blobs, as the loop runs it; nothing happens without the lease.</summary>
    public async Task SweepExpiredBlobsAsync(CancellationToken ct)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileGC.SweepExpiredBlobs");
        var sw = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(ct);

        await using var lease = await TryAcquireLeaseAsync(db, ct);
        if (lease is null) return;

        var now = DateTimeOffset.UtcNow;
        var expiredBlobs = await db.FileBlobs
            .AsNoTracking()
            .Where(b => b.ExpiresAt < now)
            .Take(100)
            .ToListAsync(ct);

        if (expiredBlobs.Count == 0) return;

        logger.LogInformation("FileGC: sweeping {Count} expired blobs", expiredBlobs.Count);

        var fileIds = expiredBlobs.Select(b => b.FileId).Distinct().ToList();
        var files = await db.Files
            .Where(f => fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        // The key of an upload that never finished is on its object row, reserved when the URL was signed.
        var objectIds = files.Values.Select(f => f.BlobId).OfType<Guid>().ToList();
        var objects = await db.Blobs
            .Where(b => objectIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, ct);

        var swept = 0;
        foreach (var blob in expiredBlobs)
        {
            // The row first: whoever deletes it owns the cleanup, and a finalize racing it fails on the missing row.
            if (await db.FileBlobs.Where(b => b.Id == blob.Id).ExecuteDeleteAsync(ct) != 1)
                continue;

            swept++;

            if (!files.Remove(blob.FileId, out var file) || file.Finalized)
                continue;

            if (file.BlobId is { } objectId && objects.Remove(objectId, out var stored))
                await UnfinishedUploads.DiscardAsync(db, s3, logger, stored, blob.UploadId, ct);

            db.Files.Remove(file);
        }

        // Saved whatever became of the lease: the tickets taken above are this pass's alone.
        await db.SaveChangesAsync(ct);
        sw.Stop();
        StorageInstruments.GcBlobsSwept.Add(swept);
        StorageInstruments.GcSweepDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("sweep_type", "blobs"));
        logger.LogInformation("FileGC: cleaned {Count} expired blobs in {ElapsedMs}ms", expiredBlobs.Count, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>One pass over finalized files nothing references any more; nothing happens without the lease.</summary>
    /// <remarks>
    ///     A file releases its object rather than deleting it; <see cref="SweepObjectsAsync"/> removes
    ///     objects once no live file points at them.
    /// </remarks>
    public async Task SweepOrphanFilesAsync(CancellationToken ct)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileGC.SweepOrphanFiles");
        var sw = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(ct);

        await using var lease = await TryAcquireLeaseAsync(db, ct);
        if (lease is null) return;

        var cutoff = DateTimeOffset.UtcNow - OrphanGracePeriod;

        // Find files where finalized=true, ref_count <= 0, and haven't been updated recently
        var orphanFiles = await (
            from f in db.Files
            join c in db.FileCounters on f.Id equals c.Id
            where f.Finalized && c.RefCount <= 0 && c.UpdatedAt < cutoff
            select new { File = f, Counter = c }
        ).Take(50).ToListAsync(ct);

        if (orphanFiles.Count == 0) return;

        logger.LogInformation("FileGC: sweeping {Count} orphan files (ref≤0)", orphanFiles.Count);

        foreach (var orphan in orphanFiles)
        {
            if (orphan.File.BlobId is { } blobId)
                await dedup.ReleaseAsync(blobId, ct);
            else
                logger.LogWarning("FileGC: orphan file {FileId} names no object; only its rows go", orphan.File.Id);

            db.FileCounters.Remove(orphan.Counter);
            db.Files.Remove(orphan.File);
        }

        if (!await lease.TryRenewAsync(ct)) return;

        await db.SaveChangesAsync(ct);
        sw.Stop();
        StorageInstruments.GcOrphansSwept.Add(orphanFiles.Count);
        StorageInstruments.GcSweepDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("sweep_type", "orphans"));
        logger.LogInformation("FileGC: cleaned {Count} orphan files in {ElapsedMs}ms", orphanFiles.Count, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>One pass over objects no live file points at and whose delay has passed; nothing happens without the lease.</summary>
    public async Task SweepObjectsAsync(CancellationToken ct)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileGC.SweepObjects");
        var sw = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(ct);

        await using var lease = await TryAcquireLeaseAsync(db, ct);
        if (lease is null) return;

        var removed = await dedup.SweepDeletableAsync(ObjectsBatch, ct);
        if (removed == 0) return;

        sw.Stop();
        StorageInstruments.GcObjectsSwept.Add(removed);
        StorageInstruments.GcSweepDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("sweep_type", "objects"));
        logger.LogInformation("FileGC: deleted {Count} unreferenced objects in {ElapsedMs}ms", removed, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>One pass of the verifier over the objects the dedup step queued; nothing happens without the lease.</summary>
    public async Task VerifyBlobsAsync(CancellationToken ct)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileGC.VerifyBlobs");
        var sw = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(ct);

        await using var lease = await TryAcquireLeaseAsync(db, ct);
        if (lease is null) return;

        var read = await dedup.VerifyQueuedAsync(VerifyBatch, ct);
        if (read == 0) return;

        sw.Stop();
        StorageInstruments.GcSweepDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("sweep_type", "verify"));
        logger.LogInformation("FileGC: hashed {Count} objects in {ElapsedMs}ms", read, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Queues the existing objects that have twins, once; true when done or when another replica did it.
    private async Task<bool> RequestBackfillAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(ct);

        await using var lease = await TryAcquireLeaseAsync(db, ct);
        if (lease is null) return false;

        var queued = await dedup.RequestBackfillAsync(ct);
        logger.LogInformation("FileGC: dedup backfill queued {Count} objects", queued);
        return true;
    }

    /// <summary>The sweep lease, or null while another replica holds it.</summary>
    /// <remarks>The connection is pinned so the lease is taken, renewed and released on the sweep's own session.</remarks>
    private async Task<SchemaReconcileLease?> TryAcquireLeaseAsync(ApplicationDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);

        return await SchemaReconcileLease.TryAcquireAsync(
            db.Database.GetDbConnection(), logger, role.Id.Value, LeaseLifetime, LockTable, ct);
    }
}

/// <summary>The object of an upload that never finished.</summary>
public static class UnfinishedUploads
{
    /// <summary>
    /// Aborts its multipart upload and deletes the object, each tried on its own. An object the store
    /// would not delete stays on its row for the object sweep to take.
    /// </summary>
    public static async Task DiscardAsync(ApplicationDbContext db, IS3StorageService s3, ILogger logger, BlobEntity stored, string? uploadId,
        CancellationToken ct)
    {
        if (uploadId is not null)
        {
            try
            {
                if (!await s3.AbortMultipartUploadAsync(stored.S3Key, uploadId, ct))
                    logger.LogInformation("Multipart upload {UploadId} of {Key} was not open to abort", uploadId, stored.S3Key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to abort multipart upload {UploadId} of {Key}", uploadId, stored.S3Key);
            }
        }

        var deleted = false;
        try
        {
            deleted = await s3.DeleteFileAsync(stored.S3Key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to delete S3 object {Key}", stored.S3Key);
        }

        if (deleted)
            db.Blobs.Remove(stored);
        else
        {
            logger.LogWarning("S3 object {Key} was not deleted; left to the object sweep", stored.S3Key);
            stored.Links       = 0;
            stored.DeleteAfter = DateTimeOffset.UtcNow;
        }
    }
}
