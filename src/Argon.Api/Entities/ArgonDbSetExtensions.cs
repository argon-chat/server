namespace Argon.Entities;

using System.Data;

public static class ArgonDbSetExtensions
{
    public async static Task<ulong> GenerateNextMessageId(this ApplicationDbContext ctx, Guid serverId, Guid channelId)
    {
        var conn = ctx.Database.GetDbConnection();

        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        var sql = """
                   WITH updated AS (
                       UPDATE "ArgonMessages_Counters"
                       SET "NextMessageId" = "NextMessageId" + 1
                       WHERE "ServerId" = @serverId AND "ChannelId" = @channelId
                       RETURNING "NextMessageId"
                   ),
                   inserted AS (
                       INSERT INTO "ArgonMessages_Counters" ("ServerId", "ChannelId", "NextMessageId")
                       SELECT @serverId, @channelId, 1
                       WHERE NOT EXISTS (SELECT 1 FROM updated)
                       RETURNING "NextMessageId"
                   )
                   SELECT "NextMessageId" FROM updated
                   UNION ALL
                   SELECT "NextMessageId" FROM inserted
                   """;

        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.CommandType = System.Data.CommandType.Text;

        var serverParam = command.CreateParameter();
        serverParam.ParameterName = "serverId";
        serverParam.Value         = serverId;
        command.Parameters.Add(serverParam);

        var channelParam = command.CreateParameter();
        channelParam.ParameterName = "channelId";
        channelParam.Value         = channelId;
        command.Parameters.Add(channelParam);

        var result = await command.ExecuteScalarAsync();

        return (ulong)(decimal)result;
    }
}