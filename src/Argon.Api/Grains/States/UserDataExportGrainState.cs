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

    // Id 8 was TotalItemsEstimate, an int that was assigned zero on every request and never
    // computed; it reached the client as DataExportStatus.totalItemsEstimate, which no consumer
    // could turn into progress. Dropped from the wire and from the state rather than made honest
    // (defect X8): the step total is only knowable once the conversation and channel cursors are
    // seeded, several ticks in, which is exactly when a progress bar would still be reading zero.
    // The ordinal stays retired — a new field reusing 8 would read an old export's zero as its own.

    [DataMember(Order = 9), Id(9)]
    public int ItemsProcessed { get; set; }

    [DataMember(Order = 10), Id(10)]
    public string? FailureReason { get; set; }

    /// <summary>How many items each file of the archive ended up carrying, keyed by its path.</summary>
    /// <remarks>
    /// Filled by the collectors and written out as <c>manifest.json</c> at assembly, so the archive
    /// says what is in it. An Art. 15 response is judged on completeness and the export reported
    /// <c>Completed</c> whether it wrote nine files or four; a person holding the zip could not tell
    /// an empty category from a missing collector (defect X1). Accumulated rather than overwritten,
    /// because a channel contributes one entry per page of messages. It grows with the number of
    /// files in the archive — one per conversation and per channel, the same order as the export's
    /// own tick count — which is the price of a manifest that names every file rather than a total
    /// that names none.
    /// </remarks>
    [DataMember(Order = 11), Id(11)]
    public Dictionary<string, int> CategoryCounts { get; set; } = new();
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

    /// <summary>How many of the current channel's messages have already been written.</summary>
    /// <remarks>
    /// The channel collector took <c>MessageBatchSize</c> messages ordered ascending by id and
    /// advanced to the next channel, so a person received their oldest N messages per channel and
    /// nothing they had written since — silently, with the archive and the status both reporting a
    /// finished export (defect X2). Every other dimension here is already paged with an index; this
    /// is the one that was missing. Pinned by
    /// <c>DataExportArchiveTests.A_channel_with_more_messages_than_one_batch_exports_all_of_them</c>.
    /// </remarks>
    [DataMember(Order = 14), Id(14)]
    public int ChannelMessageOffset { get; set; }

    // The five categories below were never collected at all: pending friend requests in both
    // directions, privacy rules and the ignore list, saved GIFs, passkeys and uploaded files — all
    // of them rows keyed on the user's own id and readable by them in-app (defect X1). New flags
    // with fresh ids, so an export already in flight when the silo restarts simply runs the new
    // steps: an old persisted cursor deserialises them as false.

    [DataMember(Order = 15), Id(15)]
    public bool FriendRequestsDone { get; set; }

    [DataMember(Order = 16), Id(16)]
    public bool PrivacyDone { get; set; }

    [DataMember(Order = 17), Id(17)]
    public bool SavedGifsDone { get; set; }

    [DataMember(Order = 18), Id(18)]
    public bool PasskeysDone { get; set; }

    [DataMember(Order = 19), Id(19)]
    public bool FilesDone { get; set; }
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
