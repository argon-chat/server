namespace Argon.Api.Grains.Persistence.States;

using Contracts;
using Entities;

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer]
public sealed partial record ChannelGrainState
{
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Dictionary<Guid, ChannelRealtimeMember> Users { get; set; } = new();
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer]
public sealed partial record RealtimeServerGrainState
{
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Dictionary<Guid, UserStatus> UserStatuses { get; set; } = new();
}