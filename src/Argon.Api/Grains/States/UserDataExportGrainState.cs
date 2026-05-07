namespace Argon.Grains.Persistence.States;

[DataContract, Serializable, GenerateSerializer]
public sealed partial record UserDataExportGrainState
{
    [DataMember(Order = 0), Id(0)]
    public ExportStatus Status { get; set; } = ExportStatus.Idle;

    [DataMember(Order = 1), Id(1)]
    public Guid? CurrentExportId { get; set; }

    [DataMember(Order = 2), Id(2)]
    public DateTimeOffset? StartedAt { get; set; }

    [DataMember(Order = 3), Id(3)]
    public DateTimeOffset? CompletedAt { get; set; }

    [DataMember(Order = 4), Id(4)]
    public DateTimeOffset? LastExportCompletedAt { get; set; }

    [DataMember(Order = 5), Id(5)]
    public string? ArchiveS3Key { get; set; }

    [DataMember(Order = 6), Id(6)]
    public string? DownloadUrl { get; set; }

    [DataMember(Order = 7), Id(7)]
    public ExportCursor Cursor { get; set; } = new();

    [DataMember(Order = 8), Id(8)]
    public int TotalItemsEstimate { get; set; }

    [DataMember(Order = 9), Id(9)]
    public int ItemsProcessed { get; set; }

    [DataMember(Order = 10), Id(10)]
    public string? FailureReason { get; set; }
}

[DataContract, Serializable, GenerateSerializer]
public sealed partial record ExportCursor
{
    [DataMember(Order = 0), Id(0)]
    public bool ProfileDone { get; set; }

    [DataMember(Order = 1), Id(1)]
    public bool FriendsDone { get; set; }

    [DataMember(Order = 2), Id(2)]
    public bool BlocksDone { get; set; }

    [DataMember(Order = 3), Id(3)]
    public bool SettingsDone { get; set; }

    [DataMember(Order = 4), Id(4)]
    public bool StatsDone { get; set; }

    [DataMember(Order = 5), Id(5)]
    public bool DevicesDone { get; set; }

    [DataMember(Order = 6), Id(6)]
    public bool SubscriptionsDone { get; set; }

    [DataMember(Order = 7), Id(7)]
    public int DmConversationIndex { get; set; }

    [DataMember(Order = 8), Id(8)]
    public bool DmConversationsDone { get; set; }

    [DataMember(Order = 9), Id(9)]
    public int SpaceMembershipIndex { get; set; }

    [DataMember(Order = 10), Id(10)]
    public int ChannelIndex { get; set; }

    [DataMember(Order = 11), Id(11)]
    public bool ChannelMessagesDone { get; set; }

    [DataMember(Order = 12), Id(12)]
    public bool DataPhaseComplete { get; set; }

    [DataMember(Order = 13), Id(13)]
    public bool AssemblyComplete { get; set; }
}

public enum ExportStatus
{
    Idle,
    Queued,
    CollectingData,
    Assembling,
    Completed,
    Expired,
    Failed
}
