namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The arithmetic behind a user's standing, one term at a time.
/// </summary>
/// <remarks>
/// <para>Every expected number below is computed from <c>TestServerConfiguration.ReportSystem</c>'s
/// <c>TrustScoring</c> section — maximum 100, default 50, weights SPAM 5 / SCAM_OR_FRAUD 15 /
/// VIOLENCE 30 and 10 for any other category, decay <c>e^(-0.5·days)</c> for thirty days and a 0.1
/// floor after, and so on — and the working is written next to each assertion.</para>
///
/// <para>Reports are written straight into the table rather than filed and resolved through the
/// console. What is under test is <c>UserTrustGrain</c>'s reading of a history, and a history that
/// spans months cannot be produced any other way; the filing and resolving paths have fixtures of
/// their own. Each report is about a user this fixture registered, so nothing a neighbour files
/// can land in these counts.</para>
/// </remarks>
[TestFixture]
public class TrustScoreTests : TestBase
{
    private readonly List<TestUserSession> reporters = [];

    [OneTimeSetUp]
    public async Task RegisterReporters()
    {
        for (var i = 0; i < 5; i++)
            reporters.Add(await CreateSessionAsync());
    }

    private IUserTrustGrain Trust(Guid userId) => GetGrainFactory().GetGrain<IUserTrustGrain>(userId);

    private Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct);

    private async Task<Guid> NewUserAsync(CancellationToken ct)
        => (await CreateSessionAsync(ct)).UserId;

    private async Task ReportAsync(
        Guid reporterId, Guid targetId, ReportCategory category, ReportStatus status, double daysAgo,
        int count = 1, int credibility = 100, CancellationToken ct = default)
    {
        var when = DateTimeOffset.UtcNow.AddDays(-daysAgo);

        await using var db = await NewDbAsync(ct);

        for (var i = 0; i < count; i++)
        {
            db.Reports.Add(new ReportEntity
            {
                Id                        = Guid.CreateVersion7(),
                ReporterId                = reporterId,
                TargetKind                = ReportTargetKind.USER,
                TargetId                  = targetId,
                Category                  = category,
                Reason                    = ReportReason.NONE,
                Status                    = status,
                CreatedAt                 = when,
                UpdatedAt                 = when,
                ResolvedAt                = status is ReportStatus.PENDING ? null : when,
                ReporterCredibilityAtTime = credibility,
                IsIndependent             = true
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<UserTrustScoreEntity> RowAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.UserTrustScores.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
    }

    private async Task UpdateUserAsync(
        Guid userId, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<UserEntity>> change, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(change, ct);
    }

    /// <summary>
    /// A confirmed report counts against the dimension its category belongs to, less the older it is.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Each_confirmed_report_weighs_on_its_own_dimension_and_fades_with_age(CancellationToken ct = default)
    {
        var target   = await NewUserAsync(ct);
        var reporter = reporters[0].UserId;

        // Content: CHILD_ABUSE weighs 10, a day old decays by e^-0.5 → 6.07.
        await ReportAsync(reporter, target, ReportCategory.CHILD_ABUSE, ReportStatus.RESOLVED_ACTION_TAKEN, 1, ct: ct);
        // Social: VIOLENCE weighs 30, sixty days old is past the first phase and on the 0.1 floor → 3.
        await ReportAsync(reporter, target, ReportCategory.VIOLENCE, ReportStatus.RESOLVED_ACTION_TAKEN, 60, ct: ct);
        // Commercial: SCAM_OR_FRAUD weighs 15, two hundred days old is on the floor → 1.5.
        await ReportAsync(reporter, target, ReportCategory.SCAM_OR_FRAUD, ReportStatus.RESOLVED_ACTION_TAKEN, 200, ct: ct);
        // Neither confirmed nor false: counted as received and as nothing else.
        await ReportAsync(reporter, target, ReportCategory.SPAM, ReportStatus.RESOLVED_NO_ACTION, 1, ct: ct);

        var info = await Trust(target).RecalculateTrustAsync(ct);
        var row  = await RowAsync(target, ct);

        Assert.Multiple(() =>
        {
            Assert.That(row.ContentViolationScore, Is.EqualTo(6));
            Assert.That(row.SocialBehaviorScore, Is.EqualTo(3));
            Assert.That(row.CommercialAbuseScore, Is.EqualTo(1));
            Assert.That(row.PositiveSignalScore, Is.Zero, "a day-old account has earned nothing yet");
            Assert.That(info.totalReportsReceived, Is.EqualTo(4));
            Assert.That(info.confirmedReportsReceived, Is.EqualTo(3));
            // 100 − 6 − 3 − 1; the last confirmed report is a day old, so no recovery bonus.
            Assert.That(info.trustScore, Is.EqualTo(90));
        });
    }

    /// <summary>
    /// A month and more without a confirmed report earns back standing — both the recovery bonus and
    /// the clean-record tier.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_quiet_month_after_the_last_confirmed_report_earns_standing_back(CancellationToken ct = default)
    {
        var target = await NewUserAsync(ct);

        // One confirmed SPAM report 45 days ago: 5 × 0.1 floor × 0.5 nuisance share → 0.25 → 0.
        await ReportAsync(reporters[0].UserId, target, ReportCategory.SPAM, ReportStatus.RESOLVED_ACTION_TAKEN, 45, ct: ct);
        // Three reports the target filed that a moderator dismissed: 3 × 10.
        await ReportAsync(target, reporters[1].UserId, ReportCategory.SPAM, ReportStatus.DISMISSED, 2, count: 3, ct: ct);

        var info = await Trust(target).RecalculateTrustAsync(ct);
        var row  = await RowAsync(target, ct);

        Assert.Multiple(() =>
        {
            Assert.That(row.PositiveSignalScore, Is.EqualTo(5), "45 clean days is the 30-day clean-record tier");
            Assert.That(info.falseReportsFiled, Is.EqualTo(3));
            Assert.That(info.totalReportsFiled, Is.EqualTo(3));
            // 100 + 5 (clean record) + 15 (recovery: 45 − 30 days) − 30 (false reports).
            Assert.That(info.trustScore, Is.EqualTo(90));
            // 50 base, and 0 of 3 filed reports upheld adds nothing for accuracy.
            Assert.That(row.ReporterCredibility, Is.EqualTo(50));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_established_verified_account_earns_positive_signals_up_to_the_cap(CancellationToken ct = default)
    {
        var target = await NewUserAsync(ct);

        // Five dismissed reports of the target's own, so the positives have room to show: −50.
        await ReportAsync(target, reporters[1].UserId, ReportCategory.SPAM, ReportStatus.DISMISSED, 2, count: 5, ct: ct);
        await UpdateUserAsync(target, set => set.SetProperty(u => u.CreatedAt, DateTimeOffset.UtcNow.AddDays(-370)), ct);

        var aged    = await Trust(target).RecalculateTrustAsync(ct);
        var agedRow = await RowAsync(target, ct);

        await UpdateUserAsync(target, set => set
           .SetProperty(u => u.PhoneNumber, $"+7{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}")
           .SetProperty(u => u.TotpSecret, Convert.ToBase64String(new byte[20]))
           .SetProperty(u => u.HasActiveUltima, true), ct);

        var verified    = await Trust(target).RecalculateTrustAsync(ct);
        var verifiedRow = await RowAsync(target, ct);

        Assert.Multiple(() =>
        {
            // 370 days is 12.3 months: the twelve-month tier alone, not the six-month one on top.
            Assert.That(agedRow.PositiveSignalScore, Is.EqualTo(10));
            // 100 + 10 + 10 (never reported) − 50.
            Assert.That(aged.trustScore, Is.EqualTo(70));
            // 50 base + 12.3 × 1.5 = 18 for age.
            Assert.That(agedRow.ReporterCredibility, Is.EqualTo(68));

            // 10 + 5 phone + 5 two-factor + 5 premium = 25, capped at 20.
            Assert.That(verifiedRow.PositiveSignalScore, Is.EqualTo(20));
            Assert.That(verified.trustScore, Is.EqualTo(80));
        });
    }

    /// <summary>
    /// A burst of confirmed reports costs more the more independent people it came from.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_burst_of_confirmed_reports_costs_more_the_more_people_it_came_from(CancellationToken ct = default)
    {
        var fromOne   = await NewUserAsync(ct);
        var fromThree = await NewUserAsync(ct);
        var fromFive  = await NewUserAsync(ct);

        // Five confirmed OTHER reports each, a day old: 10 × e^-0.5 × 0.5 nuisance share = 3.03 each,
        // 15 in all on the social dimension. Five in the seven-day window is two over the threshold.
        async Task BurstAsync(Guid target, int people)
        {
            for (var i = 0; i < 5; i++)
                await ReportAsync(reporters[i % people].UserId, target, ReportCategory.OTHER,
                    ReportStatus.RESOLVED_ACTION_TAKEN, 1, ct: ct);
        }

        await BurstAsync(fromOne, 1);
        await BurstAsync(fromThree, 3);
        await BurstAsync(fromFive, 5);

        var one   = await Trust(fromOne).RecalculateTrustAsync(ct);
        var three = await Trust(fromThree).RecalculateTrustAsync(ct);
        var five  = await Trust(fromFive).RecalculateTrustAsync(ct);
        var row   = await RowAsync(fromFive, ct);

        Assert.Multiple(() =>
        {
            Assert.That(row.SocialBehaviorScore, Is.EqualTo(15));
            // 100 − 15 − 2 × 5 (fewer than two people: low confidence).
            Assert.That(one.trustScore, Is.EqualTo(75));
            // 100 − 15 − 2 × 10 (between the two).
            Assert.That(three.trustScore, Is.EqualTo(65));
            // 100 − 15 − 2 × 20 (five or more people: high confidence).
            Assert.That(five.trustScore, Is.EqualTo(45));
            // Three or more upheld reports against someone costs them 10 as a reporter.
            Assert.That(row.ReporterCredibility, Is.EqualTo(40));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Filing_in_bulk_costs_a_reporter_credibility(CancellationToken ct = default)
    {
        var reporter = await NewUserAsync(ct);

        // Twenty-one reports in the seven-day window is one over the rate-abuse threshold.
        await ReportAsync(reporter, reporters[2].UserId, ReportCategory.SPAM, ReportStatus.PENDING, 1, count: 21, ct: ct);

        var info = await Trust(reporter).RecalculateTrustAsync(ct);
        var row  = await RowAsync(reporter, ct);

        Assert.Multiple(() =>
        {
            Assert.That(info.totalReportsFiled, Is.EqualTo(21));
            Assert.That(info.falseReportsFiled, Is.Zero, "a pending report is not a false one");
            Assert.That(row.ReporterCredibility, Is.EqualTo(35), "50 base − 15 for filing in bulk");
        });
    }

    /// <summary>
    /// Crossing the threshold flags the account once, and only an account nobody has locked yet.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Falling_below_the_threshold_flags_the_account_once(CancellationToken ct = default)
    {
        var target = await NewUserAsync(ct);
        var locked = await NewUserAsync(ct);

        // Enough fresh confirmed reports to hit every cap: content 7 × 6.07 → 40, social 2 × 18.2
        // → 30, commercial 4 × 9.1 → 30. That is 100 before the thirteen-report burst adds its own.
        foreach (var user in new[] { target, locked })
        {
            await ReportAsync(reporters[0].UserId, user, ReportCategory.CHILD_ABUSE, ReportStatus.RESOLVED_ACTION_TAKEN, 1, count: 7, ct: ct);
            await ReportAsync(reporters[0].UserId, user, ReportCategory.VIOLENCE, ReportStatus.RESOLVED_ACTION_TAKEN, 1, count: 2, ct: ct);
            await ReportAsync(reporters[0].UserId, user, ReportCategory.SCAM_OR_FRAUD, ReportStatus.RESOLVED_ACTION_TAKEN, 1, count: 4, ct: ct);
        }

        await UpdateUserAsync(locked, set => set.SetProperty(u => u.LockdownReason, LockdownReason.UNDER_INVESTIGATION), ct);

        var first  = await Trust(target).RecalculateTrustAsync(ct);
        var once   = await RowAsync(target, ct);
        var second = await Trust(target).RecalculateTrustAsync(ct);
        var twice  = await RowAsync(target, ct);

        await Trust(locked).RecalculateTrustAsync(ct);
        var lockedRow = await RowAsync(locked, ct);

        Assert.Multiple(() =>
        {
            Assert.That(once.ContentViolationScore, Is.EqualTo(40));
            Assert.That(once.SocialBehaviorScore, Is.EqualTo(30));
            Assert.That(once.CommercialAbuseScore, Is.EqualTo(30));
            Assert.That(first.trustScore, Is.Zero);
            Assert.That(once.AutoActionsApplied, Is.EqualTo(1), "falling from 50 to 0 crossed the threshold of 10");
            Assert.That(second.trustScore, Is.Zero);
            Assert.That(twice.AutoActionsApplied, Is.EqualTo(1), "staying under the threshold flagged the account again");
            Assert.That(lockedRow.TrustScore, Is.Zero);
            Assert.That(lockedRow.AutoActionsApplied, Is.Zero, "an account already locked was flagged for review as well");
        });
    }

    /// <summary>
    /// Several first recalculations at once all succeed and leave one row.
    /// </summary>
    /// <remarks>
    /// The grain is a stateless worker, so these run on separate activations and race to create the
    /// row; the loser of that race has to read the winner's row rather than fail.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Concurrent_first_recalculations_agree_and_leave_one_row(CancellationToken ct = default)
    {
        var target = await NewUserAsync(ct);

        await ReportAsync(reporters[0].UserId, target, ReportCategory.CHILD_ABUSE, ReportStatus.RESOLVED_ACTION_TAKEN, 1, ct: ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Trust(target).RecalculateTrustAsync(ct)));

        await using var db = await NewDbAsync(ct);
        var rows = await db.UserTrustScores.AsNoTracking().CountAsync(x => x.UserId == target, ct);

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.trustScore).Distinct(), Is.EqualTo(new[] { 94 }), "100 − 6");
            Assert.That(rows, Is.EqualTo(1));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Reading_a_score_that_was_never_computed_answers_the_default(CancellationToken ct = default)
    {
        var target = await NewUserAsync(ct);

        var before = await Trust(target).GetTrustScoreAsync(ct);
        await ReportAsync(reporters[0].UserId, target, ReportCategory.CHILD_ABUSE, ReportStatus.RESOLVED_ACTION_TAKEN, 1, ct: ct);
        await Trust(target).RecalculateTrustAsync(ct);
        var after = await Trust(target).GetTrustScoreAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(before.trustScore, Is.EqualTo(50));
            Assert.That(before.totalReportsReceived, Is.Zero);
            Assert.That(after.trustScore, Is.EqualTo(94), "the read does not return what the recalculation stored");
            Assert.That(after.confirmedReportsReceived, Is.EqualTo(1));
        });
    }
}
