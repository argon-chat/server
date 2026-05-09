namespace Argon.Features.Admin;

using Argon.Entities;

public interface IOperatorAuditService
{
    Task LogAsync(string action, string? targetType = null, string? targetId = null, string? details = null);
    Task<(List<OperatorAuditEntity> Entries, int TotalCount)> QueryAsync(
        Guid? operatorId, string? action, string? targetId,
        DateTimeOffset? fromDate, DateTimeOffset? toDate,
        int page, int pageSize, CancellationToken ct = default);
    Task<List<OperatorAuditEntity>> GetRecentByOperatorAsync(Guid operatorId, int count = 20, CancellationToken ct = default);
}

public sealed class OperatorAuditService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<OperatorAuditService> logger)
    : IOperatorAuditService
{
    public async Task LogAsync(string action, string? targetType, string? targetId, string? details)
    {
        var caller = OperatorRequestContext.CurrentOrDefault;
        if (caller is null)
        {
            logger.LogWarning("Audit log attempted without operator context for action={Action}", action);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        db.OperatorAuditLog.Add(new OperatorAuditEntity
        {
            OperatorId    = caller.OperatorId,
            OperatorEmail = caller.Email,
            Action        = action,
            TargetType    = targetType,
            TargetId      = targetId,
            Details       = details
        });

        await db.SaveChangesAsync();
    }

    public async Task<(List<OperatorAuditEntity> Entries, int TotalCount)> QueryAsync(
        Guid? operatorId, string? action, string? targetId,
        DateTimeOffset? fromDate, DateTimeOffset? toDate,
        int page, int pageSize, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.OperatorAuditLog.AsQueryable();

        if (operatorId.HasValue)
            query = query.Where(a => a.OperatorId == operatorId.Value);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(targetId))
            query = query.Where(a => a.TargetId == targetId);
        if (fromDate.HasValue)
            query = query.Where(a => a.CreatedAt >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(a => a.CreatedAt <= toDate.Value);

        var totalCount = await query.CountAsync(ct);

        var entries = await query
           .OrderByDescending(a => a.CreatedAt)
           .Skip(page * pageSize)
           .Take(pageSize)
           .ToListAsync(ct);

        return (entries, totalCount);
    }

    public async Task<List<OperatorAuditEntity>> GetRecentByOperatorAsync(Guid operatorId, int count, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.OperatorAuditLog
           .Where(a => a.OperatorId == operatorId)
           .OrderByDescending(a => a.CreatedAt)
           .Take(count)
           .ToListAsync(ct);
    }
}
