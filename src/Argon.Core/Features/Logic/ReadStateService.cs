namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.EF;
using Argon.Services;
using StackExchange.Redis;

public class ReadStateService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    [FromKeyedServices(RedisProfiles.Cache)] IRedisPoolConnections redis,
    ILogger<ReadStateService> logger) : IReadStateService
{
    private const int CacheDbId = 6;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(2);

    // Rows one transaction may write, which keeps it well inside CockroachDB's intent limits.
    public const int RowsPerStatement = 5000;

    // One hash tag for both keys, so a fill can be conditioned on the generation in a single slot.
    public static string GetCacheKey(Guid userId) => $"read_state:{{{userId}}}";

    // Bumped by every write; a fill from the database is only stored if it has not moved meanwhile.
    private static string GetGenerationKey(Guid userId) => $"read_state_gen:{{{userId}}}";
    /// <summary>
    /// One read state, as it sits in the hash: <c>messageId:mentions[:spaceId]</c>.
    /// </summary>
    /// <remarks>
    /// The space is here because leaving it out was a bug the client could see. A cache miss read the
    /// row and carried its <c>SpaceId</c> onto the wire; a hit rebuilt the entry with <c>null</c>,
    /// because the value had no room for it. So the field appeared on the first badge fetch after a
    /// cold cache and vanished for the two hours that entry lived — the same user, the same channel,
    /// two different answers.
    /// </remarks>
    private static string EncodeCacheValue(long msgId, int mentions, Guid? spaceId)
        => spaceId is null ? $"{msgId}:{mentions}" : $"{msgId}:{mentions}:{spaceId}";

    /// <summary>
    /// Reads a value back, in either shape.
    /// </summary>
    /// <remarks>
    /// Two fields is the old encoding and still turns up: entries written before this change live out
    /// their TTL, and a deploy does not clear the cache. Treating a short value as an error would make
    /// every user's badges fail for up to two hours after the rollout, which is a worse bug than the
    /// one being fixed. A missing space reads as unknown, which is what it was.
    /// </remarks>
    private static (long msgId, int mentions, Guid? spaceId) DecodeCacheValue(string value)
    {
        var parts = value.Split(':');

        return (long.Parse(parts[0]),
                int.Parse(parts[1]),
                parts.Length > 2 && Guid.TryParse(parts[2], out var spaceId) ? spaceId : null);
    }

    public async Task AckAsync(Guid userId, Guid channelId, Guid? spaceId, long messageId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        spaceId ??= await ctx.Channels
           .Where(c => c.Id == channelId)
           .Select(c => (Guid?)c.SpaceId)
           .FirstOrDefaultAsync(ct);

        // A stale ack moves nothing and leaves the cache alone.
        if (!await MoveMarkForwardAsync(ctx, userId, channelId, spaceId, messageId, ct))
            return;

        await UpdateCacheEntryAsync(userId, channelId, messageId, 0, spaceId);
    }

    private static async Task<bool> MoveMarkForwardAsync(ApplicationDbContext ctx, Guid userId, Guid channelId, Guid? spaceId,
        long messageId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var moved = await ctx.ChannelReadStates
               .Where(r => r.UserId == userId && r.ChannelId == channelId && r.LastReadMessageId < messageId)
               .ExecuteUpdateAsync(s => s
                   .SetProperty(r => r.LastReadMessageId, messageId)
                   .SetProperty(r => r.MentionCount, 0)
                   .SetProperty(r => r.SpaceId, r => r.SpaceId ?? spaceId)
                   .SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow), ct);

            if (moved > 0)
                return true;

            if (attempt > 0 || await ctx.ChannelReadStates.AnyAsync(r => r.UserId == userId && r.ChannelId == channelId, ct))
                return false;

            ctx.ChannelReadStates.Add(new ChannelReadStateEntity
            {
                UserId            = userId,
                ChannelId         = channelId,
                SpaceId           = spaceId,
                LastReadMessageId = messageId,
                MentionCount      = 0
            });

            try
            {
                await ctx.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException e) when (e.IsUniqueViolation())
            {
                // Another first ack for this channel got there first; move its mark instead.
                ctx.ChangeTracker.Clear();
            }
        }
    }

    public async Task IncrementMentionsAsync(Guid userId, Guid channelId, Guid? spaceId, int delta = 1, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        await AddMentionsAsync(ctx, channelId, spaceId, [userId], delta, ct);

        await InvalidateCacheAsync([userId]);
    }

    public async Task BatchIncrementMentionsAsync(Guid spaceId, Guid channelId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;

        var distinct = userIds.Distinct().ToArray();

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        foreach (var chunk in distinct.Chunk(RowsPerStatement))
            await AddMentionsAsync(ctx, channelId, spaceId, chunk, 1, ct);

        await InvalidateCacheAsync(distinct);

        logger.LogDebug("BatchIncrementMentions: {Count} users for channel {ChannelId}", distinct.Length, channelId);
    }

    /// <summary>
    /// Adds <paramref name="delta"/> mentions for each user, creating the rows they lack. The increment is a
    /// single UPDATE, so racing writers wait on the row lock instead of failing serialization.
    /// </summary>
    private static async Task AddMentionsAsync(ApplicationDbContext ctx, Guid channelId, Guid? spaceId, IReadOnlyCollection<Guid> userIds,
        int delta, CancellationToken ct)
    {
        var pending = userIds;

        while (pending.Count > 0)
        {
            ctx.ChangeTracker.Clear();

            var existing = await ctx.ChannelReadStates
               .Where(r => r.ChannelId == channelId && pending.Contains(r.UserId))
               .Select(r => r.UserId)
               .ToListAsync(ct);

            if (existing.Count > 0)
                await ctx.ChannelReadStates
                   .Where(r => r.ChannelId == channelId && existing.Contains(r.UserId))
                   .ExecuteUpdateAsync(s => s
                       .SetProperty(r => r.MentionCount, r => r.MentionCount + delta)
                       .SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow), ct);

            var missing = pending.Except(existing).ToList();

            if (missing.Count == 0)
                return;

            ctx.ChannelReadStates.AddRange(missing.Select(userId => new ChannelReadStateEntity
            {
                UserId            = userId,
                ChannelId         = channelId,
                SpaceId           = spaceId,
                LastReadMessageId = 0,
                MentionCount      = delta
            }));

            try
            {
                await ctx.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException e) when (e.IsUniqueViolation())
            {
                // Another first mention created some of these rows; count ours on them as an update.
                pending = missing;
            }
        }
    }

    public Task BumpEveryoneMentionsAsync(Guid spaceId, Guid channelId, Guid senderId, CancellationToken ct = default)
        => BumpEveryoneMentionsAsync(spaceId, channelId, senderId, RowsPerStatement, ct);

    /// <summary>
    /// The members are walked in <c>UserId</c> order, one transaction per slice of
    /// <paramref name="rowsPerStatement"/>, so no single transaction writes every member row of a large space.
    /// </summary>
    public async Task BumpEveryoneMentionsAsync(Guid spaceId, Guid channelId, Guid senderId, int rowsPerStatement, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var now   = DateTimeOffset.UtcNow;
        var after = Guid.Empty;

        while (true)
        {
            // Mirrors MuteSettingsService.FilterMutedUsersAsync and the SuppressEveryone check in
            // ChannelGrain.ProcessMentionsAsync.
            var slice = await ctx.UsersToServerRelations
               .Where(m => m.SpaceId == spaceId && m.UserId > after && m.UserId != senderId)
               .Where(m => !ctx.MuteSettings.Any(mu => mu.UserId == m.UserId
                                                     && (mu.TargetId == channelId || mu.TargetId == spaceId)
                                                     && mu.MuteLevel == MuteLevel.All
                                                     && (mu.MuteExpiresAt == null || mu.MuteExpiresAt > now)))
               .Where(m => !ctx.MuteSettings.Any(su => su.UserId == m.UserId
                                                     && su.SuppressEveryone
                                                     && (su.TargetId == spaceId || su.TargetId == channelId)))
               .OrderBy(m => m.UserId)
               .Select(m => m.UserId)
               .Take(rowsPerStatement)
               .ToListAsync(ct);

            if (slice.Count == 0)
                break;

            await AddMentionsAsync(ctx, channelId, spaceId, slice, 1, ct);

            if (slice.Count < rowsPerStatement)
                break;

            after = slice[^1];
        }

        // No per-user cache invalidation here: this path only runs for very large spaces. Those
        // read_state caches refresh on their 2h TTL; smaller spaces go through BatchIncrementMentionsAsync.
        logger.LogDebug("BumpEveryoneMentions for channel {ChannelId} in space {SpaceId}", channelId, spaceId);
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
                var (msgId, mentions, spaceId) = DecodeCacheValue(x.Value!);
                return new ReadStateEntry(Guid.Parse(x.Name.ToString()), spaceId, msgId, mentions);
            }).ToList();
        }

        var generationKey = GetGenerationKey(userId);
        var generation    = await cache.StringGetAsync(generationKey);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var states = await ctx.ChannelReadStates
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => new ReadStateEntry(x.ChannelId, x.SpaceId, x.LastReadMessageId, x.MentionCount))
            .ToListAsync(ct);

        if (states.Count > 0)
        {
            var entries = states.Select(s =>
                new HashEntry(s.ChannelId.ToString(), EncodeCacheValue(s.LastReadMessageId, s.MentionCount, s.SpaceId))
            ).ToArray();

            var tran = cache.CreateTransaction();
            tran.AddCondition(generation.IsNull
                ? Condition.KeyNotExists(generationKey)
                : Condition.StringEqual(generationKey, generation));
            _ = tran.HashSetAsync(cacheKey, entries);
            _ = tran.KeyExpireAsync(cacheKey, CacheExpiration);
            await tran.ExecuteAsync();
        }

        return states;
    }

    private async Task UpdateCacheEntryAsync(Guid userId, Guid channelId, long messageId, int mentionCount, Guid? spaceId)
    {
        try
        {
            await using var conn = redis.Rent();
            var cache = conn.GetDatabase(CacheDbId);
            var cacheKey = GetCacheKey(userId);

            await BumpGenerationAsync(cache, userId);

            // Only into a hash that is already whole: a lone field would read back as the full list.
            var tran = cache.CreateTransaction();
            tran.AddCondition(Condition.KeyExists(cacheKey));
            _ = tran.HashSetAsync(cacheKey, channelId.ToString(), EncodeCacheValue(messageId, mentionCount, spaceId));
            await tran.ExecuteAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update read state cache for user {UserId}", userId);
        }
    }

    private async Task InvalidateCacheAsync(IReadOnlyCollection<Guid> userIds)
    {
        try
        {
            await using var conn = redis.Rent();
            var cache = conn.GetDatabase(CacheDbId);

            var batch = cache.CreateBatch();
            var sent  = new List<Task>(userIds.Count * 3);
            foreach (var userId in userIds)
            {
                sent.Add(batch.StringIncrementAsync(GetGenerationKey(userId)));
                sent.Add(batch.KeyExpireAsync(GetGenerationKey(userId), CacheExpiration));
                sent.Add(batch.KeyDeleteAsync(GetCacheKey(userId)));
            }
            batch.Execute();
            await Task.WhenAll(sent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate read state cache for {Count} user(s)", userIds.Count);
        }
    }

    private static async Task BumpGenerationAsync(IDatabase cache, Guid userId)
    {
        await cache.StringIncrementAsync(GetGenerationKey(userId));
        await cache.KeyExpireAsync(GetGenerationKey(userId), CacheExpiration);
    }
}
