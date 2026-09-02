namespace Argon.Features.Moderation;

using Argon.Features.Clustering;
using ArgonContracts;

/// <summary>
/// How reports are accepted, ranked, escalated and acted on.
/// </summary>
/// <remarks>
/// <para>Every number here is a policy knob, and the policy of the public deployment is not in this
/// repository. The values below are defaults that make a fresh self-hosted instance work; the
/// production deployment overrides them from <c>deploy/pconf.d/reports.json</c>, which is not
/// committed. That split is deliberate: the source says <em>what</em> is counted, and nothing
/// about how many of a thing it takes. Someone who has read every line of the report system knows
/// which signals matter and cannot tell where the thresholds sit.</para>
///
/// <para>The mechanics were arranged so that knowing them buys little anyway. No number of reports
/// does anything to a target on its own: reports move a case up the queue and mark it urgent, and
/// a person decides what happens. The submitter is told "accepted" whether the report was kept,
/// deduplicated, rate-limited or ignored for standing, so nothing about the answer says which.
/// Reporters only count towards escalation when they are independent of each other — not the same
/// address, not the same device, not an account made yesterday — and what "independent" means is
/// decided here, per deployment. Standing is earned only from a moderator's decisions, never from
/// a report merely being filed.</para>
///
/// <para>An absent section leaves the whole system off, which is the shipped state.</para>
/// </remarks>
public sealed class ReportSystemOptions : IValidatableFeatureOptions
{
    public const string SectionName = "ReportSystem";

    public bool IsEnabled { get; set; }

    public ReportFilingOptions     Filing     { get; set; } = new();
    public ReportPriorityOptions   Priority   { get; set; } = new();
    public ReportEscalationOptions Escalation { get; set; } = new();
    public ReportActionOptions     Actions    { get; set; } = new();
    public ReportPrivacyOptions    Privacy    { get; set; } = new();

    /// <summary>Largest page the operator console may ask for at once.</summary>
    public int MaxPageSize { get; set; } = 100;

    public void Validate(IFeatureConfigurationReport report)
    {
        // Nothing about a disabled report system can hurt a deployment, and demanding a coherent
        // policy from one that turned the feature off would be its own footgun.
        if (!IsEnabled)
            return;

        Filing.Validate(report);
        Priority.Validate(report);
        Escalation.Validate(report);
        Actions.Validate(report);
        Privacy.Validate(report);

        report.RequireRange(MaxPageSize, 1, 200, nameof(MaxPageSize));
    }
}

/// <summary>
/// Who may file, how often, and what counts as the same report twice.
/// </summary>
/// <remarks>
/// A submission that fails any of these is <em>acknowledged and dropped</em>, never refused: an
/// error would tell the sender exactly where the line is. The limits are counted in the cache, so
/// they cost nothing on the database and are shared by every node.
/// </remarks>
public sealed class ReportFilingOptions
{
    /// <summary>Accounts younger than this are acknowledged and not recorded.</summary>
    public int MinAccountAgeDays { get; set; } = 1;

    public int MaxReportsPerHour { get; set; } = 10;

    public int MaxReportsPerDay { get; set; } = 40;

    /// <summary>Reports one account may file about one thing in a day, across categories.</summary>
    public int MaxReportsPerTargetPerDay { get; set; } = 3;

    /// <summary>
    /// A second report from the same account about the same thing in the same category inside this
    /// window is the same report — a double tap, not new information.
    /// </summary>
    public int DuplicateWindowHours { get; set; } = 24;

    /// <summary>The free-text comment is cut to this; the column holds 2000.</summary>
    public int MaxAdditionalInfoLength { get; set; } = 1000;

    internal void Validate(IFeatureConfigurationReport report)
    {
        const string prefix = nameof(ReportSystemOptions.Filing);

        report.Require(MinAccountAgeDays >= 0, $"{prefix}:{nameof(MinAccountAgeDays)}", "must be >= 0");
        report.Require(MaxReportsPerHour >= 1, $"{prefix}:{nameof(MaxReportsPerHour)}", "must be >= 1");
        report.Require(MaxReportsPerDay >= MaxReportsPerHour, $"{prefix}:{nameof(MaxReportsPerDay)}",
            $"is below {nameof(MaxReportsPerHour)} ({MaxReportsPerHour}), so the hourly allowance could never be spent");
        report.Require(MaxReportsPerTargetPerDay >= 1, $"{prefix}:{nameof(MaxReportsPerTargetPerDay)}", "must be >= 1");
        report.Require(DuplicateWindowHours >= 1, $"{prefix}:{nameof(DuplicateWindowHours)}", "must be >= 1");
        report.RequireRange(MaxAdditionalInfoLength, 0, 2000, $"{prefix}:{nameof(MaxAdditionalInfoLength)}");
    }
}

/// <summary>
/// How a case is ranked in the queue. Ranking is all a report can do by itself.
/// </summary>
public sealed class ReportPriorityOptions
{
    /// <summary>
    /// Base weight per category. Merged by key from configuration, so a deployment that means to
    /// change one category writes one line.
    /// </summary>
    public Dictionary<ReportCategory, int> CategoryBase { get; set; } = new()
    {
        [ReportCategory.CHILD_ABUSE]           = 5000,
        [ReportCategory.ILLEGAL_ADULT_CONTENT] = 4500,
        [ReportCategory.VIOLENCE]              = 4000,
        [ReportCategory.ILLEGAL_GOODS]         = 3500,
        [ReportCategory.SCAM_OR_FRAUD]         = 3000,
        [ReportCategory.PERSONAL_DATA]         = 2500,
        [ReportCategory.COPYRIGHT]             = 1500,
        [ReportCategory.SPAM]                  = 500,
        [ReportCategory.OTHER]                 = 200,
        [ReportCategory.I_DONT_LIKE_IT]        = 100
    };

    /// <summary>Weight of a category the table does not name.</summary>
    public int DefaultBase { get; set; } = 200;

    /// <summary>Reporter credibility (0–100) assumed for an account that has never been scored.</summary>
    public int DefaultCredibility { get; set; } = 50;

    /// <summary>Priority per point of the best reporter's credibility.</summary>
    public int CredibilityMultiplier { get; set; } = 5;

    /// <summary>Priority per independent reporter, up to <see cref="IndependentReporterBoostCap"/>.</summary>
    public int IndependentReporterBoost { get; set; } = 150;

    public int IndependentReporterBoostCap { get; set; } = 1500;

    internal void Validate(IFeatureConfigurationReport report)
    {
        const string prefix = nameof(ReportSystemOptions.Priority);

        foreach (var (category, weight) in CategoryBase)
            report.Require(weight >= 0, $"{prefix}:{nameof(CategoryBase)}:{category}", "must be >= 0");

        report.Require(DefaultBase >= 0, $"{prefix}:{nameof(DefaultBase)}", "must be >= 0");
        report.RequireRange(DefaultCredibility, 0, 100, $"{prefix}:{nameof(DefaultCredibility)}");
        report.Require(CredibilityMultiplier >= 0, $"{prefix}:{nameof(CredibilityMultiplier)}", "must be >= 0");
        report.Require(IndependentReporterBoost >= 0, $"{prefix}:{nameof(IndependentReporterBoost)}", "must be >= 0");
        report.Require(IndependentReporterBoostCap >= IndependentReporterBoost, $"{prefix}:{nameof(IndependentReporterBoostCap)}",
            $"is below {nameof(IndependentReporterBoost)}, so the first independent reporter would already be over the cap");
    }
}

/// <summary>
/// When a case is marked urgent. Urgent means the top of the queue and nothing else.
/// </summary>
/// <remarks>
/// <para>The rule that used to hide messages and lock accounts on a count of reports is gone, and
/// on purpose: three accounts a week old were enough to take down any message on the instance,
/// and the threshold was in a public file. What is left is a flag a moderator sees first.</para>
///
/// <para>The count that matters is of <em>independent</em> reporters. Two reports from one
/// address, one device or one fresh account are one reporter for this purpose, and the definition
/// of fresh lives here rather than in the code.</para>
/// </remarks>
public sealed class ReportEscalationOptions
{
    /// <summary>Independent reporters inside <see cref="WindowMinutes"/> that make a case urgent.</summary>
    public int IndependentReportersThreshold { get; set; } = 3;

    public int WindowMinutes { get; set; } = 360;

    /// <summary>A reporter at or above this credibility escalates a serious category on their own.</summary>
    public int HighCredibilityThreshold { get; set; } = 80;

    /// <summary>A target below this trust score escalates a serious category.</summary>
    public int LowTrustTargetThreshold { get; set; } = 200;

    /// <summary>Younger accounts file reports that count for ranking but not for independence.</summary>
    public int IndependentReporterMinAccountAgeDays { get; set; } = 7;

    /// <summary>Reporters below this credibility count for ranking but not for independence.</summary>
    public int IndependentReporterMinCredibility { get; set; } = 30;

    /// <summary>
    /// Categories that are urgent from the first report. Empty by default and appended to from
    /// configuration — a list cannot be shortened by a deployment, so the source ships none and the
    /// deployment states its own.
    /// </summary>
    public HashSet<ReportCategory> CriticalCategories { get; set; } = [];

    /// <summary>Categories the credibility and low-trust rules apply to.</summary>
    public HashSet<ReportCategory> SeriousCategories { get; set; } = [];

    internal void Validate(IFeatureConfigurationReport report)
    {
        const string prefix = nameof(ReportSystemOptions.Escalation);

        // A "cluster" of one is a report; the rule would fire on every single one.
        report.Require(IndependentReportersThreshold >= 2, $"{prefix}:{nameof(IndependentReportersThreshold)}", "must be >= 2");
        report.Require(WindowMinutes >= 1, $"{prefix}:{nameof(WindowMinutes)}", "must be >= 1");
        report.RequireRange(HighCredibilityThreshold, 0, 100, $"{prefix}:{nameof(HighCredibilityThreshold)}");
        report.Require(LowTrustTargetThreshold >= 0, $"{prefix}:{nameof(LowTrustTargetThreshold)}", "must be >= 0");
        report.Require(IndependentReporterMinAccountAgeDays >= 0, $"{prefix}:{nameof(IndependentReporterMinAccountAgeDays)}", "must be >= 0");
        report.RequireRange(IndependentReporterMinCredibility, 0, 100, $"{prefix}:{nameof(IndependentReporterMinCredibility)}");

        // Critical implies serious everywhere downstream; a category that is urgent on sight but
        // not serious when a credible reporter names it is a contradiction a deployment wrote.
        report.Require(CriticalCategories.IsSubsetOf(SeriousCategories), $"{prefix}:{nameof(CriticalCategories)}",
            $"must be a subset of {nameof(SeriousCategories)}");

        report.Prefer(SeriousCategories.Count > 0, $"{prefix}:{nameof(SeriousCategories)}",
            "is empty, so neither reporter credibility nor a target's low trust ever escalates anything");
        report.Prefer(CriticalCategories.Count > 0, $"{prefix}:{nameof(CriticalCategories)}",
            "is empty, so no category is urgent from the first report — including the ones the law says are");
    }
}

/// <summary>
/// What resolving a case may do to its target, and for how long.
/// </summary>
public sealed class ReportActionOptions
{
    /// <summary>Length of the middle-severity lockdown behind MUTE_USER.</summary>
    public int MuteDays { get; set; } = 3;

    /// <summary>Length of the middle-severity lockdown behind RESTRICT_USER.</summary>
    public int RestrictDays { get; set; } = 7;

    /// <summary>Length of the critical lockdown behind BAN_USER; 0 is permanent.</summary>
    public int BanDays { get; set; }

    /// <summary>Tell a reporter, through a system notification, when their case closes.</summary>
    public bool NotifyReporterOnResolution { get; set; } = true;

    /// <summary>Whether WARN_USER delivers anything. Off, it records the decision and says nothing.</summary>
    public bool NotifyTargetOnWarning { get; set; } = true;

    internal void Validate(IFeatureConfigurationReport report)
    {
        const string prefix = nameof(ReportSystemOptions.Actions);

        report.Require(MuteDays >= 1, $"{prefix}:{nameof(MuteDays)}", "must be >= 1; a mute of zero days is applied and lifted in one step");
        report.Require(RestrictDays >= 1, $"{prefix}:{nameof(RestrictDays)}", "must be >= 1");
        report.Require(BanDays >= 0, $"{prefix}:{nameof(BanDays)}", "must be >= 0; 0 is permanent");
    }
}

/// <summary>
/// What is kept about the person who filed.
/// </summary>
/// <remarks>
/// <para>A report used to carry <c>SHA256(address)</c>, which for an IPv4 address is a lookup
/// table away from the address itself. It now carries an HMAC under a key only the deployment
/// holds, or nothing. Nothing is the shipped state: without a key no address or device hash is
/// written at all, and independence between reporters is judged on accounts alone. That loses a
/// signal rather than inventing one, which is the right direction for a privacy default.</para>
/// </remarks>
public sealed class ReportPrivacyOptions
{
    /// <summary>
    /// HMAC key for the address and device hashes stored on a report. Rotating it makes old and
    /// new reports incomparable, which is fine — the hashes are only compared inside the
    /// escalation window.
    /// </summary>
    public string? ReporterIdentityPepper { get; set; }

    internal void Validate(IFeatureConfigurationReport report)
    {
        const string prefix = nameof(ReportSystemOptions.Privacy);

        report.Prefer(!string.IsNullOrWhiteSpace(ReporterIdentityPepper), $"{prefix}:{nameof(ReporterIdentityPepper)}",
            "is not set, so no address or device hash is stored and a reporter with five accounts on one machine "
          + "counts as five independent people");

        if (!string.IsNullOrWhiteSpace(ReporterIdentityPepper))
            report.Require(ReporterIdentityPepper.Length >= 16, $"{prefix}:{nameof(ReporterIdentityPepper)}",
                "is shorter than 16 characters; a key that short does not protect the addresses it hashes");
    }
}

public class TrustScoringOptions : IValidatableFeatureOptions
{
    public const string SectionName = "TrustScoring";

    /// <summary>
    /// Only meaningful while the report system is on, which is the one thing this section cannot see
    /// for itself.
    /// </summary>
    public void Validate(IFeatureConfigurationReport report)
    {
        var reports = report.Read<ReportSystemOptions>(ReportSystemOptions.SectionName);
        var result  = new TrustScoringOptionsValidator(Options.Create(reports)).Validate(null, this);

        foreach (var failure in result.Failures ?? [])
            report.Invalid(failure);
    }

    public int DefaultTrustScore { get; set; }
    public int MinTrustScore { get; set; }
    public int MaxTrustScore { get; set; }

    public Dictionary<ReportCategory, int> SeverityWeights { get; set; } = new();

    public int DefaultSeverityWeight { get; set; }

    public double DecayRate { get; set; }
    public int DecayPhase1Days { get; set; }
    public int DecayPhase2Days { get; set; }
    public double DecayPhase2Rate { get; set; }
    public double DecayMinimum { get; set; }

    public int MinCredibilityInImpact { get; set; }
    public double NuisanceToSocialFactor { get; set; }
    public int BlockCountMultiplier { get; set; }
    public int BlockCountCap { get; set; }
    public int ContentScoreCap { get; set; }
    public int SocialScoreCap { get; set; }
    public int CommercialScoreCap { get; set; }

    public int PositiveSignalCap { get; set; }
    public int PhoneVerifiedBoost { get; set; }
    public int TwoFactorBoost { get; set; }
    public int PremiumBoost { get; set; }
    public int FriendBoostDivisor { get; set; }
    public int FriendBoostCap { get; set; }

    public AccountAgeTier[] AccountAgeTiers { get; set; } = [];
    public CleanRecordTier[] CleanRecordTiers { get; set; } = [];

    public int VelocityWindowDays { get; set; }
    public int VelocityThreshold { get; set; }
    public int VelocityHighConfidenceReporters { get; set; }
    public int VelocityHighConfidencePenalty { get; set; }
    public int VelocityLowConfidenceReporters { get; set; }
    public int VelocityLowConfidencePenalty { get; set; }
    public int VelocityMidPenalty { get; set; }

    public int RecoveryStartDays { get; set; }
    public int RecoveryMaxBonus { get; set; }
    public int CleanRecordNeverReportedBonus { get; set; }

    public int FalseReportPenalty { get; set; }

    public int CredibilityBase { get; set; }
    public int CredibilityAccuracyMax { get; set; }
    public int CredibilityAgeMax { get; set; }
    public double CredibilityAgeRate { get; set; }
    public int CredibilitySelfReportedPenalty { get; set; }
    public int CredibilitySelfReportedThreshold { get; set; }
    public int CredibilityRateAbusePenalty { get; set; }
    public int CredibilityRateAbuseThreshold { get; set; }
    public int CredibilityRateAbuseWindowDays { get; set; }

    public AutoActionThreshold[] AutoActionThresholds { get; set; } = [];
}

public class AccountAgeTier
{
    public double MinMonths { get; set; }
    public int Boost { get; set; }
}

public class CleanRecordTier
{
    public int MinDays { get; set; }
    public int Boost { get; set; }
}

public class AutoActionThreshold
{
    public int ScoreBelow { get; set; }
    public string? Reason { get; set; }
    public int LockdownDays { get; set; }
}
