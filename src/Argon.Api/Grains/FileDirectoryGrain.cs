namespace Argon.Grains;

using Argon.Entities;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;

/// <inheritdoc cref="IFileDirectoryGrain"/>
[StatelessWorker]
public sealed class FileDirectoryGrain(IDbContextFactory<ApplicationDbContext> contextFactory)
    : Grain, IFileDirectoryGrain
{
    public async Task<string?> GetObjectKeyAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await (
            from f in db.Files.AsNoTracking()
            join b in db.Blobs.AsNoTracking() on f.BlobId equals b.Id
            where f.Id == fileId && f.Finalized
            select b.S3Key
        ).FirstOrDefaultAsync(ct);
    }
}
