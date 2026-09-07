namespace Argon.Grains.Persistence.States;

using Argon.Grains.Interfaces;

/// <summary>
/// The operator queue as it is written down: the entries awaiting a decision, and the accounts an
/// operator has already declined.
/// </summary>
/// <remarks>
/// <para>Kept separate from the contract types in <see cref="IAccountDeletionQueueGrain"/> even though the
/// two look alike today. What is persisted has to stay readable by the next deployment, and what a console
/// renders changes whenever the console does; folding them together makes every rename of a display field a
/// migration of live grain state. The grain maps between them in one place.</para>
///
/// <para><see cref="RejectedUntil"/> is the half of the queue that is not a projection of the scan. An
/// entry disappears the moment its account stops being a candidate, so a rejection recorded as "remove the
/// entry" would last exactly until the next pass — the same shape of defect as CON-4, one level up. The
/// stamp outlives the entry and is what <see cref="Entries"/> is rebuilt against.</para>
/// </remarks>
[DataContract, Serializable, GenerateSerializer]
public sealed partial record AccountDeletionQueueGrainState
{
    [DataMember(Order = 0), Id(0)]
    public List<AccountDeletionQueueRecord> Entries { get; set; } = [];

    /// <summary>
    /// Accounts an operator declined, and the instant the scan may propose them again.
    /// </summary>
    [DataMember(Order = 1), Id(1)]
    public Dictionary<Guid, DateTimeOffset> RejectedUntil { get; set; } = [];
}

/// <summary>One queued account, as the grain stores it.</summary>
[DataContract, Serializable, GenerateSerializer]
public sealed partial record AccountDeletionQueueRecord
{
    [DataMember(Order = 0), Id(0)]
    public Guid UserId { get; set; }

    [DataMember(Order = 1), Id(1)]
    public DateTimeOffset LastActivityAt { get; set; }

    [DataMember(Order = 2), Id(2)]
    public int ThresholdMonths { get; set; }

    [DataMember(Order = 3), Id(3)]
    public string Reason { get; set; } = AccountDeletionQueueReasons.InactivityDefault;

    /// <summary>
    /// When the account first entered the queue.
    /// </summary>
    /// <remarks>
    /// Preserved across reconciliations rather than rewritten on every pass: the scan runs daily, so a
    /// timestamp refreshed each time would report every backlog as a day old and hide exactly the thing an
    /// operator needs to see — that nobody has looked at this account for a month.
    /// </remarks>
    [DataMember(Order = 4), Id(4)]
    public DateTimeOffset EnqueuedAt { get; set; }

    [DataMember(Order = 5), Id(5)]
    public QueuedAccountDeletionState State { get; set; } = QueuedAccountDeletionState.Pending;

    [DataMember(Order = 6), Id(6)]
    public Guid? DecidedByOperatorId { get; set; }

    [DataMember(Order = 7), Id(7)]
    public string? DecidedByOperatorEmail { get; set; }

    [DataMember(Order = 8), Id(8)]
    public DateTimeOffset? DecidedAt { get; set; }

    [DataMember(Order = 9), Id(9)]
    public DateTimeOffset? ScheduledDeletionAt { get; set; }

    /// <summary>
    /// When the erasure this entry authorised was first seen to have given up, or null while it has not.
    /// </summary>
    /// <remarks>
    /// Written by the reconciliation rather than by the deletion grain, so it is "when the queue noticed"
    /// and not "when it stopped" — the two differ by at most one pass, and the alternative is the deletion
    /// grain having to know that a queue exists. It is what the stranded page sorts by: the erasure that
    /// has been half-finished longest is the one somebody should be looking at.
    /// </remarks>
    [DataMember(Order = 10), Id(10)]
    public DateTimeOffset? StrandedSince { get; set; }

    /// <summary>The deletion's last failure, as it read at the last reconciliation.</summary>
    /// <remarks>
    /// A copy of grain state that goes stale between passes, kept anyway: an operator looking at a list
    /// of stranded erasures needs to see which of them failed for the same reason without opening each
    /// one, and the authoritative value is one grain call away when they act.
    /// </remarks>
    [DataMember(Order = 11), Id(11)]
    public string? FailureReason { get; set; }

    /// <summary>Failed attempts the deletion had spent at the last reconciliation.</summary>
    [DataMember(Order = 12), Id(12)]
    public int ExecutionAttempts { get; set; }

    /// <summary>
    /// When the queue first saw the erasure this entry authorised finish, or null while it has not.
    /// </summary>
    /// <remarks>
    /// <para>The clock the retention window runs on, and the reason it is a stamp of its own rather
    /// than a reading of <c>DecidedAt</c>: the two are a grace period apart — thirty days at shipped
    /// values — so a week measured from the decision would retire most completed entries before the
    /// erasure had even run, and one measured from a deletion that stranded and was resumed months
    /// later would retire it the instant it finished.</para>
    ///
    /// <para>"When the queue noticed" rather than "when it finished", like
    /// <see cref="StrandedSince"/> and for the same reason: the difference is at most one
    /// reconciliation, and the alternative is the deletion grain having to know a queue exists. It is
    /// null on every entry written before this field, which is correct — none of them can be
    /// <see cref="QueuedAccountDeletionState.Completed"/>, that state being what stamps it.</para>
    /// </remarks>
    [DataMember(Order = 13), Id(13)]
    public DateTimeOffset? CompletedAt { get; set; }
}
