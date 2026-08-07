namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Report submission and the trust score derived from it. Neither had any coverage, and both are
/// inert unless <c>ReportSystem:IsEnabled</c> is set — see <c>TestServerConfiguration</c>, which
/// supplies a complete, validator-approved moderation configuration to the test host so these paths
/// actually execute rather than short-circuiting on their first line.
/// </summary>
[TestFixture]
public class ModerationTests : TestBase
{
    private IReportInteraction Reports(IServiceProvider provider)
        => IonClient.ForService<IReportInteraction>(provider);

    private static ReportTarget UserTarget(Guid userId)
        => new(ReportTargetKind.USER, userId, null, null);

    [Test, CancelAfter(120_000)]
    public async Task SubmitReport_AgainstAnotherUser_IsAccepted(CancellationToken ct = default)
    {
        var target = await CreateSessionAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var result = await Reports(scope.ServiceProvider).SubmitReport(
            new CreateReportInput(UserTarget(target.UserId), ReportCategory.SPAM, ReportReason.SPAM_OTHER, "test report", null),
            ct);

        Assert.That(result, Is.InstanceOf<SuccessSubmitReport>());
    }

    [Test, CancelAfter(120_000)]
    public async Task SubmitReport_AgainstYourself_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var result = await Reports(scope.ServiceProvider).SubmitReport(
            new CreateReportInput(UserTarget(me.userId), ReportCategory.SPAM, ReportReason.SPAM_OTHER, null, null),
            ct);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<FailedSubmitReport>());
            Assert.That(((FailedSubmitReport)result).error, Is.EqualTo(SubmitReportError.CANNOT_REPORT_SELF));
        });
    }

    /// <summary>
    /// GetMyReports deliberately returns nothing: exposing a user their own report history would let
    /// them enumerate which reports stuck and infer moderator behaviour (see the note in
    /// ReportGrain.GetMyReportsAsync). This pins that decision so a future change to it is a
    /// conscious one rather than an accident.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task GetMyReports_DeliberatelyReturnsNothing(CancellationToken ct = default)
    {
        var target = await CreateSessionAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        await Reports(scope.ServiceProvider).SubmitReport(
            new CreateReportInput(UserTarget(target.UserId), ReportCategory.SPAM, ReportReason.SPAM_OTHER, "listed", null),
            ct);

        var mine = await Reports(scope.ServiceProvider).GetMyReports(50, 0, ct);

        Assert.That(mine.Size, Is.EqualTo(0));
    }

    [Test, CancelAfter(120_000)]
    public async Task SubmitReport_TwiceForTheSameTargetAndCategory_IsDeduplicated(CancellationToken ct = default)
    {
        // The second submission is swallowed rather than rejected — a user who taps "report" twice
        // should not see an error, but only one report should reach the moderation queue.
        var target = await CreateSessionAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var input = new CreateReportInput(UserTarget(target.UserId), ReportCategory.SPAM, ReportReason.SPAM_OTHER, null, null);

        await Reports(scope.ServiceProvider).SubmitReport(input, ct);
        var second = await Reports(scope.ServiceProvider).SubmitReport(input, ct);

        Assert.That(second, Is.InstanceOf<SuccessSubmitReport>());

        // Only one report should have reached the queue. The reporter cannot see their own history,
        // so the trust recalculation - which counts rows - is what proves the dedupe happened.
        var trust = await GetGrainFactory().GetGrain<IUserTrustGrain>(target.UserId).RecalculateTrustAsync(ct);
        Assert.That(trust.totalReportsReceived, Is.EqualTo(1));
    }

    // ── Trust scoring ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task GetTrustScore_ForANewUser_IsTheConfiguredDefault(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var trust = await GetGrainFactory().GetGrain<IUserTrustGrain>(me.userId).GetTrustScoreAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(trust.userId, Is.EqualTo(me.userId));
            Assert.That(trust.trustScore, Is.EqualTo(50), "TrustScoring:DefaultTrustScore in the test configuration");
            Assert.That(trust.totalReportsReceived, Is.EqualTo(0));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RecalculateTrust_CountsReportsFiledAgainstTheUser(CancellationToken ct = default)
    {
        var target = await CreateSessionAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        await Reports(scope.ServiceProvider).SubmitReport(
            new CreateReportInput(UserTarget(target.UserId), ReportCategory.SPAM, ReportReason.SPAM_OTHER, null, null),
            ct);

        var trust = await GetGrainFactory().GetGrain<IUserTrustGrain>(target.UserId).RecalculateTrustAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(trust.totalReportsReceived, Is.EqualTo(1));
            Assert.That(trust.confirmedReportsReceived, Is.EqualTo(0), "nothing has been confirmed by a moderator yet");
            Assert.That(trust.trustScore, Is.InRange(0, 100));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RecalculateTrust_CountsReportsTheUserFiled(CancellationToken ct = default)
    {
        var target = await CreateSessionAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var reporter = await GetUserService(scope.ServiceProvider).GetMe(ct);

        await Reports(scope.ServiceProvider).SubmitReport(
            new CreateReportInput(UserTarget(target.UserId), ReportCategory.SPAM, ReportReason.SPAM_OTHER, null, null),
            ct);

        var trust = await GetGrainFactory().GetGrain<IUserTrustGrain>(reporter.userId).RecalculateTrustAsync(ct);

        Assert.That(trust.totalReportsFiled, Is.EqualTo(1));
    }

    [Test, CancelAfter(120_000)]
    public async Task OnReportReceived_And_OnReportResolved_AreAccepted(CancellationToken ct = default)
    {
        // Both are fire-and-forget hooks called from the report pipeline; the contract they have to
        // honour is simply that they never throw for a user with no trust row yet.
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var trustGrain = GetGrainFactory().GetGrain<IUserTrustGrain>(me.userId);

        await trustGrain.OnReportReceivedAsync(ReportCategory.SPAM, ct);
        await trustGrain.OnReportResolvedAsync(ReportStatus.DISMISSED, ct);

        var trust = await trustGrain.GetTrustScoreAsync(ct);
        Assert.That(trust.trustScore, Is.InRange(0, 100));
    }
}
