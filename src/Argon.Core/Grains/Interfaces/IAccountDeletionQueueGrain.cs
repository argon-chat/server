namespace Argon.Grains.Interfaces;

/// <summary>
/// The list of accounts the inactivity sweep proposes for deletion, waiting for an operator to say yes.
/// </summary>
/// <remarks>
/// <para><b>Why a queue exists at all.</b> The sweep used to erase accounts by itself:
/// <c>AutoDeleteSchedulerGrain</c> selected everyone whose last login was older than a threshold and
/// called <c>RequestAutoDeleteAsync</c> on each, which armed a grace period and, when it ran out,
/// anonymised the row. Three campaign defects came out of that one decision — the sweep read "auto-delete
/// off" as "delete after the default twelve months" (CON-2), it skipped the lockdown and owns-a-space bars
/// the interactive path refuses outright (CON-3), and a cancellation from the console was undone by the
/// next pass (CON-4) — and each fix made the automatic path a little more like the one a person walks. The
/// product decision that follows finishes that: nobody is erased on a timer. The scan proposes, an operator
/// disposes, and the thing that actually deletes is the same guarded call the console makes.</para>
///
/// <para><b>The queue is a projection of the scan, not a ledger.</b> Every pass of
/// <c>AutoDeleteSchedulerGrain.RunScanAsync</c> hands over the whole candidate set and
/// <see cref="ReconcileAsync"/> makes the queue equal to it: candidates it has not seen are enqueued,
/// entries whose account is no longer a candidate disappear. That is what makes "the entry goes away when
/// the person signs in again" true without anything having to watch logins — the next scan simply does not
/// propose them, so the entry is not carried forward. It also means an entry is never stale: what an
/// operator reads is what the last scan found.</para>
///
/// <para>A decided entry is the one exception, and it has to be. Approving takes the account out of the
/// candidate set immediately — the scan skips anything already scheduled — so the projection rule alone
/// would erase every approval from the worklist within a day, while its grace period was still running and
/// while it was still the thing most worth being able to see. So a decided entry does not ask the scan
/// whether to live; it asks the deletion. It is kept while that deletion is scheduled or running; for
/// <see cref="Argon.Features.Logic.AccountDeletionOptions.DecisionRetention"/> after it has run
/// (<see cref="QueuedAccountDeletionState.Completed"/>); and for ever if the erasure gave up half way
/// (<see cref="QueuedAccountDeletionState.Stranded"/>), because then the entry is the only record anywhere
/// that the account exists in that state. Only an approval the account holder cancelled is retired the
/// moment the deletion behind it goes away, because then there is nothing to have a record of.</para>
///
/// <para>The retention window is the half of that rule the projection cannot express and a first cut got
/// wrong. "The decision has played out, so retire it" is true of the worklist and false of the audit view
/// on top of it: the pass that retired a completed entry ran between a second and a day after the erasure
/// finished, so an operator who approved a deletion and came back to see how it went found the row gone,
/// and the account it named anonymised past recognition. What an operator reads on this page has to
/// outlive the thing it describes by long enough to be read.</para>
///
/// <para>Both directions of that rule matter. Being proposed again does not keep a decided entry alive
/// either: an account whose approved deletion the holder cancelled comes back into the candidate set once
/// the decline hold lapses, and carrying the year-old approval instead of enqueuing a fresh proposal left a
/// row that neither decision would touch — approve answered "already approved", reject answered "already
/// scheduled" (defect R23).</para>
///
/// <para><b>What the queue owns that the scan cannot.</b> A rejection. Declining a candidate has to
/// outlive the pass that proposed it, or the next scan re-proposes the same account tomorrow and for ever
/// — CON-4 again, one level up. So the grain keeps a hold per account, for
/// <see cref="Argon.Features.Logic.AccountDeletionOptions.DeclineHoldsFor"/>, and refuses to re-enqueue
/// while it stands. The account holder's own refusal is recorded elsewhere, in the deletion grain's
/// <c>DeclinedAt</c>; the scan honours both.</para>
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(IAccountDeletionQueueGrain)}")]
public interface IAccountDeletionQueueGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Well-known grain id. One queue for the whole cluster, beside the one scheduler that feeds it.
    /// </summary>
    static readonly Guid SingletonId = Guid.Parse("a0a0a0a0-dead-beef-0000-000000000002");

    /// <summary>
    /// How many accounts may be waiting for a decision at once.
    /// </summary>
    /// <remarks>
    /// A ceiling on grain state, not a policy: the queue is one Orleans record read and written whole on
    /// every pass, and a first scan of an old deployment can propose far more accounts than any operator
    /// will work through. Overflow is not lost — the scan runs daily and the longest-idle accounts are the
    /// ones kept, so the queue refills from the top as decisions drain it.
    ///
    /// <para>Public because the scan reads it too. It used to be private here and the whole candidate set
    /// crossed the grain boundary to be capped on arrival (defect R28): on a deployment switching
    /// auto-delete on for the first time that is hundreds of thousands of records in one Orleans message,
    /// each one preceded by a status call of its own. The scan now stops collecting at this number, which
    /// is only correct because both sides order by the same thing — longest idle first — and idle time
    /// grows at the same rate for everybody, so a newly eligible account always joins at the young end
    /// and never displaces an entry that is already queued.</para>
    /// </remarks>
    const int MaxEntries = 500;

    /// <summary>
    /// Makes the queue equal to the candidate set the scan just produced.
    /// </summary>
    /// <remarks>
    /// The whole set, in one call, deliberately: the drop half of the reconciliation is only correct if the
    /// grain can tell "not proposed this time" from "not proposed yet", and a per-candidate call cannot.
    /// </remarks>
    [Alias(nameof(ReconcileAsync))]
    ValueTask<AccountDeletionQueueScan> ReconcileAsync(List<AccountDeletionCandidate> candidates);

    /// <summary>One page of the queue, longest-idle first, pending decisions before decided ones.</summary>
    [Alias(nameof(ListAsync))]
    ValueTask<AccountDeletionQueueSnapshot> ListAsync(int offset, int limit);

    /// <summary>
    /// Approves one queued account: schedules its deletion through the guarded path and records who said so.
    /// </summary>
    /// <remarks>
    /// The scheduling goes through <see cref="IAccountDeletionGrain.RequestAutoDeleteAsync"/> rather than
    /// through anything of the queue's own, so an approval is refused by exactly the bars that refuse a
    /// person asking for the same deletion — lockdown, subscription, owned spaces, a standing refusal. The
    /// scan filters those out before proposing, so a refusal here means the account changed underneath the
    /// operator between the pass and the click, and the refusal is handed back rather than swallowed.
    /// </remarks>
    [Alias(nameof(ApproveAsync))]
    ValueTask<AccountDeletionQueueDecision> ApproveAsync(Guid userId, Guid operatorId, string operatorEmail);

    /// <summary>
    /// Rejects one queued account: drops the entry and holds the scan off for the decline period.
    /// </summary>
    /// <remarks>
    /// Refused for an account whose deletion is actually running, whatever the entry says about itself:
    /// calling off a live erasure belongs to the account holder's console, and an entry can be Pending in
    /// front of an armed deletion when an approval's state write was lost (defect R19).
    /// </remarks>
    [Alias(nameof(RejectAsync))]
    ValueTask<AccountDeletionQueueDecision> RejectAsync(Guid userId, Guid operatorId, string operatorEmail);

    /// <summary>
    /// One page of the erasures that stopped half way: approved, started, and given up on.
    /// </summary>
    /// <remarks>
    /// <para>Defect R5. An erasure that spends <c>AccountDeletionOptions.MaxExecutionAttempts</c>
    /// unregisters its own poll and is never re-armed, leaving an account that is anonymised but not
    /// finished — memberships live, credentials and devices still held, file references half released.
    /// Nobody could see it. The account holder cannot ask: after the third step there is no password
    /// digest and the address is <c>deleted_…@void.local</c>, so no session can be minted. This grain
    /// used to erase the record itself, retiring an approved entry as soon as its deletion was no longer
    /// running. Keeping and marking it instead is what makes the state addressable, and
    /// <see cref="IAccountDeletionGrain.ResumeAsync"/> is what an operator then calls.</para>
    ///
    /// <para>Complete regardless of where the deletion came from, because
    /// <see cref="MarkStrandedAsync"/> makes an entry for one the queue never proposed.</para>
    /// </remarks>
    [Alias(nameof(ListStrandedAsync))]
    ValueTask<AccountDeletionQueueSnapshot> ListStrandedAsync(int offset, int limit);

    /// <summary>
    /// Records that an erasure has given up with steps left undone, making an entry for it if the
    /// queue has never heard of the account.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the deletion grain reports in rather than the queue noticing.</b> Finding R5's
    /// residual half. <see cref="ListStrandedAsync"/> reads <c>Entries</c>, and until this existed the
    /// only thing that could write a <see cref="QueuedAccountDeletionState.Stranded"/> one was the
    /// reconciliation of an entry this queue had itself approved. That covers the deletions nobody
    /// asks for and misses the ones everybody does: a person deleting their own account from the
    /// console was never proposed by any scan, so a run that failed its way to the attempt bound left
    /// an account anonymised — <c>IsDeleted</c>, no password digest, address rewritten to
    /// <c>deleted_…@void.local</c> — still holding its memberships, passkeys, device history and
    /// pending contact changes, and visible to nobody. Its owner cannot sign in to ask, the scan will
    /// never propose an anonymised row, and <c>AdminConsoleImpl.ResumeAccountDeletion</c> works only
    /// for an operator who already knows the id. The counter and a log line were the whole record.</para>
    ///
    /// <para><b>Not a second source of truth.</b> The deletion grain's own <c>Stranded</c> flag stays
    /// authoritative — this is a notification, so that one surface can enumerate what no other surface
    /// can — and an entry made here is settled against that grain like any other decided one: by
    /// <see cref="RefreshAsync"/>, which <c>AdminConsoleImpl.ResumeAccountDeletion</c> calls the moment
    /// an operator acts, and by <see cref="ReconcileAsync"/> on each scan. An erasure resumed and
    /// finished is retired; nothing accumulates.</para>
    ///
    /// <para>Which is also the bound on how much this can add, and the bound is a workflow rather than
    /// a number. Only an operator can move one of these entries on — the account cannot ask, and the
    /// scan never proposes an anonymised row — so on a deployment with the sweep switched off (the
    /// production default, and the whole reason this grain exists) the stranded page is a worklist
    /// that only shrinks when somebody works it. That is the intended shape: an entry here means a
    /// half-erased account, and there is nothing correct to do with one except finish it.</para>
    ///
    /// <para>Idempotent, and deliberately not a state machine: an entry that is already
    /// <c>Stranded</c> keeps its <c>StrandedSince</c> (the stranded page sorts by it and an erasure
    /// reported twice has not become newer work) while its reason and attempt count are refreshed. An
    /// entry that is <c>Pending</c> or <c>Approved</c> is moved, because a deletion that has given up
    /// is what it now is, whatever the queue believed.</para>
    /// </remarks>
    /// <param name="userId">The account whose erasure stopped.</param>
    /// <param name="reason">The failure the erasure last recorded, for an operator to read.</param>
    /// <param name="attempts">How many attempts it had spent when it gave up.</param>
    [Alias(nameof(MarkStrandedAsync))]
    ValueTask MarkStrandedAsync(Guid userId, string? reason, int attempts);

    /// <summary>
    /// Re-reads one decided entry's deletion and settles the entry against it, now rather than on the
    /// next daily pass.
    /// </summary>
    /// <remarks>
    /// For the moment after an operator acts — resuming a stranded erasure has to take its entry off the
    /// stranded page immediately, or the page invites the same operator to resume it again. A no-op for a
    /// pending entry and for an account the queue has never heard of.
    /// </remarks>
    [Alias(nameof(RefreshAsync))]
    ValueTask RefreshAsync(Guid userId);

    /// <summary>
    /// The accounts an operator has declined and whose hold still stands.
    /// </summary>
    /// <remarks>
    /// Read by the scan before it collects, so that the accounts it must not propose do not occupy the
    /// <see cref="MaxEntries"/> places it has to fill. They would occupy all of them, given time: a
    /// declined account is dormant by definition and goes on getting more dormant, so it sorts to the very
    /// top of "longest idle" and stays there for the whole decline period. Filtering only inside
    /// <see cref="ReconcileAsync"/> — where the holds live — would then propose nothing but held accounts
    /// and leave the queue permanently empty.
    /// </remarks>
    [Alias(nameof(GetDeclineHoldsAsync))]
    ValueTask<List<Guid>> GetDeclineHoldsAsync();
}

/// <summary>One account the inactivity scan proposes for deletion, as the scan found it.</summary>
/// <remarks>
/// Carries the arithmetic that selected the account rather than only its id, because that is what an
/// operator has to check before approving: how long the account has been silent, and against which
/// threshold — the platform default, or a shorter one the account holder chose for itself.
/// </remarks>
[GenerateSerializer, Immutable]
public sealed record AccountDeletionCandidate
{
    [Id(0)] public required Guid UserId { get; init; }

    /// <summary>The newest <c>DeviceHistories.LastLoginTime</c>, or the account's creation date when there is none.</summary>
    [Id(1)] public required DateTimeOffset LastActivityAt { get; init; }

    /// <summary>The inactivity threshold the scan measured against, in months.</summary>
    [Id(2)] public required int ThresholdMonths { get; init; }

    /// <summary>Why this account was proposed, in the terms the scan decided it — see <see cref="AccountDeletionQueueReasons"/>.</summary>
    [Id(3)] public required string Reason { get; init; }
}

/// <summary>The stable <c>Reason</c> strings, so the scan and a console cannot disagree about spelling.</summary>
public static class AccountDeletionQueueReasons
{
    /// <summary>Silent for longer than the platform's own retention threshold.</summary>
    public const string InactivityDefault = "inactivity-default";

    /// <summary>Silent for longer than the threshold the account holder chose for themselves.</summary>
    public const string InactivityChosen = "inactivity-chosen";

    /// <summary>
    /// Not proposed by any scan: the entry exists only because an erasure gave up half way.
    /// </summary>
    /// <remarks>
    /// The reason on an entry <see cref="IAccountDeletionQueueGrain.MarkStrandedAsync"/> created. It
    /// is the honest answer to "why is this account here", and it also tells whoever reads the row
    /// that the inactivity arithmetic beside it — <c>LastActivityAt</c>, <c>ThresholdMonths</c> — was
    /// never measured for this account and means nothing.
    /// </remarks>
    public const string StrandedErasure = "stranded-erasure";
}

/// <summary>One entry of the queue, with whatever decision has been taken on it.</summary>
[GenerateSerializer, Immutable]
public sealed record QueuedAccountDeletion
{
    [Id(0)] public required Guid UserId { get; init; }
    [Id(1)] public required DateTimeOffset LastActivityAt { get; init; }
    [Id(2)] public required int ThresholdMonths { get; init; }
    [Id(3)] public required string Reason { get; init; }

    /// <summary>When the account first entered the queue — kept across scans, so an operator can see a backlog age.</summary>
    [Id(4)] public required DateTimeOffset EnqueuedAt { get; init; }

    [Id(5)] public required QueuedAccountDeletionState State { get; init; }
    [Id(6)] public Guid? DecidedByOperatorId { get; init; }
    [Id(7)] public string? DecidedByOperatorEmail { get; init; }
    [Id(8)] public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>When the approved deletion will execute, as the deletion grain answered it.</summary>
    [Id(9)] public DateTimeOffset? ScheduledDeletionAt { get; init; }

    /// <summary>When the queue first saw this entry's erasure give up, for a stranded one.</summary>
    [Id(10)] public DateTimeOffset? StrandedSince { get; init; }

    /// <summary>Why the erasure last stopped, as of the last reconciliation.</summary>
    [Id(11)] public string? FailureReason { get; init; }

    /// <summary>Failed attempts the erasure had spent, as of the last reconciliation.</summary>
    [Id(12)] public int ExecutionAttempts { get; init; }

    /// <summary>
    /// When the queue saw the erasure this entry authorised finish, or null while it has not.
    /// </summary>
    /// <remarks>
    /// Also what the retention window is measured from, so it is the one field that says when the row
    /// will stop being shown — see <see cref="Argon.Features.Logic.AccountDeletionOptions.DecisionRetention"/>.
    /// </remarks>
    [Id(13)] public DateTimeOffset? CompletedAt { get; init; }
}

public enum QueuedAccountDeletionState
{
    /// <summary>Proposed by the scan, waiting for an operator.</summary>
    Pending,

    /// <summary>
    /// An operator approved it and the deletion is scheduled or running. The entry stays for as long as
    /// that deletion does: it becomes <see cref="Completed"/> when the erasure runs, and is retired when
    /// the account holder cancels inside the grace period.
    /// </summary>
    Approved,

    /// <summary>
    /// The erasure this entry authorised started and stopped with steps left undone, and nothing will
    /// pick it up on its own.
    /// </summary>
    /// <remarks>
    /// The state a half-erased account settles into (defect R5). It is not "failed": a failure with
    /// attempts left is a retry that has not happened yet and needs nobody. This is the account that
    /// needs a person, and it is kept in the queue precisely because there is no other surface it can be
    /// seen from — the row is anonymised, so the scan will never propose it again, and its owner can no
    /// longer sign in to ask.
    /// </remarks>
    Stranded,

    /// <summary>
    /// The erasure this entry authorised has run to the end. The entry is no longer work of any kind;
    /// it is the record of a decision that took effect, kept for
    /// <see cref="Argon.Features.Logic.AccountDeletionOptions.DecisionRetention"/> and then retired.
    /// </summary>
    /// <remarks>
    /// <para>The state that closes the hole a projection leaves behind. Everything else in this queue
    /// answers "is there still something to do about this account", and a finished erasure answers no —
    /// so the reconciliation used to drop the entry on the first pass after the deletion completed, which
    /// is somewhere between a second and a day after the operator pressed Approve. The row an operator
    /// goes looking for the moment the irreversible thing happens was the one row the queue could not
    /// show them, and the account behind it is a tombstone by then, so nothing else on the console can
    /// stand in for it.</para>
    ///
    /// <para>A state of its own rather than an <c>Approved</c> entry with a completion stamp, for
    /// defect F9's reason one enum member later: a badge that reads "Approved" against an erasure that
    /// finished last Tuesday is indistinguishable from one still inside its grace period, where the
    /// account holder can still call it off and an operator can still be asked about it. The two rows
    /// mean opposite things and only the badge is read.</para>
    ///
    /// <para>It is deliberately not where a <em>cancelled</em> approval goes. An account whose holder
    /// called the deletion off inside the grace is alive, back under the scan's rule, and its entry is
    /// retired at once as it always was — carrying it here would put "erased" in front of an operator
    /// for an account that still exists, and carrying it as <c>Approved</c> is defect R23.</para>
    /// </remarks>
    Completed
}

/// <summary>One page of the queue.</summary>
[GenerateSerializer, Immutable]
public sealed record AccountDeletionQueueSnapshot
{
    [Id(0)] public required List<QueuedAccountDeletion> Entries { get; init; }
    [Id(1)] public required int TotalCount { get; init; }
    [Id(2)] public required int Offset { get; init; }
    [Id(3)] public required int Limit { get; init; }
}

/// <summary>What one pass of the scan did to the queue.</summary>
[GenerateSerializer, Immutable]
public sealed record AccountDeletionQueueScan
{
    /// <summary>Candidates the queue had not seen before.</summary>
    [Id(0)] public required int Enqueued { get; init; }

    /// <summary>
    /// Entries the pass dropped: a pending one whose account the scan no longer proposes (it signed in),
    /// an approval the account holder cancelled, and a completed one whose retention window has run out.
    /// </summary>
    [Id(1)] public required int Retired { get; init; }

    /// <summary>Candidates skipped because an operator has already declined them.</summary>
    [Id(2)] public required int Held { get; init; }

    /// <summary>How long the queue is now.</summary>
    [Id(3)] public required int Length { get; init; }
}

/// <summary>What an approval or a rejection did.</summary>
[GenerateSerializer, Immutable]
public sealed record AccountDeletionQueueDecision
{
    [Id(0)] public required bool Success { get; init; }
    [Id(1)] public AccountDeletionQueueDecisionError? Error { get; init; }

    /// <summary>
    /// The refusal the deletion grain gave, when <see cref="Error"/> is
    /// <see cref="AccountDeletionQueueDecisionError.RefusedByDeletionGrain"/>.
    /// </summary>
    [Id(2)] public AccountDeletionRequestError? RequestError { get; init; }

    /// <summary>When the approved deletion executes.</summary>
    [Id(3)] public DateTimeOffset? ScheduledDeletionAt { get; init; }
}

public enum AccountDeletionQueueDecisionError
{
    /// <summary>No such entry — the account was never proposed, or a scan retired it in the meantime.</summary>
    NotQueued,

    /// <summary>The entry has already been approved; approving twice would arm a second countdown.</summary>
    AlreadyDecided,

    /// <summary>
    /// The guarded path refused to schedule the deletion; <see cref="AccountDeletionQueueDecision.RequestError"/>
    /// says which bar stood.
    /// </summary>
    RefusedByDeletionGrain
}
