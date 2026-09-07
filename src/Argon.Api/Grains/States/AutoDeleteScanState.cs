namespace Argon.Grains.Persistence.States;

/// <summary>
/// What the inactivity scan did last, kept because nothing else records it.
/// </summary>
/// <remarks>
/// The scan is a reminder and a query: it writes to the queue and to the log, and both answer "what
/// was proposed" rather than "did a pass happen, and when is the next one". An operator looking at an
/// empty queue cannot tell the difference between nothing to propose and nothing running — which is
/// exactly the state production was in for a day — so the pass records itself here and the admin
/// console reads it.
/// </remarks>
[DataContract, Serializable, GenerateSerializer]
public sealed partial record AutoDeleteScanState
{
    /// <summary>When the recurring pass was registered. Written once, by the activation that arms it.</summary>
    [DataMember(Order = 0), Id(0)]
    public DateTimeOffset? ArmedAt { get; set; }

    /// <summary>
    /// When the next timed pass is due.
    /// </summary>
    /// <remarks>
    /// Kept here rather than read back from the reminder because Orleans' <c>IGrainReminder</c> carries
    /// a name and nothing else. Written when the pass is armed and again by each tick; a pass an
    /// operator forced deliberately leaves it alone, since forcing one does not reschedule the timer.
    /// </remarks>
    [DataMember(Order = 1), Id(1)]
    public DateTimeOffset? NextDueAt { get; set; }

    [DataMember(Order = 2), Id(2)]
    public DateTimeOffset? LastStartedAt { get; set; }

    [DataMember(Order = 3), Id(3)]
    public DateTimeOffset? LastFinishedAt { get; set; }

    /// <summary><c>reminder</c> or <c>operator</c>.</summary>
    [DataMember(Order = 4), Id(4)]
    public string? LastTrigger { get; set; }

    [DataMember(Order = 5), Id(5)]
    public int Runs { get; set; }

    [DataMember(Order = 6), Id(6)]
    public int LastProcessed { get; set; }

    [DataMember(Order = 7), Id(7)]
    public int LastProposed { get; set; }

    [DataMember(Order = 8), Id(8)]
    public int LastEnqueued { get; set; }

    [DataMember(Order = 9), Id(9)]
    public int LastRetired { get; set; }

    [DataMember(Order = 10), Id(10)]
    public int LastHeld { get; set; }

    [DataMember(Order = 11), Id(11)]
    public int LastQueueLength { get; set; }

    /// <summary>Why the last pass stopped, kept until a pass finishes without stopping.</summary>
    [DataMember(Order = 12), Id(12)]
    public string? LastError { get; set; }

    [DataMember(Order = 13), Id(13)]
    public DateTimeOffset? LastErrorAt { get; set; }
}
