namespace Argon.Api.Features.CoreLogic.Messages;

using Argon.Core.Services;
using Services;
using SnowflakeId.Core;

public static class MessagesLayoutExtensions
{
    public static void AddMessagesLayout(this WebApplicationBuilder builder)
    {
        // Singleton, both of them: the write buffer is one queue for the process, and it needs the
        // dedup service from outside any request scope. Its only dependency is the cache, which is
        // already a singleton.
        builder.Services.AddSingleton<MessageDeduplicationService>();
        builder.Services.AddSingleton<MessageWriteBuffer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MessageWriteBuffer>());

        builder.Services.AddScoped<IMessagesLayout, PgSqlMessagesLayout>();
        builder.Services.AddScoped<ISystemMessageService, SystemMessageService>();
        builder.Services.AddScoped<IConversationService, ConversationService>();
    }
}

public interface IMessagesLayout
{
    Task<List<ArgonMessageEntity>> QueryMessages(
        Guid spaceId,
        Guid channelId,
        long? fromMessageId = null,
        int limit = 50, CancellationToken ct = default);

    /// <summary>Up to <paramref name="older"/> messages at or before the message and <paramref name="newer"/> after it.</summary>
    Task<MessageWindowEntity> QueryMessagesAround(Guid spaceId, Guid channelId, long messageId, int older, int newer, CancellationToken ct = default);

    Task<long?> CheckDuplicationAsync(ArgonMessageEntity msg, long randomId, CancellationToken ct = default);

    Task<long> ExecuteInsertMessage(ArgonMessageEntity msg, long randomId, CancellationToken ct = default);
}

public class MessageDeduplicationService(IArgonCacheDatabase cache)
{
    private static string GetDedupKey(Guid spaceId, Guid channelId, long randomId)
        => $"dedup:{spaceId}:{channelId}:{randomId}";

    public async Task<long?> CheckDuplicationAsync(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
    {
        var key   = GetDedupKey(msg.SpaceId, msg.ChannelId, randomId);
        var value = await cache.StringGetAsync(key, ct);
        if (string.IsNullOrEmpty(value))
            return null;

        return long.Parse(value);
    }

    public async Task SetDeduplicationAsync(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
    {
        var key = GetDedupKey(msg.SpaceId, msg.ChannelId, randomId);
        await cache.StringSetAsync(key, msg.MessageId.ToString(), TimeSpan.FromMinutes(2), ct);
    }
}

public class PgSqlMessagesLayout(
    IDbContextFactory<ApplicationDbContext> context,
    MessageDeduplicationService deduplication,
    MessageWriteBuffer writes,
    ISnowflakeService snowflake,
    ILogger<PgSqlMessagesLayout> logger) : IMessagesLayout
{
    public const int MaxQueryLimit = 100;

    public async Task<List<ArgonMessageEntity>> QueryMessages(Guid spaceId, Guid channelId, long? fromMessageId = null, int limit = 50,
        CancellationToken ct = default)
    {
        // The limit arrives from the client unchecked.
        limit = Math.Clamp(limit, 1, MaxQueryLimit);

        await using var ctx = await context.CreateDbContextAsync(ct);

        // IsDeleted is filtered here rather than by the global query filter: that filter only covers
        // ArgonEntity, and ArgonMessageEntity descends from ArgonEntityNoKey (composite key), so a
        // deleted message would otherwise keep being served.
        if (fromMessageId.HasValue)
            return await ctx.Messages
               .AsNoTracking()
               .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId && m.MessageId < fromMessageId.Value && !m.IsDeleted)
               .OrderByDescending(m => m.MessageId)
               .Take(limit)
               .ToListAsync(cancellationToken: ct);

        return await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId && !m.IsDeleted)
           .OrderByDescending(m => m.MessageId)
           .Take(limit)
           .ToListAsync(cancellationToken: ct);
    }

    public async Task<MessageWindowEntity> QueryMessagesAround(Guid spaceId, Guid channelId, long messageId, int older, int newer,
        CancellationToken ct = default)
    {
        older = Math.Clamp(older, 0, MaxQueryLimit);
        newer = Math.Clamp(newer, 0, MaxQueryLimit);

        await using var ctx = await context.CreateDbContextAsync(ct);

        // One more on each side than asked: whether it comes back says if more lies beyond.
        var before = await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId && m.MessageId <= messageId && !m.IsDeleted)
           .OrderByDescending(m => m.MessageId)
           .Take(older + 1)
           .ToListAsync(ct);

        var after = await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId && m.MessageId > messageId && !m.IsDeleted)
           .OrderBy(m => m.MessageId)
           .Take(newer + 1)
           .ToListAsync(ct);

        var containsAnchor = older > 0 && before.Count > 0 && before[0].MessageId == messageId;
        var hasOlder       = before.Count > older;
        var hasNewer       = after.Count > newer;

        var newest = after.Take(newer).Reverse();
        return new MessageWindowEntity([..newest, ..before.Take(older)], hasOlder, hasNewer, containsAnchor);
    }

    public async Task<long?> CheckDuplicationAsync(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
        => await deduplication.CheckDuplicationAsync(msg, randomId, ct);

    public async Task<long> ExecuteInsertMessage(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
    {
        // Minted here rather than read back from the column default: waiting for the database to say
        // what the id is would put the insert back in the caller's path, which is the whole cost.
        msg.MessageId = snowflake.GenerateSnowflakeId();

        logger.LogDebug("queued message {MessageId} for space {SpaceId}, channel {ChannelId}",
            msg.MessageId, msg.SpaceId, msg.ChannelId);

        // Awaited, deliberately. The batching is what makes the insert cheap — a hundred senders share
        // one round trip instead of paying one each — and that benefit survives waiting for the batch
        // to land. What waiting buys back is the guarantee a send used to have: when this returns, the
        // message is committed, so a silo lost a moment later has not swallowed it.
        await writes.EnqueueAsync(msg, randomId);

        return msg.MessageId;
    }

}