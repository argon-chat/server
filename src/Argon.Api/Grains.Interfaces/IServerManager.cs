namespace Argon.Api.Grains.Interfaces;

using System.Runtime.Serialization;
using Entities;
using MemoryPack;
using MessagePack;

public interface IServerManager : IGrainWithGuidKey
{
    [Alias(alias: "CreateServer")]
    Task<ServerDto> CreateServer(ServerInput input, Guid creatorId);

    [Alias(alias: "GetServer")]
    Task<ServerDto> GetServer();

    [Alias(alias: "UpdateServer")]
    Task<ServerDto> UpdateServer(ServerInput input);

    [Alias(alias: "DeleteServer")]
    Task DeleteServer();
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(alias: nameof(ServerInput))]
public sealed partial record ServerInput(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0), Id(id: 0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1), Id(id: 1)]
    string? Description,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2), Id(id: 2)]
    string? AvatarUrl
);