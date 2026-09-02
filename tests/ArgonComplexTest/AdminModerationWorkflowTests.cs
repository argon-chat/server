namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ConsoleContracts;

/// <summary>
/// The moderation loop end to end: a case in the queue, an operator picking it up, a decision
/// that is applied rather than recorded, and the trust scores and notifications that follow.
/// </summary>
/// <remarks>
/// <para>Every action here is checked by its effect, not by the console saying it happened: a
/// delete is a message the room no longer returns, a ban is a lockdown on the user row, a warning
/// is a notification in the author's feed. The old console recorded the action in the audit log
/// and did nothing, and no test noticed.</para>
///
/// <para>The refusals are the other half: a resolved case cannot be resolved again, an action
/// needs the resolution that says an action was taken, and a user action on a case about a space
/// has nobody to land on.</para>
/// </remarks>
[TestFixture]
public class AdminModerationWorkflowTests : ReportTestBase
{
    private sealed record Scenario(TestUserSession Owner, TestUserSession Guest, Guid SpaceId, Guid ChannelId, long MessageId, Guid ReportId);

    /// <summary>A guest reports one of the owner's messages.</summary>
    private async Task<Scenario> ReportedMessageAsync(CancellationToken ct,
        ReportCategory category = ReportCategory.SPAM, ReportReason reason = ReportReason.SPAM_OTHER)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await SayAsync(owner, spaceId, channelId, "the reported text", ct);
        var reportId  = await FileAsync(guest, Report(MessageTarget(owner.UserId, channelId, messageId), category, reason, "filed by the workflow tests"), ct);

        return new Scenario(owner, guest, spaceId, channelId, messageId, reportId);
    }

    // ── Queue ───────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(240_000)]
    public async Task ReportedMessage_AppearsInTheQueueAsACase(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.topCategory, Is.EqualTo(ReportCategory.SPAM));
            Assert.That(@case.status, Is.EqualTo(ReportStatus.PENDING));
            Assert.That(@case.reportCount, Is.EqualTo(1));
            Assert.That(@case.appliedAction, Is.EqualTo(ReportActionKind.NONE));
            Assert.That(@case.targetDisplayName, Is.Not.Empty);
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task GetReportById_ReturnsTheEntryWithItsCase(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var entry = await admin.GetReportById(s.ReportId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(entry.caseId, Is.EqualTo(@case.caseId));
            Assert.That(entry.reporterId, Is.EqualTo(s.Guest.UserId));
            Assert.That(entry.status, Is.EqualTo(ReportStatus.PENDING));
            Assert.That(entry.additionalInfo, Is.EqualTo("filed by the workflow tests"));
            Assert.That(entry.isIndependent, Is.True);
        });
    }

    /// <summary>Through the older per-report method, which now acts on the report's case.</summary>
    [Test, CancelAfter(240_000)]
    public async Task AssignReport_MovesTheWholeCaseToUnderReview(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var assigned = await admin.AssignReport(s.ReportId, OperatorId, ct);
        Assert.That(assigned.success, Is.True, assigned.error);

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var entry = await admin.GetReportById(s.ReportId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.status, Is.EqualTo(ReportStatus.UNDER_REVIEW));
            Assert.That(@case.assignedOperatorId, Is.EqualTo(OperatorId));
            Assert.That(entry.status, Is.EqualTo(ReportStatus.UNDER_REVIEW));
            Assert.That(entry.assignedOperatorId, Is.EqualTo(OperatorId));
        });
    }

    // ── Actions ─────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_WithDeleteContent_TakesTheMessageDown(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.DELETE_CONTENT, "spam", ct);
        Assert.That(result.success, Is.True, result.error);

        var messages = await s.Guest.Channels.QueryMessages(s.SpaceId, s.ChannelId, null, 50, ct);
        var details  = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(messages.Values.Select(m => m.messageId), Does.Not.Contain(s.MessageId), "the room no longer shows it");
            Assert.That(details.summary.status, Is.EqualTo(ReportStatus.RESOLVED_ACTION_TAKEN));
            Assert.That(details.summary.appliedAction, Is.EqualTo(ReportActionKind.DELETE_CONTENT));
            Assert.That(details.summary.resolvedAt, Is.Not.Null);
            Assert.That(details.resolvedByOperatorId, Is.EqualTo(OperatorId));
            Assert.That(details.resolutionNote, Is.EqualTo("spam"));
            Assert.That(details.contentSnapshot, Does.Contain("the reported text"), "what was deleted is still on the case");
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_WithBan_LocksTheAuthor(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.BAN_USER, "banned", ct);
        Assert.That(result.success, Is.True, result.error);

        var user = await UserRowAsync(s.Owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(user.LockdownReason, Is.EqualTo(LockdownReason.TOS_VIOLATION));
            Assert.That(user.LockDownExpiration, Is.Null, "Actions:BanDays is 0 in the test configuration — permanent");
            Assert.That(user.LockDownIsAppealable, Is.True);
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_WithMute_IsTimed(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.MUTE_USER, null, ct);
        Assert.That(result.success, Is.True, result.error);

        var user = await UserRowAsync(s.Owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(user.LockdownReason, Is.EqualTo(LockdownReason.INCITING_MOMENT));
            Assert.That(user.LockDownExpiration, Is.Not.Null.And.GreaterThan(DateTimeOffset.UtcNow.AddHours(23)).And.LessThan(DateTimeOffset.UtcNow.AddHours(25)),
                "Actions:MuteDays is 1 in the test configuration");
        });
    }

    /// <summary>A mute must never shorten a ban.</summary>
    [Test, CancelAfter(240_000)]
    public async Task AWeakerAction_NeverWeakensAnExistingLockdown(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        // A second message, said before the ban lands — a banned author can no longer post, which
        // is the ban working, not the thing under test here.
        var second = await SayAsync(s.Owner, s.SpaceId, s.ChannelId, "another one", ct);
        await FileAsync(s.Guest, Report(MessageTarget(s.Owner.UserId, s.ChannelId, second)), ct);

        var first = await FindCaseAsync(admin, s.Owner.UserId, ct);
        Assert.That((await ResolveAsync(admin, first.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.BAN_USER, null, ct)).success, Is.True);

        // The second case about the same author, resolved with a mute.
        var secondCase = await FindCaseAsync(admin, s.Owner.UserId, ct, openOnly: true);
        var result     = await ResolveAsync(admin, secondCase.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.MUTE_USER, null, ct);
        Assert.That(result.success, Is.True, result.error);

        var user = await UserRowAsync(s.Owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(user.LockdownReason, Is.EqualTo(LockdownReason.TOS_VIOLATION));
            Assert.That(user.LockDownExpiration, Is.Null);
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_WithAWarning_NotifiesTheAuthor(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.WARN_USER, null, ct);
        Assert.That(result.success, Is.True, result.error);

        var feed = await s.Owner.Users.GetNotificationFeed(50, null, ct);

        Assert.That(feed.Values.Select(n => n.type), Does.Contain("moderation.warning"));
    }

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_TellsTheReporterWithoutSayingWhat(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.DISMISSED, ReportActionKind.NONE, "nonsense", ct);
        Assert.That(result.success, Is.True, result.error);

        var feed   = await s.Guest.Users.GetNotificationFeed(50, null, ct);
        var notice = feed.Values.FirstOrDefault(n => n.type == "report.resolved" && n.referenceId == @case.caseId);

        Assert.That(notice, Is.Not.Null, "the reporter is told the case closed");
        Assert.That(notice!.body, Does.Not.Contain("dismiss").IgnoreCase,
            "and not that it was dismissed — the word a nuisance reporter would tune against");
    }

    // ── Trust ───────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_WithActionTaken_CountsAgainstTheTarget(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case    = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var baseline = (await TrustOfAsync(s.Owner.UserId, ct)).trustScore;
        Assert.That((await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.NONE, "confirmed", ct)).success, Is.True);

        var target   = await TrustOfAsync(s.Owner.UserId, ct);
        var reporter = await TrustOfAsync(s.Guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(target.confirmedReportsReceived, Is.EqualTo(1));
            Assert.That(target.trustScore, Is.LessThan(baseline), "a confirmed report is what damages standing");
            Assert.That(reporter.falseReportsFiled, Is.EqualTo(0));
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_AsDismissed_CountsAgainstTheReporter(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case    = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var baseline = (await TrustOfAsync(s.Owner.UserId, ct)).trustScore;
        Assert.That((await ResolveAsync(admin, @case.caseId, ReportStatus.DISMISSED, ReportActionKind.NONE, "bogus", ct)).success, Is.True);

        var target   = await TrustOfAsync(s.Owner.UserId, ct);
        var reporter = await TrustOfAsync(s.Guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(reporter.falseReportsFiled, Is.EqualTo(1), "dismissed is how the system learns a nuisance reporter");
            Assert.That(target.confirmedReportsReceived, Is.EqualTo(0));
            Assert.That(target.trustScore, Is.EqualTo(baseline), "a dismissed report must not count against the target");
        });
    }

    /// <summary>An honest report that turned out not to be a violation costs nobody anything.</summary>
    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_AsNoAction_CountsAgainstNobody(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        Assert.That((await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_NO_ACTION, ReportActionKind.NONE, null, ct)).success, Is.True);

        var target   = await TrustOfAsync(s.Owner.UserId, ct);
        var reporter = await TrustOfAsync(s.Guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(target.confirmedReportsReceived, Is.EqualTo(0));
            Assert.That(reporter.falseReportsFiled, Is.EqualTo(0));
        });
    }

    /// <summary>An escalated case is an open case; escalation confirms nothing on either side.</summary>
    [Test, CancelAfter(240_000)]
    public async Task AnEscalatedCase_ConfirmsNothingUntilSomeoneDecides(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct, ReportCategory.VIOLENCE, ReportReason.EXTREME_VIOLENCE);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        Assert.That(@case.status, Is.EqualTo(ReportStatus.ESCALATED), "premise: VIOLENCE is critical in the test configuration");

        var target   = await TrustOfAsync(s.Owner.UserId, ct);
        var reporter = await TrustOfAsync(s.Guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(target.confirmedReportsReceived, Is.EqualTo(0), "urgent is not confirmed");
            Assert.That(reporter.totalReportsFiled, Is.EqualTo(1));
            Assert.That(reporter.falseReportsFiled, Is.EqualTo(0));
        });
    }

    // ── Refusals ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_Twice_IsRefused(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        Assert.That((await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_NO_ACTION, ReportActionKind.NONE, null, ct)).success, Is.True);

        var again = await ResolveAsync(admin, @case.caseId, ReportStatus.DISMISSED, ReportActionKind.NONE, null, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(again.success, Is.False);
            Assert.That((await admin.GetReportCase(@case.caseId, ct)).summary.status, Is.EqualTo(ReportStatus.RESOLVED_NO_ACTION),
                "the first decision stands");
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task ResolveCase_WithAnActionButNoActionTaken_IsRefused(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_NO_ACTION, ReportActionKind.BAN_USER, null, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(result.success, Is.False);
            Assert.That((await admin.GetReportCase(@case.caseId, ct)).summary.status, Is.EqualTo(ReportStatus.PENDING), "nothing happened");
            Assert.That((await UserRowAsync(s.Owner.UserId, ct)).LockdownReason, Is.EqualTo(LockdownReason.NONE));
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task AUserAction_OnACaseAboutASpace_IsRefused(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await FileAsync(guest, Report(new ReportTarget(ReportTargetKind.SPACE, spaceId, null, null)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, spaceId, ct);
        Assert.That(@case.target.kind, Is.EqualTo(ReportTargetKind.SPACE), "premise");

        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.BAN_USER, null, ct);

        Assert.That(result.success, Is.False, "there is no person on a space case to ban");
    }

    [Test, CancelAfter(240_000)]
    public async Task ResolveReport_ForAnUnknownReport_Fails(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.Multiple(async () =>
        {
            Assert.That((await admin.ResolveReport(new ResolveReportInput(Guid.NewGuid(), ReportStatus.DISMISSED, null, ReportActionKind.NONE), ct)).success, Is.False);
            Assert.That((await admin.AssignReport(Guid.NewGuid(), OperatorId, ct)).success, Is.False);
            Assert.That((await admin.ResolveReportCase(new ResolveReportCaseInput(Guid.NewGuid(), ReportStatus.DISMISSED, null, ReportActionKind.NONE), ct)).success, Is.False);
        });
    }

    // ── Reopening ───────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(240_000)]
    public async Task ReopenCase_ReturnsItToTheQueueAndUndoesTheConfirmation(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        Assert.That((await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.NONE, null, ct)).success, Is.True);
        Assert.That((await TrustOfAsync(s.Owner.UserId, ct)).confirmedReportsReceived, Is.EqualTo(1), "premise");

        var reopened = await admin.ReopenReportCase(@case.caseId, "second look", ct);
        Assert.That(reopened.success, Is.True, reopened.error);

        var details = await admin.GetReportCase(@case.caseId, ct);
        var entry   = await admin.GetReportById(s.ReportId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(details.summary.status, Is.EqualTo(ReportStatus.PENDING));
            Assert.That(details.summary.resolvedAt, Is.Null);
            Assert.That(details.resolutionNote, Is.EqualTo("second look"));
            Assert.That(entry.status, Is.EqualTo(ReportStatus.PENDING));
            Assert.That((await TrustOfAsync(s.Owner.UserId, ct)).confirmedReportsReceived, Is.EqualTo(0),
                "a reopened confirmation is not a confirmation");
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task ReopenCase_WhenANewerOneIsOpen_IsRefused(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var closed = await FindCaseAsync(admin, s.Owner.UserId, ct);
        Assert.That((await ResolveAsync(admin, closed.caseId, ReportStatus.RESOLVED_NO_ACTION, ReportActionKind.NONE, null, ct)).success, Is.True);

        // Another guest complains about the same message: a new open case for the same thing.
        var another = await CreateSessionAsync(ct);
        await JoinAsync(s.Owner, another, s.SpaceId, ct);
        await FileAsync(another, Report(MessageTarget(s.Owner.UserId, s.ChannelId, s.MessageId)), ct);

        var result = await admin.ReopenReportCase(closed.caseId, null, ct);

        Assert.That(result.success, Is.False, "one open case per thing; the newer one is the one to work");
    }

    // ── The older per-report surface ────────────────────────────────────────────────────────────

    [Test, CancelAfter(240_000)]
    public async Task ResolveReport_ResolvesTheReportsCase(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var resolved = await admin.ResolveReport(new ResolveReportInput(s.ReportId, ReportStatus.RESOLVED_NO_ACTION, "via the old path", ReportActionKind.NONE), ct);
        Assert.That(resolved.success, Is.True, resolved.error);

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        var entry = await admin.GetReportById(s.ReportId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.status, Is.EqualTo(ReportStatus.RESOLVED_NO_ACTION));
            Assert.That(entry.status, Is.EqualTo(ReportStatus.RESOLVED_NO_ACTION));
            Assert.That(entry.resolutionNote, Is.EqualTo("via the old path"));
            Assert.That(entry.resolvedAt, Is.Not.Null);
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task GetReports_FilteredByCategory_ReturnsOnlyThatCategory(CancellationToken ct = default)
    {
        await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var page = await admin.GetReports(null, ReportCategory.SPAM, 50, 0, ct);

        Assert.Multiple(() =>
        {
            Assert.That(page.reports.Values.All(r => r.category == ReportCategory.SPAM), Is.True);
            Assert.That(page.totalCount, Is.GreaterThan(0));
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task GetUserTrustCard_ReflectsAResolvedCase(CancellationToken ct = default)
    {
        var s = await ReportedMessageAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, s.Owner.UserId, ct);
        Assert.That((await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.NONE, null, ct)).success, Is.True);

        var card = await admin.RecalculateUserTrust(s.Owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(card.totalReportsReceived, Is.GreaterThanOrEqualTo(1));
            Assert.That(card.confirmedReportsReceived, Is.GreaterThanOrEqualTo(1));
            Assert.That(card.username, Is.Not.Empty);
        });
    }
}
