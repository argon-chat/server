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
    NotConfigured
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
    [Id(6)] public int TotalItemsEstimate { get; init; }
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
