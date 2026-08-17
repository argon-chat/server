namespace Argon.Grains.Persistence.States;

using ArgonContracts;

[DataContract, Serializable, GenerateSerializer]
public sealed partial record SpaceDeletionGrainState
{
    [DataMember(Order = 0), Id(0)]
    public SpaceDeletionStatus Status { get; set; } = SpaceDeletionStatus.NONE;

    [DataMember(Order = 1), Id(1)]
    public DateTimeOffset? ScheduledAt { get; set; }

    [DataMember(Order = 2), Id(2)]
    public DateTimeOffset? ExecutionAt { get; set; }

    /// <summary>Who asked, kept so an audit can answer "who deleted this" after the row is gone.</summary>
    [DataMember(Order = 3), Id(3)]
    public Guid? RequestedBy { get; set; }

    [DataMember(Order = 4), Id(4)]
    public string? FailureReason { get; set; }
}
