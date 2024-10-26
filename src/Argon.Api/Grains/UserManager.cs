namespace Argon.Api.Grains;

using BCrypt.Net;
using Interfaces;
using Persistence.States;
using Services;

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
        var salt = BCrypt.GenerateSalt();
        userStore.State.Password = BCrypt.HashPassword(password, salt);
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
        var match = BCrypt.Verify(password, userStore.State.Password);

        if (!match)
            throw new Exception("Invalid credentials"); // TODO: Come up with application specific errors

        return managerService.GenerateJwt(id: userStore.State.Id, username: this.GetPrimaryKeyString());
    }

    public async Task<ServerStorage> CreateServer(string name, string description)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.NewGuid());
        await serverManager.CreateServer(name, description);
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


    private Task EnsureUnique()
    {
        if (userStore.State.Id != Guid.Empty)
            throw new Exception("User already exists"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }
}