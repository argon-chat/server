namespace Argon.Grains.Interfaces;

using Argon.Features.Moderation;
using ArgonContracts;

/// <summary>
/// The report system: filing, and everything the operator console does with what was filed.
/// </summary>
/// <remarks>
/// <para>Stateless and keyed by nothing — callers pass a throwaway key. All state is in the
/// database, and this is the only thing that touches the report tables: the console calls in
/// here rather than reading them itself.</para>
///
/// <para><see cref="SubmitReportAsync"/> reads the caller from the request context and is the one
/// method a user reaches. The rest take the operator explicitly, because an operator's identity
/// lives in a different context that does not cross a grain call.</para>
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(IReportGrain)}")]
public interface IReportGrain : IGrainWithGuidKey
{
    [Alias(nameof(SubmitReportAsync))]
    Task<ISubmitReportResult> SubmitReportAsync(CreateReportInput input, CancellationToken ct = default);

    /// <summary>Always empty. See the contract for why.</summary>
    [Alias(nameof(GetMyReportsAsync))]
    Task<List<ReportInfo>> GetMyReportsAsync(int limit, int offset, CancellationToken ct = default);

    [Alias(nameof(GetCasesAsync))]
    Task<ReportCasePage> GetCasesAsync(ReportCaseQuery query, CancellationToken ct = default);

    [Alias(nameof(GetCaseAsync))]
    Task<ReportCaseView?> GetCaseAsync(Guid caseId, CancellationToken ct = default);

    [Alias(nameof(GetReportsAsync))]
    Task<ReportPage> GetReportsAsync(ReportCaseQuery query, CancellationToken ct = default);

    [Alias(nameof(GetReportAsync))]
    Task<ReportEntryView?> GetReportAsync(Guid reportId, CancellationToken ct = default);

    [Alias(nameof(FindCaseByReportAsync))]
    Task<Guid?> FindCaseByReportAsync(Guid reportId, CancellationToken ct = default);

    [Alias(nameof(AssignCaseAsync))]
    Task<ReportOperationResult> AssignCaseAsync(Guid caseId, Guid operatorId, CancellationToken ct = default);

    [Alias(nameof(ResolveCaseAsync))]
    Task<ReportOperationResult> ResolveCaseAsync(ResolveReportCaseCommand command, CancellationToken ct = default);

    [Alias(nameof(ReopenCaseAsync))]
    Task<ReportOperationResult> ReopenCaseAsync(Guid caseId, Guid operatorId, string? note, CancellationToken ct = default);
}
