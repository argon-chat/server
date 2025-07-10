namespace Argon.Features.Messages;

using Cassandra.Features.Messages;
using Entities;
using System;

public class MessageProcessor(ArgonCassandraDbContext ctx)
{
    public async Task<List<ArgonMessage>> QueryMessages(
        Guid serverId,
        Guid channelId,
        ulong? fromMessageId = null,
        int limit = 50)
    {
        if (fromMessageId.HasValue)
            return await ctx.ArgonMessages
               .Where(m => m.ServerId == serverId && m.ChannelId == channelId && m.MessageId < fromMessageId.Value)
               .OrderByDescending(m => m.MessageId)
               .ToListAsync();
        return await ctx.ArgonMessages
           .Where(m => m.ServerId == serverId && m.ChannelId == channelId)
           .OrderByDescending(m => m.MessageId)
           .ToListAsync();
    }


    public async Task<bool> CheckDuplicationAsync(ArgonMessage msg, ulong randomId)
    {
        var (serverId, channelId) = (msg.ServerId, msg.ChannelId);

        return ctx.Set<ArgonMessageDeduplication>()
           .Any(x => x.RandomId == randomId && x.ChannelId == channelId && x.ServerId == serverId);
    }

    public async Task ExecuteInsertMessage(ulong nextMessageId, ArgonMessage msg, ulong randomId)
    {
        msg.MessageId = nextMessageId;

        await ctx.Set<ArgonMessage>()
           .AddAsync(msg);
        await ctx.Set<ArgonMessageDeduplication>()
           .AddAsync(new ArgonMessageDeduplication()
            {
                ChannelId = msg.ChannelId,
                MessageId = nextMessageId,
                RandomId  = randomId,
                ServerId  = msg.ServerId
            });
        await ctx.SaveChangesAsync();
    }
}