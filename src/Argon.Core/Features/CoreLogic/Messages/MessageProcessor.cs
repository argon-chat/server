namespace Argon.Api.Features.CoreLogic.Messages;

using Argon.Features.EF;
using Cassandra;
using global::Cassandra;
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

public class CassandraMessagesLayout(
    ArgonCassandraDbContext cassandraContext,
    ISnowflakeService snowflake,
    MessageDeduplicationService deduplication,
    ILogger<IMessagesLayout> logger) : IMessagesLayout
{
    public async Task<List<ArgonMessageEntity>> QueryMessages(
        Guid spaceId,
        Guid channelId,
        long? fromMessageId = null,
        int limit = 50, CancellationToken ct = default)
    {
        var query = fromMessageId.HasValue
            ? "SELECT MessageId, SpaceId, ChannelId, CreatorId, Reply, Text, Entities, CreatedAt, IsDeleted, DeletedAt, UpdatedAt FROM ArgonMessage WHERE SpaceId = ? AND ChannelId = ? AND MessageId < ? ORDER BY MessageId DESC LIMIT ?"
            : "SELECT MessageId, SpaceId, ChannelId, CreatorId, Reply, Text, Entities, CreatedAt, IsDeleted, DeletedAt, UpdatedAt FROM ArgonMessage WHERE SpaceId = ? AND ChannelId = ? ORDER BY MessageId DESC LIMIT ?";

        var statement = new SimpleStatement(
            query,
            fromMessageId.HasValue
                ? [spaceId, channelId, fromMessageId.Value, limit]
                : [spaceId, channelId, limit]);

        statement.SetConsistencyLevel(ConsistencyLevel.LocalQuorum);

        try
        {
            var rs     = await cassandraContext.Session.ExecuteAsync(statement).ConfigureAwait(false);
            var result = new List<ArgonMessageEntity>(limit);
            result.AddRange(rs.Select(row => new ArgonMessageEntity
            {
                MessageId = row.GetValue<long>("MessageId"),
                SpaceId   = row.GetValue<Guid>("SpaceId"),
                ChannelId = row.GetValue<Guid>("ChannelId"),
                CreatorId = row.GetValue<Guid>("CreatorId"),
                Reply     = row.GetValue<long?>("Reply"),
                Text      = row.GetValue<string>("Text"),
                Entities  = [], // row.GetValue<string>("Entities")
                CreatedAt = row.GetValue<DateTimeOffset>("CreatedAt"),
                IsDeleted = row.GetValue<bool>("IsDeleted"),
                DeletedAt = row.GetValue<DateTimeOffset?>("DeletedAt"),
                UpdatedAt = row.GetValue<DateTimeOffset>("UpdatedAt")
            }));

            return result;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unexpected Cassandra error");
            throw;
        }
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