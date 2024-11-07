namespace Argon.Api.Grains.Interfaces;

using Entities;

public interface IUserManager : IGrainWithGuidKey
{
    [Alias("CreateUser")]
    Task<UserDto> CreateUser(UserCredentialsInput input);

    [Alias("UpdateUser")]
    Task<UserDto> UpdateUser(UserCredentialsInput input);

    [Alias("DeleteUser")]
    Task DeleteUser();

    [Alias("GetUser")]
    Task<UserDto> GetUser();
}

