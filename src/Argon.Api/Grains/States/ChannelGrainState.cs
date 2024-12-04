namespace Argon.Grains.Persistence.States;

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject(true), Serializable, GenerateSerializer]
public sealed partial record ChannelGrainState
{
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Dictionary<Guid, RealtimeChannelUser> Users { get; set; } = new();
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject(true), Serializable, GenerateSerializer]
public sealed partial record RealtimeServerGrainState
{
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Dictionary<Guid, UserStatus> UserStatuses { get; set; } = new();
}