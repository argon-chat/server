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
    /// The HMAC key the test host hashes reporter addresses and devices with. Without one, no
    /// hash is stored and every account is an independent reporter — which would make the
    /// sock-puppet tests pass for the wrong reason.
    /// </summary>
    public const string ReporterIdentityPepper = "integration-tests-reporter-identity-pepper";

    /// <summary>Independent reporters that make a case urgent in the test configuration.</summary>
    public const int IndependentReportersThreshold = 3;

    /// <summary>
    /// A complete, valid moderation configuration. Without it <c>ReportSystem:IsEnabled</c> is false
    /// and the report/trust code paths short-circuit on their first line, so none of that behaviour
    /// can be tested at all.
    /// </summary>
    /// <remarks>
    /// The filing limits are raised out of the way and the account-age and credibility floors are
    /// zero: every test user is registered seconds before it files, and the suite is about the
    /// rules, not the throttles. Escalation and actions keep small, exact values the tests assert
    /// against by name.
    /// </remarks>
    public static IEnumerable<KeyValuePair<string, string?>> ReportSystem { get; } = new Dictionary<string, string?>
    {
        ["ReportSystem:IsEnabled"] = "true",
        ["ReportSystem:MaxPageSize"] = "200",

        ["ReportSystem:Filing:MinAccountAgeDays"]         = "0",
        ["ReportSystem:Filing:MaxReportsPerHour"]         = "1000",
        ["ReportSystem:Filing:MaxReportsPerDay"]          = "5000",
        ["ReportSystem:Filing:MaxReportsPerTargetPerDay"] = "100",
        ["ReportSystem:Filing:DuplicateWindowHours"]      = "24",
        ["ReportSystem:Filing:MaxAdditionalInfoLength"]   = "200",

        ["ReportSystem:Priority:CategoryBase:SPAM"]          = "10",
        ["ReportSystem:Priority:CategoryBase:SCAM_OR_FRAUD"] = "40",
        ["ReportSystem:Priority:CategoryBase:VIOLENCE"]      = "80",
        ["ReportSystem:Priority:DefaultBase"]                = "20",
        ["ReportSystem:Priority:DefaultCredibility"]         = "50",
        ["ReportSystem:Priority:CredibilityMultiplier"]      = "1",
        ["ReportSystem:Priority:IndependentReporterBoost"]   = "100",
        ["ReportSystem:Priority:IndependentReporterBoostCap"] = "1000",

        ["ReportSystem:Escalation:IndependentReportersThreshold"]        = IndependentReportersThreshold.ToString(),
        ["ReportSystem:Escalation:WindowMinutes"]                        = "60",
        ["ReportSystem:Escalation:HighCredibilityThreshold"]             = "80",
        ["ReportSystem:Escalation:LowTrustTargetThreshold"]              = "30",
        ["ReportSystem:Escalation:IndependentReporterMinAccountAgeDays"] = "0",
        ["ReportSystem:Escalation:IndependentReporterMinCredibility"]    = "0",
        ["ReportSystem:Escalation:CriticalCategories:0"]                 = "VIOLENCE",
        ["ReportSystem:Escalation:SeriousCategories:0"]                  = "VIOLENCE",
        ["ReportSystem:Escalation:SeriousCategories:1"]                  = "CHILD_ABUSE",

        ["ReportSystem:Actions:MuteDays"]                   = "1",
        ["ReportSystem:Actions:RestrictDays"]               = "2",
        ["ReportSystem:Actions:BanDays"]                    = "0",
        ["ReportSystem:Actions:NotifyReporterOnResolution"] = "true",
        ["ReportSystem:Actions:NotifyTargetOnWarning"]      = "true",

        ["ReportSystem:Privacy:ReporterIdentityPepper"] = ReporterIdentityPepper,

        ["TrustScoring:DefaultTrustScore"]                   = "50",
        ["TrustScoring:MinTrustScore"]                       = "0",
        ["TrustScoring:MaxTrustScore"]                       = "100",
        ["TrustScoring:SeverityWeights:SPAM"]                = "5",
        ["TrustScoring:SeverityWeights:SCAM_OR_FRAUD"]       = "15",
        ["TrustScoring:SeverityWeights:VIOLENCE"]            = "30",
        ["TrustScoring:DefaultSeverityWeight"]               = "10",
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

    /// <summary>
    /// No meaningful rate limit on the identity server.
    /// </summary>
    /// <remarks>
    /// Shipped, one address gets five credential attempts a minute — which is the point of the
    /// limiter and exactly what a fixture exercising sign-in does in its first few seconds. Every
    /// request in a test run also comes from the same (absent) address, so the global partition is
    /// shared by the whole suite. Raised rather than slept around; the limiter's own configuration is
    /// covered by its unit tests.
    /// </remarks>
    public static IEnumerable<KeyValuePair<string, string?>> Aegis { get; } = new Dictionary<string, string?>
    {
        ["AegisRateLimits:Auth:Permits"]   = "100000",
        ["AegisRateLimits:Token:Permits"]  = "100000",
        ["AegisRateLimits:Global:Permits"] = "100000",

        // Nothing is excluded by default, because this role maps no path that would want to be:
        // the Sentry tunnel lives on the entry point, not here. One is configured anyway so the
        // exclusion itself stays covered -- it is a branch whose absence looks exactly like a
        // branch that works, since both leave every real path with a policy.
        ["Aegis:CspExcludedPaths:0"]       = "/k",

        // Where userinfo builds avatar addresses. Empty in the shipped defaults, so without this the
        // field is simply absent and a test for it would be asserting the absence of configuration.
        ["Aegis:AvatarBaseUrl"]            = "https://api.test.local"
    };

    /// <summary>Account deletion needs a grace period configured before it will schedule anything.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> AccountDeletion { get; } = new Dictionary<string, string?>
    {
        ["AccountDeletion:GracePeriodDays"]     = "30",
        ["AccountDeletion:ReminderDays:0"]      = "7",
        ["AccountDeletion:ReminderDays:1"]      = "1"
    };
}
