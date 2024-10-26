namespace Argon.Api.Grains;

using Interfaces;
using Persistence.States;

public class ServerManager(
    [PersistentState("serverUsers", "OrleansStorage")]
    IPersistentState<ServerToUserRelations> serverUserStore,
    [PersistentState("server", "OrleansStorage")]
    IPersistentState<ServerStorage> serverStore,
    IGrainFactory grainFactory
) : Grain, IServerManager
{
    public async Task<ServerStorage> CreateServer(string name, string description, Guid userId)
    {
        serverStore.State.Id = this.GetPrimaryKey();
        serverStore.State.Name = name;
        serverStore.State.Description = description;
        serverStore.State.UpdatedAt = DateTime.UtcNow;
        await serverStore.WriteStateAsync();
        await CreateDefaultChannels(userId);
        return serverStore.State;
    }
    
    public async Task<string> CreateJoinLink()
    {
        return await Task.Run(() => ""); // TODO: register url generator grain for this one line
    }

    public async Task AddUser(UserToServerRelation Relation)
    {
        serverUserStore.State.Users.Add(Relation);
        await serverUserStore.WriteStateAsync();
    }

    public async Task<ServerStorage> GetServer()
    {
        await serverStore.ReadStateAsync();
        return serverStore.State;
    }

    private async Task CreateDefaultChannels(Guid userId)
    {
        await CreateDefaultChannel(userId, "General", "Default text channel", ChannelType.Text);
        await CreateDefaultChannel(userId, "General", "Default voice channel", ChannelType.Voice);
        await CreateDefaultChannel(userId, "Announcements", "Default announcement channel", ChannelType.Announcement);
    }

    private async Task CreateDefaultChannel(Guid userId, string name, string description, ChannelType channelType)
    {
        var id = Guid.NewGuid();
        await grainFactory.GetGrain<IChannelManager>(id)
            .CreateChannel(new ChannelStorage
            {
                Id = id,
                Name = name,
                Description = description,
                ChannelType = channelType,
                CreatedBy = userId,
            });
    }
}