namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonContracts;
using ion.runtime;

/// <summary>
/// Filing a report: what is refused, what is acknowledged, and what the acknowledgement hides.
/// </summary>
/// <remarks>
/// <para>The rules that matter are the refusals and the silences. A stranger cannot be reported
/// from a bare id; a message the reporter cannot see cannot be reported at all; and "does not
/// exist" and "not yours to see" are one answer. A duplicate is acknowledged exactly like a first
/// report — a user who taps twice sees no error — and only the queue knows it was one report.</para>
///
/// <para>Everything here needs <c>ReportSystem:IsEnabled</c>, which <c>TestServerConfiguration</c>
/// supplies along with limits raised out of the way.</para>
/// </remarks>
[TestFixture]
public class ModerationTests : ReportTestBase
{
    // ── Refusals ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_AgainstYourself_IsRefused(CancellationToken ct = default)
    {
        var me = await CreateSessionAsync(ct);

        var result = await me.Reports.SubmitReport(Report(UserTarget(me.UserId)), ct);

        Assert.That(result, Is.EqualTo(new FailedSubmitReport(SubmitReportError.CANNOT_REPORT_SELF)));
    }

    /// <summary>
    /// The list-of-ids attack: without an anchor — a space in common, a friendship, a
    /// conversation — a user id is not enough to open a case against somebody.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_AgainstAStranger_IsRefused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var result = await alice.Reports.SubmitReport(Report(UserTarget(bob.UserId)), ct);

        Assert.That(result, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
    }

    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_AgainstSomeoneYouShareASpaceWith_IsAccepted(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var result = await guest.Reports.SubmitReport(Report(UserTarget(owner.UserId), note: "shares a space"), ct);

        Assert.That(result, Is.InstanceOf<SuccessSubmitReport>());
    }

    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_WithAReasonFromAnotherCategory_IsRefused(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var result = await guest.Reports.SubmitReport(
            Report(UserTarget(owner.UserId), ReportCategory.SPAM, ReportReason.CHILD_SEXUAL_ABUSE), ct);

        Assert.That(result, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
    }

    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_ForAMessageOutsideYourSpaces_IsRefused(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);
        var messageId = await SayAsync(owner, spaceId, channelId, "members only", ct);

        var result = await stranger.Reports.SubmitReport(Report(MessageTarget(owner.UserId, channelId, messageId)), ct);

        Assert.That(result, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)),
            "a channel and message id the reporter could only have guessed must not open a case");
    }

    /// <summary>Same answer as the test above, on purpose: a probe learns nothing from the difference.</summary>
    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_ForAMessageThatDoesNotExist_IsRefusedTheSameWay(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var result = await guest.Reports.SubmitReport(Report(MessageTarget(owner.UserId, channelId, 123_456_789)), ct);

        Assert.That(result, Is.EqualTo(new FailedSubmitReport(SubmitReportError.INVALID_TARGET)));
    }

    // ── Direct messages ─────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_ForADirectMessage_IsAcceptedInBothShapes(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var messageId = await alice.Chats.SendDirectMessage(bob.UserId, "hello bob", NoEntities, Random.Shared.NextInt64(), null, ct);

        var explicitShape = await bob.Reports.SubmitReport(
            Report(new ReportTarget(ReportTargetKind.DIRECT_MESSAGE, alice.UserId, null, (ulong)messageId)), ct);

        // What clients from before DIRECT_MESSAGE existed send: MESSAGE, with the peer in the
        // channel slot. The server has to recognise the shape rather than answer INVALID_TARGET.
        var legacyShape = await bob.Reports.SubmitReport(
            Report(new ReportTarget(ReportTargetKind.MESSAGE, alice.UserId, alice.UserId, (ulong)messageId), ReportCategory.SCAM_OR_FRAUD, ReportReason.PHISHING), ct);

        Assert.Multiple(() =>
        {
            Assert.That(explicitShape, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(legacyShape, Is.InstanceOf<SuccessSubmitReport>());
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_ForYourOwnDirectMessage_IsRefused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var messageId = await alice.Chats.SendDirectMessage(bob.UserId, "I wrote this", NoEntities, Random.Shared.NextInt64(), null, ct);

        // The target names the peer, as the client would; the message itself is the reporter's.
        var result = await alice.Reports.SubmitReport(
            Report(new ReportTarget(ReportTargetKind.DIRECT_MESSAGE, bob.UserId, null, (ulong)messageId)), ct);

        Assert.That(result, Is.EqualTo(new FailedSubmitReport(SubmitReportError.CANNOT_REPORT_SELF)));
    }

    // ── What the acknowledgement hides ──────────────────────────────────────────────────────────

    /// <summary>
    /// A reporter cannot browse their own history: it would say which reports stuck and which
    /// were dropped, which is the signal a nuisance reporter tunes against. The outcome reaches
    /// them as a notification when the case closes (see the workflow tests).
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task GetMyReports_DeliberatelyReturnsNothing(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await FileAsync(guest, Report(UserTarget(owner.UserId), note: "listed"), ct);

        var mine = await guest.Reports.GetMyReports(50, 0, ct);

        Assert.That(mine.Size, Is.EqualTo(0));
    }

    [Test, CancelAfter(180_000)]
    public async Task SubmitReport_TwiceForTheSameThing_IsOneReport(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, channelId) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);
        var messageId = await SayAsync(owner, spaceId, channelId, "tap tap", ct);

        var first  = await guest.Reports.SubmitReport(Report(MessageTarget(owner.UserId, channelId, messageId)), ct);
        var second = await guest.Reports.SubmitReport(Report(MessageTarget(owner.UserId, channelId, messageId)), ct);

        // Both acknowledged — the second answer must not say "duplicate" — and one report kept.
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<SuccessSubmitReport>());
            Assert.That(second, Is.InstanceOf<SuccessSubmitReport>());
        });

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindCaseAsync(admin, owner.UserId, ct);

        Assert.That(@case.reportCount, Is.EqualTo(1));
    }

    // ── Trust ───────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task GetTrustScore_ForANewUser_IsTheConfiguredDefault(CancellationToken ct = default)
    {
        var me = await CreateSessionAsync(ct);

        var trust = await GetGrainFactory().GetGrain<IUserTrustGrain>(me.UserId).GetTrustScoreAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(trust.userId, Is.EqualTo(me.UserId));
            Assert.That(trust.trustScore, Is.EqualTo(50), "TrustScoring:DefaultTrustScore in the test configuration");
            Assert.That(trust.totalReportsReceived, Is.EqualTo(0));
        });
    }

    /// <summary>A report merely filed is counted, and confirms nothing.</summary>
    [Test, CancelAfter(180_000)]
    public async Task RecalculateTrust_CountsAFiledReportWithoutConfirmingIt(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateRoomAsync(owner, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        // What a fresh, unreported account scores once the formula has run — the ceiling, in the
        // test configuration. The default score is only what an account has before that.
        var baseline = (await TrustOfAsync(owner.UserId, ct)).trustScore;

        await FileAsync(guest, Report(UserTarget(owner.UserId)), ct);

        var target   = await TrustOfAsync(owner.UserId, ct);
        var reporter = await TrustOfAsync(guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(target.totalReportsReceived, Is.EqualTo(1));
            Assert.That(target.confirmedReportsReceived, Is.EqualTo(0), "nothing has been confirmed by a moderator yet");
            Assert.That(target.trustScore, Is.EqualTo(baseline), "an unreviewed report must not move the score");
            Assert.That(reporter.totalReportsFiled, Is.EqualTo(1));
            Assert.That(reporter.falseReportsFiled, Is.EqualTo(0));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task OnReportResolved_IsAcceptedForAUserWithNoTrustRow(CancellationToken ct = default)
    {
        var me = await CreateSessionAsync(ct);

        var trustGrain = GetGrainFactory().GetGrain<IUserTrustGrain>(me.UserId);

        await trustGrain.OnReportResolvedAsync(ReportStatus.DISMISSED, ct);

        Assert.That((await trustGrain.GetTrustScoreAsync(ct)).trustScore, Is.InRange(0, 100));
    }
}
