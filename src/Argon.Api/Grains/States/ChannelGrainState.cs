namespace Argon.Grains.Persistence.States;

[DataContract, MessagePackObject(true), Serializable, GenerateSerializer]
public sealed partial record ChannelGrainState
{
    [DataMember(Order = 0), Id(0)]
    public Dictionary<Guid, RealtimeChannelUser> Users { get; set; } = new();
}

[DataContract, MessagePackObject(true), Serializable, GenerateSerializer]
public sealed partial record RealtimeServerGrainState
{
    [DataMember(Order = 0), Id(0)]
    public Dictionary<Guid, (DateTime lastSetStatus, UserStatus Status)> UserStatuses { get; set; } = new();
}