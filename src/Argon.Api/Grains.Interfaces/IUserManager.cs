namespace Argon.Api.Grains.Interfaces;

using Persistence.States;

public interface IUserManager : IGrainWithStringKey
{
    [Alias("Create")]
    Task<UserStorageDto> Create(string password);

    [Alias("Get")]
    Task<UserStorageDto> Get();

    [Alias("GetByUsername")]
    Task<UserStorageDto> GetByUsername(string username);

    [Alias("Authenticate")]
    Task<string> Authenticate(string username, string password);
}