namespace Argon.Api.Grains.Interfaces;

using Contracts;
using Entities;

[Alias("Argon.Api.Grains.Interfaces.IUserGrain")]
public interface IUserGrain : IGrainWithGuidKey
{
    [Alias("CreateUser")]
    Task<UserDto> CreateUser(UserCredentialsInput input);

    [Alias("UpdateUser")]
    Task<UserDto> UpdateUser(UserEditInput input);

    [Alias("DeleteUser")]
    Task DeleteUser();

    [Alias("GetUser")]
    Task<UserDto> GetUser();

    [Alias("GetMyServers")]
    Task<List<ServerDto>> GetMyServers();
}

