namespace Argon.Grains.Persistence.States;

[DataContract, Serializable, GenerateSerializer]
public sealed partial record ChannelGrainState
{
    [DataMember(Order = 0), Id(0)]
    public Dictionary<Guid, RealtimeChannelUser> Users { get; set; } = new();

    [DataMember(Order = 1), Id(1)]
    public bool    EgressActive      { get; set; }
    [DataMember(Order = 2), Id(2)]
    public string? EgressId          { get; set; }
    [DataMember(Order = 3), Id(3)]
    public Guid?   UserCreatedEgress { get; set; }

    /// <summary>
    /// Tracks when each user joined the voice channel for stats calculation.
    /// </summary>
    [DataMember(Order = 4), Id(4)]
    public Dictionary<Guid, DateTimeOffset> UserJoinTimes { get; set; } = new();

    /// <summary>
    /// ID of the linked meeting, if any.
    /// </summary>
    [DataMember(Order = 5), Id(5)]
    public Guid? LinkedMeetId { get; set; }

    /// <summary>
    /// Invite code for the linked meeting.
    /// </summary>
    [DataMember(Order = 6), Id(6)]
    public string? LinkedMeetInviteCode { get; set; }
}

[DataContract, Serializable, GenerateSerializer]
public sealed partial record RealtimeServerGrainState
{
    [DataMember(Order = 0), Id(0)]
    public Dictionary<Guid, (DateTime lastSetStatus, UserStatus Status)> UserStatuses { get; set; } = new();
    [DataMember(Order = 1), Id(1)]
    public long Revision { get; set; } = 0;
}