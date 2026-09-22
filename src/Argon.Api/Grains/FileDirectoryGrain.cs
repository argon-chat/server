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

        return await db.Files
           .AsNoTracking()
           .Where(f => f.Id == fileId && f.Finalized)
           .Select(f => f.S3Key)
           .FirstOrDefaultAsync(ct);
    }
}
