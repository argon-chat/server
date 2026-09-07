namespace ArgonComplexTest.Infrastructure;

using Argon.Features.Logic;

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

    /// <summary>
    /// The account-lifecycle clocks the integration host runs on, and the keys that set them.
    /// </summary>
    /// <remarks>
    /// <para>Deletion and export are almost entirely a story about time: a grace that elapses, two
    /// reminders that fall due inside it, a poll that is the only thing that notices either, an
    /// archive that expires, a rate limit that opens again. Every one of those is worth a test and
    /// none can be observed faster than the clock pacing it, and the shipped clocks are a thirty-day
    /// grace with reminders a week and a day out — so at product values a single reminder test is a
    /// twenty-three-day wait and the whole campaign is unrunnable, not merely slow.</para>
    ///
    /// <para>So the host runs the same code on the same wiring with the numbers compressed, and the
    /// fixtures never write a number of their own: they read the bound options back off the host
    /// (<c>AccountTimings</c>) and express every wait as a ratio — half the grace, one poll, the
    /// grace plus a poll. Those ratios hold at both scales, which is why the same assertion passes
    /// for the same reason in eight seconds that it would have in thirty days. What keeps the choice
    /// honest is that <c>AccountDeletionOptions.Validate</c> and <c>DataExportOptions.Validate</c>
    /// run against this section at start-up like any other: a set of values that breaks the
    /// subsystem's own invariants — a poll wider than the closest reminder, an archive that expires
    /// inside a tick — fails the host rather than producing a suite that quietly tests something
    /// else.</para>
    ///
    /// <para><c>UseSetting</c> pairs rather than an in-memory collection, for the reason
    /// <see cref="RoleHost"/> spells out: a feature reading its options while the container is built
    /// does so before <c>WebApplicationFactory</c> applies its configuration callbacks. Durations are
    /// written <c>hh:mm:ss</c> because the binder reads a bare number as a count of days.</para>
    ///
    /// <para>The day-shaped names (<c>GracePeriodDays</c>, <c>ReminderDays</c>) are deliberately
    /// <em>not</em> set here even though the host used to set them to the shipped values: they take
    /// precedence over the duration forms by design, so setting one would silently pin the host back
    /// to thirty days.</para>
    /// </remarks>
    public static IEnumerable<(string Setting, string Value)> AccountDeletion
    {
        get
        {
            const string section = AccountDeletionOptions.SectionName;

            // Eight seconds of grace with reminders at six and three. Two reminders rather than one
            // because "exactly one mail per threshold, in order, never twice" is the assertion, and a
            // single threshold cannot show ordering. Three seconds apart so a poll can land between
            // them; the grace clears the second by five, which is the room a test needs to observe
            // the reminder before the account it belongs to is gone.
            yield return ($"{section}:{nameof(AccountDeletionOptions.GracePeriod)}", "00:00:08");
            yield return ($"{section}:{nameof(AccountDeletionOptions.ReminderBefore)}:0", "00:00:06");
            yield return ($"{section}:{nameof(AccountDeletionOptions.ReminderBefore)}:1", "00:00:03");

            // The poll. Under the closest reminder, as Validate requires — the fixtures call
            // CheckAndExecuteAsync by hand, but the grain's own timer runs regardless and a period
            // that could step over a threshold would make "sent exactly once" depend on which of the
            // two callers got there first.
            yield return ($"{section}:{nameof(AccountDeletionOptions.CheckInterval)}", "00:00:02");

            // How long the operator queue keeps a decision whose erasure has run — a week in
            // production, thirty seconds here. It is the one value in this block that is sized
            // against the suite rather than against the product's own arithmetic, because it is the
            // only one another fixture can consume: the queue is one cluster-wide singleton and every
            // fixture that drives AutoDeleteSchedulerGrain reconciles it, so a completed entry's
            // window is being spent by the neighbours as well as by the test reading it. Thirty
            // seconds is two orders of magnitude above the gap it has to cover (an erasure finishing,
            // then a console page being read — under a second unloaded, a few seconds on a runner
            // hosting four shards and a container stack) and still short enough that a test can wait
            // the far side of it out, which one does.
            yield return ($"{section}:{nameof(AccountDeletionOptions.DecisionRetention)}", "00:00:30");

            // The platform's inactivity threshold, and the one value here that is not compressed: the
            // fixtures backdate a login by four hundred days to make an account dormant, and a shorter
            // threshold would make every account another fixture created a candidate as well. Set
            // explicitly all the same, because it is configuration now rather than a constant, and a
            // suite that never states it would not notice the shipped default changing under it.
            yield return ($"{section}:{nameof(AccountDeletionOptions.DefaultInactivityMonths)}", "12");
        }
    }

    /// <summary>The GDPR export's clocks and batch ceiling, compressed on the same argument.</summary>
    /// <remarks>
    /// <para>The tick is a second, so an export that takes a dozen steps finishes inside a dozen
    /// seconds instead of six minutes. The rate limit is twenty seconds rather than thirty days, so
    /// the fixture that asserts a second request is refused <em>and then accepted</em> can wait the
    /// window out. The archive lives forty seconds — longer than the rate limit on purpose, so an
    /// expiry test and a rate-limit test do not have to be the same test.</para>
    ///
    /// <para>The batch size is the one value here that is not a clock, and it is compressed for the
    /// same reason: at two hundred, "a channel with more messages than one batch exports the first
    /// batch and moves on" needs two hundred and one seeded messages per case. At five it needs six,
    /// and it is the same branch.</para>
    /// </remarks>
    public static IEnumerable<(string Setting, string Value)> DataExport
    {
        get
        {
            const string section = DataExportOptions.SectionName;

            yield return ($"{section}:{nameof(DataExportOptions.ProcessInterval)}", "00:00:01");
            yield return ($"{section}:{nameof(DataExportOptions.FirstTickDelay)}", "00:00:01");
            yield return ($"{section}:{nameof(DataExportOptions.RateLimitPeriod)}", "00:00:20");
            yield return ($"{section}:{nameof(DataExportOptions.ArchiveTtl)}", "00:00:40");
            yield return ($"{section}:{nameof(DataExportOptions.MessageBatchSize)}", "5");
        }
    }
}
