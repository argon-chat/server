namespace Argon.Features.Storage;

using Argon.Features.Clustering;
using Argon.Features.EF;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
///     Background service for garbage collecting expired blobs and orphaned files.
///     - Every 5 minutes: deletes expired upload blobs + their S3 objects
///     - Every hour: deletes finalized files with ref_count ≤ 0 (1-hour grace period)
/// </summary>
/// <remarks>
///     Every media replica runs this loop, so each sweep first takes the <see cref="LockTable"/> lease — the
///     one <see cref="SchemaReconcileLease"/> the TTL sweeper also uses — and a replica that does not get it
///     skips the sweep instead of deleting the same batch as the others.
/// </remarks>
public class FileGcService(
    IServiceScopeFactory scopeFactory,
    IS3StorageService s3,
    IOptions<FileLimitsOptions> limitsOptions,
    IOptions<Argon.Features.Logic.FileGcOptions> gcOptions,
    RoleDescriptor role,
    ILogger<FileGcService> logger) : BackgroundService
{
    /// <summary>The lease row that makes one replica the collector for the whole database.</summary>
    public const string LockTable = "__FileGcLock";

    /// <summary>Longer than a sweep of a full batch, S3 round trips included.</summary>
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(5);

    private TimeSpan BlobSweepInterval   => gcOptions.Value.BlobSweepInterval;
    private TimeSpan OrphanSweepInterval => gcOptions.Value.OrphanSweepInterval;
    private TimeSpan OrphanGracePeriod   => gcOptions.Value.OrphanGracePeriod;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastBlobSweep   = DateTimeOffset.MinValue;
        var lastOrphanSweep = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                if (now - lastBlobSweep >= BlobSweepInterval)
                {
                    await SweepExpiredBlobsAsync(stoppingToken);
                    lastBlobSweep = now;
                }

                if (now - lastOrphanSweep >= OrphanSweepInterval)
                {
                    await SweepOrphanFilesAsync(stoppingToken);
                    lastOrphanSweep = now;
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
            .Where(b => b.ExpiresAt < now)
            .Take(100)
            .ToListAsync(ct);

        if (expiredBlobs.Count == 0) return;

        logger.LogInformation("FileGC: sweeping {Count} expired blobs", expiredBlobs.Count);

        var fileIds = expiredBlobs.Select(b => b.FileId).Distinct().ToList();
        var files = await db.Files
            .Where(f => fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        foreach (var blob in expiredBlobs)
        {
            if (files.Remove(blob.FileId, out var file))
            {
                try
                {
                    await s3.DeleteFileAsync(file.S3Key, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "FileGC: failed to delete S3 object {Key}", file.S3Key);
                }
                db.Files.Remove(file);
            }
            db.FileBlobs.Remove(blob);
        }

        // The S3 deletes are idempotent; the rows are not written by a replica whose lease has moved on.
        if (!await lease.TryRenewAsync(ct)) return;

        await db.SaveChangesAsync(ct);
        sw.Stop();
        StorageInstruments.GcBlobsSwept.Add(expiredBlobs.Count);
        StorageInstruments.GcSweepDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("sweep_type", "blobs"));
        logger.LogInformation("FileGC: cleaned {Count} expired blobs in {ElapsedMs}ms", expiredBlobs.Count, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>One pass over finalized files nothing references any more; nothing happens without the lease.</summary>
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
            try
            {
                await s3.DeleteFileAsync(orphan.File.S3Key, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FileGC: failed to delete orphan S3 object {Key}", orphan.File.S3Key);
            }

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

    /// <summary>The sweep lease, or null while another replica holds it.</summary>
    /// <remarks>The connection is pinned so the lease is taken, renewed and released on the sweep's own session.</remarks>
    private async Task<SchemaReconcileLease?> TryAcquireLeaseAsync(ApplicationDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);

        return await SchemaReconcileLease.TryAcquireAsync(
            db.Database.GetDbConnection(), logger, role.Id.Value, LeaseLifetime, LockTable, ct);
    }
}
