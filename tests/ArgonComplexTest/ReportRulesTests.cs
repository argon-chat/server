namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Moderation;
using Argon.Grains.Interfaces;
using Argon.Services;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReportActionKind = ConsoleContracts.ReportActionKind;

/// <summary>
/// The report grain's rules at the edges: the targets the other report fixtures do not file against,
/// the limits that silence a reporter, and the operator moves the console must refuse.
/// </summary>
/// <remarks>
/// <para><c>ModerationTests</c>, <c>ReportCaseTests</c> and <c>AdminModerationWorkflowTests</c> own the
/// main line — a message reported, a case formed, a decision applied. This fixture owns what surrounds
/// it. On the filing side: channels and spaces as targets, the accounts nobody may report, the legacy
/// direct-message shape pointed back at its sender, and the three filing limits and the lockdown that
/// make a report heard but not kept. On the operator side: every move the case state machine refuses,
/// an action the grain does not know, a ban with nobody left to land on, and content removal in a
/// direct conversation.</para>
///
/// <para>"Heard but not kept" is asserted in the database rather than in the answer, because the
/// answer is deliberately identical to a kept report's — that is the rule. Counting the reporter's
/// rows is the only way to tell the two apart.</para>
/// </remarks>
[TestFixture]
public class ReportRulesTests : ReportTestBase
{
    private static ReportFilingOptions Filing
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<IOptions<ReportSystemOptions>>().Value.Filing;

    // ── targets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A channel is reported from inside its space, and the case is named after the channel.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task A_channel_is_reported_from_inside_its_space_and_named_in_the_queue(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var guest    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var fromOutside = await stranger.Reports.SubmitReport(Report(ChannelTarget(channelId)), ct);
        var nowhere     = await guest.Reports.SubmitReport(Report(ChannelTarget(Guid.NewGuid())), ct);

        await FileAsync(guest, Report(ChannelTarget(channelId), note: "the whole channel"), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case   = await FindCaseAsync(admin, channelId, ct);
        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(fromOutside, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)),
                "a channel the reporter cannot see was reportable");
            Assert.That(nowhere, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));

            Assert.That(@case.target.kind, Is.EqualTo(ReportTargetKind.CHANNEL));
            Assert.That(@case.target.channelId, Is.EqualTo(channelId));
            Assert.That(@case.targetDisplayName, Is.EqualTo("general"), "the queue does not say which channel");
            Assert.That(details.contentSnapshot, Does.Contain("general"));
            Assert.That(details.targetTrustScore, Is.Null, "a channel has no trust score to show");
        });
    }

    /// <summary>
    /// A private space is reportable only from inside; a community space from anywhere, since anyone
    /// can see it from its invite.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task A_private_space_is_reportable_only_from_inside_and_a_community_from_anywhere(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);

        var privateFromOutside = await stranger.Reports.SubmitReport(Report(SpaceTarget(spaceId)), ct);
        var nowhere            = await stranger.Reports.SubmitReport(Report(SpaceTarget(Guid.NewGuid())), ct);

        await using (var db = await NewDbAsync(ct))
            await db.Spaces.Where(s => s.Id == spaceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsCommunity, true), ct);

        var communityFromOutside = await stranger.Reports.SubmitReport(Report(SpaceTarget(spaceId)), ct);
        var kept                 = await ReportsFiledByAsync(stranger.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(privateFromOutside, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)),
                "a private space was reportable by somebody who has never been inside it");
            Assert.That(nowhere, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
            Assert.That(communityFromOutside, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(kept, Is.EqualTo(1), "only the community report is kept");
        });
    }

    /// <summary>
    /// The system accounts, and an id that names nobody, cannot be reported.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task The_system_accounts_and_unknown_users_cannot_be_reported(CancellationToken ct = default)
    {
        var reporter = await CreateSessionAsync(ct);

        var system  = await reporter.Reports.SubmitReport(Report(UserTarget(UserEntity.SystemUser)), ct);
        var echo    = await reporter.Reports.SubmitReport(Report(UserTarget(UserEntity.EchoUser)), ct);
        var nobody  = await reporter.Reports.SubmitReport(Report(UserTarget(Guid.NewGuid())), ct);
        var kept    = await ReportsFiledByAsync(reporter.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(system, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
            Assert.That(echo, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
            Assert.That(nobody, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
            Assert.That(kept, Is.Zero);
        });
    }

    /// <summary>
    /// The legacy direct-message shape with the reporter's own id where the peer goes names a
    /// conversation with themself, which does not exist.
    /// </summary>
    /// <remarks>
    /// Older clients send a direct message as <c>MESSAGE</c> with the peer in the channel slot, so a
    /// channel id that names no channel is read as a peer. The reporter's own id in that slot must not
    /// resolve to anything — there is no conversation between an account and itself.
    /// </remarks>
    [Test, CancelAfter(240_000)]
    public async Task A_legacy_direct_message_report_naming_the_reporter_as_peer_is_refused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var messageId = await alice.Chats.SendDirectMessage(bob.UserId, "hello", NoEntities, Random.Shared.NextInt64(), null, ct);

        var result = await bob.Reports.SubmitReport(
            Report(new ReportTarget(ReportTargetKind.MESSAGE, alice.UserId, bob.UserId, (ulong)messageId)), ct);

        Assert.That(result, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
    }

    // ── heard, not kept ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A reporter past the hourly or the daily filing limit is acknowledged and nothing is kept.
    /// </summary>
    /// <remarks>
    /// The counters are the grain's own cache keys, set to the configured maximum so the next report
    /// is the first one over it; spending the limits for real would take a thousand reports here. The
    /// key names mirror <c>ReportGrain.FileAsync</c>, which keeps them private.
    /// </remarks>
    [TestCase("h", TestName = "{m}(hourly)")]
    [TestCase("d", TestName = "{m}(daily)")]
    [CancelAfter(240_000)]
    public async Task A_reporter_past_a_filing_limit_is_acknowledged_and_nothing_is_kept(string window, CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var max = window == "h" ? Filing.MaxReportsPerHour : Filing.MaxReportsPerDay;

        await Cache.StringSetAsync($"report:rl:{guest.UserId:N}:{window}", max.ToString(), TimeSpan.FromHours(1), ct);

        var result = await guest.Reports.SubmitReport(Report(UserTarget(owner.UserId)), ct);
        var kept   = await ReportsFiledByAsync(guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<SuccessSubmitReport>(), "a limit must not be distinguishable from a report");
            Assert.That(kept, Is.Zero, "a report over the limit was kept");
        });
    }

    /// <summary>
    /// The per-target limit silences reports about that target only.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task The_per_target_limit_silences_only_that_target(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var other = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, other, spaceId, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var groupKey = ReportTargetRules.GroupKey(ReportTargetKind.USER, owner.UserId, null, null, null);

        await Cache.StringSetAsync($"report:rl:{guest.UserId:N}:t:{groupKey}", Filing.MaxReportsPerTargetPerDay.ToString(),
            TimeSpan.FromHours(1), ct);

        var silenced = await guest.Reports.SubmitReport(Report(UserTarget(owner.UserId)), ct);
        var heard    = await guest.Reports.SubmitReport(Report(UserTarget(other.UserId)), ct);

        await using var db = await NewDbAsync(ct);

        var kept = await db.Reports.AsNoTracking().Where(r => r.ReporterId == guest.UserId).Select(r => r.TargetId).ToListAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(silenced, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(heard, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(kept, Is.EqualTo(new[] { other.UserId }), "the limit on one target spilled onto another, or did not hold");
        });
    }

    /// <summary>
    /// An account under a critical lockdown is not a witness while the lockdown lasts, and is one
    /// again once it has lapsed.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task A_reporter_under_a_critical_lockdown_is_heard_and_not_kept_until_it_lapses(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await SetLockdownAsync(guest.UserId, LockdownReason.TOS_VIOLATION, DateTimeOffset.UtcNow.AddDays(1), ct);

        var whileLocked = await guest.Reports.SubmitReport(Report(UserTarget(owner.UserId)), ct);
        var keptLocked  = await ReportsFiledByAsync(guest.UserId, ct);

        await SetLockdownAsync(guest.UserId, LockdownReason.TOS_VIOLATION, DateTimeOffset.UtcNow.AddMinutes(-1), ct);

        var afterLapse = await guest.Reports.SubmitReport(Report(UserTarget(owner.UserId)), ct);
        var keptLapsed = await ReportsFiledByAsync(guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(whileLocked, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(keptLocked, Is.Zero, "a banned account's report was kept");
            Assert.That(afterLapse, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(keptLapsed, Is.EqualTo(1), "a lockdown that has lapsed still silenced the account");
        });
    }

    /// <summary>A report whose caller has no account row is acknowledged and dropped.</summary>
    [Test, CancelAfter(240_000)]
    public async Task A_report_from_an_account_that_does_not_exist_is_acknowledged_and_dropped(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var ghost = Guid.NewGuid();

        var result = await AsCallerAsync(ghost, g => g.SubmitReportAsync(Report(UserTarget(owner.UserId)), ct));
        var kept   = await ReportsFiledByAsync(ghost, ct);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(kept, Is.Zero);
        });
    }

    /// <summary>
    /// Several reports about one thing at once make one case that counts every one of them.
    /// </summary>
    /// <remarks>
    /// <para>Two first reporters racing to open the case collide on the partial unique index (one open
    /// case per group key), and the loser files again against the winner's case. That half always
    /// held. The half that did not was the count: every filer read the case, added one and wrote it
    /// back, so reports landing together overwrote each other's increment — four reports on the case,
    /// a <c>ReportCount</c> of two — and each computed its independent-reporter count without the
    /// others, so a burst that crossed the escalation threshold did not escalate. Filing now takes the
    /// open case's row before it reads it (<c>ReportGrain.FileAsync</c>), so concurrent reports queue
    /// and each counts from what the one before it wrote.</para>
    ///
    /// <para>Whether a given run races is up to the scheduler; against the unlocked grain this failed
    /// three runs out of three. What is asserted is the outcome either way.</para>
    /// </remarks>
    [Test, CancelAfter(240_000)]
    public async Task Several_first_reports_at_once_make_one_case_holding_all_of_them(CancellationToken ct = default)
    {
        const int reporters = 4;

        var owner = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);

        var guests = new List<TestUserSession>();

        for (var i = 0; i < reporters; i++)
        {
            var guest = await CreateSessionAsync(ct);
            await JoinAsync(owner, guest, spaceId, ct);
            guests.Add(guest);
        }

        var messageId = await SayAsync(owner, spaceId, channelId, "reported by everybody at once", ct);

        var results = await Task.WhenAll(guests.Select(g => g.Reports.SubmitReport(Report(MessageTarget(owner.UserId, channelId, messageId)), ct)));

        await using var db = await NewDbAsync(ct);

        var groupKey = ReportTargetRules.GroupKey(ReportTargetKind.MESSAGE, owner.UserId, channelId, null, messageId);
        var cases    = await db.ReportCases.AsNoTracking().Where(c => c.GroupKey == groupKey).ToListAsync(ct);
        var caseIds  = cases.Select(c => c.Id).ToList();
        var reports  = await db.Reports.AsNoTracking().CountAsync(r => r.CaseId != null && caseIds.Contains(r.CaseId.Value), ct);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.All.InstanceOf<SuccessSubmitReport>());
            Assert.That(cases, Has.Count.EqualTo(1), "one message, one open case");
            Assert.That(reports, Is.EqualTo(reporters), "a report was lost in the race to open the case");
            Assert.That(cases.Single().ReportCount, Is.EqualTo(reporters),
                "the case counts fewer reports than it holds: a concurrent report's increment was overwritten");
            Assert.That(cases.Single().IndependentReporterCount, Is.EqualTo(reporters),
                "strangers on different machines filing at once were not all counted as independent");
            Assert.That(cases.Single().IsEscalated, Is.True,
                $"{reporters} independent reporters at once did not make the case urgent, as they would one after another");
        });
    }

    // ── the queue ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The queue filters by status and by category, together.</summary>
    [Test, CancelAfter(240_000)]
    public async Task The_queue_filters_by_status_and_category(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await SayAsync(owner, spaceId, channelId, "violent enough to escalate", ct);
        await FileAsync(guest, Report(MessageTarget(owner.UserId, channelId, messageId), ReportCategory.VIOLENCE, ReportReason.EXTREME_VIOLENCE), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var page = await admin.GetReportCases(ReportStatus.ESCALATED, ReportCategory.VIOLENCE, 200, 0, ct);

        Assert.Multiple(() =>
        {
            Assert.That(page.cases.Values, Is.Not.Empty);
            Assert.That(page.cases.Values.All(c => c.status == ReportStatus.ESCALATED && c.topCategory == ReportCategory.VIOLENCE), Is.True,
                "the queue returned a case outside the filter");
            Assert.That(page.totalCount, Is.GreaterThanOrEqualTo(page.cases.Size));
        });
    }

    // ── refusals ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A case that does not exist cannot be read, assigned or reopened.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task A_case_that_does_not_exist_cannot_be_read_assigned_or_reopened(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var missing = Guid.NewGuid();

        var read     = await Reports().GetCaseAsync(missing, ct);
        var assigned = await admin.AssignReportCase(missing, OperatorId, ct);
        var reopened = await admin.ReopenReportCase(missing, "look again", ct);

        Assert.Multiple(() =>
        {
            Assert.That(read, Is.Null);
            Assert.That(assigned.success, Is.False);
            Assert.That(reopened.success, Is.False);
            Assert.That(async () => await admin.GetReportCase(missing, ct), Throws.Exception,
                "the console answered for a case that does not exist");
        });
    }

    /// <summary>
    /// A decided case cannot be picked up, an open one cannot be reopened, and a resolution has to be
    /// one of the three resolutions.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task The_case_state_machine_refuses_moves_it_does_not_have(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await FileAsync(guest, Report(UserTarget(owner.UserId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        var reopenOpen      = await admin.ReopenReportCase(@case.caseId, null, ct);
        var resolveToOpen   = await ResolveAsync(admin, @case.caseId, ReportStatus.UNDER_REVIEW, ReportActionKind.NONE, null, ct);
        var deleteNoContent = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.DELETE_CONTENT, null, ct);

        var decided       = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_NO_ACTION, ReportActionKind.NONE, null, ct);
        var assignDecided = await admin.AssignReportCase(@case.caseId, OperatorId, ct);

        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(reopenOpen.success, Is.False, "an open case was reopened");
            Assert.That(resolveToOpen.success, Is.False, "UNDER_REVIEW was accepted as a resolution");
            Assert.That(deleteNoContent.success, Is.False, "content removal was accepted on a case about a person");
            Assert.That(decided.success, Is.True, decided.error);
            Assert.That(assignDecided.success, Is.False, "a decided case was picked up again");
            Assert.That(details.summary.status, Is.EqualTo(ReportStatus.RESOLVED_NO_ACTION), "a refused move changed the case");
            Assert.That(details.summary.assignedOperatorId, Is.Null);
        });
    }

    /// <summary>An action the grain does not know is refused and the case stays open.</summary>
    [Test, CancelAfter(240_000)]
    public async Task An_unknown_action_is_refused_and_the_case_stays_open(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await FileAsync(guest, Report(UserTarget(owner.UserId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        var result = await Reports().ResolveCaseAsync(new ResolveReportCaseCommand(
            @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, null, (ReportActionKind)999, OperatorId), ct);

        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "an action nobody implements was recorded as applied");
            Assert.That(result.Error, Does.Contain("Unknown action"));
            Assert.That(details.summary.status, Is.EqualTo(ReportStatus.PENDING));
            Assert.That(details.summary.resolvedAt, Is.Null);
        });
    }

    /// <summary>
    /// A ban on an account that has since been erased has nobody to land on: it is refused, and the
    /// case stays open for a decision that can be carried out.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task A_ban_on_an_erased_account_is_refused_and_the_case_stays_open(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await FileAsync(guest, Report(UserTarget(owner.UserId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        // What AccountDeletionGrain leaves of the account row at the end of an erasure.
        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == owner.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true), ct);

        var banned  = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.BAN_USER, null, ct);
        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(banned.success, Is.False, "a ban on an account that no longer exists was reported as applied");
            Assert.That(details.summary.status, Is.EqualTo(ReportStatus.PENDING), "the refused ban closed the case");
            Assert.That(details.summary.appliedAction, Is.EqualTo(ReportActionKind.NONE));
        });
    }

    // ── direct messages ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deleting the content of a direct-message case takes that message down, and only that one.
    /// </summary>
    [Test, CancelAfter(240_000)]
    public async Task Deleting_the_content_of_a_direct_message_case_takes_the_message_down(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var reported = await alice.Chats.SendDirectMessage(bob.UserId, "the reported one", NoEntities, Random.Shared.NextInt64(), null, ct);
        var kept     = await alice.Chats.SendDirectMessage(bob.UserId, "an innocent one", NoEntities, Random.Shared.NextInt64(), null, ct);

        await FileAsync(bob, Report(new ReportTarget(ReportTargetKind.DIRECT_MESSAGE, alice.UserId, null, (ulong)reported)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, alice.UserId, ct);
        var result = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.DELETE_CONTENT, "abuse in DMs", ct);

        Assert.That(result.success, Is.True, result.error);

        var conversationId = ConversationEntity.GenerateConversationId(alice.UserId, bob.UserId);

        await using var db = await NewDbAsync(ct);

        var messages = await db.DirectMessages.IgnoreQueryFilters().AsNoTracking()
           .Where(m => m.ConversationId == conversationId)
           .ToDictionaryAsync(m => m.MessageId, m => m.IsDeleted, ct);

        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.target.kind, Is.EqualTo(ReportTargetKind.DIRECT_MESSAGE), "premise");
            Assert.That(messages[reported], Is.True, "the reported direct message is still there");
            Assert.That(messages[kept], Is.False, "the removal took a message nobody reported");
            Assert.That(details.summary.appliedAction, Is.EqualTo(ReportActionKind.DELETE_CONTENT));
            Assert.That(details.contentSnapshot, Does.Contain("the reported one"), "the evidence went with the message");
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IArgonCacheDatabase Cache
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<IArgonCacheDatabase>();

    private static ReportTarget ChannelTarget(Guid channelId)
        => new(ReportTargetKind.CHANNEL, channelId, null, null);

    private static ReportTarget SpaceTarget(Guid spaceId)
        => new(ReportTargetKind.SPACE, spaceId, null, null);

    private IReportGrain Reports()
        => GetGrainFactory().GetGrain<IReportGrain>(Guid.CreateVersion7());

    /// <summary>Calls the report grain as the given user, the way the Ion layer's context does.</summary>
    private async Task<T> AsCallerAsync<T>(Guid userId, Func<IReportGrain, Task<T>> call)
    {
        RequestContext.Set("$caller_user_id", userId);

        try
        {
            return await call(Reports());
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    private async Task<int> ReportsFiledByAsync(Guid reporterId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        return await db.Reports.AsNoTracking().CountAsync(r => r.ReporterId == reporterId, ct);
    }

    private async Task SetLockdownAsync(Guid userId, LockdownReason reason, DateTimeOffset until, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
           .SetProperty(u => u.LockdownReason, reason)
           .SetProperty(u => u.LockDownExpiration, (DateTimeOffset?)until), ct);
    }
}
