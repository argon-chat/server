namespace ArgonSharedLogicTest;

using Argon.Features.Moderation;
using ArgonContracts;
using Microsoft.Extensions.Options;

/// <summary>
/// These validators are the only thing standing between a typo in <c>appsettings</c> and a
/// moderation system that divides by zero or auto-locks accounts on a threshold outside its own
/// score range. They run at start-up, so every rule here is a rule that fails the deploy rather
/// than the request.
/// </summary>
[TestFixture]
public class ReportSystemOptionsValidatorTests
{
    private static ReportSystemOptions ValidReportOptions() => new()
    {
        IsEnabled                          = true,
        MinAccountAgeDays                  = 7,
        MaxReportsPerHour                  = 10,
        MaxReportsPerTargetPerDay          = 3,
        MaxReportsPerPage                  = 50,
        CategoryPriorityBase               = new() { [ReportCategory.SPAM] = 10 },
        CredibilityPriorityMultiplier      = 2,
        DefaultPriorityBase                = 5,
        ClusterEscalationThreshold         = 3,
        ClusterEscalationWindowMinutes     = 60,
        HighCredibilityThreshold           = 80,
        LowTrustTargetThreshold            = 20,
        DefaultReporterCredibility         = 50,
        MinCredibilityForTrustNotification = 40,
        CriticalCategoryLockdownDays       = 30,
        CriticalCategories                 = [ReportCategory.SPAM],
        SeriousCategories                  = [ReportCategory.SPAM, ReportCategory.SCAM_OR_FRAUD]
    };

    private static ValidateOptionsResult Validate(ReportSystemOptions options)
        => new ReportSystemOptionsValidator().Validate(null, options);

    [Test]
    public void ValidOptions_Pass()
        => Assert.That(Validate(ValidReportOptions()).Succeeded, Is.True);

    [Test]
    public void DisabledSystem_SkipsValidationEntirely()
    {
        // Nothing about a disabled report system can hurt production, and demanding a fully
        // populated config to turn the feature *off* would be its own footgun.
        var nonsense = new ReportSystemOptions { IsEnabled = false, MaxReportsPerHour = -1 };

        Assert.That(Validate(nonsense).Succeeded, Is.True);
    }

    [Test]
    public void NegativeMinAccountAge_Fails()
    {
        var options = ValidReportOptions();
        options.MinAccountAgeDays = -1;

        Assert.That(Validate(options).Failures, Does.Contain("MinAccountAgeDays must be >= 0"));
    }

    [Test]
    public void NonPositiveRateLimits_Fail()
    {
        var options = ValidReportOptions();
        options.MaxReportsPerHour         = 0;
        options.MaxReportsPerTargetPerDay = 0;

        Assert.That(Validate(options).Failures, Is.SupersetOf(new[]
        {
            "MaxReportsPerHour must be > 0",
            "MaxReportsPerTargetPerDay must be > 0"
        }));
    }

    [Test]
    public void PageSizeOutsideRange_Fails([Values(0, 201)] int pageSize)
    {
        var options = ValidReportOptions();
        options.MaxReportsPerPage = pageSize;

        Assert.That(Validate(options).Failures, Does.Contain("MaxReportsPerPage must be between 1 and 200"));
    }

    [Test]
    public void EmptyCategoryPriorityBase_Fails()
    {
        var options = ValidReportOptions();
        options.CategoryPriorityBase = new();

        Assert.That(Validate(options).Failures, Does.Contain("CategoryPriorityBase must have at least one entry"));
    }

    [Test]
    public void ClusterEscalationThresholdBelowTwo_Fails()
    {
        // A "cluster" of one is just a report; the escalation path would fire on every single one.
        var options = ValidReportOptions();
        options.ClusterEscalationThreshold = 1;

        Assert.That(Validate(options).Failures, Does.Contain("ClusterEscalationThreshold must be >= 2"));
    }

    [Test]
    public void CredibilityThresholdOutsidePercentRange_Fails([Values(-1, 101)] int threshold)
    {
        var options = ValidReportOptions();
        options.HighCredibilityThreshold = threshold;

        Assert.That(Validate(options).Failures, Does.Contain("HighCredibilityThreshold must be between 0 and 100"));
    }

    [Test]
    public void CriticalCategoriesNotASubsetOfSerious_Fails()
    {
        // Critical implies serious everywhere downstream; letting the two sets diverge means a
        // category that triggers a lockdown but is not treated as serious when scoring trust.
        var options = ValidReportOptions();
        options.CriticalCategories = [ReportCategory.VIOLENCE];
        options.SeriousCategories  = [ReportCategory.SPAM];

        Assert.That(Validate(options).Failures, Does.Contain("CriticalCategories must be a subset of SeriousCategories"));
    }

    [Test]
    public void EmptyCategorySets_Fail()
    {
        var options = ValidReportOptions();
        options.CriticalCategories = [];
        options.SeriousCategories  = [];

        Assert.That(Validate(options).Failures, Is.SupersetOf(new[]
        {
            "CriticalCategories must not be empty",
            "SeriousCategories must not be empty"
        }));
    }

    [Test]
    public void MultipleProblems_AreAllReported()
    {
        // The validator collects rather than short-circuits, so one deploy surfaces every mistake.
        var options = ValidReportOptions();
        options.MinAccountAgeDays = -1;
        options.MaxReportsPerHour = 0;
        options.MaxReportsPerPage = 500;

        Assert.That(Validate(options).Failures!.Count(), Is.GreaterThanOrEqualTo(3));
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
        ProvisionalPenaltyDivisor        = 2,
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
    public void ZeroDivisors_Fail()
    {
        // Both of these are used as denominators; the validator calls that out by name rather than
        // letting a DivideByZeroException surface inside trust recalculation.
        var options = ValidTrustOptions();
        options.ProvisionalPenaltyDivisor = 0;
        options.FriendBoostDivisor        = 0;

        Assert.That(Validate(options).Failures, Is.SupersetOf(new[]
        {
            "ProvisionalPenaltyDivisor must be > 0 (division by zero)",
            "FriendBoostDivisor must be > 0 (division by zero)"
        }));
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
