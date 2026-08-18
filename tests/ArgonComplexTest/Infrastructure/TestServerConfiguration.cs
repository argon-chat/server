namespace ArgonComplexTest.Infrastructure;

/// <summary>
/// Configuration the test host injects on top of the application's own defaults.
/// <para>
/// Kept as data rather than a wall of <c>UseSetting</c> calls because some of these are whole
/// subsystems that refuse to start unless every key is present and internally consistent — the
/// report system validates its options at start-up and fails the host outright otherwise.
/// </para>
/// </summary>
public static class TestServerConfiguration
{
    /// <summary>
    /// A complete, valid moderation configuration. Without it <c>ReportSystem:IsEnabled</c> is false
    /// and the report/trust code paths short-circuit on their first line, so none of that behaviour
    /// can be tested at all.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string?>> ReportSystem { get; } = new Dictionary<string, string?>
    {
        ["ReportSystem:IsEnabled"]                          = "true",
        // Zero so freshly registered test users can file reports immediately.
        ["ReportSystem:MinAccountAgeDays"]                   = "0",
        ["ReportSystem:MaxReportsPerHour"]                   = "1000",
        ["ReportSystem:MaxReportsPerTargetPerDay"]           = "100",
        ["ReportSystem:MaxReportsPerPage"]                   = "50",
        ["ReportSystem:CategoryPriorityBase:SPAM"]           = "10",
        ["ReportSystem:CategoryPriorityBase:SCAM_OR_FRAUD"]  = "40",
        ["ReportSystem:CategoryPriorityBase:VIOLENCE"]       = "80",
        ["ReportSystem:CredibilityPriorityMultiplier"]       = "1",
        ["ReportSystem:DefaultPriorityBase"]                 = "20",
        ["ReportSystem:ClusterEscalationThreshold"]          = "3",
        ["ReportSystem:ClusterEscalationWindowMinutes"]      = "60",
        ["ReportSystem:HighCredibilityThreshold"]            = "80",
        ["ReportSystem:LowTrustTargetThreshold"]             = "30",
        ["ReportSystem:DefaultReporterCredibility"]          = "50",
        ["ReportSystem:MinCredibilityForTrustNotification"]  = "40",
        ["ReportSystem:CriticalCategoryLockdownDays"]        = "30",
        ["ReportSystem:CriticalCategories:0"]                = "VIOLENCE",
        ["ReportSystem:SeriousCategories:0"]                 = "VIOLENCE",
        ["ReportSystem:SeriousCategories:1"]                 = "CHILD_ABUSE",

        ["TrustScoring:DefaultTrustScore"]                   = "50",
        ["TrustScoring:MinTrustScore"]                       = "0",
        ["TrustScoring:MaxTrustScore"]                       = "100",
        ["TrustScoring:SeverityWeights:SPAM"]                = "5",
        ["TrustScoring:SeverityWeights:SCAM_OR_FRAUD"]       = "15",
        ["TrustScoring:SeverityWeights:VIOLENCE"]            = "30",
        ["TrustScoring:DefaultSeverityWeight"]               = "10",
        ["TrustScoring:ProvisionalPenaltyDivisor"]           = "2",
        ["TrustScoring:DecayRate"]                           = "0.5",
        ["TrustScoring:DecayPhase1Days"]                     = "30",
        ["TrustScoring:DecayPhase2Days"]                     = "90",
        ["TrustScoring:DecayPhase2Rate"]                     = "0.25",
        ["TrustScoring:DecayMinimum"]                        = "0.1",
        ["TrustScoring:MinCredibilityInImpact"]              = "10",
        ["TrustScoring:NuisanceToSocialFactor"]              = "0.5",
        ["TrustScoring:BlockCountMultiplier"]                = "2",
        ["TrustScoring:BlockCountCap"]                       = "20",
        ["TrustScoring:ContentScoreCap"]                     = "40",
        ["TrustScoring:SocialScoreCap"]                      = "30",
        ["TrustScoring:CommercialScoreCap"]                  = "30",
        ["TrustScoring:PositiveSignalCap"]                   = "20",
        ["TrustScoring:PhoneVerifiedBoost"]                  = "5",
        ["TrustScoring:TwoFactorBoost"]                      = "5",
        ["TrustScoring:PremiumBoost"]                        = "5",
        ["TrustScoring:FriendBoostDivisor"]                  = "10",
        ["TrustScoring:FriendBoostCap"]                      = "10",
        ["TrustScoring:AccountAgeTiers:0:MinMonths"]         = "6",
        ["TrustScoring:AccountAgeTiers:0:Boost"]             = "5",
        ["TrustScoring:AccountAgeTiers:1:MinMonths"]         = "12",
        ["TrustScoring:AccountAgeTiers:1:Boost"]             = "10",
        ["TrustScoring:CleanRecordTiers:0:MinDays"]          = "30",
        ["TrustScoring:CleanRecordTiers:0:Boost"]            = "5",
        ["TrustScoring:VelocityWindowDays"]                  = "7",
        ["TrustScoring:VelocityThreshold"]                   = "3",
        ["TrustScoring:VelocityHighConfidenceReporters"]     = "5",
        ["TrustScoring:VelocityHighConfidencePenalty"]       = "20",
        ["TrustScoring:VelocityLowConfidenceReporters"]      = "2",
        ["TrustScoring:VelocityLowConfidencePenalty"]        = "5",
        ["TrustScoring:VelocityMidPenalty"]                  = "10",
        ["TrustScoring:RecoveryStartDays"]                   = "30",
        ["TrustScoring:RecoveryMaxBonus"]                    = "20",
        ["TrustScoring:CleanRecordNeverReportedBonus"]       = "10",
        ["TrustScoring:FalseReportPenalty"]                  = "10",
        ["TrustScoring:CredibilityBase"]                     = "50",
        ["TrustScoring:CredibilityAccuracyMax"]              = "30",
        ["TrustScoring:CredibilityAgeMax"]                   = "20",
        ["TrustScoring:CredibilityAgeRate"]                  = "1.5",
        ["TrustScoring:CredibilitySelfReportedPenalty"]      = "10",
        ["TrustScoring:CredibilitySelfReportedThreshold"]    = "3",
        ["TrustScoring:CredibilityRateAbusePenalty"]         = "15",
        ["TrustScoring:CredibilityRateAbuseThreshold"]       = "20",
        ["TrustScoring:CredibilityRateAbuseWindowDays"]      = "7",
        ["TrustScoring:AutoActionThresholds:0:ScoreBelow"]   = "10",
        ["TrustScoring:AutoActionThresholds:0:LockdownDays"] = "0"
    };

    /// <summary>
    /// No per-channel rate cap. The suite sends as fast as it can on purpose, which is the one thing
    /// the cap exists to refuse, and a test that trips it would be reporting on the cap rather than
    /// on whatever it was written to check.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string?>> Messages { get; } = new Dictionary<string, string?>
    {
        ["Messages:PerChannelPerSecond"] = "0"
    };

    /// <summary>Account deletion needs a grace period configured before it will schedule anything.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> AccountDeletion { get; } = new Dictionary<string, string?>
    {
        ["AccountDeletion:GracePeriodDays"]     = "30",
        ["AccountDeletion:ReminderDays:0"]      = "7",
        ["AccountDeletion:ReminderDays:1"]      = "1"
    };
}
