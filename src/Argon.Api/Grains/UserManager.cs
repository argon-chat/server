namespace Argon.Api.Grains;

using System.Security.Cryptography;
using System.Text;
using Interfaces;
using Persistence.States;
using Services;
using Sfu;

public class UserManager(
    ILogger<UserManager> logger,
    [PersistentState("users", "OrleansStorage")]
    IPersistentState<UserStorage> userStore,
    [PersistentState("userServers", "OrleansStorage")]
    IPersistentState<UserToServerRelations> userServerStore,
    UserManagerService managerService,
    IGrainFactory grainFactory
) : Grain, IUserManager
{
    public async Task<UserStorageDto> Create(string password)
    {
        var username = this.GetPrimaryKeyString();
        await EnsureUnique();
        await managerService.Validate(username, password);

        userStore.State.Id = Guid.NewGuid();
        userStore.State.Username = username;
        userStore.State.Password = HashPassword(password);
        await userStore.WriteStateAsync();
        return userStore.State;
    }

    public async Task<UserStorageDto> Get()
    {
        await userStore.ReadStateAsync();
        return userStore.State;
    }

    public Task<string> Authenticate(string password)
    {
        var match = userStore.State.Password == HashPassword(password);

        if (!match)
            throw new Exception("Invalid credentials"); // TODO: Come up with application specific errors

        return managerService.GenerateJwt(id: userStore.State.Id, username: this.GetPrimaryKeyString());
    }

    public async Task<ServerStorage> CreateServer(string name, string description)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.NewGuid());
        await serverManager.CreateServer(name, description, userStore.State.Id);
        var relation = new UserToServerRelation
        {
            ServerId = serverManager.GetPrimaryKey(),
            Role = ServerRole.Owner,
            Username = this.GetPrimaryKeyString(),
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
        return await grainFactory.GetGrain<IServerManager>(serverId).JoinChannel(userStore.State.Id, channelId);
    }


    private Task EnsureUnique()
    {
        if (userStore.State.Id != Guid.Empty)
            throw new Exception("User already exists"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }

    private static string HashPassword(string input) // TODO: replace with an actual secure hashing mechanism
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }
}