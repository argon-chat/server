namespace Argon.Api.Grains;

using Interfaces;
using Persistence.States;
using Sfu;

public class UserManager(
    [PersistentState("userServers", "OrleansStorage")]
    IPersistentState<UserToServerRelations> userServerStore,
    IGrainFactory grainFactory
) : Grain, IUserManager
{
    public async Task<ServerStorage> CreateServer(string name, string description)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.NewGuid());
        await serverManager.CreateServer(name, description, this.GetPrimaryKey());
        var relation = new UserToServerRelation
        {
            ServerId = serverManager.GetPrimaryKey(),
            Role = ServerRole.Owner,
            UserId = this.GetPrimaryKey(),
            CustomUsername = this.GetPrimaryKeyString()
        };
        userServerStore.State.Servers.Add(relation);
        await serverManager.AddUser(relation);
        await userServerStore.WriteStateAsync();
        return await serverManager.GetServer();
    }

    public async Task<List<ServerStorage>> GetServers()
    {
        await userServerStore.ReadStateAsync();
        var servers =
            userServerStore.State.Servers.Select(x => grainFactory.GetGrain<IServerManager>(x.ServerId).GetServer());
        return (await Task.WhenAll(servers)).ToList();
    }

    public async Task<IEnumerable<ChannelStorage>> GetServerChannels(Guid serverId)
    {
        return await grainFactory.GetGrain<IServerManager>(serverId).GetChannels();
    }

    public async Task<ChannelStorage> GetChannel(Guid serverId, Guid channelId)
    {
        return await grainFactory.GetGrain<IServerManager>(serverId).GetChannel(channelId);
    }

    public async Task<RealtimeToken> JoinChannel(Guid serverId, Guid channelId)
    {
        return await grainFactory.GetGrain<IServerManager>(serverId).JoinChannel(this.GetPrimaryKey(), channelId);
    }
}