namespace ArgonSharedLogicTest.Moderation;

using Argon.Features.Moderation;
using ArgonContracts;
using ConsoleContracts;

/// <summary>
/// What each resolution action turns into, and — the half that matters — when a lockdown it asks
/// for must not replace the one the account already has.
/// </summary>
[TestFixture]
public class ReportActionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static ReportActionOptions Actions() => new() { MuteDays = 3, RestrictDays = 7, BanDays = 0 };

    [Test]
    public void Mute_and_restrict_are_middle_severity_and_timed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportActionPlanner.Lockdown(Actions(), ReportActionKind.MUTE_USER),
                Is.EqualTo(new LockdownPlan(LockdownReason.INCITING_MOMENT, TimeSpan.FromDays(3), true)));
            Assert.That(ReportActionPlanner.Lockdown(Actions(), ReportActionKind.RESTRICT_USER),
                Is.EqualTo(new LockdownPlan(LockdownReason.UNDER_INVESTIGATION, TimeSpan.FromDays(7), true)));
        });
    }

    [Test]
    public void A_ban_of_zero_days_is_permanent_and_otherwise_timed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportActionPlanner.Lockdown(Actions(), ReportActionKind.BAN_USER),
                Is.EqualTo(new LockdownPlan(LockdownReason.TOS_VIOLATION, null, true)));
            Assert.That(ReportActionPlanner.Lockdown(new ReportActionOptions { BanDays = 30 }, ReportActionKind.BAN_USER)!.Duration,
                Is.EqualTo(TimeSpan.FromDays(30)));
        });
    }

    [Test]
    public void Actions_that_are_not_lockdowns_plan_none([Values(
        ReportActionKind.NONE, ReportActionKind.WARN_USER, ReportActionKind.DELETE_CONTENT, ReportActionKind.QUARANTINE_CONTENT)] ReportActionKind action)
        => Assert.That(ReportActionPlanner.Lockdown(Actions(), action), Is.Null);

    [Test]
    public void An_action_is_only_consistent_with_action_taken()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportActionPlanner.IsConsistent(ReportActionKind.BAN_USER, ReportStatus.RESOLVED_ACTION_TAKEN), Is.True);
            Assert.That(ReportActionPlanner.IsConsistent(ReportActionKind.BAN_USER, ReportStatus.RESOLVED_NO_ACTION), Is.False);
            Assert.That(ReportActionPlanner.IsConsistent(ReportActionKind.BAN_USER, ReportStatus.DISMISSED), Is.False);
            Assert.That(ReportActionPlanner.IsConsistent(ReportActionKind.NONE, ReportStatus.DISMISSED), Is.True);
        });
    }

    [Test]
    public void Which_actions_need_a_person_and_which_need_content()
    {
        Assert.Multiple(() =>
        {
            foreach (var action in new[] { ReportActionKind.WARN_USER, ReportActionKind.MUTE_USER, ReportActionKind.RESTRICT_USER, ReportActionKind.BAN_USER })
                Assert.That(ReportActionPlanner.TargetsAPerson(action), Is.True, action.ToString());
            foreach (var action in new[] { ReportActionKind.DELETE_CONTENT, ReportActionKind.QUARANTINE_CONTENT })
                Assert.That(ReportActionPlanner.TargetsContent(action), Is.True, action.ToString());
            Assert.That(ReportActionPlanner.TargetsAPerson(ReportActionKind.NONE) || ReportActionPlanner.TargetsContent(ReportActionKind.NONE), Is.False);
        });
    }

    [Test]
    public void The_severity_ladder_matches_the_request_interceptor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportActionPlanner.SeverityOf(LockdownReason.NONE), Is.EqualTo(LockdownSeverity.Low));
            Assert.That(ReportActionPlanner.SeverityOf(LockdownReason.UNDER_INVESTIGATION), Is.EqualTo(LockdownSeverity.Middle));
            Assert.That(ReportActionPlanner.SeverityOf(LockdownReason.INCITING_MOMENT), Is.EqualTo(LockdownSeverity.Middle));
            Assert.That(ReportActionPlanner.SeverityOf(LockdownReason.TOS_VIOLATION), Is.EqualTo(LockdownSeverity.Critical));
            Assert.That(ReportActionPlanner.SeverityOf(LockdownReason.CSAM), Is.EqualTo(LockdownSeverity.Critical));
        });
    }

    #region should apply

    private static readonly LockdownPlan Mute      = new(LockdownReason.INCITING_MOMENT, TimeSpan.FromDays(3), true);
    private static readonly LockdownPlan Ban       = new(LockdownReason.TOS_VIOLATION, null, true);
    private static readonly LockdownPlan TimedBan  = new(LockdownReason.TOS_VIOLATION, TimeSpan.FromDays(30), true);

    [Test]
    public void An_account_with_no_lockdown_takes_any()
        => Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.NONE, null, Mute, Now), Is.True);

    [Test]
    public void A_lapsed_lockdown_counts_as_none()
        => Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.TOS_VIOLATION, Now.AddDays(-1), Mute, Now), Is.True);

    [Test]
    public void A_stricter_plan_replaces_a_weaker_lockdown()
        => Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.INCITING_MOMENT, Now.AddDays(2), Ban, Now), Is.True);

    /// <summary>The property the comparison exists for: a mute must not shorten a ban.</summary>
    [Test]
    public void A_weaker_plan_never_replaces_a_stricter_lockdown()
        => Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.TOS_VIOLATION, null, Mute, Now), Is.False);

    [Test]
    public void At_equal_severity_the_longer_one_wins()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.INCITING_MOMENT, Now.AddDays(1), Mute, Now), Is.True, "3 days beats 1 left");
            Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.INCITING_MOMENT, Now.AddDays(10), Mute, Now), Is.False, "3 days does not beat 10 left");
            Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.TOS_VIOLATION, Now.AddDays(10), Ban, Now), Is.True, "permanent beats timed");
            Assert.That(ReportActionPlanner.ShouldApply(LockdownReason.TOS_VIOLATION, null, TimedBan, Now), Is.False, "timed does not beat permanent");
        });
    }

    #endregion
}
