namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Services;
using StackExchange.Redis;

public class ReadStateService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IRedisPoolConnections redis,
    ILogger<ReadStateService> logger) : IReadStateService
{
    private const int CacheDbId = 6;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(2);

    private static string GetCacheKey(Guid userId) => $"read_state:{userId}";
    private static string EncodeCacheValue(long msgId, int mentions) => $"{msgId}:{mentions}";

    private static (long msgId, int mentions) DecodeCacheValue(string value)
    {
        var sep = value.IndexOf(':');
        return (long.Parse(value.AsSpan(0, sep)), int.Parse(value.AsSpan(sep + 1)));
    }

    public async Task AckAsync(Guid userId, Guid channelId, Guid? spaceId, long messageId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        if (spaceId is null)
        {
            spaceId = await ctx.Channels
                .Where(c => c.Id == channelId)
                .Select(c => (Guid?)c.SpaceId)
                .FirstOrDefaultAsync(ct);
        }

        var existing = await ctx.ChannelReadStates
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ChannelId == channelId, ct);

        if (existing is null)
        {
            ctx.ChannelReadStates.Add(new ChannelReadStateEntity
            {
                UserId            = userId,
                ChannelId         = channelId,
                SpaceId           = spaceId,
                LastReadMessageId = messageId,
                MentionCount      = 0,
                UpdatedAt         = DateTimeOffset.UtcNow
            });
        }
        else
        {
            if (messageId <= existing.LastReadMessageId)
                return;

            existing.LastReadMessageId = messageId;
            existing.MentionCount      = 0;
            existing.UpdatedAt         = DateTimeOffset.UtcNow;

            if (existing.SpaceId is null && spaceId is not null)
                existing.SpaceId = spaceId;
        }

        await ctx.SaveChangesAsync(ct);
        await UpdateCacheEntryAsync(userId, channelId, messageId, 0);
    }

    public async Task IncrementMentionsAsync(Guid userId, Guid channelId, Guid? spaceId, int delta = 1, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var existing = await ctx.ChannelReadStates
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ChannelId == channelId, ct);

        if (existing is null)
        {
            existing = new ChannelReadStateEntity
            {
                UserId            = userId,
                ChannelId         = channelId,
                SpaceId           = spaceId,
                LastReadMessageId = 0,
                MentionCount      = delta,
                UpdatedAt         = DateTimeOffset.UtcNow
            };
            ctx.ChannelReadStates.Add(existing);
        }
        else
        {
            existing.MentionCount += delta;
            existing.UpdatedAt    = DateTimeOffset.UtcNow;
        }

        await ctx.SaveChangesAsync(ct);
        await InvalidateCacheAsync(userId);
    }

    public async Task BatchIncrementMentionsAsync(Guid spaceId, Guid channelId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;

        const int batchSize = 500;

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        for (var i = 0; i < userIds.Count; i += batchSize)
        {
            var batch = userIds.Skip(i).Take(batchSize).ToList();

            var existingUserIds = await ctx.ChannelReadStates
                .Where(x => x.ChannelId == channelId && batch.Contains(x.UserId))
                .Select(x => x.UserId)
                .ToListAsync(ct);

            await ctx.ChannelReadStates
                .Where(x => x.ChannelId == channelId && batch.Contains(x.UserId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.MentionCount, x => x.MentionCount + 1)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);

            var newUserIds = batch.Except(existingUserIds).ToList();
            if (newUserIds.Count > 0)
            {
                var newEntities = newUserIds.Select(uid => new ChannelReadStateEntity
                {
                    UserId            = uid,
                    ChannelId         = channelId,
                    SpaceId           = spaceId,
                    LastReadMessageId = 0,
                    MentionCount      = 1,
                    UpdatedAt         = DateTimeOffset.UtcNow
                });

                ctx.ChannelReadStates.AddRange(newEntities);
                await ctx.SaveChangesAsync(ct);
            }
        }

        await using var conn = redis.Rent();
        var cache = conn.GetDatabase(CacheDbId);
        var keys = userIds.Select(uid => (RedisKey)GetCacheKey(uid)).ToArray();
        await cache.KeyDeleteAsync(keys);

        logger.LogDebug("BatchIncrementMentions: {Count} users for channel {ChannelId}", userIds.Count, channelId);
    }

    public async Task BumpEveryoneMentionsAsync(Guid spaceId, Guid channelId, Guid senderId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);

            // Single set-based INSERT..SELECT..ON CONFLICT DO UPDATE. Member enumeration plus the
            // mute(All)/suppress-everyone exclusion all run in SQL, so the full member list is never
            // materialized in the silo heap and there are no per-member writes. Semantics mirror
            // IncrementMentionsAsync exactly:
            //   existing row -> MentionCount += 1, UpdatedAt = now
            //   missing row  -> LastReadMessageId = 0, MentionCount = 1, UpdatedAt = now
            // The mute/suppress predicates mirror MuteSettingsService.FilterMutedUsersAsync and the
            // SuppressEveryone query in ChannelGrain.ProcessMentionsAsync. IsDeleted is filtered
            // explicitly because raw SQL bypasses the global soft-delete query filter.
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO ""ChannelReadStates"" (""UserId"", ""ChannelId"", ""SpaceId"", ""LastReadMessageId"", ""MentionCount"", ""UpdatedAt"")
SELECT m.""UserId"", {channelId}, {spaceId}, 0, 1, {now}
FROM ""UsersToServerRelations"" m
WHERE m.""SpaceId"" = {spaceId}
  AND m.""IsDeleted"" = false
  AND m.""UserId"" <> {senderId}
  AND NOT EXISTS (
      SELECT 1 FROM ""MuteSettings"" mu
      WHERE mu.""UserId"" = m.""UserId""
        AND (mu.""TargetId"" = {channelId} OR mu.""TargetId"" = {spaceId})
        AND mu.""MuteLevel"" = {(int)MuteLevel.All}
        AND (mu.""MuteExpiresAt"" IS NULL OR mu.""MuteExpiresAt"" > {now}))
  AND NOT EXISTS (
      SELECT 1 FROM ""MuteSettings"" su
      WHERE su.""UserId"" = m.""UserId""
        AND su.""SuppressEveryone"" = true
        AND (su.""TargetId"" = {spaceId} OR su.""TargetId"" = {channelId}))
ON CONFLICT (""UserId"", ""ChannelId"")
DO UPDATE SET ""MentionCount"" = ""ChannelReadStates"".""MentionCount"" + 1,
              ""UpdatedAt"" = {now}", ct);

            await tx.CommitAsync(ct);
        });

        // No per-user cache invalidation here: this path only runs for very large spaces where
        // enumerating affected users would defeat the heap-free goal. Those read_state caches
        // refresh on their 2h TTL. Spaces below the inline cap keep immediate invalidation via
        // BatchIncrementMentionsAsync (see ChannelGrain.ProcessMentionsAsync).
        logger.LogDebug("BumpEveryoneMentions (set-based) for channel {ChannelId} in space {SpaceId}", channelId, spaceId);
    }

    public async Task<List<ReadStateEntry>> GetReadStatesForSpaceAsync(Guid userId, Guid spaceId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        return await ctx.ChannelReadStates
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.SpaceId == spaceId)
            .Select(x => new ReadStateEntry(x.ChannelId, x.SpaceId, x.LastReadMessageId, x.MentionCount))
            .ToListAsync(ct);
    }

    public async Task<List<ReadStateEntry>> GetAllReadStatesAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetCacheKey(userId);

        await using var conn = redis.Rent();
        var cache = conn.GetDatabase(CacheDbId);
        var cached = await cache.HashGetAllAsync(cacheKey);

        if (cached.Length > 0)
        {
            return cached.Select(x =>
            {
                var (msgId, mentions) = DecodeCacheValue(x.Value!);
                return new ReadStateEntry(Guid.Parse(x.Name.ToString()), null, msgId, mentions);
            }).ToList();
        }

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var states = await ctx.ChannelReadStates
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => new ReadStateEntry(x.ChannelId, x.SpaceId, x.LastReadMessageId, x.MentionCount))
            .ToListAsync(ct);

        if (states.Count > 0)
        {
            var entries = states.Select(s =>
                new HashEntry(s.ChannelId.ToString(), EncodeCacheValue(s.LastReadMessageId, s.MentionCount))
            ).ToArray();

            await cache.HashSetAsync(cacheKey, entries);
            await cache.KeyExpireAsync(cacheKey, CacheExpiration);
        }

        return states;
    }

    private async Task UpdateCacheEntryAsync(Guid userId, Guid channelId, long messageId, int mentionCount)
    {
        try
        {
            await using var conn = redis.Rent();
            var cache = conn.GetDatabase(CacheDbId);
            var cacheKey = GetCacheKey(userId);
            await cache.HashSetAsync(cacheKey, channelId.ToString(), EncodeCacheValue(messageId, mentionCount));
            await cache.KeyExpireAsync(cacheKey, CacheExpiration);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update read state cache for user {UserId}", userId);
        }
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
            logger.LogWarning(ex, "Failed to invalidate read state cache for user {UserId}", userId);
        }
    }
}
