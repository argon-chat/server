namespace Argon.Features.Storage;

public interface IReferenceCountService
{
    Task<long> IncrementAsync(Guid fileId, long increment = 1, CancellationToken ct = default);
    Task<long> DecrementAsync(Guid fileId, long decrement = 1, CancellationToken ct = default);
    Task<long?> GetRefCountAsync(Guid fileId, CancellationToken ct = default);
}

public class ReferenceCountService(IDbContextFactory<ApplicationDbContext> dbFactory) : IReferenceCountService
{
    public Task<long> IncrementAsync(Guid fileId, long increment = 1, CancellationToken ct = default)
        => AddAsync(fileId, increment, ct);

    public Task<long> DecrementAsync(Guid fileId, long decrement = 1, CancellationToken ct = default)
        => AddAsync(fileId, -decrement, ct);

    public async Task<long?> GetRefCountAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counter = await db.FileCounters.FindAsync([fileId], ct);
        return counter?.RefCount;
    }

    // One implicit transaction, which CockroachDB retries server-side on contention.
    private async Task<long> AddAsync(Guid fileId, long delta, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var updated = await db.Database
           .SqlQuery<long>($"""
                UPDATE "FileCounters"
                SET "RefCount" = "RefCount" + {delta}, "UpdatedAt" = now()
                WHERE "Id" = {fileId} AND "IsDeleted" = false
                RETURNING "RefCount" AS "Value"
                """)
           .ToListAsync(ct);

        if (updated.Count == 0)
            throw new KeyNotFoundException($"FileCounter not found for file {fileId}");

        return updated[0];
    }
}
