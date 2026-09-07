namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// Singleton grain that periodically looks for dormant accounts and proposes them for deletion.
/// Keyed by well-known GUID. Uses Orleans Reminders for cluster-safe periodic execution.
/// </summary>
/// <remarks>
/// It proposes and nothing more: every account the scan selects is written into
/// <see cref="IAccountDeletionQueueGrain"/> for an operator to approve or reject from the admin console.
/// Erasing an account on a timer, with nobody in the loop, is what this grain used to do and what three
/// campaign defects came out of (CON-2, CON-3, CON-4); the scan's arithmetic is unchanged, its authority is
/// gone.
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(IAutoDeleteSchedulerGrain)}")]
public interface IAutoDeleteSchedulerGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Well-known grain ID. Only one instance exists across the entire cluster.
    /// </summary>
    static readonly Guid SingletonId = Guid.Parse("a0a0a0a0-dead-beef-0000-000000000001");

    /// <summary>
    /// Ensures the reminder is registered. Called once on startup.
    /// </summary>
    [Alias(nameof(EnsureSchedulerActiveAsync))]
    ValueTask EnsureSchedulerActiveAsync();

    /// <summary>
    /// Force an immediate scan (e.g. for admin/debug purposes).
    /// </summary>
    /// <remarks>
    /// Unlike the reminder, this runs whatever <c>AccountDeletionOptions.AutoDeleteEnabled</c> says: the
    /// switch decides whether the platform sweeps on its own, not whether a person may ask it to look now.
    /// A scan only ever writes to the queue, so asking is safe.
    /// </remarks>
    [Alias(nameof(RunScanAsync))]
    ValueTask RunScanAsync();

    /// <summary>What the last pass did, and when the next one is due.</summary>
    /// <remarks>
    /// The scan leaves nothing behind but log lines and the queue it reconciles, and neither answers
    /// "did it run at all?" — the question every operator looking at an empty queue actually has, and
    /// the one that took a production morning to answer by hand.
    /// </remarks>
    [Alias(nameof(GetScanStatusAsync)), AlwaysInterleave]
    ValueTask<AutoDeleteScanReport> GetScanStatusAsync();
}

/// <summary>The inactivity scan, as the admin console shows it.</summary>
[GenerateSerializer, Immutable]
public sealed record AutoDeleteScanReport
{
    /// <summary>Whether the reminder's own passes do anything, i.e. <c>AccountDeletion:AutoDeleteEnabled</c>.</summary>
    /// <remarks>An operator can still force a pass while this is false; the switch governs the timer, not the button.</remarks>
    [Id(0)] public required bool Enabled { get; init; }

    /// <summary>When the recurring pass was first registered, or null if it never has been.</summary>
    [Id(1)] public DateTimeOffset? ArmedAt { get; init; }

    /// <summary>When the next timed pass is due. Null before the first one is armed; a forced pass does not move it.</summary>
    [Id(2)] public DateTimeOffset? NextDueAt { get; init; }

    [Id(3)] public DateTimeOffset? LastStartedAt { get; init; }
    [Id(4)] public DateTimeOffset? LastFinishedAt { get; init; }

    /// <summary><c>reminder</c> or <c>operator</c> — what asked for the last pass.</summary>
    [Id(5)] public string? LastTrigger { get; init; }

    /// <summary>How many passes have started since the state was first written.</summary>
    [Id(6)] public int Runs { get; init; }

    /// <summary>Accounts the last pass read.</summary>
    [Id(7)] public int LastProcessed { get; init; }

    /// <summary>Accounts it decided to propose, after every filter and the queue's ceiling.</summary>
    [Id(8)] public int LastProposed { get; init; }

    /// <summary>Of those, the ones the queue had not already listed.</summary>
    [Id(9)] public int LastEnqueued { get; init; }

    /// <summary>Entries the queue dropped because the pass no longer proposes them.</summary>
    [Id(10)] public int LastRetired { get; init; }

    /// <summary>Proposals the queue refused because an operator's rejection still stands.</summary>
    [Id(11)] public int LastHeld { get; init; }

    /// <summary>How long the queue was when the pass finished.</summary>
    [Id(12)] public int LastQueueLength { get; init; }

    /// <summary>Why the last pass stopped, or null if it did not.</summary>
    [Id(13)] public string? LastError { get; init; }

    [Id(14)] public DateTimeOffset? LastErrorAt { get; init; }

    /// <summary>The platform's inactivity threshold for accounts that never chose one.</summary>
    /// <remarks>
    /// The single number that decides how much of the user table the sweep can see, and the answer to
    /// "why is the queue so short" nearly every time it is asked. Shown next to the counts so the
    /// question answers itself.
    /// </remarks>
    [Id(15)] public required int DefaultThresholdMonths { get; init; }
}
