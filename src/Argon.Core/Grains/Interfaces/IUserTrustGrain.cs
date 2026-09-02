namespace Argon.Grains.Interfaces;

using ArgonContracts;

/// <summary>
/// A user's standing — as a target of reports and as a filer of them.
/// </summary>
/// <remarks>
/// Moves only on a moderator's decision. The hook that docked a target's score the moment anyone
/// reported them is gone: it made every report a small punishment that needed no review, which
/// is the property a brigade is built on.
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(IUserTrustGrain)}")]
public interface IUserTrustGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetTrustScoreAsync))]
    Task<UserTrustInfo> GetTrustScoreAsync(CancellationToken ct = default);

    [Alias(nameof(RecalculateTrustAsync))]
    Task<UserTrustInfo> RecalculateTrustAsync(CancellationToken ct = default);

    /// <summary>A case naming this user as target was resolved or reopened; recompute.</summary>
    [Alias(nameof(OnReportResolvedAsync))]
    Task OnReportResolvedAsync(ReportStatus resolution, CancellationToken ct = default);
}
