namespace Argon.Api.Features.CoreLogic.Messages;

using Argon.Features.EF;
using Services;
using SnowflakeId.Core;

public static class MessagesLayoutExtensions
{
    public static void AddMessagesLayout(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<MessageDeduplicationService>();
        builder.Services.AddScoped<IMessagesLayout>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<CassandraOptions>>();

            if (opt.Value.Disabled)
                return ActivatorUtilities.CreateInstance<PgSqlMessagesLayout>(sp);
            return ActivatorUtilities.CreateInstance<CassandraMessagesLayout>(sp);
        });
    }
}

public interface IMessagesLayout
{
    Task<List<ArgonMessageEntity>> QueryMessages(
        Guid spaceId,
        Guid channelId,
        long? fromMessageId = null,
        int limit = 50, CancellationToken ct = default);

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

public class PgSqlMessagesLayout(IDbContextFactory<ApplicationDbContext> context, MessageDeduplicationService deduplication) : IMessagesLayout
{
    public async Task<List<ArgonMessageEntity>> QueryMessages(Guid spaceId, Guid channelId, long? fromMessageId = null, int limit = 50,
        CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        if (fromMessageId.HasValue)
            return await ctx.Messages
               .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId && m.MessageId < fromMessageId.Value)
               .OrderByDescending(m => m.MessageId)
               .ToListAsync(cancellationToken: ct);
        return await ctx.Messages
           .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId)
           .OrderByDescending(m => m.MessageId)
           .ToListAsync(cancellationToken: ct);
    }

    public async Task<long?> CheckDuplicationAsync(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
        => await deduplication.CheckDuplicationAsync(msg, randomId, ct);

    public async Task<long> ExecuteInsertMessage(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        await ctx.Messages.AddAsync(msg, ct);
        await ctx.SaveChangesAsync(ct);

        await deduplication.SetDeduplicationAsync(msg, randomId, ct);

        return msg.MessageId;
    }
}

public class CassandraMessagesLayout(ArgonCassandraDbContext cassandraContext, ISnowflakeService snowflake, MessageDeduplicationService deduplication) : IMessagesLayout
{
    public async Task<List<ArgonMessageEntity>> QueryMessages(
        Guid spaceId,
        Guid channelId,
        long? fromMessageId = null,
        int limit = 50, CancellationToken ct = default)
    {
        if (fromMessageId.HasValue)
            return await cassandraContext.ArgonMessages
               .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId && m.MessageId < fromMessageId.Value)
               .OrderByDescending(m => m.MessageId)
               .ToListAsync(cancellationToken: ct);
        return await cassandraContext.ArgonMessages
           .Where(m => m.SpaceId == spaceId && m.ChannelId == channelId)
           .OrderByDescending(m => m.MessageId)
           .ToListAsync(cancellationToken: ct);
    }

    public async Task<long?> CheckDuplicationAsync(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
        => await deduplication.CheckDuplicationAsync(msg, randomId, ct);

    public async Task<long> ExecuteInsertMessage(ArgonMessageEntity msg, long randomId, CancellationToken ct = default)
    {
        var nextMessageId = await snowflake.GenerateSnowflakeIdAsync(ct);

        msg = msg with
        {
            MessageId = nextMessageId
        };

        await cassandraContext.Set<ArgonMessageEntity>()
           .AddAsync(msg, ct);
        
        await cassandraContext.SaveChangesAsync(ct);

        await deduplication.SetDeduplicationAsync(msg, randomId, ct);

        return nextMessageId;
    }
}
