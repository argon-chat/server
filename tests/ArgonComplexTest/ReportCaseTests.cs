namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure;
using ArgonContracts;

/// <summary>
/// Cases: how reports about one thing become one unit of work, who counts as a distinct reporter,
/// and what makes a case urgent.
/// </summary>
/// <remarks>
/// <para>The sock-puppet scenario is the one this fixture exists for. Three accounts on one machine
/// used to be enough to hide any message on the instance; here they are three reports and one
/// reporter, and the case stays where it was. The three-strangers scenario beside it is what the
/// same rule is meant to let through.</para>
///
/// <para>Device identity is what the test host can vary — every request arrives from the same
/// absent address, which the server reports as "unknown" and the hasher declines to hash — so
/// independence here is decided on the machine id each session claims.</para>
/// </remarks>
[TestFixture]
public class ReportCaseTests : ReportTestBase
{
    private async Task<(TestUserSession Owner, Guid SpaceId, Guid ChannelId, long MessageId)> ReportableMessageAsync(string text, CancellationToken ct)
    {
        var owner = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);
        var messageId = await SayAsync(owner, spaceId, channelId, text, ct);

        return (owner, spaceId, channelId, messageId);
    }

    [Test, CancelAfter(240_000)]
    public async Task TwoReporters_ShareOneCase(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await ReportableMessageAsync("buy cheap crypto", ct);
        var first  = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);
        await JoinAsync(owner, first, spaceId, ct);
        await JoinAsync(owner, second, spaceId, ct);

        await FileAsync(first, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);
        await FileAsync(second, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.target.kind, Is.EqualTo(ReportTargetKind.MESSAGE));
            Assert.That(@case.target.channelId, Is.EqualTo(channelId));
            Assert.That(@case.target.messageId, Is.EqualTo((ulong)messageId));
            Assert.That(@case.reportCount, Is.EqualTo(2));
            Assert.That(@case.independentReporterCount, Is.EqualTo(2), "two strangers on two machines");
            Assert.That(@case.status, Is.EqualTo(ReportStatus.PENDING), "two is under the threshold of three");
            Assert.That(@case.isEscalated, Is.False);
        });

        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.That(details.reports.Values.Select(r => r.reporterId), Is.EquivalentTo(new[] { first.UserId, second.UserId }));
    }

    [Test, CancelAfter(240_000)]
    public async Task ThreeStrangers_MakeTheCaseUrgent(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await ReportableMessageAsync("something three people mind", ct);

        for (var i = 0; i < TestServerConfiguration.IndependentReportersThreshold; i++)
        {
            var reporter = await CreateSessionAsync(ct);
            await JoinAsync(owner, reporter, spaceId, ct);
            await FileAsync(reporter, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);
        }

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.independentReporterCount, Is.EqualTo(TestServerConfiguration.IndependentReportersThreshold));
            Assert.That(@case.isEscalated, Is.True);
            Assert.That(@case.escalationRule, Is.EqualTo("INDEPENDENT_CLUSTER"));
            Assert.That(@case.status, Is.EqualTo(ReportStatus.ESCALATED));
        });
    }

    /// <summary>The property the whole redesign turns on.</summary>
    [Test, CancelAfter(240_000)]
    public async Task ThreeAccountsOnOneMachine_AreOneReporter(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await ReportableMessageAsync("a perfectly fine message", ct);
        var machine = Guid.CreateVersion7();

        for (var i = 0; i < TestServerConfiguration.IndependentReportersThreshold; i++)
        {
            var puppet = await CreateSessionOnMachineAsync(machine, ct);
            await JoinAsync(owner, puppet, spaceId, ct);
            await FileAsync(puppet, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);
        }

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.reportCount, Is.EqualTo(TestServerConfiguration.IndependentReportersThreshold), "every report is kept and shown");
            Assert.That(@case.independentReporterCount, Is.EqualTo(1), "and they are one person for the purpose of escalation");
            Assert.That(@case.isEscalated, Is.False);
            Assert.That(@case.status, Is.EqualTo(ReportStatus.PENDING));
        });

        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.That(details.reports.Values.Count(r => r.isIndependent), Is.EqualTo(1),
            "the first report from the machine is the independent one; the rest are marked as not");
    }

    [Test, CancelAfter(240_000)]
    public async Task ACriticalCategory_IsUrgentFromTheFirstReport(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await ReportableMessageAsync("something nobody should have to see", ct);
        var reporter = await CreateSessionAsync(ct);
        await JoinAsync(owner, reporter, spaceId, ct);

        // VIOLENCE is the critical category in the test configuration.
        await FileAsync(reporter, Report(MessageTarget(owner.UserId, channelId, messageId), ReportCategory.VIOLENCE, ReportReason.EXTREME_VIOLENCE), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.status, Is.EqualTo(ReportStatus.ESCALATED));
            Assert.That(@case.escalationRule, Is.EqualTo("CRITICAL_CATEGORY"));
            Assert.That(@case.topCategory, Is.EqualTo(ReportCategory.VIOLENCE));
        });
    }

    /// <summary>
    /// The old system's one automatic action overwrote the message text; the evidence the case
    /// was about was the first thing it destroyed. The snapshot is what a moderator now reads.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task TheCase_KeepsWhatWasReportedAfterTheAuthorDeletesIt(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await ReportableMessageAsync("the exact words that were reported", ct);
        var reporter = await CreateSessionAsync(ct);
        await JoinAsync(owner, reporter, spaceId, ct);

        await FileAsync(reporter, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);

        var deleted = await owner.Channels.DeleteMessage(spaceId, channelId, messageId, ct);
        Assert.That(deleted, Is.InstanceOf<SuccessDeleteMessage>());

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case   = await FindCaseAsync(admin, owner.UserId, ct);
        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(details.contentSnapshot, Does.Contain("the exact words that were reported"));
            Assert.That(details.contentSnapshot, Does.Contain(owner.UserId.ToString()), "the author travels with the snapshot");
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task AResolvedCase_DoesNotAbsorbNewReports(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await ReportableMessageAsync("reported twice, in two lives", ct);
        var first  = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);
        await JoinAsync(owner, first, spaceId, ct);
        await JoinAsync(owner, second, spaceId, ct);

        await FileAsync(first, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var closed = await FindCaseAsync(admin, owner.UserId, ct);
        var result = await ResolveAsync(admin, closed.caseId, ReportStatus.RESOLVED_NO_ACTION, ConsoleContracts.ReportActionKind.NONE, "looked fine", ct);
        Assert.That(result.success, Is.True, result.error);

        await FileAsync(second, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);

        var reopened = await FindCaseAsync(admin, owner.UserId, ct, openOnly: true);
        var decided  = await admin.GetReportCase(closed.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(reopened.caseId, Is.Not.EqualTo(closed.caseId), "a new complaint opens a new case beside the decided one");
            Assert.That(reopened.reportCount, Is.EqualTo(1));
            Assert.That(decided.summary.status, Is.EqualTo(ReportStatus.RESOLVED_NO_ACTION), "and the decided one is not rewritten");
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task AProfileReport_AndAUserReport_AreOneCase(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var first  = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, first, spaceId, ct);
        await JoinAsync(owner, second, spaceId, ct);

        await FileAsync(first, Report(new ReportTarget(ReportTargetKind.PROFILE, owner.UserId, null, null)), ct);
        await FileAsync(second, Report(UserTarget(owner.UserId), ReportCategory.SCAM_OR_FRAUD, ReportReason.IMPERSONATION), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.target.kind, Is.EqualTo(ReportTargetKind.USER));
            Assert.That(@case.reportCount, Is.EqualTo(2));
            Assert.That(@case.topCategory, Is.EqualTo(ReportCategory.SCAM_OR_FRAUD), "the weightier category names the case");
        });
    }
}
