namespace Argon.Grains.Persistence.States;

/// <summary>
/// Redis-backed state for daily user statistics.
/// Accumulated in memory/Redis and periodically flushed to database.
/// </summary>
[DataContract, Serializable, GenerateSerializer]
public sealed partial record UserStatsGrainState
{
    /// <summary>
    /// Date for which these stats are being tracked.
    /// </summary>
    [DataMember(Order = 0), Id(0)]
    public DateOnly CurrentDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// Time spent in voice channels in seconds.
    /// </summary>
    [DataMember(Order = 1), Id(1)]
    public int TimeInVoiceSeconds { get; set; }

    /// <summary>
    /// Number of voice calls joined.
    /// </summary>
    [DataMember(Order = 2), Id(2)]
    public int CallsMade { get; set; }

    /// <summary>
    /// Number of messages sent.
    /// </summary>
    [DataMember(Order = 3), Id(3)]
    public int MessagesSent { get; set; }

    /// <summary>
    /// XP earned today (accumulated before flush).
    /// </summary>
    [DataMember(Order = 4), Id(4)]
    public int XpEarnedToday { get; set; }

    /// <summary>
    /// XP earned from messages today (for tracking the 50 XP/day cap).
    /// </summary>
    [DataMember(Order = 7), Id(7)]
    public int MessageXpEarnedToday { get; set; }

    /// <summary>
    /// Last time stats were flushed to database.
    /// </summary>
    [DataMember(Order = 5), Id(5)]
    public DateTimeOffset LastFlush { get; set; }

    /// <summary>
    /// Whether state has been modified since last flush.
    /// </summary>
    [DataMember(Order = 6), Id(6)]
    public bool IsDirty { get; set; }
}
