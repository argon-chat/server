namespace Argon.Grains.Persistence.States;

/// <summary>
/// Redis-backed state for user level progression.
/// </summary>
[DataContract, Serializable, GenerateSerializer]
public sealed partial record UserLevelGrainState
{
    /// <summary>
    /// Total XP accumulated all time.
    /// </summary>
    [DataMember(Order = 0), Id(0)]
    public long TotalXpAllTime { get; set; }

    /// <summary>
    /// Current XP in the current level cycle.
    /// </summary>
    [DataMember(Order = 1), Id(1)]
    public int CurrentCycleXp { get; set; }

    /// <summary>
    /// Current level (1-100).
    /// </summary>
    [DataMember(Order = 2), Id(2)]
    public int CurrentLevel { get; set; } = 1;

    /// <summary>
    /// Whether user can claim a medal (reached level 100).
    /// </summary>
    [DataMember(Order = 3), Id(3)]
    public bool CanClaimMedal { get; set; }

    /// <summary>
    /// Last time XP was awarded.
    /// </summary>
    [DataMember(Order = 4), Id(4)]
    public DateTimeOffset LastXpAward { get; set; }

    /// <summary>
    /// Whether state has been loaded from database.
    /// </summary>
    [DataMember(Order = 5), Id(5)]
    public bool IsInitialized { get; set; }

    /// <summary>
    /// Whether state has been modified since last save.
    /// </summary>
    [DataMember(Order = 6), Id(6)]
    public bool IsDirty { get; set; }

    /// <summary>
    /// Last time state was persisted to database.
    /// </summary>
    [DataMember(Order = 7), Id(7)]
    public DateTimeOffset LastPersist { get; set; }
}
