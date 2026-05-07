namespace Argon.Grains.Persistence.States;

[DataContract, Serializable, GenerateSerializer]
public sealed partial record AccountDeletionGrainState
{
    [DataMember(Order = 0), Id(0)]
    public AccountDeletionStatus Status { get; set; } = AccountDeletionStatus.None;

    [DataMember(Order = 1), Id(1)]
    public DateTimeOffset? ScheduledAt { get; set; }

    [DataMember(Order = 2), Id(2)]
    public DateTimeOffset? ExecutionAt { get; set; }

    [DataMember(Order = 3), Id(3)]
    public HashSet<int> RemindersSent { get; set; } = [];

    [DataMember(Order = 4), Id(4)]
    public string? OriginalEmail { get; set; }

    [DataMember(Order = 5), Id(5)]
    public string? OriginalUsername { get; set; }

    [DataMember(Order = 6), Id(6)]
    public string? OriginalDisplayName { get; set; }

    [DataMember(Order = 7), Id(7)]
    public DateTimeOffset? CompletedAt { get; set; }

    [DataMember(Order = 8), Id(8)]
    public string? FailureReason { get; set; }
}

public enum AccountDeletionStatus
{
    None,
    Scheduled,
    Executing,
    Completed,
    Failed
}
