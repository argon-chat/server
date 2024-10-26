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
        await CreateDefaultTextChannel(userId);
        await CreateDefaultVoiceChannel(userId);
        await CreateDefaultAnnouncementChannel(userId);
    }

    private async Task CreateDefaultAnnouncementChannel(Guid UserId)
    {
        var id = Guid.NewGuid();
        await grainFactory.GetGrain<IChannelManager>(id)
            .CreateChannel(new ChannelStorage
            {
                Id = id,
                Name = "General",
                Description = "Default announcement channel",
                ChannelType = ChannelType.Announcement,
                CreatedBy = UserId
            });
    }

    private async Task CreateDefaultVoiceChannel(Guid UserId)
    {
        var id = Guid.NewGuid();
        await grainFactory.GetGrain<IChannelManager>(id)
            .CreateChannel(new ChannelStorage
            {
                Id = id,
                Name = "General",
                Description = "Default voice channel",
                CreatedBy = UserId,
                ChannelType = ChannelType.Voice
            });
    }

    private async Task CreateDefaultTextChannel(Guid userId)
    {
        var id = Guid.NewGuid();
        await grainFactory.GetGrain<IChannelManager>(id)
            .CreateChannel(new ChannelStorage
            {
                Id = id,
                Name = "General",
                Description = "Default text channel",
                ChannelType = ChannelType.Text,
                CreatedBy = userId
            });
    }
}