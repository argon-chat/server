namespace Argon.Api.Grains;

using Interfaces;
using Persistence.States;

public class ServerManager(
    [PersistentState("serverUsers", "OrleansStorage")]
    IPersistentState<ServerToUserRelations> serverUserStore,
    [PersistentState("server", "OrleansStorage")]
    IPersistentState<ServerStorage> serverStore
) : Grain, IServerManager
{
    public async Task<ServerStorage> CreateServer(string name, string description)
    {
        serverStore.State.Id = this.GetPrimaryKey();
        serverStore.State.Name = name;
        serverStore.State.Description = description;
        serverStore.State.UpdatedAt = DateTime.UtcNow;
        await serverStore.WriteStateAsync();
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
}