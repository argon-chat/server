namespace Argon.Features.Storage;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
///     Background service for garbage collecting expired blobs and orphaned files.
///     - Every 5 minutes: deletes expired upload blobs + their S3 objects
///     - Every hour: deletes finalized files with ref_count ≤ 0 (1-hour grace period)
/// </summary>
public class FileGcService(
    IServiceScopeFactory scopeFactory,
    IS3StorageService s3,
    IOptions<FileLimitsOptions> limitsOptions,
    ILogger<FileGcService> logger) : BackgroundService
{
    private static readonly TimeSpan BlobSweepInterval   = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OrphanSweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrphanGracePeriod   = TimeSpan.FromHours(1);

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

    private async Task SweepExpiredBlobsAsync(CancellationToken ct)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileGC.SweepExpiredBlobs");
        var sw = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContext();
        await using var _ = db;

        var now = DateTimeOffset.UtcNow;
        var expiredBlobs = await db.FileBlobs
            .Where(b => b.ExpiresAt < now)
            .Take(100)
            .ToListAsync(ct);

        if (expiredBlobs.Count == 0) return;

        logger.LogInformation("FileGC: sweeping {Count} expired blobs", expiredBlobs.Count);

        foreach (var blob in expiredBlobs)
        {
            var file = await db.Files.FindAsync([blob.FileId], ct);
            if (file is not null)
            {
                try
                {
                    await s3.DeleteFileAsync(file.S3Key, file.Purpose.IsPublic(), ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "FileGC: failed to delete S3 object {Key}", file.S3Key);
                }
                db.Files.Remove(file);
            }
            db.FileBlobs.Remove(blob);
        }

        await db.SaveChangesAsync(ct);
        sw.Stop();
        StorageInstruments.GcBlobsSwept.Add(expiredBlobs.Count);
        StorageInstruments.GcSweepDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("sweep_type", "blobs"));
        logger.LogInformation("FileGC: cleaned {Count} expired blobs in {ElapsedMs}ms", expiredBlobs.Count, sw.Elapsed.TotalMilliseconds);
    }

    private async Task SweepOrphanFilesAsync(CancellationToken ct)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("FileGC.SweepOrphanFiles");
        var sw = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContext();
        await using var _ = db;

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
                await s3.DeleteFileAsync(orphan.File.S3Key, orphan.File.Purpose.IsPublic(), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FileGC: failed to delete orphan S3 object {Key}", orphan.File.S3Key);
            }

            db.FileCounters.Remove(orphan.Counter);
            db.Files.Remove(orphan.File);
        }

        await db.SaveChangesAsync(ct);
        sw.Stop();
        StorageInstruments.GcOrphansSwept.Add(orphanFiles.Count);
        StorageInstruments.GcSweepDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("sweep_type", "orphans"));
        logger.LogInformation("FileGC: cleaned {Count} orphan files in {ElapsedMs}ms", orphanFiles.Count, sw.Elapsed.TotalMilliseconds);
    }
}
