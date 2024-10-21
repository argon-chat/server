namespace Argon.Api.Grains.Interfaces;

using Persistence.States;

public interface IUserManager : IGrainWithGuidCompoundKey
{
    [Alias("Create")]
    Task<UserStorageDto> Create(string username, string password);

    [Alias("Get")]
    Task<UserStorageDto> Get(Guid id);

    [Alias("Authenticate")]
    Task<string> Authenticate(string username, string password);
}