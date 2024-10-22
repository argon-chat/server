namespace Argon.Api.Grains;

using BCrypt.Net;
using Interfaces;
using Persistence.States;
using Services;

public class UserManager(
    ILogger<UserManager> logger,
    [PersistentState("users", "OrleansStorage")]
    IPersistentState<UserStorage> userStore,
    UserManagerService managerService
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


    private Task EnsureUnique()
    {
        if (userStore.State.Id != Guid.Empty)
            throw new Exception("User already exists"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }
}