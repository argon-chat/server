namespace Argon.Api.Grains;

using Interfaces;
using Persistence.States;

public class UserManager(
    ILogger<UserManager> logger,
    [PersistentState("users", "OrleansStorage")]
    IPersistentState<UserStorage> userStore) : Grain, IUserManager
{
    public async Task<UserStorageDto> Create(string password)
    {
        // await Validate(username, password);

        userStore.State.Username = this.GetPrimaryKeyString();
        userStore.State.Id = Guid.NewGuid();
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

    public Task<UserStorageDto> Get()
    {
        throw new NotImplementedException();
    }

    public Task<UserStorageDto> GetByUsername(string username)
    {
        var sql = @"with decoded_payload as (
    select (encode(payloadbinary, 'escape'))::jsonb as payload
    from orleansstorage
    where graintypestring = 'users'
)
select payload
from decoded_payload
where payload->>'Username' = ?;";

        return Task.FromResult(new UserStorageDto());
    }

    public Task<string> Authenticate(string username, string password)
    {
        throw new NotImplementedException();
    }
}