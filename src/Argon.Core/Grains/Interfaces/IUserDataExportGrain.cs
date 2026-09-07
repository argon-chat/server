namespace Argon.Grains.Interfaces;

/// <summary>
/// Grain for managing user data export archive generation.
/// Keyed by UserId. Only one active export per user at a time.
/// Rate limited to one export per 30 days.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IUserDataExportGrain)}")]
public interface IUserDataExportGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Requests a new data export. Fails if an export is already in progress or rate limited.
    /// </summary>
    [Alias(nameof(RequestExportAsync))]
    ValueTask<ExportRequestResult> RequestExportAsync();

    /// <summary>
    /// Returns the current export status and progress information.
    /// </summary>
    [Alias(nameof(GetExportStatusAsync))]
    ValueTask<ExportStatusDto> GetExportStatusAsync();

    /// <summary>
    /// Quick check if an export is currently in progress.
    /// </summary>
    [Alias(nameof(IsExportInProgressAsync))]
    ValueTask<bool> IsExportInProgressAsync();

    /// <summary>
    /// Cancels an in-progress export.
    /// </summary>
    [Alias(nameof(CancelExportAsync))]
    ValueTask CancelExportAsync();
}

[GenerateSerializer, Immutable]
public sealed record ExportRequestResult
{
    [Id(0)] public required bool Success { get; init; }
    [Id(1)] public ExportRequestError? Error { get; init; }
    [Id(2)] public Guid? ExportId { get; init; }
}

public enum ExportRequestError
{
    AlreadyInProgress,
    RateLimited,
    NotConfigured,

    /// <summary>The account is scheduled for erasure, executing it, or already erased.</summary>
    /// <remarks>
    /// The export front door is shut for an account on its way out (defect ACC-11): building a
    /// fresh, downloadable copy of everything that is about to be erased — one that outlives the
    /// account by the archive's lifetime — is the opposite of what the erasure was asked for.
    /// Cancelling the deletion lifts the refusal, which is what the console tells the person.
    /// Pinned by <c>AccountDeletionTests.An_export_cannot_be_started_for_an_account_under_deletion</c>.
    /// </remarks>
    AccountDeletionScheduled
}

[GenerateSerializer, Immutable]
public sealed record ExportStatusDto
{
    [Id(0)] public required ExportStatusKind Status { get; init; }
    [Id(1)] public Guid? ExportId { get; init; }
    [Id(2)] public DateTimeOffset? StartedAt { get; init; }
    [Id(3)] public DateTimeOffset? CompletedAt { get; init; }
    [Id(4)] public string? DownloadUrl { get; init; }
    [Id(5)] public int ItemsProcessed { get; init; }
    // Id 6 was TotalItemsEstimate, always zero and carried to the client as a denominator nothing
    // could divide by; dropped from the wire and from here with defect X8. The ordinal is retired.
    [Id(7)] public string? FailureReason { get; init; }
}

public enum ExportStatusKind
{
    Idle,
    Queued,
    CollectingData,
    Assembling,
    Completed,
    Expired,
    Failed
}
