namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Services;
using StackExchange.Redis;

public class ReadStateService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    [FromKeyedServices(RedisProfiles.Cache)] IRedisPoolConnections redis,
    ILogger<ReadStateService> logger) : IReadStateService
{
    private const int CacheDbId = 6;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(2);

    // Rows one upsert may write, which keeps a statement well inside CockroachDB's intent limits.
    public const int RowsPerStatement = 5000;

    private static readonly Guid LastUuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private static string GetCacheKey(Guid userId) => $"read_state:{userId}";
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

        // An ack that does not move the mark forward updates nothing and returns no row.
        var acked = await ctx.Database.SqlQuery<Guid?>($"""
            INSERT INTO "ChannelReadStates" ("UserId", "ChannelId", "SpaceId", "LastReadMessageId", "MentionCount", "UpdatedAt")
            VALUES ({userId}, {channelId},
                    COALESCE(CAST({spaceId} AS uuid),
                             (SELECT c."SpaceId" FROM "Channels" c WHERE c."Id" = {channelId} AND c."IsDeleted" = false)),
                    {messageId}, 0, now())
            ON CONFLICT ("UserId", "ChannelId") DO UPDATE
            SET "LastReadMessageId" = EXCLUDED."LastReadMessageId",
                "MentionCount"      = 0,
                "UpdatedAt"         = EXCLUDED."UpdatedAt",
                "SpaceId"           = COALESCE("ChannelReadStates"."SpaceId", EXCLUDED."SpaceId")
            WHERE "ChannelReadStates"."LastReadMessageId" < EXCLUDED."LastReadMessageId"
            RETURNING "SpaceId" AS "Value"
            """).ToListAsync(ct);

        if (acked.Count == 0)
            return;

        await UpdateCacheEntryAsync(userId, channelId, messageId, 0, acked[0]);
    }

    public async Task IncrementMentionsAsync(Guid userId, Guid channelId, Guid? spaceId, int delta = 1, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ChannelReadStates" ("UserId", "ChannelId", "SpaceId", "LastReadMessageId", "MentionCount", "UpdatedAt")
            VALUES ({userId}, {channelId}, CAST({spaceId} AS uuid), 0, {delta}, now())
            ON CONFLICT ("UserId", "ChannelId") DO UPDATE
            SET "MentionCount" = "ChannelReadStates"."MentionCount" + EXCLUDED."MentionCount",
                "UpdatedAt"    = EXCLUDED."UpdatedAt"
            """, ct);

        await InvalidateCacheAsync(userId);
    }

    public async Task BatchIncrementMentionsAsync(Guid spaceId, Guid channelId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;

        // Distinct because one upsert statement may not touch the same row twice.
        var distinct = userIds.Distinct().ToArray();

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        foreach (var chunk in distinct.Chunk(RowsPerStatement))
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ChannelReadStates" ("UserId", "ChannelId", "SpaceId", "LastReadMessageId", "MentionCount", "UpdatedAt")
                SELECT t.id, {channelId}, {spaceId}, 0, 1, now()
                FROM unnest({chunk}) AS t(id)
                ON CONFLICT ("UserId", "ChannelId") DO UPDATE
                SET "MentionCount" = "ChannelReadStates"."MentionCount" + 1,
                    "UpdatedAt"    = EXCLUDED."UpdatedAt"
                """, ct);
        }

        await using var conn = redis.Rent();
        var cache = conn.GetDatabase(CacheDbId);
        var keys = distinct.Select(uid => (RedisKey)GetCacheKey(uid)).ToArray();
        await cache.KeyDeleteAsync(keys);

        logger.LogDebug("BatchIncrementMentions: {Count} users for channel {ChannelId}", distinct.Length, channelId);
    }

    public Task BumpEveryoneMentionsAsync(Guid spaceId, Guid channelId, Guid senderId, CancellationToken ct = default)
        => BumpEveryoneMentionsAsync(spaceId, channelId, senderId, RowsPerStatement, ct);

    /// <summary>
    /// The members are walked in <c>UserId</c> order, one upsert per slice of
    /// <paramref name="rowsPerStatement"/>, so no single transaction writes every member row of a large space.
    /// </summary>
    public async Task BumpEveryoneMentionsAsync(Guid spaceId, Guid channelId, Guid senderId, int rowsPerStatement, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        // The nil uuid sorts below every member id and the all-ones uuid above every one.
        var after = Guid.Empty;

        while (true)
        {
            var bound = await ctx.Database.SqlQuery<Guid>($"""
                SELECT m."UserId" AS "Value"
                FROM "UsersToServerRelations" m
                WHERE m."SpaceId" = {spaceId}
                  AND m."IsDeleted" = false
                  AND m."UserId" > {after}
                ORDER BY m."UserId"
                OFFSET {rowsPerStatement - 1L} LIMIT 1
                """).ToListAsync(ct);

            var last = bound.Count == 0;
            var upTo = last ? LastUuid : bound[0];

            // Member enumeration plus the mute(All)/suppress-everyone exclusion all run in SQL, so the
            // member list is never materialized in the silo heap. Semantics mirror IncrementMentionsAsync:
            //   existing row -> MentionCount += 1, UpdatedAt = now
            //   missing row  -> LastReadMessageId = 0, MentionCount = 1, UpdatedAt = now
            // The mute/suppress predicates mirror MuteSettingsService.FilterMutedUsersAsync and the
            // SuppressEveryone query in ChannelGrain.ProcessMentionsAsync. IsDeleted is filtered
            // explicitly because raw SQL bypasses the global soft-delete query filter.
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO ""ChannelReadStates"" (""UserId"", ""ChannelId"", ""SpaceId"", ""LastReadMessageId"", ""MentionCount"", ""UpdatedAt"")
SELECT m.""UserId"", {channelId}, {spaceId}, 0, 1, now()
FROM ""UsersToServerRelations"" m
WHERE m.""SpaceId"" = {spaceId}
  AND m.""IsDeleted"" = false
  AND m.""UserId"" > {after}
  AND m.""UserId"" <= {upTo}
  AND m.""UserId"" <> {senderId}
  AND NOT EXISTS (
      SELECT 1 FROM ""MuteSettings"" mu
      WHERE mu.""UserId"" = m.""UserId""
        AND (mu.""TargetId"" = {channelId} OR mu.""TargetId"" = {spaceId})
        AND mu.""MuteLevel"" = {(int)MuteLevel.All}
        AND (mu.""MuteExpiresAt"" IS NULL OR mu.""MuteExpiresAt"" > now()))
  AND NOT EXISTS (
      SELECT 1 FROM ""MuteSettings"" su
      WHERE su.""UserId"" = m.""UserId""
        AND su.""SuppressEveryone"" = true
        AND (su.""TargetId"" = {spaceId} OR su.""TargetId"" = {channelId}))
ON CONFLICT (""UserId"", ""ChannelId"")
DO UPDATE SET ""MentionCount"" = ""ChannelReadStates"".""MentionCount"" + 1,
              ""UpdatedAt"" = EXCLUDED.""UpdatedAt""", ct);

            if (last)
                break;

            after = upTo;
        }

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
                var (msgId, mentions, spaceId) = DecodeCacheValue(x.Value!);
                return new ReadStateEntry(Guid.Parse(x.Name.ToString()), spaceId, msgId, mentions);
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
                new HashEntry(s.ChannelId.ToString(), EncodeCacheValue(s.LastReadMessageId, s.MentionCount, s.SpaceId))
            ).ToArray();

            await cache.HashSetAsync(cacheKey, entries);
            await cache.KeyExpireAsync(cacheKey, CacheExpiration);
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
            await cache.HashSetAsync(cacheKey, channelId.ToString(), EncodeCacheValue(messageId, mentionCount, spaceId));
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
