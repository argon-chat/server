namespace Argon.Api.Grains.Interfaces;

using Contracts;
using Contracts.Models;

[Alias("Argon.Api.Grains.Interfaces.IUserGrain")]
public interface IUserGrain : IGrainWithGuidKey
{
    [Alias("UpdateUser")]
    Task<User> UpdateUser(UserEditInput input);

    [Alias("DeleteUser")]
    Task DeleteUser();

    [Alias("GetUser")]
    Task<User> GetUser();

    [Alias("GetMyServers")]
    Task<List<Server>> GetMyServers();

    [Alias("GetMyServersIds")]
    Task<List<Guid>> GetMyServersIds();
}

