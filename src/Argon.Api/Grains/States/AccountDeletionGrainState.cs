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

    /// <summary>
    /// The steps of the execution that have already happened, so an interrupted one can be picked up
    /// where it stopped instead of starting over or stopping for ever.
    /// </summary>
    /// <remarks>
    /// <para>Defect ACC-15, pinned by
    /// <c>AccountDeletionTests.An_interrupted_execution_does_not_strand_the_account</c>. The execution
    /// writes <see cref="AccountDeletionStatus.Executing"/> before it does any work and then runs
    /// eleven steps in one turn, so losing the activation half way — a deploy, an eviction, an OOM —
    /// used to leave the account anonymised, un-erased, un-cancellable and un-re-requestable for ever.
    /// Resumption is the fix, and a cursor is what makes resumption safe rather than a second kind of
    /// corruption: most steps are set-based and idempotent, but
    /// <c>AccountDeletionGrain.DecrementFileRefsAsync</c> is not — a second unconditional pass would
    /// release every owned file a second time.</para>
    ///
    /// <para>Shaped like <see cref="RemindersSent"/> deliberately: a set of small stable integers,
    /// written after each step, so a record produced by an older build (no cursor at all) reads back
    /// as "nothing done yet" and simply runs the whole execution again — which is the behaviour that
    /// build already had.</para>
    /// </remarks>
    [DataMember(Order = 9), Id(9)]
    public HashSet<int> StepsDone { get; set; } = [];

    /// <summary>
    /// How many attempts have failed since the last one that got a step further.
    /// </summary>
    /// <remarks>
    /// <para>The bound on the retry loop ACC-15 introduces. A deletion that keeps failing for a reason
    /// the grain cannot fix — a constraint violation, a database that refuses the write — must stop
    /// hammering the tables and stay <see cref="AccountDeletionStatus.Failed"/> with its reason
    /// visible, rather than retry every poll for the rest of the deployment's life.</para>
    ///
    /// <para><b>Failures only, and progress clears it.</b> It used to count entries into the
    /// execution, which charged a resumption the same as a crash: a grace that elapsed during a
    /// rolling deploy could lose two activations with no exception anywhere and arrive at the next
    /// poll with two of its three attempts gone, so the first ordinary transient error exhausted the
    /// bound and stranded an account that was already anonymised and still holding everything the
    /// remaining steps erase. <c>AccountDeletionGrain.RunStepAsync</c> resets it whenever a step
    /// records itself, so the bound is on attempts that achieve nothing — which is the thing it was
    /// always meant to stop.</para>
    ///
    /// <para>Reported on <c>AccountDeletionStatusDto.ExecutionAttempts</c> alongside
    /// <c>Stranded</c>, because once it reaches the bound nobody who could act on the account can
    /// reach it any other way.</para>
    /// </remarks>
    [DataMember(Order = 10), Id(10)]
    public int ExecutionAttempts { get; set; }

    /// <summary>
    /// When the account holder last said no to a deletion.
    /// </summary>
    /// <remarks>
    /// <para>Defect CON-4, pinned by
    /// <c>AccountConsoleTests.An_auto_delete_cancelled_from_the_console_is_not_reinstated_by_the_next_scan</c>.
    /// A cancellation used to reset this grain's own state and write nothing else, while
    /// <c>AutoDeleteSchedulerGrain</c> decides purely from
    /// <c>max(DeviceHistories.LastLoginTime) ?? Users.CreatedAt</c> — which no console action touches,
    /// because the console authenticates against Aegis and never reaches
    /// <c>UserGrain.UpdateUserDeviceHistory</c>. So the sweeper re-scheduled the same account on its
    /// next pass, with a fresh notice mail and a fresh grace, for ever.</para>
    ///
    /// <para>Written on every cancellation rather than only on cancelled auto-deletions: a person who
    /// calls off a deletion they asked for themselves is an even stronger sign of life than one who
    /// answers a notice. Read by <c>AccountDeletionGrain.RequestAutoDeleteAsync</c> and exposed on
    /// <c>AccountDeletionStatusDto</c> so the scheduler side can read it too.</para>
    /// </remarks>
    [DataMember(Order = 11), Id(11)]
    public DateTimeOffset? DeclinedAt { get; set; }

    /// <summary>
    /// Spaces this account's membership row has already been removed from, and which have not been
    /// told about it yet.
    /// </summary>
    /// <remarks>
    /// <para>The one thing <see cref="StepsDone"/> cannot express. Finding R22:
    /// <c>SpaceGrain.RemoveMemberAsync</c> commits the soft-delete, then invalidates the cached
    /// roster, then fires <c>LeavedFromServerUser</c>, so a bus or cache outage in the middle leaves a
    /// space whose row is gone and whose members were never told. The next attempt of
    /// <c>AccountDeletionGrain.RemoveMembershipsAsync</c> selects memberships <c>where !IsDeleted</c>
    /// and no longer sees that space, and <c>RemoveMemberAsync</c> is silent by design once the row is
    /// gone, so the retry announces nothing and records the step as done. No bot ever gets its
    /// <c>MemberLeave</c> and every client holding the space keeps the erased member until it happens
    /// to bootstrap again — defect ACC-03, made permanent on the error path.</para>
    ///
    /// <para>So the debt is written down where a retry can find it, and
    /// <c>ISpaceGrain.AnnounceMemberLeftAsync</c> is what pays it. Ids rather than a count, because
    /// paying it means naming the spaces; a set rather than a list, because the same space may fail
    /// on several attempts and the announcement is owed once. Cleared per space as each announcement
    /// lands, so an attempt that recovers three of four spaces is not asked for those three again,
    /// and cleared wholesale by <c>BeginCountdown</c> and by a cancellation, both of which are
    /// starting a different deletion.</para>
    ///
    /// <para>An old record simply has none, which reads as "nothing owed" — the behaviour that build
    /// already had.</para>
    /// </remarks>
    [DataMember(Order = 12), Id(12)]
    public HashSet<Guid> PendingDepartureAnnouncements { get; set; } = [];
}

public enum AccountDeletionStatus
{
    None,
    Scheduled,
    Executing,
    Completed,
    Failed
}
