namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Services;
using StackExchange.Redis;

public class MuteSettingsService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IRedisPoolConnections redis,
    ILogger<MuteSettingsService> logger) : IMuteSettingsService
{
    private const int CacheDbId = 6;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(1);

    private static string GetCacheKey(Guid userId) => $"mute:{userId}";

    public async Task MuteAsync(Guid userId, Guid targetId, MuteTargetType targetType, MuteLevel muteLevel,
        bool suppressEveryone = false, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var existing = await ctx.MuteSettings
            .FirstOrDefaultAsync(x => x.UserId == userId && x.TargetId == targetId, ct);

        if (existing is null)
        {
            ctx.MuteSettings.Add(new MuteSettingsEntity
            {
                UserId           = userId,
                TargetId         = targetId,
                TargetType       = targetType,
                MuteLevel        = muteLevel,
                SuppressEveryone = suppressEveryone,
                MuteExpiresAt    = expiresAt,
                CreatedAt        = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.MuteLevel        = muteLevel;
            existing.SuppressEveryone = suppressEveryone;
            existing.MuteExpiresAt    = expiresAt;
        }

        await ctx.SaveChangesAsync(ct);
        await InvalidateCacheAsync(userId);
    }

    public async Task UnmuteAsync(Guid userId, Guid targetId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        await ctx.MuteSettings
            .Where(x => x.UserId == userId && x.TargetId == targetId)
            .ExecuteDeleteAsync(ct);

        await InvalidateCacheAsync(userId);
    }

    public async Task<List<MuteSettingsEntity>> GetMuteSettingsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var settings = await ctx.MuteSettings
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        return settings.Where(s => s.MuteExpiresAt is null || s.MuteExpiresAt > now).ToList();
    }

    public async Task<bool> IsMutedAsync(Guid userId, Guid targetId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var setting = await ctx.MuteSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.TargetId == targetId, ct);

        if (setting is null) return false;
        if (setting.MuteExpiresAt.HasValue && setting.MuteExpiresAt <= DateTimeOffset.UtcNow) return false;

        return setting.MuteLevel == MuteLevel.All;
    }

    public async Task<HashSet<Guid>> FilterMutedUsersAsync(Guid channelId, Guid spaceId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var mutedUsers = await ctx.MuteSettings
            .AsNoTracking()
            .Where(x => userIds.Contains(x.UserId)
                && (x.TargetId == channelId || x.TargetId == spaceId)
                && x.MuteLevel == MuteLevel.All
                && (x.MuteExpiresAt == null || x.MuteExpiresAt > now))
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);

        return mutedUsers.ToHashSet();
    }

    private async Task InvalidateCacheAsync(Guid userId)
    {
        try
        {
            await using var conn = redis.Rent();
            var cache = conn.GetDatabase(CacheDbId);
            await cache.KeyDeleteAsync(GetCacheKey(userId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate mute cache for user {UserId}", userId);
        }
    }
}
