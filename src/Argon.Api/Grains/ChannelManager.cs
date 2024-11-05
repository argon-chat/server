namespace Argon.Api.Grains;

using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Persistence.States;
using Sfu;

public class ChannelManager(
    IArgonSelectiveForwardingUnit sfu,
    ApplicationDbContext          context,
    [PersistentState(stateName: "joinedUsers", storageName: "OrleansStorage")]
    IPersistentState<UsersJoinedToChannel> joinedUsers
) : Grain, IChannelManager
{
    public async Task<RealtimeToken> Join(Guid userId)
    {
        var channel = await GetChannel();
        if (channel.ChannelType != ChannelType.Voice) throw new Exception(message: "k mamke svoey podklyuchaysa");

        var user = (await context.Servers.Include(navigationPropertyPath: x => x.UsersToServerRelations)
                                 .FirstAsync(predicate: x => x.Id == channel.ServerId))
                   .UsersToServerRelations.First(predicate: x => x.UserId == userId);

        joinedUsers.State.Users.Add(item: user);
        await joinedUsers.WriteStateAsync();

        return await sfu.IssueAuthorizationTokenAsync(
                                                      userId: new ArgonUserId(id: userId),
                                                      channelId: new ArgonChannelId(
                                                                                    serverId: new ArgonServerId(id: channel.ServerId),
                                                                                    channelId: this.GetPrimaryKey()
                                                                                   ),
                                                      permission: SfuPermission.DefaultUser // TODO: sort out permissions
                                                     );
    }

    public Task Leave(Guid userId)
    {
        joinedUsers.State.Users.RemoveAll(match: x => x.UserId == userId);
        return joinedUsers.WriteStateAsync();
    }

    public async Task<ChannelDto> GetChannel()
    {
        ChannelDto channel = await Get();
        channel.ConnectedUsers = joinedUsers.State.Users;
        return channel;
    }

    public async Task<ChannelDto> UpdateChannel(ChannelInput input)
    {
        var channel = await Get();
        channel.Name        = input.Name;
        channel.AccessLevel = input.AccessLevel;
        channel.Description = input.Description ?? channel.Description;
        channel.ChannelType = input.ChannelType;
        context.Channels.Update(entity: channel);
        await context.SaveChangesAsync();
        return await Get();
    }

    private async Task<Channel> Get()
    {
        return await context.Channels.FirstAsync(predicate: c => c.Id == this.GetPrimaryKey());
    }
}