namespace Argon.Grains;

using Argon.Entities;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;

/// <inheritdoc cref="IIdentityDirectoryGrain"/>
[StatelessWorker]
public sealed class IdentityDirectoryGrain(IDbContextFactory<ApplicationDbContext> contextFactory)
    : Grain, IIdentityDirectoryGrain
{
    public async Task<UserBasicInfo?> GetUserBasicInfoAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.Users
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new UserBasicInfo(u.Id, u.Username, u.AvatarFileId))
           .FirstOrDefaultAsync(ct);
    }

    public async Task<Guid?> GetUserIdByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // NormalizedEmail, not Email: that is the column with the unique index on it, and comparing
        // the raw one is both case-sensitive and a sequential scan.
        var normalized = email.ToLowerInvariant();

        return await db.Users
           .AsNoTracking()
           .Where(u => u.NormalizedEmail == normalized)
           .Select(u => (Guid?)u.Id)
           .FirstOrDefaultAsync(ct);
    }

    public async Task<OperatorBasicInfo?> GetUserOperatorInfoAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.Operators
           .AsNoTracking()
           .Where(o => o.UserId == userId && !o.IsDeleted)
           .Select(o => new OperatorBasicInfo(
                o.Id,
                o.Email,
                o.DisplayName,
                o.Certificates.Any(c => c.RevokedAt == null && !c.IsDeleted),
                o.IsActive,
                o.IsSystemOperator))
           .FirstOrDefaultAsync(ct);
    }

    public async Task<OperatorAppAccessInfo?> GetOperatorAppAccessAsync(
        Guid operatorId, Guid appId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.OperatorAppAccess
           .AsNoTracking()
           .Where(a => a.OperatorId == operatorId && a.AppId == appId && a.IsActive)
           .Select(a => new OperatorAppAccessInfo(a.OperatorId, a.AppId, a.AllowedScopes, a.Claims, a.IsActive))
           .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> GetOperatorHasAnyAppAccessAsync(Guid operatorId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.OperatorAppAccess
           .AsNoTracking()
           .AnyAsync(a => a.OperatorId == operatorId, ct);
    }

    // Under the soft-delete filter, so an erased account answers "no".
    public async Task<bool> UserExistsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct);
    }

    public async Task<LockdownSnapshot> GetLockdownAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.Users
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new LockdownSnapshot(u.LockdownReason, u.LockDownExpiration))
           .FirstOrDefaultAsync(ct) ?? new LockdownSnapshot(LockdownReason.NONE, null);
    }

    public async Task<bool> CanReachAsync(Guid callerId, Guid targetId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await SocialReach.CanReachAsync(db, callerId, targetId, ct);
    }

    public async Task<string?> ResolveTenantInstanceAsync(string domain, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.TenantDirectory
           .AsNoTracking()
           .Where(t => t.Domain == domain && t.IsVerified)
           .Select(t => t.InstanceUrl)
           .FirstOrDefaultAsync(ct);
    }
}
