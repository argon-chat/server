namespace Argon.Features.Moderation;

using ArgonContracts;

/// <summary>
/// The states a case (and every report on it) moves through, and which moves are allowed.
/// </summary>
/// <remarks>
/// <para><c>PENDING</c> and <c>ESCALATED</c> are both "open, nobody has picked it up";
/// <c>ESCALATED</c> is the same state at the top of the queue. <c>UNDER_REVIEW</c> is open and
/// assigned. The three resolutions are terminal, with one way back: an operator reopening a case,
/// which returns it to whichever open state its escalation flag says.</para>
///
/// <para>A resolution does not overwrite a resolution. The old console let an operator set any
/// status on any report at any time, which meant a second click silently rewrote the outcome the
/// trust scores had already been computed from.</para>
/// </remarks>
public static class ReportCaseTransitions
{
    public static bool IsOpen(ReportStatus status)
        => status is ReportStatus.PENDING or ReportStatus.UNDER_REVIEW or ReportStatus.ESCALATED;

    public static bool IsResolution(ReportStatus status)
        => status is ReportStatus.RESOLVED_ACTION_TAKEN or ReportStatus.RESOLVED_NO_ACTION or ReportStatus.DISMISSED;

    public static bool CanAssign(ReportStatus current)
        => IsOpen(current);

    public static bool CanResolve(ReportStatus current, ReportStatus resolution)
        => IsOpen(current) && IsResolution(resolution);

    public static bool CanReopen(ReportStatus current)
        => IsResolution(current);

    /// <summary>The open state a case sits in when nobody holds it.</summary>
    public static ReportStatus OpenStateFor(bool isEscalated)
        => isEscalated ? ReportStatus.ESCALATED : ReportStatus.PENDING;
}
