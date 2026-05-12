namespace Argon.Services.Ion;

using ion.runtime;

public class ReportInteractionImpl : IReportInteraction
{
    public async Task<ISubmitReportResult> SubmitReport(CreateReportInput input, CancellationToken ct = default)
        => await this.GetGrain<IReportGrain>(Guid.CreateVersion7()).SubmitReportAsync(input, ct);

    public async Task<IonArray<ReportInfo>> GetMyReports(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IReportGrain>(Guid.CreateVersion7()).GetMyReportsAsync(limit, offset, ct);
}
