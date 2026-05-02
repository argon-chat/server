namespace Argon.Features.Storage;

public interface IReferenceCountService
{
    Task<long> IncrementAsync(Guid fileId, long increment = 1, CancellationToken ct = default);
    Task<long> DecrementAsync(Guid fileId, long decrement = 1, CancellationToken ct = default);
    Task<long?> GetRefCountAsync(Guid fileId, CancellationToken ct = default);
}

public class ReferenceCountService(IDbContextFactory<ApplicationDbContext> dbFactory) : IReferenceCountService
{
    public async Task<long> IncrementAsync(Guid fileId, long increment = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var counter = await db.FileCounters
            .FromSqlRaw("SELECT * FROM \"FileCounters\" WHERE \"Id\" = {0} FOR UPDATE", fileId)
            .FirstOrDefaultAsync(ct);

        if (counter is null)
            throw new KeyNotFoundException($"FileCounter not found for file {fileId}");

        counter.RefCount += increment;
        counter.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return counter.RefCount;
    }

    public async Task<long> DecrementAsync(Guid fileId, long decrement = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var counter = await db.FileCounters
            .FromSqlRaw("SELECT * FROM \"FileCounters\" WHERE \"Id\" = {0} FOR UPDATE", fileId)
            .FirstOrDefaultAsync(ct);

        if (counter is null)
            throw new KeyNotFoundException($"FileCounter not found for file {fileId}");

        counter.RefCount -= decrement;
        counter.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return counter.RefCount;
    }

    public async Task<long?> GetRefCountAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counter = await db.FileCounters.FindAsync([fileId], ct);
        return counter?.RefCount;
    }
}
