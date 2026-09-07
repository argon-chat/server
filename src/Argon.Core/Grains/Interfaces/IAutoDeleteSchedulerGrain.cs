namespace Argon.Grains.Interfaces;

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
}
