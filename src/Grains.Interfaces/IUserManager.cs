namespace Grains.Interfaces;

using Models;
using Models.DTO;
using Orleans;

public interface IUserManager : IGrainWithGuidKey
{
    [Alias(nameof(CreateUser))]
    Task<UserDto> CreateUser(UserCredentialsInput input);

    [Alias(nameof(UpdateUser))]
    Task<UserDto> UpdateUser(UserCredentialsInput input);

    [Alias(nameof(DeleteUser))]
    Task DeleteUser();

    [Alias(nameof(GetUser))]
    Task<UserDto> GetUser();
}