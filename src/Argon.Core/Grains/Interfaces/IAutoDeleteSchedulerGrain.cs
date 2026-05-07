namespace Argon.Grains.Interfaces;

/// <summary>
/// Singleton grain that periodically checks for inactive users and triggers auto-deletion.
/// Keyed by well-known GUID. Uses Orleans Reminders for cluster-safe periodic execution.
/// </summary>
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
    [Alias(nameof(RunScanAsync))]
    ValueTask RunScanAsync();
}
