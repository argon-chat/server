namespace ArgonComplexTest.Infrastructure.Account;

using Argon.Features.Logic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Every interval an account-lifecycle test waits out, read from the host's own configuration.
/// </summary>
/// <remarks>
/// <para>A deletion or export assertion is about a ratio rather than about a number of seconds:
/// "before the grace", "after the grace plus one poll", "inside the rate-limit window", "past the
/// archive's lifetime". Written as literals those ratios are invisible and unchangeable — a reader
/// would have to know that 10 was the eight-second grace plus slack — and a fixture that hard-coded
/// them would silently stop testing anything the day the host's clocks moved.</para>
///
/// <para>Read back off the running host rather than from <see cref="TestServerConfiguration"/>, for
/// the reason <c>PresenceWaits</c> gives: a setting that failed to reach configuration then shows up
/// as a fixture waiting thirty days, which is a loud failure, instead of one asserting against
/// numbers nothing is using, which is a silent one.</para>
/// </remarks>
public static class AccountTimings
{
    /// <summary>The deletion clocks the host under test is running on.</summary>
    public static AccountDeletionOptions Deletion
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IOptions<AccountDeletionOptions>>().Value;

    /// <summary>The export clocks and batch ceiling the host under test is running on.</summary>
    public static DataExportOptions Export
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IOptions<DataExportOptions>>().Value;

    /// <summary>Every e-mail the host decided to send.</summary>
    public static RecordingEmailSink Emails
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<RecordingEmailSink>();

    /// <summary>
    /// The cushion added to any wait that must be strictly longer than a product interval.
    /// </summary>
    /// <remarks>
    /// Absolute rather than proportional, exactly as in the presence harness: what it covers is a
    /// grain call, a state write and a database round trip on a loaded runner, and none of those get
    /// faster because a grace period got shorter.
    /// </remarks>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    // ── deletion ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grace between a deletion request and its execution.</summary>
    public static TimeSpan Grace => Deletion.EffectiveGracePeriod;

    /// <summary>The reminder thresholds, furthest-out first, as the host resolved them.</summary>
    public static IReadOnlyList<TimeSpan> Reminders => Deletion.EffectiveReminders;

    /// <summary>The period of the grain timer that polls a scheduled deletion.</summary>
    public static TimeSpan DeletionPoll => Deletion.CheckInterval;

    /// <summary>Long enough for the grace to have certainly elapsed.</summary>
    public static TimeSpan GraceAndABit => Grace + Slack;

    /// <summary>
    /// How long to allow a deletion that has passed its grace to actually finish.
    /// </summary>
    /// <remarks>
    /// Execution is a dozen table writes and a file-reference pass, all inside one grain call, so
    /// this is a budget for work rather than a product interval — hence the flat multiple of
    /// <see cref="Slack"/> rather than a ratio of the grace.
    /// </remarks>
    public static TimeSpan ExecutionBudget => TimeSpan.FromSeconds(30);

    /// <summary>How long the operator queue keeps a decision whose erasure has finished.</summary>
    public static TimeSpan DecisionRetention => Deletion.DecisionRetention;

    /// <summary>Long enough for a completed entry's retention window to have certainly run out.</summary>
    /// <remarks>
    /// The window is enforced inside <c>AccountDeletionQueueGrain</c>, so nothing can retire the entry
    /// early and the only wait a test needs is this one — plus a reconciliation, which is what actually
    /// does the retiring and which the test drives itself rather than waiting for a neighbour's sweep.
    /// </remarks>
    public static TimeSpan RetentionAndABit => DecisionRetention + Slack;

    /// <summary>
    /// A wait that is meaningfully short of the grace, for asserting that nothing happened yet.
    /// </summary>
    /// <remarks>
    /// Half the grace: far enough in that a poll has certainly run, far enough from the deadline that
    /// a slow runner cannot turn "not yet" into "just now".
    /// </remarks>
    public static TimeSpan WellBeforeExecution => Grace / 2;

    // ── export ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One tick of the export pump.</summary>
    public static TimeSpan ExportTick => Export.ProcessInterval;

    /// <summary>How long a completed archive stays downloadable.</summary>
    public static TimeSpan ArchiveTtl => Export.ArchiveTtl;

    /// <summary>How long after a completed export the next request is refused.</summary>
    public static TimeSpan ExportRateLimit => Export.RateLimitPeriod;

    /// <summary>How many of the caller's messages one channel contributes to the archive.</summary>
    public static int MessageBatchSize => Export.MessageBatchSize;

    /// <summary>
    /// How long to allow a whole export to run before calling it stuck.
    /// </summary>
    /// <remarks>
    /// One export is one step per tick and the collector has a fixed number of steps — profile,
    /// friends, blocks, settings, stats, devices, subscriptions, then one per conversation and one
    /// per channel, then assembly — so the budget is a generous multiple of the tick rather than a
    /// guess. Thirty ticks covers a seeded account with several conversations and channels; anything
    /// past it is a stall, not a slow runner.
    /// </remarks>
    public static TimeSpan ExportBudget => ExportTick * 30 + Slack;
}
