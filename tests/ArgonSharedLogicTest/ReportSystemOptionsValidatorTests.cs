namespace ArgonSharedLogicTest;

using Argon.Features.Clustering;
using Argon.Features.Moderation;
using ArgonContracts;
using ArgonSharedLogicTest.Clustering;
using Microsoft.Extensions.Options;

/// <summary>
/// The rules a report-system policy is checked against before a role starts.
/// </summary>
/// <remarks>
/// <para>These run at start-up and on <c>--validate-config</c>, so every rule here is a rule that
/// fails the deploy rather than the request. The values under test are invented: what is tested is
/// that a wrong policy is refused and a coherent one accepted, which six made-up numbers show as
/// well as the real ones.</para>
///
/// <para>Through <see cref="FeatureConfigurationValidator"/> rather than by calling the options
/// class directly, because the binder is part of what is under test: a section that is absent, a
/// list that appends, a dictionary that merges — those are binder behaviours the rules were written
/// around.</para>
/// </remarks>
[TestFixture]
public class ReportSystemOptionsValidatorTests
{
    private const string Section = ReportOptionsFeature.Section;

    private static (string[] Errors, string[] Warnings) Validate(params (string Key, string? Value)[] overrides)
    {
        var settings = new List<(string, string?)> { ($"{Section}:IsEnabled", "true") };

        foreach (var (key, value) in overrides)
        {
            settings.RemoveAll(s => s.Item1 == key);
            settings.Add((key, value));
        }

        var report = FeatureConfigurationValidator.Validate(
            ConfigurationFixtures.Role<ReportOptionsRole>(),
            ConfigurationFixtures.From([.. settings]));

        return (report.Errors.Select(d => d.ToString()).ToArray(), report.Warnings.Select(d => d.ToString()).ToArray());
    }

    [Test]
    public void The_defaults_are_a_working_policy()
        => Assert.That(Validate().Errors, Is.Empty, "enabling the system with nothing else set has to start");

    /// <summary>
    /// The two things a self-hosted instance most likely forgot, said out loud rather than quietly
    /// degraded around.
    /// </summary>
    [Test]
    public void The_defaults_warn_about_the_pepper_and_the_category_lists()
    {
        var (_, warnings) = Validate();

        Assert.Multiple(() =>
        {
            Assert.That(warnings, Has.Some.Contains("privacy:ReporterIdentityPepper"));
            Assert.That(warnings, Has.Some.Contains("escalation:CriticalCategories"));
            Assert.That(warnings, Has.Some.Contains("escalation:SeriousCategories"));
        });
    }

    [Test]
    public void A_disabled_system_is_not_checked_at_all()
    {
        // Nothing about a disabled report system can hurt production, and demanding a coherent
        // policy from a deployment that turned the feature *off* would be its own footgun.
        var (errors, warnings) = Validate(($"{Section}:IsEnabled", "false"), ($"{Section}:Filing:MaxReportsPerHour", "-1"));

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(warnings, Is.Empty);
        });
    }

    [Test]
    public void An_absent_section_is_a_system_that_is_off()
        => Assert.That(FeatureConfigurationValidator.Validate(ConfigurationFixtures.Role<ReportOptionsRole>(), ConfigurationFixtures.From()).Errors, Is.Empty);

    [Test]
    public void A_negative_account_age_is_refused()
        => Assert.That(Validate(($"{Section}:Filing:MinAccountAgeDays", "-1")).Errors, Has.Some.Contains("filing:MinAccountAgeDays"));

    [Test]
    public void A_daily_limit_below_the_hourly_one_is_refused()
        => Assert.That(Validate(($"{Section}:Filing:MaxReportsPerHour", "50"), ($"{Section}:Filing:MaxReportsPerDay", "10")).Errors,
            Has.Some.Contains("filing:MaxReportsPerDay"));

    [Test]
    public void A_comment_longer_than_the_column_is_refused()
        => Assert.That(Validate(($"{Section}:Filing:MaxAdditionalInfoLength", "5000")).Errors, Has.Some.Contains("filing:MaxAdditionalInfoLength"));

    /// <summary>A "cluster" of one is a report; the rule would fire on every single one.</summary>
    [Test]
    public void A_cluster_threshold_below_two_is_refused()
        => Assert.That(Validate(($"{Section}:Escalation:IndependentReportersThreshold", "1")).Errors,
            Has.Some.Contains("escalation:IndependentReportersThreshold"));

    [Test]
    public void A_critical_category_that_is_not_serious_is_refused()
    {
        // Critical implies serious everywhere downstream; letting the two sets diverge means a
        // category that is urgent on sight but not serious when a credible reporter names it.
        var (errors, _) = Validate(
            ($"{Section}:Escalation:CriticalCategories:0", nameof(ReportCategory.VIOLENCE)),
            ($"{Section}:Escalation:SeriousCategories:0", nameof(ReportCategory.SPAM)));

        Assert.That(errors, Has.Some.Contains("escalation:CriticalCategories"));
    }

    [Test]
    public void A_coherent_category_pair_is_accepted_without_the_warnings()
    {
        var (errors, warnings) = Validate(
            ($"{Section}:Escalation:CriticalCategories:0", nameof(ReportCategory.CHILD_ABUSE)),
            ($"{Section}:Escalation:SeriousCategories:0", nameof(ReportCategory.CHILD_ABUSE)),
            ($"{Section}:Escalation:SeriousCategories:1", nameof(ReportCategory.VIOLENCE)));

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(warnings.Where(w => w.Contains("Categories")), Is.Empty);
        });
    }

    [Test]
    public void A_credibility_outside_percent_is_refused([Values(-1, 101)] int value)
        => Assert.That(Validate(($"{Section}:Escalation:HighCredibilityThreshold", value.ToString())).Errors,
            Has.Some.Contains("escalation:HighCredibilityThreshold"));

    [Test]
    public void A_boost_cap_below_one_boost_is_refused()
        => Assert.That(Validate(($"{Section}:Priority:IndependentReporterBoost", "100"), ($"{Section}:Priority:IndependentReporterBoostCap", "50")).Errors,
            Has.Some.Contains("priority:IndependentReporterBoostCap"));

    [Test]
    public void A_short_pepper_is_refused_and_a_real_one_silences_the_warning()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Validate(($"{Section}:Privacy:ReporterIdentityPepper", "short")).Errors, Has.Some.Contains("privacy:ReporterIdentityPepper"));

            var (errors, warnings) = Validate(($"{Section}:Privacy:ReporterIdentityPepper", "long-enough-to-be-a-key-1234"));
            Assert.That(errors, Is.Empty);
            Assert.That(warnings.Where(w => w.Contains("ReporterIdentityPepper")), Is.Empty);
        });
    }

    [Test]
    public void A_mute_of_zero_days_is_refused()
        => Assert.That(Validate(($"{Section}:Actions:MuteDays", "0")).Errors, Has.Some.Contains("actions:MuteDays"));

    [Test]
    public void A_permanent_ban_is_spelled_zero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Validate(($"{Section}:Actions:BanDays", "0")).Errors, Is.Empty);
            Assert.That(Validate(($"{Section}:Actions:BanDays", "-1")).Errors, Has.Some.Contains("actions:BanDays"));
        });
    }

    [Test]
    public void A_page_size_outside_range_is_refused([Values(0, 201)] int value)
        => Assert.That(Validate(($"{Section}:MaxPageSize", value.ToString())).Errors, Has.Some.Contains("maxPageSize"));

    [Test]
    public void Every_problem_is_reported_at_once()
    {
        // The validator collects rather than short-circuits, so one deploy surfaces every mistake.
        var (errors, _) = Validate(
            ($"{Section}:Filing:MinAccountAgeDays", "-1"),
            ($"{Section}:Escalation:IndependentReportersThreshold", "0"),
            ($"{Section}:MaxPageSize", "500"));

        Assert.That(errors, Has.Length.GreaterThanOrEqualTo(3));
    }
}

[TestFixture]
public class TrustScoringOptionsValidatorTests
{
    private static TrustScoringOptions ValidTrustOptions() => new()
    {
        DefaultTrustScore                = 50,
        MinTrustScore                    = 0,
        MaxTrustScore                    = 100,
        SeverityWeights                  = new() { [ReportCategory.SPAM] = 5 },
        DefaultSeverityWeight            = 3,
        DecayRate                        = 0.5,
        DecayPhase1Days                  = 30,
        DecayPhase2Days                  = 90,
        DecayPhase2Rate                  = 0.25,
        DecayMinimum                     = 0.1,
        MinCredibilityInImpact           = 10,
        NuisanceToSocialFactor           = 0.5,
        BlockCountMultiplier             = 2,
        BlockCountCap                    = 20,
        ContentScoreCap                  = 40,
        SocialScoreCap                   = 30,
        CommercialScoreCap               = 30,
        PositiveSignalCap                = 20,
        PhoneVerifiedBoost               = 5,
        TwoFactorBoost                   = 5,
        PremiumBoost                     = 5,
        FriendBoostDivisor               = 10,
        FriendBoostCap                   = 10,
        AccountAgeTiers                  = [new AccountAgeTier { MinMonths = 6, Boost = 5 }],
        CleanRecordTiers                 = [new CleanRecordTier { MinDays = 30, Boost = 5 }],
        VelocityWindowDays               = 7,
        VelocityThreshold                = 3,
        VelocityHighConfidenceReporters  = 5,
        VelocityHighConfidencePenalty    = 20,
        VelocityLowConfidenceReporters   = 2,
        VelocityLowConfidencePenalty     = 5,
        VelocityMidPenalty               = 10,
        RecoveryStartDays                = 30,
        RecoveryMaxBonus                 = 20,
        CleanRecordNeverReportedBonus    = 10,
        FalseReportPenalty               = 10,
        CredibilityBase                  = 50,
        CredibilityAccuracyMax           = 30,
        CredibilityAgeMax                = 20,
        CredibilityAgeRate               = 1.5,
        CredibilitySelfReportedPenalty   = 10,
        CredibilitySelfReportedThreshold = 3,
        CredibilityRateAbusePenalty      = 15,
        CredibilityRateAbuseThreshold    = 20,
        CredibilityRateAbuseWindowDays   = 7,
        AutoActionThresholds             = [new AutoActionThreshold { ScoreBelow = 10, Reason = null, LockdownDays = 0 }]
    };

    private static ValidateOptionsResult Validate(TrustScoringOptions options, bool reportSystemEnabled = true)
    {
        var reportOptions = Options.Create(new ReportSystemOptions { IsEnabled = reportSystemEnabled });
        return new TrustScoringOptionsValidator(reportOptions).Validate(null, options);
    }

    [Test]
    public void ValidOptions_Pass()
        => Assert.That(Validate(ValidTrustOptions()).Succeeded, Is.True);

    [Test]
    public void WithTheReportSystemDisabled_ValidationIsSkipped()
        => Assert.That(Validate(new TrustScoringOptions(), reportSystemEnabled: false).Succeeded, Is.True);

    [Test]
    public void InvertedScoreRange_Fails()
    {
        var options = ValidTrustOptions();
        options.MinTrustScore = 100;
        options.MaxTrustScore = 50;

        Assert.That(Validate(options).Failures, Does.Contain("MinTrustScore must be less than MaxTrustScore"));
    }

    [Test]
    public void DefaultScoreOutsideTheRange_Fails()
    {
        var options = ValidTrustOptions();
        options.DefaultTrustScore = 150;

        Assert.That(Validate(options).Failures, Does.Contain("DefaultTrustScore must be between 0 and 100"));
    }

    [Test]
    public void ZeroDivisor_Fails()
    {
        // Used as a denominator; the validator calls that out by name rather than letting a
        // DivideByZeroException surface inside trust recalculation.
        var options = ValidTrustOptions();
        options.FriendBoostDivisor = 0;

        Assert.That(Validate(options).Failures, Does.Contain("FriendBoostDivisor must be > 0 (division by zero)"));
    }

    [Test]
    public void DecayPhasesOutOfOrder_Fail()
    {
        var options = ValidTrustOptions();
        options.DecayPhase1Days = 90;
        options.DecayPhase2Days = 30;

        Assert.That(Validate(options).Failures, Does.Contain("DecayPhase2Days must be greater than DecayPhase1Days"));
    }

    [Test]
    public void VelocityConfidenceBandsOutOfOrder_Fail()
    {
        var options = ValidTrustOptions();
        options.VelocityLowConfidenceReporters  = 10;
        options.VelocityHighConfidenceReporters = 5;

        Assert.That(Validate(options).Failures,
            Does.Contain("VelocityLowConfidenceReporters must be less than VelocityHighConfidenceReporters"));
    }

    [Test]
    public void AutoActionThresholdOutsideTheScoreRange_Fails()
    {
        var options = ValidTrustOptions();
        options.AutoActionThresholds = [new AutoActionThreshold { ScoreBelow = 500, LockdownDays = 1, Reason = null }];

        Assert.That(Validate(options).Failures!.Any(f => f.Contains("is outside")), Is.True);
    }

    [Test]
    public void AutoActionThresholdWithAnUnknownReason_Fails()
    {
        var options = ValidTrustOptions();
        options.AutoActionThresholds =
            [new AutoActionThreshold { ScoreBelow = 10, Reason = "NOT_A_REAL_REASON", LockdownDays = 5 }];

        Assert.That(Validate(options).Failures!.Any(f => f.Contains("is not a valid LockdownReason")), Is.True);
    }

    [Test]
    public void AutoActionThresholdWithAReasonButNoDuration_Fails()
    {
        // A lockdown reason with zero days would ban and immediately un-ban.
        var options = ValidTrustOptions();
        options.AutoActionThresholds =
            [new AutoActionThreshold { ScoreBelow = 10, Reason = nameof(LockdownReason.SPAM_SCAM_ACCOUNT), LockdownDays = 0 }];

        Assert.That(Validate(options).Failures!.Any(f => f.Contains("must have LockdownDays > 0")), Is.True);
    }

    [Test]
    public void EmptyTierArrays_Fail()
    {
        var options = ValidTrustOptions();
        options.AccountAgeTiers      = [];
        options.CleanRecordTiers     = [];
        options.AutoActionThresholds = [];

        Assert.That(Validate(options).Failures, Is.SupersetOf(new[]
        {
            "AccountAgeTiers must have at least one entry",
            "CleanRecordTiers must have at least one entry",
            "AutoActionThresholds must have at least one entry"
        }));
    }
}
