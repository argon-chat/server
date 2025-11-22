namespace Argon.Grains.Persistence.States;

[DataContract, Serializable, GenerateSerializer]
public sealed partial record ChannelGrainState
{
    [DataMember(Order = 0), Id(0)]
    public Dictionary<Guid, RealtimeChannelUser> Users { get; set; } = new();

    public bool    EgressActive      { get; set; }
    public string? EgressId          { get; set; }
    public Guid?   UserCreatedEgress { get; set; }
}

[DataContract, Serializable, GenerateSerializer]
public sealed partial record RealtimeServerGrainState
{
    [DataMember(Order = 0), Id(0)]
    public Dictionary<Guid, (DateTime lastSetStatus, UserStatus Status)> UserStatuses { get; set; } = new();
    [DataMember(Order = 1), Id(1)]
    public long Revision { get; set; } = 0;
}