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

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(ServerInput))]
public sealed partial record ServerInput(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    [property: Id(0)]
    string Name,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    [property: Id(1)]
    string? Description,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    [property: Id(2)]
    string? AvatarUrl
);