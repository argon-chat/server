namespace ArgonComplexTest.Tests;

using Argon.Api.Features.AdminApi;
using Argon.Features.Admin;
using Argon.Grains.Interfaces;
using ArgonContracts;
using ConsoleContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The end-to-end moderation loop: a user files a report, an operator picks it up through the admin
/// console, resolves it, and the trust scores on both sides move. Each half had coverage on its own;
/// the seam between them — where a resolution feeds back into trust — did not.
/// </summary>
[TestFixture]
public class AdminModerationWorkflowTests : TestBase
{
    private static readonly Guid OperatorId = Guid.Parse("00000000-0000-0000-0000-0000000ad002");

    private (AsyncServiceScope Scope, IAdminConsole Console) Admin()
    {
        var scope = FactoryAsp.Services.CreateAsyncScope();

        OperatorRequestContext.Set(new OperatorRequestContextData
        {
            UserId                = Guid.Parse("00000000-0000-0000-0000-0000000ad003"),
            OperatorId            = OperatorId,
            Email                 = "moderator@argon.test",
            CertificateThumbprint = "TEST-THUMBPRINT"
        });

        return (scope, scope.ServiceProvider.GetRequiredService<IAdminConsole>());
    }

    /// <summary>Files a report against a fresh user and returns (reporterId, targetId).</summary>
    private async Task<(Guid ReporterId, Guid TargetId)> FileReportAsync(
        ReportCategory category, ReportReason reason, CancellationToken ct)
    {
        var target = await CreateSessionAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var reporter = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var result = await IonClient.ForService<IReportInteraction>(scope.ServiceProvider).SubmitReport(
            new CreateReportInput(
                new ReportTarget(ReportTargetKind.USER, target.UserId, null, null),
                category, reason, "filed by the moderation workflow tests", null),
            ct);

        Assert.That(result, Is.InstanceOf<SuccessSubmitReport>());

        return (reporter.userId, target.UserId);
    }

    private async Task<AdminReportEntry> FindReportAsync(IAdminConsole admin, Guid targetId, CancellationToken ct)
    {
        var page = await admin.GetReports(null, null, 200, 0, ct);
        var entry = page.reports.Values.FirstOrDefault(r => r.target.targetId == targetId);

        Assert.That(entry, Is.Not.Null, "the report should be visible in the moderation queue");
        return entry!;
    }

    [Test, CancelAfter(180_000)]
    public async Task ReportedUser_AppearsInTheModerationQueueWithItsCategory(CancellationToken ct = default)
    {
        var (_, targetId) = await FileReportAsync(ReportCategory.SPAM, ReportReason.SPAM_OTHER, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var entry = await FindReportAsync(admin, targetId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(entry.category, Is.EqualTo(ReportCategory.SPAM));
            Assert.That(entry.status, Is.EqualTo(ReportStatus.PENDING));
            Assert.That(entry.reporterUsername, Is.Not.Empty);
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task GetReportById_ReturnsTheSameEntryAsTheQueue(CancellationToken ct = default)
    {
        var (_, targetId) = await FileReportAsync(ReportCategory.SPAM, ReportReason.SPAM_OTHER, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var fromQueue = await FindReportAsync(admin, targetId, ct);
        var byId      = await admin.GetReportById(fromQueue.reportId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(byId.reportId, Is.EqualTo(fromQueue.reportId));
            Assert.That(byId.target.targetId, Is.EqualTo(targetId));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task AssignReport_MovesItToUnderReview(CancellationToken ct = default)
    {
        var (_, targetId) = await FileReportAsync(ReportCategory.SPAM, ReportReason.SPAM_OTHER, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var entry = await FindReportAsync(admin, targetId, ct);

        var assigned = await admin.AssignReport(entry.reportId, OperatorId, ct);
        Assert.That(assigned.success, Is.True, assigned.error);

        var reloaded = await admin.GetReportById(entry.reportId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.status, Is.EqualTo(ReportStatus.UNDER_REVIEW));
            Assert.That(reloaded.assignedOperatorId, Is.EqualTo(OperatorId));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task ResolveReport_WithActionTaken_CountsAgainstTheTargetsTrust(CancellationToken ct = default)
    {
        var (_, targetId) = await FileReportAsync(ReportCategory.SPAM, ReportReason.SPAM_OTHER, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var entry = await FindReportAsync(admin, targetId, ct);

        var resolved = await admin.ResolveReport(
            new ResolveReportInput(entry.reportId, ReportStatus.RESOLVED_ACTION_TAKEN, "confirmed spam", ReportActionKind.NONE),
            ct);
        Assert.That(resolved.success, Is.True, resolved.error);

        var reloaded = await admin.GetReportById(entry.reportId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.status, Is.EqualTo(ReportStatus.RESOLVED_ACTION_TAKEN));
            Assert.That(reloaded.resolutionNote, Is.EqualTo("confirmed spam"));
            Assert.That(reloaded.resolvedAt, Is.Not.Null);
        });

        // A confirmed report is what actually damages a user's standing; a merely pending one is not.
        var trust = await GetGrainFactory().GetGrain<IUserTrustGrain>(targetId).RecalculateTrustAsync(ct);
        Assert.That(trust.confirmedReportsReceived, Is.EqualTo(1));
    }

    [Test, CancelAfter(180_000)]
    public async Task ResolveReport_AsDismissed_CountsAgainstTheReporter(CancellationToken ct = default)
    {
        var (reporterId, targetId) = await FileReportAsync(ReportCategory.SPAM, ReportReason.SPAM_OTHER, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var entry = await FindReportAsync(admin, targetId, ct);

        var resolved = await admin.ResolveReport(
            new ResolveReportInput(entry.reportId, ReportStatus.DISMISSED, "not actionable", ReportActionKind.NONE),
            ct);
        Assert.That(resolved.success, Is.True, resolved.error);

        Assert.Multiple(async () =>
        {
            // Dismissed reports are how the system detects a nuisance reporter.
            var reporterTrust = await GetGrainFactory().GetGrain<IUserTrustGrain>(reporterId).RecalculateTrustAsync(ct);
            Assert.That(reporterTrust.falseReportsFiled, Is.EqualTo(1));

            var targetTrust = await GetGrainFactory().GetGrain<IUserTrustGrain>(targetId).RecalculateTrustAsync(ct);
            Assert.That(targetTrust.confirmedReportsReceived, Is.EqualTo(0),
                "a dismissed report must not count as confirmed against the target");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task ResolveReport_ForAnUnknownReport_Fails(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.ResolveReport(
            new ResolveReportInput(Guid.NewGuid(), ReportStatus.DISMISSED, null, ReportActionKind.NONE), ct);

        Assert.That(result.success, Is.False);
    }

    [Test, CancelAfter(180_000)]
    public async Task AssignReport_ForAnUnknownReport_Fails(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That((await admin.AssignReport(Guid.NewGuid(), OperatorId, ct)).success, Is.False);
    }

    [Test, CancelAfter(180_000)]
    public async Task GetReports_FilteredByCategory_ReturnsOnlyThatCategory(CancellationToken ct = default)
    {
        await FileReportAsync(ReportCategory.SPAM, ReportReason.SPAM_OTHER, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var page = await admin.GetReports(null, ReportCategory.SPAM, 50, 0, ct);

        Assert.Multiple(() =>
        {
            Assert.That(page.reports.Values.All(r => r.category == ReportCategory.SPAM), Is.True);
            Assert.That(page.totalCount, Is.GreaterThan(0));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task GetUserTrustCard_ReflectsAResolvedReport(CancellationToken ct = default)
    {
        var (_, targetId) = await FileReportAsync(ReportCategory.SPAM, ReportReason.SPAM_OTHER, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var entry = await FindReportAsync(admin, targetId, ct);
        await admin.ResolveReport(
            new ResolveReportInput(entry.reportId, ReportStatus.RESOLVED_ACTION_TAKEN, null, ReportActionKind.NONE), ct);

        var card = await admin.RecalculateUserTrust(targetId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(card.totalReportsReceived, Is.GreaterThanOrEqualTo(1));
            Assert.That(card.confirmedReportsReceived, Is.GreaterThanOrEqualTo(1));
            Assert.That(card.username, Is.Not.Empty);
        });
    }
}
