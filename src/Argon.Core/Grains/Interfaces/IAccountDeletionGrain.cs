namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// Grain for managing scheduled account deletion with configurable grace period.
/// Keyed by UserId. Handles scheduling, reminders, cancellation, and execution.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IAccountDeletionGrain)}")]
public interface IAccountDeletionGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Requests account deletion. Validates preconditions (no active subscription, no owned spaces)
    /// and schedules deletion after the configured grace period.
    /// </summary>
    [Alias(nameof(RequestDeletionAsync))]
    ValueTask<AccountDeletionRequestResult> RequestDeletionAsync(string password);

    /// <summary>
    /// Cancels a scheduled deletion. Only valid when status is Scheduled.
    /// </summary>
    [Alias(nameof(CancelDeletionAsync))]
    ValueTask<AccountDeletionCancelResult> CancelDeletionAsync();

    /// <summary>
    /// Returns the current deletion status and scheduling information.
    /// </summary>
    /// <remarks>
    /// <para><b><c>[AlwaysInterleave]</c> is what keeps this grain out of a deadlock.</b> Four callers
    /// read the status — the account console, <c>UserDataExportGrain.RequestExportAsync</c>,
    /// <c>AutoDeleteSchedulerGrain</c> and <c>AccountDeletionQueueGrain</c> — and one of them closes a
    /// cycle between two non-reentrant grains: the export grain holds its own turn while it asks
    /// whether a deletion stands, and the erasure calls <c>IUserDataExportGrain.CancelExportAsync</c>
    /// from step 2 of an execution that holds this grain's turn across all ten steps. Somebody
    /// pressing "export my data" while their own erasure runs used to leave both activations waiting
    /// on each other until the Orleans request timeout broke one of them: the console call failed and
    /// the erasure's archive purge was skipped for that attempt.</para>
    ///
    /// <para><c>[ReadOnly]</c> would not have been enough, and the difference is the whole point.
    /// Orleans lets a read-only request interleave only when the request currently holding the
    /// activation is itself read-only; the one holding it here is <c>CheckAndExecuteAsync</c>, a
    /// writer. <c>[AlwaysInterleave]</c> is the attribute that interleaves with a write, which is why
    /// <see cref="IAuthorizationGrain"/> and <c>ISpaceReadGrain</c> reach for it too.</para>
    ///
    /// <para>Safe to interleave because the implementation is a synchronous projection of grain state
    /// with no <c>await</c> anywhere in it: it reads one turn's worth of fields and cannot observe a
    /// half-applied write. It also stops the daily inactivity scan and the operator queue — one such
    /// call per candidate — from queueing behind a multi-minute erasure.</para>
    /// </remarks>
    [Alias(nameof(GetDeletionStatusAsync)), AlwaysInterleave]
    ValueTask<AccountDeletionStatusDto> GetDeletionStatusAsync();

    /// <summary>
    /// Internal: called by timer to check reminders and execute deletion when time arrives.
    /// </summary>
    [Alias(nameof(CheckAndExecuteAsync))]
    ValueTask CheckAndExecuteAsync();

    /// <summary>
    /// Requests auto-deletion due to inactivity. Called by the system worker (no password required).
    /// Skips password check but still validates active subscription and owned spaces.
    /// </summary>
    [Alias(nameof(RequestAutoDeleteAsync))]
    ValueTask<AccountDeletionRequestResult> RequestAutoDeleteAsync();

    /// <summary>
    /// Picks a half-finished erasure back up after it has spent its attempts and stopped polling
    /// itself, and answers with the status an operator should now see.
    /// </summary>
    /// <remarks>
    /// <para>A run that exhausts <c>AccountDeletionOptions.MaxExecutionAttempts</c> unregisters its
    /// own poll and is never re-armed on any later activation. That part is deliberate — an erasure
    /// the grain cannot finish must stop hammering the tables — but it used to be the end of the
    /// story, and the account it left behind was unreachable by everybody. The holder cannot reach
    /// it: once the third step has run there is no password digest and the address is
    /// <c>deleted_{id}@void.local</c>, so no session can be minted. The operator queue cannot: its
    /// reconciliation retires an entry whose account is no longer proposed, and an anonymised row is
    /// not proposed. What was left of a half-erased account was a counter and a log line.</para>
    ///
    /// <para>This is the way back in. It clears the failure count and arms the poll to fire at once;
    /// <see cref="CheckAndExecuteAsync"/> then resumes from
    /// <c>AccountDeletionGrainState.StepsDone</c> exactly as it does after a lost activation, so no
    /// step that already happened runs a second time — which matters, because releasing a file
    /// reference twice is not something a later attempt can take back.</para>
    ///
    /// <para>Deliberately not the execution itself: an erasure is minutes of database work and one
    /// grain call per space and per file, and an operator's request must not be held open for it. The
    /// status that comes back is the one to render — the cleared attempt count is visible in it
    /// immediately, the completion is not.</para>
    ///
    /// <para>A no-op for a status with nothing in front of it (<c>None</c>, <c>Completed</c>): asking
    /// to resume a deletion that never started or has already finished must not arm a poll.</para>
    /// </remarks>
    [Alias(nameof(ResumeAsync))]
    ValueTask<AccountDeletionStatusDto> ResumeAsync();
}

[GenerateSerializer, Immutable]
public sealed record AccountDeletionRequestResult
{
    [Id(0)] public required bool Success { get; init; }
    [Id(1)] public AccountDeletionRequestError? Error { get; init; }
    [Id(2)] public DateTimeOffset? ScheduledDeletionAt { get; init; }
}

public enum AccountDeletionRequestError
{
    InvalidPassword,
    AlreadyScheduled,
    HasActiveSubscription,
    OwnsSpaces,
    AccountLocked,
    InternalError,

    /// <summary>
    /// The account holder has already refused a deletion recently, and the refusal still stands.
    /// </summary>
    /// <remarks>
    /// Only ever answered to <see cref="IAccountDeletionGrain.RequestAutoDeleteAsync"/> — a person
    /// asking to delete their own account is never refused because they once declined. Defect CON-4:
    /// without a durable record of the refusal the inactivity sweeper re-scheduled a cancelled
    /// account on its very next pass, so "no" lasted a day.
    /// </remarks>
    RecentlyDeclined
}

[GenerateSerializer, Immutable]
public sealed record AccountDeletionCancelResult
{
    [Id(0)] public required bool Success { get; init; }
    [Id(1)] public AccountDeletionCancelError? Error { get; init; }
}

public enum AccountDeletionCancelError
{
    NotScheduled,
    AlreadyExecuting,
    AlreadyCompleted,
    InternalError
}

[GenerateSerializer, Immutable]
public sealed record AccountDeletionStatusDto
{
    [Id(0)] public required AccountDeletionStatusKind Status { get; init; }
    [Id(1)] public DateTimeOffset? ScheduledAt { get; init; }
    [Id(2)] public DateTimeOffset? ExecutionAt { get; init; }
    [Id(3)] public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Why the last attempt stopped, or null if none ever has.</summary>
    /// <remarks>
    /// Kept whatever happens next — a retry that is still coming leaves it in place — so this field
    /// alone cannot say whether anything is still trying. <see cref="Stranded"/> is what says that.
    /// </remarks>
    [Id(4)] public string? FailureReason { get; init; }

    /// <summary>
    /// When the account holder last called a deletion off, or null if they never have.
    /// </summary>
    /// <remarks>
    /// Carried on the status so the inactivity scan can see a refusal it would otherwise have no way
    /// of learning about (defect CON-4): the console writes no activity row, so
    /// <c>max(DeviceHistories.LastLoginTime)</c> — the only thing the scan reads today — is unchanged
    /// by a person answering the notice mail. The grain already refuses
    /// <see cref="IAccountDeletionGrain.RequestAutoDeleteAsync"/> while the refusal stands; exposing
    /// the instant lets the scheduler skip the candidate before it makes the call at all.
    /// </remarks>
    [Id(5)] public DateTimeOffset? DeclinedAt { get; init; }

    /// <summary>
    /// Failed attempts since the last one that got a step further.
    /// </summary>
    /// <remarks>
    /// Only failures count and progress resets the count, so an erasure that is advancing is never
    /// starved of attempts and one that cannot advance stops after
    /// <c>AccountDeletionOptions.MaxExecutionAttempts</c>. Exposed because it is the difference
    /// between "this failed once and will be retried in a moment" and "this has given up", and
    /// <see cref="FailureReason"/> reads identically in both.
    /// </remarks>
    [Id(6)] public int ExecutionAttempts { get; init; }

    /// <summary>
    /// True when the erasure has stopped by itself and nothing will ever poll it again.
    /// </summary>
    /// <remarks>
    /// <para>The state a half-erased account settles into: <c>Failed</c> with the attempt bound spent,
    /// the poll unregistered, and no activation that will re-arm it. It has to be visible somewhere or
    /// the account is simply lost — the person it belongs to can no longer sign in to ask about it,
    /// and the operator queue has already retired its row. An operator surface reads this flag, and
    /// <see cref="IAccountDeletionGrain.ResumeAsync"/> is what it calls.</para>
    ///
    /// <para>Not a synonym for <c>Failed</c>: a failure with attempts left is a retry that has not
    /// happened yet, and nothing needs to be done about it.</para>
    /// </remarks>
    [Id(7)] public bool Stranded { get; init; }
}

public enum AccountDeletionStatusKind
{
    None,
    Scheduled,
    Executing,
    Completed,
    Failed
}
