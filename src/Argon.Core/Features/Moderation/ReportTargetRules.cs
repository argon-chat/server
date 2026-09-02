namespace Argon.Features.Moderation;

using ArgonContracts;

/// <summary>
/// What a report may point at, decided before anything is looked up.
/// </summary>
/// <remarks>
/// <para>Shape only: whether the fields a kind needs are present and whether the caller is
/// reporting themself. Whether the thing exists and whether the reporter can see it is the
/// grain's business, because it needs the database, and it answers both with the same error —
/// "not found" and "not yours to see" are one answer on purpose, since telling them apart lets a
/// bare id confirm that a user or a message exists.</para>
///
/// <para><see cref="ReportTargetKind.PROFILE"/> and <see cref="ReportTargetKind.USER"/> are one
/// thing here. Both are on the wire already and clients send either; the case they open is the
/// same case.</para>
/// </remarks>
public static class ReportTargetRules
{
    public static ReportTargetKind Canonical(ReportTargetKind kind)
        => kind == ReportTargetKind.PROFILE ? ReportTargetKind.USER : kind;

    /// <summary>Whether the case's target is a person — the author for a message.</summary>
    public static bool TargetsAPerson(ReportTargetKind kind)
        => Canonical(kind) is ReportTargetKind.USER or ReportTargetKind.MESSAGE or ReportTargetKind.DIRECT_MESSAGE;

    /// <summary>Whether the case is about a piece of content that can be taken down.</summary>
    public static bool CarriesContent(ReportTargetKind kind)
        => kind is ReportTargetKind.MESSAGE or ReportTargetKind.DIRECT_MESSAGE;

    /// <summary>The error a target's shape earns, or null when it is worth looking up.</summary>
    public static SubmitReportError? Check(ReportTarget target, Guid reporterId)
    {
        if (!target.kind.IsKnown() || target.targetId == Guid.Empty)
            return SubmitReportError.INVALID_TARGET;

        if (target.targetId == reporterId)
            return SubmitReportError.CANNOT_REPORT_SELF;

        switch (Canonical(target.kind))
        {
            case ReportTargetKind.USER:
            case ReportTargetKind.SPACE:
            case ReportTargetKind.CHANNEL:
                return target.messageId is null ? null : SubmitReportError.INVALID_TARGET;

            case ReportTargetKind.MESSAGE:
                return target.channelId is null || target.channelId == Guid.Empty || target.messageId is null or 0
                    ? SubmitReportError.INVALID_TARGET
                    : null;

            case ReportTargetKind.DIRECT_MESSAGE:
                // The peer is the author; a channel id, when sent, has to be the same person.
                if (target.messageId is null or 0)
                    return SubmitReportError.INVALID_TARGET;
                if (target.channelId is { } channel && channel != target.targetId)
                    return SubmitReportError.INVALID_TARGET;
                return null;

            default:
                return SubmitReportError.INVALID_TARGET;
        }
    }

    /// <summary>
    /// The key every report about one thing shares. One open case per key.
    /// </summary>
    /// <remarks>
    /// A message is keyed by where it is, not by who wrote it, so reports filed as "the author of
    /// this message" and "this message" land on one case. A person is keyed by id; a profile
    /// report and a user report are the same case.
    /// </remarks>
    public static string GroupKey(ReportTargetKind kind, Guid targetId, Guid? channelId, Guid? conversationId, long? messageId)
        => Canonical(kind) switch
        {
            ReportTargetKind.USER           => $"user:{targetId:N}",
            ReportTargetKind.SPACE          => $"space:{targetId:N}",
            ReportTargetKind.CHANNEL        => $"channel:{targetId:N}",
            ReportTargetKind.MESSAGE        => $"message:{channelId:N}:{messageId}",
            ReportTargetKind.DIRECT_MESSAGE => $"dm:{conversationId:N}:{messageId}",
            _                               => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no group key for this target kind")
        };
}
