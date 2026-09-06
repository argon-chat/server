namespace Argon.Features.Logic;

using Argon.Features.Clustering;

/// <summary>
/// Every clock scheduled account deletion runs on, in one place.
/// </summary>
/// <remarks>
/// <para>Deletion is a countdown with two reminders hanging off it, and the three numbers are a
/// system rather than three independent settings: a reminder further out than the grace itself can
/// never fire, and a check interval longer than the closest reminder steps straight over it — the
/// mail is never sent and nothing anywhere says so, because the only evidence would have been an
/// e-mail that did not arrive. Written down as a <c>static readonly TimeSpan</c> in the grain and two
/// integers here, those relationships were invisible and unenforced; written down together
/// <see cref="Validate"/> is what enforces them.</para>
///
/// <para><b>Days are still the configuration names.</b> A deployment that already sets
/// <c>GracePeriodDays</c> and <c>ReminderDays</c> keeps working and keeps winning: those are what an
/// operator reasons in and what the shipped documentation calls them, and re-spelling a 30-day grace
/// as <c>30.00:00:00</c> to satisfy a refactor would be a breaking change dressed as a cleanup. The
/// <see cref="TimeSpan"/> forms exist for the integration suite, which asserts what happens on the
/// far side of the grace and of each reminder; at production values a single such test would cost a
/// month of wall clock, and at eight seconds it costs eight seconds and asserts exactly the same
/// thing, because every wait in it is written as a ratio of these values rather than as a date.</para>
///
/// <para><b>Precedence, in one sentence:</b> whichever of the two spellings is <em>set</em> wins, and
/// when both are set the day-shaped one does — <see cref="GracePeriodDays"/> over
/// <see cref="GracePeriod"/>, <see cref="ReminderDays"/> over <see cref="ReminderBefore"/> — so a
/// deployment cannot have its familiar setting silently overridden by a default it never wrote.
/// Nothing reads the raw properties: <see cref="EffectiveGracePeriod"/> and
/// <see cref="EffectiveReminders"/> are the resolved values, and they are what the grain, the
/// console and the tests all use.</para>
/// </remarks>
public sealed class AccountDeletionOptions : IValidatableFeatureOptions
{
    public const string SectionName = "AccountDeletion";

    /// <summary>What ships when neither spelling of the grace is configured.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromDays(30);

    /// <summary>What ships when neither spelling of the reminders is configured.</summary>
    public static readonly TimeSpan[] DefaultReminders = [TimeSpan.FromDays(7), TimeSpan.FromDays(1)];

    /// <summary>Six hours, the period of the grain timer that polls a scheduled deletion.</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    ///     Whether automatic deletion of inactive accounts is enabled.
    /// </summary>
    public bool AutoDeleteEnabled { get; set; }

    /// <summary>
    /// The grace period in whole days — the name a deployment already sets.
    /// </summary>
    /// <remarks>
    /// Nullable, and that is the whole mechanism: a default of 30 here would be indistinguishable
    /// from an operator writing 30, so the day form could never yield to
    /// <see cref="GracePeriod"/> and no host could run on a sub-day grace. Unset means "I did not
    /// choose", which is the only thing that lets the two spellings coexist. The shipped default
    /// lives in <see cref="DefaultGracePeriod"/> instead.
    /// </remarks>
    public int? GracePeriodDays { get; set; }

    /// <summary>
    /// The grace period as a duration, for a host that needs one shorter than a day.
    /// </summary>
    /// <remarks>
    /// Yields to <see cref="GracePeriodDays"/> when that is set. Also unset-by-default, for the same
    /// reason: a value here has to be distinguishable from a value nobody wrote.
    /// </remarks>
    public TimeSpan? GracePeriod { get; set; }

    /// <summary>
    ///     Days before execution at which to send reminder emails.
    ///     Sorted descending (e.g., [7, 1] means reminders at 7 days and 1 day before).
    /// </summary>
    /// <remarks>
    /// Empty rather than <c>[7, 1]</c> by default, and not for symmetry with the grace: the
    /// configuration binder <em>appends</em> to an array it finds populated rather than replacing it,
    /// so a shipped default of two entries would turn <c>ReminderDays:0</c> and <c>:1</c> in a
    /// deployment's file into a four-element array with the defaults still in front of it. The
    /// shipped pair lives in <see cref="DefaultReminders"/>, where the binder cannot reach it.
    /// </remarks>
    public int[] ReminderDays { get; set; } = [];

    /// <summary>
    /// The same reminders as durations, for a host whose grace is shorter than a day.
    /// </summary>
    /// <remarks>Yields to <see cref="ReminderDays"/> when that is non-empty; empty by default for
    /// the binder reason above.</remarks>
    public TimeSpan[] ReminderBefore { get; set; } = [];

    /// <summary>
    /// How often a scheduled account is re-examined: reminders due are sent and an elapsed grace is
    /// executed.
    /// </summary>
    /// <remarks>
    /// The resolution of the whole countdown, and therefore the reason
    /// <see cref="Validate"/> insists it fits inside the closest reminder — a poll slower than the
    /// last reminder's window steps from "not due yet" to "already deleted" without ever passing
    /// through "due", and the mail that was the entire point of a grace period is never sent.
    /// </remarks>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The grace period actually in force, after the precedence rule.</summary>
    public TimeSpan EffectiveGracePeriod
        => GracePeriodDays is { } days ? TimeSpan.FromDays(days)
         : GracePeriod ?? DefaultGracePeriod;

    /// <summary>
    /// The reminder thresholds actually in force, after the precedence rule, furthest-out first.
    /// </summary>
    public IReadOnlyList<TimeSpan> EffectiveReminders
        => ReminderDays.Length > 0 ? [.. ReminderDays.Select(days => TimeSpan.FromDays((double)days))]
         : ReminderBefore.Length > 0 ? ReminderBefore
         : DefaultReminders;

    /// <summary>
    /// The stable integer a sent reminder is remembered by in
    /// <c>AccountDeletionGrainState.RemindersSent</c>.
    /// </summary>
    /// <remarks>
    /// A whole number of days maps to itself, which is what keeps state written before these options
    /// existed — and state written by a production host, where every threshold is a whole number of
    /// days — readable unchanged: a 7-day reminder is still remembered as <c>7</c>. Anything finer
    /// maps to negative seconds, which cannot collide with a day count and gives a compressed host
    /// two distinguishable keys for two thresholds that would both floor to zero days.
    /// </remarks>
    public static int ReminderKey(TimeSpan before)
        => before.Ticks % TimeSpan.TicksPerDay == 0 && before >= TimeSpan.FromDays(1)
            ? (int)before.TotalDays
            : -(int)Math.Round(before.TotalSeconds);

    /// <summary>
    /// The relationships between the values, which is the only reason they are one options class.
    /// </summary>
    /// <remarks>
    /// Each rule below is a way a deployment could look plausible and silently stop sending the
    /// warnings a grace period exists to give — or delete accounts on a schedule nobody intended —
    /// so all of them are errors rather than warnings. There is no upper bound on anything: a long
    /// grace is a product decision, a reminder that lands after the account is gone is not.
    /// </remarks>
    public void Validate(IFeatureConfigurationReport report)
    {
        var grace     = EffectiveGracePeriod;
        var reminders = EffectiveReminders;

        report.Require(grace > TimeSpan.Zero, nameof(GracePeriod),
            $"is {grace}; the grace period is the window in which a person can change their mind, " +
            "and zero or negative deletes the account on the first poll after the request");

        report.Require(CheckInterval > TimeSpan.Zero, nameof(CheckInterval),
            $"is {CheckInterval}; a timer period of zero or less is one Orleans will not register, " +
            "so nothing would ever poll a scheduled deletion");

        foreach (var before in reminders)
            report.Require(before > TimeSpan.Zero, nameof(ReminderDays),
                $"contains {before}; a reminder is a duration before execution, and zero or negative " +
                "is a reminder that is either due the moment it is scheduled or never due at all");

        // A threshold at or beyond the grace is already elapsed when the deletion is scheduled: the
        // first poll sends it immediately, which is not a warning, it is a duplicate of the
        // confirmation the request already sent.
        var beyond = reminders.Where(before => before >= grace).ToArray();

        report.Require(beyond.Length == 0, nameof(ReminderDays),
            $"contains {string.Join(", ", beyond)}, at or beyond the {grace} grace period; such a " +
            "reminder is already elapsed when the deletion is scheduled, so it is sent on the first " +
            "poll after the request rather than as a warning that time is running out");

        report.Require(reminders.Distinct().Count() == reminders.Count, nameof(ReminderDays),
            $"repeats a threshold ({string.Join(", ", reminders)}); a repeat is remembered as sent " +
            "the first time and silently does nothing after that, so it reads as a reminder that " +
            "goes missing");

        // The poll is the only thing that ever sends a reminder, so a poll wider than the closest
        // threshold's window can step from "not due" straight past "due" to "execute".
        if (reminders.Count > 0)
        {
            var closest = reminders.Min();

            report.Require(CheckInterval < closest, nameof(CheckInterval),
                $"is {CheckInterval}, at or above the closest reminder threshold of {closest}; the " +
                "poll is the only thing that sends a reminder, so one this wide can pass over the " +
                "threshold entirely and the last warning before deletion is never sent");
        }

        report.Require(CheckInterval < grace, nameof(CheckInterval),
            $"is {CheckInterval}, at or above the {grace} grace period; the first poll after the " +
            "request would already be past the execution time, so the grace would not exist");
    }
}
