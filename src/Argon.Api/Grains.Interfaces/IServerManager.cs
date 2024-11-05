namespace Argon.Api.Grains.Interfaces;

using System.Runtime.Serialization;
using Entities;
using MemoryPack;
using MessagePack;

public interface IServerManager : IGrainWithGuidKey
{
    [Alias("CreateServer")]
    Task<ServerDto> CreateServer(ServerInput input, Guid creatorId);

    [Alias("GetServer")]
    Task<ServerDto> GetServer();

    [Alias("UpdateServer")]
    Task<ServerDto> UpdateServer(ServerInput input);

    [Alias("DeleteServer")]
    Task DeleteServer();
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ServerInput))]
public sealed partial record ServerInput(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    string? Description,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2), Id(2)]
    string? AvatarUrl);