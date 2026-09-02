namespace Argon.Features.Moderation;

using ArgonContracts;
using ConsoleContracts;

// What the report grain hands the operator console. Plain records: Orleans carries them as JSON
// and the console maps them onto its own contract, so neither side's wire types leak into the
// other.

public sealed record ReportCaseQuery(ReportStatus? Status, ReportCategory? Category, int Limit, int Offset);

public sealed record ReportCaseSummary(
    Guid             CaseId,
    ReportTargetKind TargetKind,
    Guid             TargetId,
    Guid?            ChannelId,
    long?            MessageId,
    string           TargetDisplayName,
    ReportStatus     Status,
    ReportCategory   TopCategory,
    int              PriorityScore,
    int              ReportCount,
    int              IndependentReporterCount,
    bool             IsEscalated,
    string?          EscalationRule,
    Guid?            AssignedOperatorId,
    DateTimeOffset   FirstReportedAt,
    DateTimeOffset   LastReportedAt,
    DateTimeOffset?  ResolvedAt,
    ReportActionKind AppliedAction);

public sealed record ReportCasePage(List<ReportCaseSummary> Cases, int TotalCount, int Offset, int Limit);

public sealed record ReportEntryView(
    Guid             ReportId,
    Guid             ReporterId,
    string           ReporterUsername,
    ReportTargetKind TargetKind,
    Guid             TargetId,
    Guid?            ChannelId,
    long?            MessageId,
    string           TargetDisplayName,
    ReportCategory   Category,
    ReportReason     Reason,
    string?          AdditionalInfo,
    ReportStatus     Status,
    Guid?            AssignedOperatorId,
    string?          ResolutionNote,
    DateTimeOffset   CreatedAt,
    DateTimeOffset?  ResolvedAt,
    Guid?            CaseId,
    int              PriorityScore,
    string?          EscalationRule,
    bool             IsIndependent);

public sealed record ReportPage(List<ReportEntryView> Reports, int TotalCount, int Offset, int Limit);

public sealed record ReportCaseView(
    ReportCaseSummary     Summary,
    string?               ContentSnapshot,
    List<ReportEntryView> Reports,
    string?               ResolutionNote,
    Guid?                 ResolvedByOperatorId,
    int?                  TargetTrustScore);

public sealed record ResolveReportCaseCommand(
    Guid             CaseId,
    ReportStatus     Status,
    string?          ResolutionNote,
    ReportActionKind Action,
    Guid             OperatorId);

public sealed record ReportOperationResult(bool Success, string? Error)
{
    public static ReportOperationResult Ok { get; } = new(true, null);

    public static ReportOperationResult Fail(string error) => new(false, error);
}
