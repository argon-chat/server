namespace Grains.States;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;
using Models;

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(nameof(UsersJoinedToChannel))]
public sealed partial record UsersJoinedToChannel
{
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public List<UsersToServerRelationDto> Users { get; set; } = new();
}