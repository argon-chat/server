namespace Argon.Api.Grains.Interfaces;

using Persistence.States;

public interface IUserManager : IGrainWithStringKey
{
    [Alias("Create")]
    Task<UserStorageDto> Create(string password);

    [Alias("Get")]
    Task<UserStorageDto> Get();

    [Alias("Authenticate")]
    Task<string> Authenticate(string password);

    [Alias("CreateServer")]
    Task<ServerStorage> CreateServer(string name, string description);

    [Alias("GetServers")]
    Task<List<UserToServerRelation>> GetServers();
}