namespace Argon.Api.Grains.Interfaces;

using Persistence.States;

public interface IServerManager : IGrainWithGuidKey
{
    [Alias("CreateServer")]
    Task<ServerStorage> CreateServer(string name, string description);

    [Alias("CreateJoinLink")]
    Task<string> CreateJoinLink();

    [Alias("AddUser")]
    Task AddUser(UserToServerRelation Relation);

    [Alias("GetServer")]
    Task<ServerStorage> GetServer();


}