namespace Argon.Features.EF;

using Argon.Entities;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

public static class MassMentionQueries
{
    /// <summary>
    /// Restores the latest possible mass pings, including soft-deleted messages. Filter before
    /// limiting: ordinary messages must not hide an older ping. Only IDs and timestamps leave the DB.
    /// </summary>
    public static async Task<Dictionary<long, DateTimeOffset>> RecentMassPingsAsync(this ApplicationDbContext db,
        Guid spaceId, Guid channelId, DateTimeOffset since, long before, int massUsers, int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            return new();

        var entity = db.Model.FindEntityType(typeof(ArgonMessageEntity))!;
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();
        var table = sql.DelimitIdentifier(store.Name, store.Schema);
        string Column(string name) => "m." + sql.DelimitIdentifier(entity.FindProperty(name)!.GetColumnName(store)!);

        // PolymorphicListConverter persists CLR identity in $type. Match that identity rather
        // than the independently supplied `type` enum; the previous CLR predicate used runtime types.
        // Newtonsoft's simple assembly name follows a comma, and is immaterial to the type test.
        var query = $"""
            SELECT {Column(nameof(ArgonMessageEntity.MessageId))} AS "MessageId",
                   {Column(nameof(ArgonMessageEntity.CreatedAt))} AS "CreatedAt"
            FROM {table} AS m
            WHERE {Column(nameof(ArgonMessageEntity.SpaceId))} = @spaceId
              AND {Column(nameof(ArgonMessageEntity.ChannelId))} = @channelId
              AND {Column(nameof(ArgonMessageEntity.CreatedAt))} >= @since
              AND {Column(nameof(ArgonMessageEntity.MessageId))} < @before
              AND (
                EXISTS (
                    SELECT 1 FROM jsonb_array_elements({Column(nameof(ArgonMessageEntity.Entities))}) AS e(value)
                    WHERE split_part(e.value ->> '$type', ',', 1) IN (@everyoneType, @roleType)
                )
                OR (
                    SELECT COUNT(DISTINCT (e.value ->> 'userId')::uuid)
                    FROM jsonb_array_elements({Column(nameof(ArgonMessageEntity.Entities))}) AS e(value)
                    WHERE split_part(e.value ->> '$type', ',', 1) = @mentionType
                      AND (e.value ->> 'userId')::uuid <> {Column(nameof(ArgonMessageEntity.CreatorId))}
                ) > @massUsers
              )
            ORDER BY {Column(nameof(ArgonMessageEntity.MessageId))} DESC
            LIMIT @limit
            """;

        var rows = await db.Database.SqlQueryRaw<MassPingRow>(query,
                new NpgsqlParameter("spaceId", spaceId), new NpgsqlParameter("channelId", channelId),
                new NpgsqlParameter("since", since), new NpgsqlParameter("before", before),
                new NpgsqlParameter("everyoneType", typeof(MessageEntityMentionEveryone).FullName!),
                new NpgsqlParameter("roleType", typeof(MessageEntityMentionRole).FullName!),
                new NpgsqlParameter("mentionType", typeof(MessageEntityMention).FullName!),
                new NpgsqlParameter("massUsers", massUsers), new NpgsqlParameter("limit", limit))
            .ToListAsync(ct);

        return rows.ToDictionary(m => m.MessageId, m => m.CreatedAt);
    }

    private sealed class MassPingRow
    {
        public long MessageId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
