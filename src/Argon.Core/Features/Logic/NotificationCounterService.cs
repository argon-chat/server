namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Services;
using ArgonContracts;
using ion.runtime;
using StackExchange.Redis;

public class NotificationCounterService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IRedisPoolConnections redis,
    ILogger<NotificationCounterService> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier) : INotificationCounterService
{
    private const int CacheDbId = 5;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(15);

    private static string GetCacheKey(Guid userId) => $"notif_counters:{userId}";
    private static string GetCounterCacheKey(Guid userId, string counterType) => $"notif_counter:{userId}:{counterType}";

    public async Task<NotificationCounters> GetAllCountersAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetCacheKey(userId);

        await using var conn = redis.Rent();
        var cache = conn.GetDatabase(CacheDbId);

        var cachedData = await cache.HashGetAllAsync(cacheKey);

        if (cachedData.Length > 0)
        {
            var dict = cachedData.ToDictionary(x => x.Name.ToString(), x => (long)x.Value);

            return new NotificationCounters(
                UnreadInventoryItems: dict.GetValueOrDefault(NotificationCounterType.UnreadInventoryItems, 0),
                PendingFriendRequests: dict.GetValueOrDefault(NotificationCounterType.PendingFriendRequests, 0),
                UnreadDirectMessages: dict.GetValueOrDefault(NotificationCounterType.UnreadDirectMessages, 0)
            );
        }

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var counters = await ctx.Set<NotificationCounterEntity>()
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);

        var result = new NotificationCounters(
            UnreadInventoryItems: counters.FirstOrDefault(c => c.CounterType == NotificationCounterType.UnreadInventoryItems)?.Count ?? 0,
            PendingFriendRequests: counters.FirstOrDefault(c => c.CounterType == NotificationCounterType.PendingFriendRequests)?.Count ?? 0,
            UnreadDirectMessages: counters.FirstOrDefault(c => c.CounterType == NotificationCounterType.UnreadDirectMessages)?.Count ?? 0
        );

        var hashEntries = new[]
        {
            new HashEntry(NotificationCounterType.UnreadInventoryItems, result.UnreadInventoryItems),
            new HashEntry(NotificationCounterType.PendingFriendRequests, result.PendingFriendRequests),
            new HashEntry(NotificationCounterType.UnreadDirectMessages, result.UnreadDirectMessages)
        };

        await cache.HashSetAsync(cacheKey, hashEntries);
        await cache.KeyExpireAsync(cacheKey, CacheExpiration);

        return result;
    }

    public async Task<long> GetCounterAsync(Guid userId, string counterType, CancellationToken ct = default)
    {
        var counterCacheKey = GetCounterCacheKey(userId, counterType);

        await using var conn = redis.Rent();
        var cache = conn.GetDatabase(CacheDbId);

        var cachedValue = await cache.StringGetAsync(counterCacheKey);
        if (cachedValue.HasValue)
        {
            return (long)cachedValue;
        }

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var counter = await ctx.Set<NotificationCounterEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.CounterType == counterType, ct);

        var value = counter?.Count ?? 0;

        await cache.StringSetAsync(counterCacheKey, value, CacheExpiration);

        return value;
    }

    public async Task IncrementAsync(Guid userId, string counterType, long delta = 1, CancellationToken ct = default)
    {
        if (delta <= 0)
        {
            logger.LogWarning("IncrementAsync called with non-positive delta {Delta}", delta);
            return;
        }

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var strategy = ctx.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(ct);

            try
            {
                var counter = await ctx.Set<NotificationCounterEntity>()
                    .FirstOrDefaultAsync(x => x.UserId == userId && x.CounterType == counterType, ct);

                if (counter is null)
                {
                    counter = new NotificationCounterEntity
                    {
                        UserId = userId,
                        CounterType = counterType,
                        Count = delta,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    ctx.Set<NotificationCounterEntity>().Add(counter);
                }
                else
                {
                    counter.Count += delta;
                    counter.UpdatedAt = DateTimeOffset.UtcNow;
                    ctx.Set<NotificationCounterEntity>().Update(counter);
                }

                await ctx.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                await InvalidateCacheAsync(userId, ct);

                logger.LogDebug("Incremented {CounterType} for user {UserId} by {Delta}, new count: {Count}",
                    counterType, userId, delta, counter.Count);

                await EmitCounterUpdateEventAsync(userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to increment {CounterType} for user {UserId}", counterType, userId);
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async Task DecrementAsync(Guid userId, string counterType, long delta = 1, CancellationToken ct = default)
    {
        if (delta <= 0)
        {
            logger.LogWarning("DecrementAsync called with non-positive delta {Delta}", delta);
            return;
        }

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var strategy = ctx.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(ct);

            try
            {
                var counter = await ctx.Set<NotificationCounterEntity>()
                    .FirstOrDefaultAsync(x => x.UserId == userId && x.CounterType == counterType, ct);

                if (counter is null)
                {
                    logger.LogDebug("No counter to decrement for {CounterType} and user {UserId}", counterType, userId);
                    await transaction.CommitAsync(ct);
                    return;
                }

                counter.Count = Math.Max(0, counter.Count - delta);
                counter.UpdatedAt = DateTimeOffset.UtcNow;
                ctx.Set<NotificationCounterEntity>().Update(counter);

                await ctx.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                await InvalidateCacheAsync(userId, ct);

                logger.LogDebug("Decremented {CounterType} for user {UserId} by {Delta}, new count: {Count}",
                    counterType, userId, delta, counter.Count);

                await EmitCounterUpdateEventAsync(userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to decrement {CounterType} for user {UserId}", counterType, userId);
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async Task SetAsync(Guid userId, string counterType, long value, CancellationToken ct = default)
    {
        if (value < 0)
        {
            logger.LogWarning("SetAsync called with negative value {Value}", value);
            value = 0;
        }

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var strategy = ctx.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(ct);

            try
            {
                var counter = await ctx.Set<NotificationCounterEntity>()
                    .FirstOrDefaultAsync(x => x.UserId == userId && x.CounterType == counterType, ct);

                if (counter is null)
                {
                    counter = new NotificationCounterEntity
                    {
                        UserId = userId,
                        CounterType = counterType,
                        Count = value,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    ctx.Set<NotificationCounterEntity>().Add(counter);
                }
                else
                {
                    counter.Count = value;
                    counter.UpdatedAt = DateTimeOffset.UtcNow;
                    ctx.Set<NotificationCounterEntity>().Update(counter);
                }

                await ctx.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                await InvalidateCacheAsync(userId, ct);

                logger.LogDebug("Set {CounterType} for user {UserId} to {Value}", counterType, userId, value);

                await EmitCounterUpdateEventAsync(userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to set {CounterType} for user {UserId}", counterType, userId);
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async Task ResetAsync(Guid userId, string counterType, CancellationToken ct = default)
        => await SetAsync(userId, counterType, 0, ct);

    public async Task InvalidateCacheAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = redis.Rent();
        var cache = conn.GetDatabase(CacheDbId);

        var cacheKey = GetCacheKey(userId);
        await cache.KeyDeleteAsync(cacheKey);

        var counterKeys = new[]
        {
            GetCounterCacheKey(userId, NotificationCounterType.UnreadInventoryItems),
            GetCounterCacheKey(userId, NotificationCounterType.PendingFriendRequests),
            GetCounterCacheKey(userId, NotificationCounterType.UnreadDirectMessages)
        };

        await cache.KeyDeleteAsync(counterKeys.Select(k => (RedisKey)k).ToArray());

        logger.LogDebug("Invalidated notification cache for user {UserId}", userId);
    }

    private async Task EmitCounterUpdateEventAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var sessions = await sessionDiscovery.GetUserSessionsAsync(userId, ct);

            if (sessions.Count == 0)
            {
                logger.LogDebug("No active sessions for user {UserId}, skipping counter update event", userId);
                return;
            }

            var counters = await GetAllCountersAsync(userId, ct);

            var counterArray = new[]
            {
                new NotificationCounterKv(NotificationCounterType.UnreadInventoryItems, counters.UnreadInventoryItems),
                new NotificationCounterKv(NotificationCounterType.PendingFriendRequests, counters.PendingFriendRequests),
                new NotificationCounterKv(NotificationCounterType.UnreadDirectMessages, counters.UnreadDirectMessages)
            };

            var updateEvent = new UpdatedNotificationCounters(userId, new IonArray<NotificationCounterKv>(counterArray));

            await notifier.NotifySessionsAsync(sessions, updateEvent, ct);

            logger.LogDebug("Emitted UpdatedNotificationCounters event for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to emit counter update event for user {UserId}", userId);
        }
    }
}
