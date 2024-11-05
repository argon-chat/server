namespace Argon.Api.Grains;

using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Persistence.States;
using Sfu;

public class ChannelManager(
    IArgonSelectiveForwardingUnit sfu,
    ApplicationDbContext context,
    [PersistentState("joinedUsers", "OrleansStorage")]
    IPersistentState<UsersJoinedToChannel> joinedUsers
) : Grain, IChannelManager
{
    public async Task<RealtimeToken> Join(Guid userId)
    {
        var channel = await GetChannel();

        var user = (await context.Servers.Include(x => x.UsersToServerRelations)
                .FirstAsync(x => x.Id == channel.ServerId))
            .UsersToServerRelations.First(x => x.UserId == userId);

        joinedUsers.State.Users.Add(user);
        await joinedUsers.WriteStateAsync();

        return await sfu.IssueAuthorizationTokenAsync(
            new ArgonUserId(userId),
            new ArgonChannelId(
                new ArgonServerId(channel.ServerId),
                this.GetPrimaryKey()
            ),
            SfuPermission.DefaultUser // TODO: sort out permissions
        );
    }

    public Task Leave(Guid userId)
    {
        joinedUsers.State.Users.RemoveAll(x => x.UserId == userId);
        return joinedUsers.WriteStateAsync();
    }

    public async Task<ChannelDto> GetChannel()
    {
        return await Get();
    }

    private async Task<Channel> Get()
    {
        return await context.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
    }
}