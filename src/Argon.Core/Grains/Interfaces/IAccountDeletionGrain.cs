namespace Argon.Grains.Interfaces;

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
    [Alias(nameof(GetDeletionStatusAsync))]
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
    InternalError
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
    [Id(4)] public string? FailureReason { get; init; }
}

public enum AccountDeletionStatusKind
{
    None,
    Scheduled,
    Executing,
    Completed,
    Failed
}
