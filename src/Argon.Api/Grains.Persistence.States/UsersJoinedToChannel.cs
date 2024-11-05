namespace Argon.Api.Grains.Persistence.States;

using System.Runtime.Serialization;
using Entities;
using MemoryPack;
using MessagePack;

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer]
public sealed partial record UsersJoinedToChannel
{
    [DataMember(Order = 0), MemoryPackOrder(order: 0), Id(id: 0)]
    public List<UsersToServerRelationDto> Users { get; set; } = new();
}