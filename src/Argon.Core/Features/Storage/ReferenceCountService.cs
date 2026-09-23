namespace Argon.Features.Storage;

public interface IReferenceCountService
{
    Task IncrementAsync(Guid fileId, long increment = 1, CancellationToken ct = default);

    /// <summary>Takes references away, never below zero. False when the counter held fewer than asked.</summary>
    Task<bool> DecrementAsync(Guid fileId, long decrement = 1, CancellationToken ct = default);

    Task<long?> GetRefCountAsync(Guid fileId, CancellationToken ct = default);
}

public class ReferenceCountService(IDbContextFactory<ApplicationDbContext> dbFactory) : IReferenceCountService
{
    public async Task IncrementAsync(Guid fileId, long increment = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await AddAsync(db.FileCounters.Where(c => c.Id == fileId), increment, ct) == 0)
            throw new KeyNotFoundException($"FileCounter not found for file {fileId}");
    }

    public async Task<bool> DecrementAsync(Guid fileId, long decrement = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The guard is in the WHERE, so concurrent releases can never take the count below zero.
        if (await AddAsync(db.FileCounters.Where(c => c.Id == fileId && c.RefCount >= decrement), -decrement, ct) > 0)
            return true;

        if (!await db.FileCounters.AnyAsync(c => c.Id == fileId, ct))
            throw new KeyNotFoundException($"FileCounter not found for file {fileId}");

        return false;
    }

    public async Task<long?> GetRefCountAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counter = await db.FileCounters.FindAsync([fileId], ct);
        return counter?.RefCount;
    }

    private static Task<int> AddAsync(IQueryable<FileCounterEntity> counter, long delta, CancellationToken ct)
        => counter.ExecuteUpdateAsync(s => s
               .SetProperty(c => c.RefCount, c => c.RefCount + delta)
               .SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), ct);
}
