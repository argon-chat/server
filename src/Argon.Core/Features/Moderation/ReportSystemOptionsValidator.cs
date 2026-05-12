namespace Argon.Features.Moderation;

using ArgonContracts;
using Microsoft.Extensions.Options;

public class ReportSystemOptionsValidator : IValidateOptions<ReportSystemOptions>
{
    public ValidateOptionsResult Validate(string? name, ReportSystemOptions o)
    {
        if (!o.IsEnabled)
            return ValidateOptionsResult.Success;

        var errors = new List<string>();

        if (o.MinAccountAgeDays < 0)
            errors.Add("MinAccountAgeDays must be >= 0");
        if (o.MaxReportsPerHour <= 0)
            errors.Add("MaxReportsPerHour must be > 0");
        if (o.MaxReportsPerTargetPerDay <= 0)
            errors.Add("MaxReportsPerTargetPerDay must be > 0");
        if (o.MaxReportsPerPage is <= 0 or > 200)
            errors.Add("MaxReportsPerPage must be between 1 and 200");

        if (o.CategoryPriorityBase.Count == 0)
            errors.Add("CategoryPriorityBase must have at least one entry");

        foreach (var (cat, priority) in o.CategoryPriorityBase)
        {
            if (priority < 0)
                errors.Add($"CategoryPriorityBase[{cat}] must be >= 0");
        }

        if (o.CredibilityPriorityMultiplier < 0)
            errors.Add("CredibilityPriorityMultiplier must be >= 0");
        if (o.DefaultPriorityBase < 0)
            errors.Add("DefaultPriorityBase must be >= 0");

        if (o.ClusterEscalationThreshold < 2)
            errors.Add("ClusterEscalationThreshold must be >= 2");
        if (o.ClusterEscalationWindowMinutes <= 0)
            errors.Add("ClusterEscalationWindowMinutes must be > 0");
        if (o.HighCredibilityThreshold is < 0 or > 100)
            errors.Add("HighCredibilityThreshold must be between 0 and 100");
        if (o.LowTrustTargetThreshold < 0)
            errors.Add("LowTrustTargetThreshold must be >= 0");

        if (o.DefaultReporterCredibility is < 0 or > 100)
            errors.Add("DefaultReporterCredibility must be between 0 and 100");
        if (o.MinCredibilityForTrustNotification is < 0 or > 100)
            errors.Add("MinCredibilityForTrustNotification must be between 0 and 100");

        if (o.CriticalCategoryLockdownDays <= 0)
            errors.Add("CriticalCategoryLockdownDays must be > 0");

        if (o.CriticalCategories.Count == 0)
            errors.Add("CriticalCategories must not be empty");
        if (o.SeriousCategories.Count == 0)
            errors.Add("SeriousCategories must not be empty");

        if (!o.CriticalCategories.IsSubsetOf(o.SeriousCategories))
            errors.Add("CriticalCategories must be a subset of SeriousCategories");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}

public class TrustScoringOptionsValidator : IValidateOptions<TrustScoringOptions>
{
    private readonly IOptions<ReportSystemOptions> _reportOptions;

    public TrustScoringOptionsValidator(IOptions<ReportSystemOptions> reportOptions)
        => _reportOptions = reportOptions;

    public ValidateOptionsResult Validate(string? name, TrustScoringOptions o)
    {
        if (!_reportOptions.Value.IsEnabled)
            return ValidateOptionsResult.Success;

        var errors = new List<string>();

        if (o.MaxTrustScore <= 0)
            errors.Add("MaxTrustScore must be > 0");
        if (o.MinTrustScore < 0)
            errors.Add("MinTrustScore must be >= 0");
        if (o.MinTrustScore >= o.MaxTrustScore)
            errors.Add("MinTrustScore must be less than MaxTrustScore");
        if (o.DefaultTrustScore < o.MinTrustScore || o.DefaultTrustScore > o.MaxTrustScore)
            errors.Add($"DefaultTrustScore must be between {o.MinTrustScore} and {o.MaxTrustScore}");

        if (o.SeverityWeights.Count == 0)
            errors.Add("SeverityWeights must have at least one entry");

        foreach (var (cat, weight) in o.SeverityWeights)
        {
            if (weight < 0)
                errors.Add($"SeverityWeights[{cat}] must be >= 0");
        }

        if (o.DefaultSeverityWeight < 0)
            errors.Add("DefaultSeverityWeight must be >= 0");
        if (o.ProvisionalPenaltyDivisor <= 0)
            errors.Add("ProvisionalPenaltyDivisor must be > 0 (division by zero)");

        if (o.DecayRate <= 0)
            errors.Add("DecayRate must be > 0");
        if (o.DecayPhase1Days <= 0)
            errors.Add("DecayPhase1Days must be > 0");
        if (o.DecayPhase2Days <= o.DecayPhase1Days)
            errors.Add("DecayPhase2Days must be greater than DecayPhase1Days");
        if (o.DecayPhase2Rate <= 0)
            errors.Add("DecayPhase2Rate must be > 0");
        if (o.DecayMinimum is < 0 or > 1)
            errors.Add("DecayMinimum must be between 0 and 1");

        if (o.MinCredibilityInImpact < 0)
            errors.Add("MinCredibilityInImpact must be >= 0");
        if (o.NuisanceToSocialFactor is < 0 or > 1)
            errors.Add("NuisanceToSocialFactor must be between 0 and 1");
        if (o.BlockCountMultiplier < 0)
            errors.Add("BlockCountMultiplier must be >= 0");
        if (o.BlockCountCap < 0)
            errors.Add("BlockCountCap must be >= 0");
        if (o.ContentScoreCap <= 0)
            errors.Add("ContentScoreCap must be > 0");
        if (o.SocialScoreCap <= 0)
            errors.Add("SocialScoreCap must be > 0");
        if (o.CommercialScoreCap <= 0)
            errors.Add("CommercialScoreCap must be > 0");

        if (o.PositiveSignalCap < 0)
            errors.Add("PositiveSignalCap must be >= 0");
        if (o.PhoneVerifiedBoost < 0)
            errors.Add("PhoneVerifiedBoost must be >= 0");
        if (o.TwoFactorBoost < 0)
            errors.Add("TwoFactorBoost must be >= 0");
        if (o.PremiumBoost < 0)
            errors.Add("PremiumBoost must be >= 0");
        if (o.FriendBoostDivisor <= 0)
            errors.Add("FriendBoostDivisor must be > 0 (division by zero)");
        if (o.FriendBoostCap < 0)
            errors.Add("FriendBoostCap must be >= 0");

        if (o.AccountAgeTiers.Length == 0)
            errors.Add("AccountAgeTiers must have at least one entry");

        foreach (var tier in o.AccountAgeTiers)
        {
            if (tier.MinMonths < 0)
                errors.Add($"AccountAgeTier.MinMonths must be >= 0 (got {tier.MinMonths})");
            if (tier.Boost < 0)
                errors.Add($"AccountAgeTier.Boost must be >= 0 (got {tier.Boost})");
        }

        if (o.CleanRecordTiers.Length == 0)
            errors.Add("CleanRecordTiers must have at least one entry");

        foreach (var tier in o.CleanRecordTiers)
        {
            if (tier.MinDays <= 0)
                errors.Add($"CleanRecordTier.MinDays must be > 0 (got {tier.MinDays})");
            if (tier.Boost < 0)
                errors.Add($"CleanRecordTier.Boost must be >= 0 (got {tier.Boost})");
        }

        if (o.VelocityWindowDays <= 0)
            errors.Add("VelocityWindowDays must be > 0");
        if (o.VelocityThreshold <= 0)
            errors.Add("VelocityThreshold must be > 0");
        if (o.VelocityHighConfidenceReporters <= 0)
            errors.Add("VelocityHighConfidenceReporters must be > 0");
        if (o.VelocityLowConfidenceReporters <= 0)
            errors.Add("VelocityLowConfidenceReporters must be > 0");
        if (o.VelocityLowConfidenceReporters >= o.VelocityHighConfidenceReporters)
            errors.Add("VelocityLowConfidenceReporters must be less than VelocityHighConfidenceReporters");
        if (o.VelocityHighConfidencePenalty < 0)
            errors.Add("VelocityHighConfidencePenalty must be >= 0");
        if (o.VelocityLowConfidencePenalty < 0)
            errors.Add("VelocityLowConfidencePenalty must be >= 0");
        if (o.VelocityMidPenalty < 0)
            errors.Add("VelocityMidPenalty must be >= 0");

        if (o.RecoveryStartDays <= 0)
            errors.Add("RecoveryStartDays must be > 0");
        if (o.RecoveryMaxBonus < 0)
            errors.Add("RecoveryMaxBonus must be >= 0");
        if (o.CleanRecordNeverReportedBonus < 0)
            errors.Add("CleanRecordNeverReportedBonus must be >= 0");
        if (o.FalseReportPenalty < 0)
            errors.Add("FalseReportPenalty must be >= 0");

        if (o.CredibilityBase is < 0 or > 100)
            errors.Add("CredibilityBase must be between 0 and 100");
        if (o.CredibilityAccuracyMax < 0)
            errors.Add("CredibilityAccuracyMax must be >= 0");
        if (o.CredibilityAgeMax < 0)
            errors.Add("CredibilityAgeMax must be >= 0");
        if (o.CredibilityAgeRate < 0)
            errors.Add("CredibilityAgeRate must be >= 0");
        if (o.CredibilitySelfReportedPenalty < 0)
            errors.Add("CredibilitySelfReportedPenalty must be >= 0");
        if (o.CredibilitySelfReportedThreshold <= 0)
            errors.Add("CredibilitySelfReportedThreshold must be > 0");
        if (o.CredibilityRateAbusePenalty < 0)
            errors.Add("CredibilityRateAbusePenalty must be >= 0");
        if (o.CredibilityRateAbuseThreshold <= 0)
            errors.Add("CredibilityRateAbuseThreshold must be > 0");
        if (o.CredibilityRateAbuseWindowDays <= 0)
            errors.Add("CredibilityRateAbuseWindowDays must be > 0");

        if (o.AutoActionThresholds.Length == 0)
            errors.Add("AutoActionThresholds must have at least one entry");

        foreach (var t in o.AutoActionThresholds)
        {
            if (t.ScoreBelow < o.MinTrustScore || t.ScoreBelow > o.MaxTrustScore)
                errors.Add($"AutoActionThreshold.ScoreBelow {t.ScoreBelow} is outside [{o.MinTrustScore}, {o.MaxTrustScore}]");
            if (t.Reason is not null && !Enum.TryParse<LockdownReason>(t.Reason, out _))
                errors.Add($"AutoActionThreshold.Reason '{t.Reason}' is not a valid LockdownReason");
            if (t.LockdownDays < 0)
                errors.Add($"AutoActionThreshold.LockdownDays must be >= 0 (got {t.LockdownDays})");
            if (t.Reason is not null && t.LockdownDays <= 0)
                errors.Add($"AutoActionThreshold with Reason '{t.Reason}' must have LockdownDays > 0");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
