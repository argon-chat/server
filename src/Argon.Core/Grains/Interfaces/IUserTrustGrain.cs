namespace Argon.Grains.Interfaces;

using ArgonContracts;

[Alias($"Argon.Grains.Interfaces.{nameof(IUserTrustGrain)}")]
public interface IUserTrustGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetTrustScoreAsync))]
    Task<UserTrustInfo> GetTrustScoreAsync(CancellationToken ct = default);

    [Alias(nameof(RecalculateTrustAsync))]
    Task<UserTrustInfo> RecalculateTrustAsync(CancellationToken ct = default);

    [Alias(nameof(OnReportReceivedAsync))]
    Task OnReportReceivedAsync(ReportCategory category, CancellationToken ct = default);

    [Alias(nameof(OnReportResolvedAsync))]
    Task OnReportResolvedAsync(ReportStatus resolution, CancellationToken ct = default);
}
