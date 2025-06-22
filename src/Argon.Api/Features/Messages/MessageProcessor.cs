namespace Argon.Features.Messages;

using Cassandra;
using System;
using Newtonsoft.Json;
public class MessageProcessor(ISession cassandraSession)
{
    public async Task<List<ArgonMessage>> QueryMessages(
        Guid serverId,
        Guid channelId,
        long? fromMessageId = null,
        int limit = 50
    )
    {
        var parameters = new List<object> { serverId, channelId };
        var cql = "SELECT message_id, reply, text, entities, author_id, created_at, is_deleted, deleted_at, updated_at " +
                  "FROM argon_messages WHERE server_id = ? AND channel_id = ?";

        if (fromMessageId is not null)
        {
            cql += " AND message_id >= ?";
            parameters.Add(fromMessageId.Value);
        }

        cql += " LIMIT ?";
        parameters.Add(limit);

        var statement = new SimpleStatement(cql, parameters.ToArray());
        var rowSet = await cassandraSession.ExecuteAsync(statement);

        var result = new List<ArgonMessage>();

        foreach (var row in rowSet)
        {
            var msg = new ArgonMessage
            {
                ServerId = serverId,
                ChannelId = channelId,
                MessageId = unchecked((ulong)row.GetValue<long>("message_id")),
                Reply = row.IsNull("reply") ? null : unchecked((ulong?)row.GetValue<long>("reply")),
                Text = row.GetValue<string>("text") ?? string.Empty,
                Entities = JsonConvert.DeserializeObject<List<MessageEntity>>(row.GetValue<string>("entities") ?? "[]") ?? [],
                CreatorId = row.GetValue<Guid>("author_id"),
                CreatedAt = row.GetValue<DateTimeOffset>("created_at"),
                IsDeleted = row.GetValue<bool>("is_deleted"),
                DeletedAt = row.IsNull("deleted_at") ? null : row.GetValue<DateTimeOffset?>("deleted_at"),
                UpdatedAt = row.GetValue<DateTimeOffset>("updated_at")
            };

            result.Add(msg);
        }

        return result;
    }

    public async Task<List<ArgonMessage>> GetMessages(Guid serverId, Guid channelId, int count, int offset)
    {
        var fetch = count + offset;

        var cql = "SELECT message_id, reply, text, entities, author_id, created_at, is_deleted, deleted_at, updated_at " +
                  "FROM argon_messages WHERE server_id = ? AND channel_id = ? ORDER BY message_id DESC LIMIT ?";

        var statement = new SimpleStatement(cql, serverId, channelId, fetch)
            .SetAutoPage(false)
            .SetPageSize(fetch);

        var rowSet = await cassandraSession.ExecuteAsync(statement);
        var result = new List<ArgonMessage>(count);
        var i = 0;

        foreach (var row in rowSet)
        {
            if (i++ < offset) continue;
            if (result.Count == count) break;

            result.Add(new ArgonMessage
            {
                ServerId = serverId,
                ChannelId = channelId,
                MessageId = unchecked((ulong)row.GetValue<long>("message_id")),
                Reply = row.IsNull("reply") ? null : unchecked((ulong?)row.GetValue<long>("reply")),
                Text = row.GetValue<string>("text") ?? string.Empty,
                Entities = JsonConvert.DeserializeObject<List<MessageEntity>>(row.GetValue<string>("entities") ?? "[]") ?? [],
                CreatorId = row.GetValue<Guid>("author_id"),
                CreatedAt = row.GetValue<DateTimeOffset>("created_at"),
                IsDeleted = row.GetValue<bool>("is_deleted"),
                DeletedAt = row.IsNull("deleted_at") ? null : row.GetValue<DateTimeOffset?>("deleted_at"),
                UpdatedAt = row.GetValue<DateTimeOffset>("updated_at")
            });
        }

        return result;
    }

    public Task<RowSet?> ExecuteDeduplicationAsync(ArgonMessage msg, long randomId)
        => cassandraSession.ExecuteAsync(new SimpleStatement(
            "SELECT message_id FROM message_deduplication WHERE server_id=? AND channel_id=? AND random_id=?",
            msg.ServerId, msg.ChannelId, randomId
        ));

    public async Task<RowSet?> ExecuteInsertMessage(ulong nextMessageId, ArgonMessage msg, long randomId)
    {
        var entitiesJson = JsonConvert.SerializeObject(msg.Entities ?? []);
        var now = DateTimeOffset.UtcNow;

        var batch = new BatchStatement()
            .Add(new SimpleStatement(
                "INSERT INTO argon_messages (server_id, channel_id, message_id, author_id, reply, text, entities, created_at, is_deleted, deleted_at, updated_at) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                msg.ServerId,
                msg.ChannelId,
                unchecked((long)nextMessageId),
                msg.CreatorId,
                msg.Reply ?? null,
                msg.Text,
                entitiesJson,
                now,
                false,
                null,
                now
            ))
            .Add(new SimpleStatement(
                "INSERT INTO message_deduplication (server_id, channel_id, random_id, message_id) " +
                "VALUES (?, ?, ?, ?) USING TTL 86400",
                msg.ServerId,
                msg.ChannelId,
                randomId,
                unchecked((long)nextMessageId)
            ));

        var rowSet = await cassandraSession.ExecuteAsync(batch);

        msg.MessageId = nextMessageId;
        msg.CreatedAt = now;
        msg.UpdatedAt = now;
        msg.IsDeleted = false;
        msg.DeletedAt = null;

        return rowSet;
    }
}