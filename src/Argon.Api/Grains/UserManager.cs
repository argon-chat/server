namespace Argon.Api.Grains;

using Interfaces;
using Persistence.States;

public class UserManager(
    ILogger<UserManager> logger,
    [PersistentState("users", "OrleansStorage")]
    IPersistentState<UserStorage> userStore) : Grain, IUserManager
{
    public async Task<UserStorageDto> Create(string username, string password)
    {
        // await Validate(username, password);
        userStore.State.Id = this.GetPrimaryKey();
        userStore.State.Username = username;
        userStore.State.Password = password;
        await userStore.WriteStateAsync();
        return userStore.State;
    }

    // private async Task Validate(string username, string password)
    // {
    //     await EnsureUnique(username);
    //     await ValidateLength(username, 3, 50);
    //     await ValidateLength(password, 8, 32);
    //     await ValidatePasswordStrength(password);
    // }
    //
    // private async Task ValidatePasswordStrength(string Password)
    // {
    //     throw new NotImplementedException();
    // }
    //
    // private async Task ValidateLength(string username, int min, int max)
    // {
    //     throw new NotImplementedException();
    // }
    //
    // private async Task EnsureUnique(string username)
    // {
    //     
    // }

    public Task<UserStorageDto> Get(Guid id)
    {
        throw new NotImplementedException();
    }

    public Task<string> Authenticate(string username, string password)
    {
        throw new NotImplementedException();
    }
}