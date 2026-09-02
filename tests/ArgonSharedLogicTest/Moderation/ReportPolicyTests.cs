namespace ArgonSharedLogicTest.Moderation;

using Argon.Features.Moderation;
using ArgonContracts;

/// <summary>
/// The arithmetic of the report system, with no database: who counts as a distinct reporter, when
/// a case is urgent, and how it is ranked.
/// </summary>
/// <remarks>
/// <para>The properties pinned here are the ones the redesign exists for. A farm of accounts on
/// one machine is one reporter. A fresh account is a reporter for ranking and not for escalation.
/// Nothing here is anything but a number a person reads — there is no method to call that hides
/// a message or locks an account, and that absence is the design.</para>
///
/// <para>The options are invented, like the tables in the device-matching tests and for the same
/// reason: what is tested is a rule, and a rule is exercised as well by round numbers as by the
/// ones a deployment actually chose.</para>
/// </remarks>
[TestFixture]
public class ReportPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static ReportEscalationOptions Escalation() => new()
    {
        IndependentReportersThreshold        = 3,
        WindowMinutes                        = 60,
        HighCredibilityThreshold             = 80,
        LowTrustTargetThreshold              = 200,
        IndependentReporterMinAccountAgeDays = 7,
        IndependentReporterMinCredibility    = 30,
        CriticalCategories                   = [ReportCategory.CHILD_ABUSE],
        SeriousCategories                    = [ReportCategory.CHILD_ABUSE, ReportCategory.VIOLENCE]
    };

    private static ReportPriorityOptions Priority() => new()
    {
        CategoryBase                = new() { [ReportCategory.SPAM] = 100, [ReportCategory.VIOLENCE] = 1000 },
        DefaultBase                 = 50,
        CredibilityMultiplier       = 2,
        IndependentReporterBoost    = 10,
        IndependentReporterBoostCap = 25
    };

    /// <summary>A reporter who qualifies on every count, distinct from every other by default.</summary>
    private static ReporterSignal Reporter(
        string? address = null, string? device = null, int ageDays = 30, int credibility = 50,
        int minutesAgo = 0, Guid? id = null)
        => new(id ?? Guid.NewGuid(), address, device, ageDays, credibility, Now.AddMinutes(-minutesAgo));

    #region independence

    [Test]
    public void Three_distinct_people_are_three_reporters()
        => Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter("a", "x"), Reporter("b", "y"), Reporter("c", "z")], Now), Is.EqualTo(3));

    [Test]
    public void The_same_account_twice_is_one_reporter()
    {
        var id = Guid.NewGuid();

        Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter("a", "x", id: id), Reporter("b", "y", id: id)], Now), Is.EqualTo(1));
    }

    /// <summary>The property the whole redesign turns on.</summary>
    [Test]
    public void Accounts_on_one_device_are_one_reporter()
        => Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter("a", "same"), Reporter("b", "same"), Reporter("c", "same")], Now), Is.EqualTo(1),
            "three accounts, one machine — that is one person with a script");

    [Test]
    public void Accounts_on_one_address_are_one_reporter()
        => Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter("same", "x"), Reporter("same", "y"), Reporter("same", "z")], Now), Is.EqualTo(1));

    /// <summary>
    /// A deployment without a pepper stores no hashes. "Unknown" must not read as "the same".
    /// </summary>
    [Test]
    public void Unknown_addresses_and_devices_do_not_collide()
        => Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter(), Reporter(), Reporter()], Now), Is.EqualTo(3));

    [Test]
    public void A_fresh_account_does_not_count()
        => Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter("a", "x", ageDays: 1), Reporter("b", "y")], Now), Is.EqualTo(1));

    [Test]
    public void A_low_credibility_account_does_not_count()
        => Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter("a", "x", credibility: 10), Reporter("b", "y")], Now), Is.EqualTo(1));

    [Test]
    public void A_report_outside_the_window_does_not_count()
        => Assert.That(ReportPolicy.CountIndependent(Escalation(),
            [Reporter("a", "x", minutesAgo: 61), Reporter("b", "y", minutesAgo: 59)], Now), Is.EqualTo(1));

    /// <summary>
    /// Which of two accounts on one device is "the" reporter is decided by who filed first, so the
    /// answer does not depend on the order rows come back from the database.
    /// </summary>
    [Test]
    public void The_earlier_of_two_on_one_device_is_the_one_counted()
    {
        var early = Reporter("a", "same", minutesAgo: 30);
        var late  = Reporter("b", "same", minutesAgo: 5);

        Assert.That(ReportPolicy.Independent(Escalation(), [late, early], Now), Is.EqualTo(new[] { early.ReporterId }));
    }

    #endregion

    #region escalation

    [Test]
    public void A_critical_category_is_urgent_from_the_first_report()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.CHILD_ABUSE, 1, 0, null),
            Is.EqualTo(new EscalationDecision(true, EscalationRules.CriticalCategory)));

    [Test]
    public void Enough_independent_reporters_make_a_case_urgent()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.SPAM, 3, 50, null),
            Is.EqualTo(new EscalationDecision(true, EscalationRules.IndependentCluster)));

    [Test]
    public void One_short_of_the_threshold_is_not_urgent()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.SPAM, 2, 50, null).IsEscalated, Is.False);

    [Test]
    public void A_credible_reporter_escalates_a_serious_category()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.VIOLENCE, 1, 80, null),
            Is.EqualTo(new EscalationDecision(true, EscalationRules.HighCredibilitySerious)));

    [Test]
    public void A_credible_reporter_does_not_escalate_a_nuisance_category()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.SPAM, 1, 100, null).IsEscalated, Is.False);

    [Test]
    public void A_low_trust_target_escalates_a_serious_category()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.VIOLENCE, 1, 50, 100),
            Is.EqualTo(new EscalationDecision(true, EscalationRules.LowTrustTarget)));

    [Test]
    public void A_low_trust_target_does_not_escalate_a_nuisance_category()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.SPAM, 1, 50, 0).IsEscalated, Is.False);

    [Test]
    public void An_unscored_target_is_not_a_low_trust_target()
        => Assert.That(ReportPolicy.Evaluate(Escalation(), ReportCategory.VIOLENCE, 1, 50, null).IsEscalated, Is.False);

    #endregion

    #region priority

    [Test]
    public void Priority_is_base_plus_credibility_plus_a_capped_boost()
        => Assert.That(ReportPolicy.ComputePriority(Priority(), ReportCategory.SPAM, 50, 1),
            Is.EqualTo(100 + 50 * 2 + 10));

    [Test]
    public void The_independent_reporter_boost_is_capped()
        => Assert.That(ReportPolicy.ComputePriority(Priority(), ReportCategory.SPAM, 0, 100), Is.EqualTo(100 + 25));

    [Test]
    public void A_category_the_table_does_not_name_gets_the_default_base()
        => Assert.That(ReportPolicy.ComputePriority(Priority(), ReportCategory.COPYRIGHT, 0, 0), Is.EqualTo(50));

    [Test]
    public void Negative_inputs_cannot_lower_a_priority()
        => Assert.That(ReportPolicy.ComputePriority(Priority(), ReportCategory.SPAM, -100, -100), Is.EqualTo(100));

    [Test]
    public void The_weightier_category_wins_and_ties_keep_the_first()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportPolicy.Higher(Priority(), ReportCategory.SPAM, ReportCategory.VIOLENCE), Is.EqualTo(ReportCategory.VIOLENCE));
            Assert.That(ReportPolicy.Higher(Priority(), ReportCategory.VIOLENCE, ReportCategory.SPAM), Is.EqualTo(ReportCategory.VIOLENCE));
            Assert.That(ReportPolicy.Higher(Priority(), ReportCategory.COPYRIGHT, ReportCategory.OTHER), Is.EqualTo(ReportCategory.COPYRIGHT),
                "both unnamed, both the default base; the case keeps the category it had");
        });
    }

    #endregion
}
