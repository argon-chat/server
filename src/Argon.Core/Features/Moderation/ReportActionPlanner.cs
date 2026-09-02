namespace Argon.Features.Moderation;

using ArgonContracts;
using ConsoleContracts;

/// <summary>A lockdown an action asks for, before it is compared with the one already in place.</summary>
public sealed record LockdownPlan(LockdownReason Reason, TimeSpan? Duration, bool IsAppealable);

/// <summary>
/// What each <see cref="ReportActionKind"/> means in terms the rest of the server understands.
/// </summary>
/// <remarks>
/// <para>The old console recorded the action a moderator picked in the audit log and did nothing
/// with it; a "ban" was a note. Every kind now maps to one concrete effect, and the mapping is
/// here — pure, so the table can be read and tested — while applying it is the grain's job.</para>
///
/// <para>A lockdown never weakens one already in place. Resolving a spam case with a three-day
/// mute against someone serving a permanent ban must not shorten the ban, and the comparison that
/// guarantees it is <see cref="ShouldApply"/>.</para>
/// </remarks>
public static class ReportActionPlanner
{
    /// <summary>Actions that need the case's target to be a person.</summary>
    public static bool TargetsAPerson(ReportActionKind action)
        => action is ReportActionKind.WARN_USER or ReportActionKind.MUTE_USER
                  or ReportActionKind.RESTRICT_USER or ReportActionKind.BAN_USER;

    /// <summary>Actions that need the case to be about a message.</summary>
    public static bool TargetsContent(ReportActionKind action)
        => action is ReportActionKind.DELETE_CONTENT or ReportActionKind.QUARANTINE_CONTENT;

    /// <summary>
    /// An action is a statement that something was done; it only makes sense with the resolution
    /// that says so.
    /// </summary>
    public static bool IsConsistent(ReportActionKind action, ReportStatus resolution)
        => action == ReportActionKind.NONE || resolution == ReportStatus.RESOLVED_ACTION_TAKEN;

    /// <summary>The lockdown an action asks for, or null for actions that are not lockdowns.</summary>
    public static LockdownPlan? Lockdown(ReportActionOptions options, ReportActionKind action)
        => action switch
        {
            ReportActionKind.MUTE_USER     => new LockdownPlan(LockdownReason.INCITING_MOMENT, TimeSpan.FromDays(options.MuteDays), true),
            ReportActionKind.RESTRICT_USER => new LockdownPlan(LockdownReason.UNDER_INVESTIGATION, TimeSpan.FromDays(options.RestrictDays), true),
            ReportActionKind.BAN_USER      => new LockdownPlan(LockdownReason.TOS_VIOLATION,
                options.BanDays > 0 ? TimeSpan.FromDays(options.BanDays) : null, true),
            _ => null
        };

    /// <summary>
    /// The same ladder the request interceptor climbs: what each reason stops the account from doing.
    /// </summary>
    public static LockdownSeverity SeverityOf(LockdownReason reason)
        => reason switch
        {
            LockdownReason.NONE                => LockdownSeverity.Low,
            LockdownReason.UNDER_INVESTIGATION => LockdownSeverity.Middle,
            LockdownReason.INCITING_MOMENT     => LockdownSeverity.Middle,
            _                                  => LockdownSeverity.Critical
        };

    /// <summary>
    /// Whether the planned lockdown replaces the one the account already has.
    /// </summary>
    /// <remarks>
    /// Yes when there is none, or it has lapsed, or the plan is stricter. At equal severity the
    /// plan wins only if it lasts longer — permanent beats timed, later expiry beats earlier — so
    /// repeated resolutions never shorten a sentence.
    /// </remarks>
    public static bool ShouldApply(LockdownReason current, DateTimeOffset? currentExpiry, LockdownPlan plan, DateTimeOffset now)
    {
        if (current == LockdownReason.NONE || currentExpiry is { } lapsed && lapsed <= now)
            return true;

        var currentSeverity = SeverityOf(current);
        var plannedSeverity = SeverityOf(plan.Reason);

        if (plannedSeverity != currentSeverity)
            return plannedSeverity > currentSeverity;

        if (plan.Duration is null)
            return currentExpiry is not null;

        return currentExpiry is { } expiry && now + plan.Duration.Value > expiry;
    }
}
