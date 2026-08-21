namespace Argon.Grains.Interfaces;

using ArgonContracts;

[Alias($"Argon.Grains.Interfaces.{nameof(IReportGrain)}")]
public interface IReportGrain : IGrainWithGuidKey
{
    [Alias(nameof(SubmitReportAsync))]
    Task<ISubmitReportResult> SubmitReportAsync(CreateReportInput input, CancellationToken ct = default);

    [Alias(nameof(GetMyReportsAsync))]
    Task<List<ReportInfo>> GetMyReportsAsync(int limit, int offset, CancellationToken ct = default);
}
