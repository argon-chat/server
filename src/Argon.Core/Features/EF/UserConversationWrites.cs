namespace Argon.Features.EF;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

/// <summary>Atomic per-user DM metadata writes for PostgreSQL and CockroachDB.</summary>
public static class UserConversationWrites
{
    /// <summary>
    /// Records one side's metadata inside the caller's explicit, retried message transaction.
    /// </summary>
    public static Task<int> RecordMessageAsync(
        ApplicationDbContext ctx,
        Guid userId,
        Guid peerId,
        Guid conversationId,
        string? previewText,
        DateTimeOffset timestamp,
        bool incrementUnread,
        CancellationToken ct = default)
        => RecordAsync(ctx, conversationId, previewText, timestamp,
            [new(userId, peerId, incrementUnread)], ct);

    /// <summary>
    /// Records both participants in one round trip, in a fixed order. The caller's explicit,
    /// retried transaction commits these counters together with the message.
    /// </summary>
    public static Task<int> RecordParticipantsAsync(
        ApplicationDbContext ctx,
        Guid senderId,
        Guid receiverId,
        Guid conversationId,
        string? previewText,
        DateTimeOffset timestamp,
        bool incrementReceiverUnread,
        CancellationToken ct = default)
    {
        var (first, second) = ConversationEntity.OrderParticipants(senderId, receiverId);
        return RecordAsync(ctx, conversationId, previewText, timestamp,
        [
            new(first, second, first != senderId && incrementReceiverUnread),
            new(second, first, second == receiverId && incrementReceiverUnread)
        ], ct);
    }

    private static async Task<int> RecordAsync(ApplicationDbContext ctx, Guid conversationId,
        string? previewText, DateTimeOffset timestamp, MetadataWrite[] writes, CancellationToken ct)
    {
        var transaction = ctx.Database.CurrentTransaction
            ?? throw new InvalidOperationException("DM metadata writes require an explicit transaction.");
        var entity = ctx.Model.FindEntityType(typeof(UserConversationEntity))!;
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var sql = ctx.GetService<ISqlGenerationHelper>();
        var table = sql.DelimitIdentifier(store.Name, store.Schema);
        string Column(string property) => sql.DelimitIdentifier(entity.FindProperty(property)!.GetColumnName(store)!);
        var user = Column(nameof(UserConversationEntity.UserId));
        var conversation = Column(nameof(UserConversationEntity.ConversationId));
        var peer = Column(nameof(UserConversationEntity.PeerId));
        var lastAt = Column(nameof(UserConversationEntity.LastMessageAt));
        var lastText = Column(nameof(UserConversationEntity.LastMessageText));
        var unread = Column(nameof(UserConversationEntity.UnreadCount));
        var pinned = Column(nameof(UserConversationEntity.IsPinned));
        var archived = Column(nameof(UserConversationEntity.IsArchived));
        var muted = Column(nameof(UserConversationEntity.IsMuted));

        // DO NOTHING covers both unique indexes. The next statement sees the winning insert
        // under READ COMMITTED; CockroachDB snapshot conflicts retry the caller's transaction.
        // A provider batch keeps all statements in one round trip and exposes each UPDATE's
        // affected rows, so a mismatched second participant cannot be masked by the first.
        var insertSql = $"""
            INSERT INTO {table}
                ({user}, {conversation}, {peer}, {lastAt}, {lastText}, {unread}, {pinned}, {archived}, {muted})
            VALUES (@userId, @conversationId, @peerId, @timestamp, @previewText, 0, FALSE, FALSE, FALSE)
            ON CONFLICT DO NOTHING
            """;
        var updateSql = $"""
            UPDATE {table}
            SET {lastAt} = @timestamp,
                {lastText} = @previewText,
                {archived} = FALSE,
                {unread} = {unread} + @unreadDelta
            WHERE {user} = @userId AND {conversation} = @conversationId AND {peer} = @peerId
            """;

        // Use the context's already-open connection and transaction. Npgsql retains its own
        // command tracing; the configured EF command timeout and cancellation also apply.
        await using var batch = new NpgsqlBatch((NpgsqlConnection)ctx.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction());
        if (ctx.Database.GetCommandTimeout() is { } timeout)
            batch.Timeout = timeout;
        var updates = new List<NpgsqlBatchCommand>(writes.Length);
        foreach (var write in writes)
        {
            var insert = new NpgsqlBatchCommand(insertSql);
            AddParameters(insert, write);
            batch.BatchCommands.Add(insert);
            var update = new NpgsqlBatchCommand(updateSql);
            AddParameters(update, write);
            update.Parameters.Add(new NpgsqlParameter("unreadDelta", write.IncrementUnread ? 1 : 0));
            batch.BatchCommands.Add(update);
            updates.Add(update);
        }

        var affected = await batch.ExecuteNonQueryAsync(ct);
        if (updates.Any(update => update.RecordsAffected != 1))
            throw new InvalidOperationException("The peer is already associated with a different conversation.");
        return affected;

        void AddParameters(NpgsqlBatchCommand command, MetadataWrite write)
        {
            command.Parameters.Add(new NpgsqlParameter("userId", write.UserId));
            command.Parameters.Add(new NpgsqlParameter("conversationId", conversationId));
            command.Parameters.Add(new NpgsqlParameter("peerId", write.PeerId));
            command.Parameters.Add(new NpgsqlParameter("timestamp", timestamp));
            command.Parameters.Add(new NpgsqlParameter("previewText", NpgsqlDbType.Text)
                { Value = (object?)previewText ?? DBNull.Value });
        }
    }

    private readonly record struct MetadataWrite(Guid UserId, Guid PeerId, bool IncrementUnread);
}
