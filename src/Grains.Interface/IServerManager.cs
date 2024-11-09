namespace Grains.Interface;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;
using Models;
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

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ServerInput))]
public sealed record ServerInput(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    string? Description,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2), Id(2)]
    string? AvatarUrl);