namespace Grains.Interface;

using Models;
using Models.DTO;
using Orleans;

public interface IServerManager : IGrainWithGuidKey
{
    [Alias(nameof(CreateServer))]
    Task<ServerDto> CreateServer(ServerInput input, Guid creatorId);

    [Alias(nameof(GetServer))]
    Task<ServerDto> GetServer();

    [Alias(nameof(UpdateServer))]
    Task<ServerDto> UpdateServer(ServerInput input);

    [Alias(nameof(DeleteServer))]
    Task DeleteServer();
}