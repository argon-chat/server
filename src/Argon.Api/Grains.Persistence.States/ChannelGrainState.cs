namespace Argon.Api.Grains.Persistence.States;

using Entities;

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer]
public sealed partial record ChannelGrainState
{
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Dictionary<Guid, UsersToServerRelationDto> Users { get; set; } = new();
}