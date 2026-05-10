namespace Argon.Features.Moderation;

using ArgonContracts;

public class ReportSystemOptions
{
    public const string SectionName = "ReportSystem";

    public bool IsEnabled { get; set; }

    public int MinAccountAgeDays { get; set; }
    public int MaxReportsPerHour { get; set; }
    public int MaxReportsPerTargetPerDay { get; set; }
    public int MaxReportsPerPage { get; set; }

    public Dictionary<ReportCategory, int> CategoryPriorityBase { get; set; } = new();

    public int CredibilityPriorityMultiplier { get; set; }
    public int DefaultPriorityBase { get; set; }

    public int ClusterEscalationThreshold { get; set; }
    public int ClusterEscalationWindowMinutes { get; set; }
    public int HighCredibilityThreshold { get; set; }
    public int LowTrustTargetThreshold { get; set; }

    public int DefaultReporterCredibility { get; set; }
    public int MinCredibilityForTrustNotification { get; set; }

    public int CriticalCategoryLockdownDays { get; set; }

    public HashSet<ReportCategory> CriticalCategories { get; set; } = [];
    public HashSet<ReportCategory> SeriousCategories { get; set; } = [];
}

public class TrustScoringOptions
{
    public const string SectionName = "TrustScoring";

    public int DefaultTrustScore { get; set; }
    public int MinTrustScore { get; set; }
    public int MaxTrustScore { get; set; }

    public Dictionary<ReportCategory, int> SeverityWeights { get; set; } = new();

    public int DefaultSeverityWeight { get; set; }
    public int ProvisionalPenaltyDivisor { get; set; }

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
