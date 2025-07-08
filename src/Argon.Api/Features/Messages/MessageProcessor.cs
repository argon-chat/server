namespace Argon.Features.Messages;

using System;
using Cassandra.Features.Messages;
using Entities;
public class MessageProcessor(ArgonCassandraDbContext ctx)
{
    public async Task<List<ArgonMessage>> QueryMessages(
        Guid serverId,
        Guid channelId,
        ulong? fromMessageId = null,
        int limit = 50)
    {
        var query = ctx.ArgonMessages
           .Where(m => m.ServerId == serverId && m.ChannelId == channelId);

        if (fromMessageId.HasValue)
            query = query.Where(m => m.MessageId >= fromMessageId.Value);

        return await query
           .OrderBy(m => m.MessageId)
           .Take(limit)
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