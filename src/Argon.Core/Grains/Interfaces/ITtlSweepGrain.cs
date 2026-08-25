namespace Argon.Grains.Interfaces;

/// <summary>
/// The one thing in the fleet that deletes expired rows on PostgreSQL, on a reminder.
/// </summary>
/// <remarks>
/// <para>A grain rather than a <c>BackgroundService</c> for two reasons that both matter here. Database
/// access in this codebase lives in grains — <c>FileGcService</c> is the exception, not the pattern, and
/// it exists because it also has to talk to S3. And a well-known key gives one activation per cluster
/// for free, where a hosted service would have run a sweep on every pod of every role that happens to
/// have a <c>DbContext</c>. The database-wide guarantee still comes from a lease
/// (<see cref="Argon.Features.EF.TtlSweeper.LockTable"/>), because "one per cluster" and "one per
/// database" stop being the same sentence as soon as two regions share a database.</para>
///
/// <para>Modelled on <see cref="IAutoDeleteSchedulerGrain"/> down to the startup call, which is the
/// established shape for periodic work here: the role declares <c>AddStartupCall</c>, hosting sees the
/// declaration and calls the grain once, the activation registers its own reminder.</para>
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(ITtlSweepGrain)}")]
public interface ITtlSweepGrain : IGrainWithGuidKey
{
    /// <summary>Well-known grain id. One instance across the cluster.</summary>
    static readonly Guid SingletonId = Guid.Parse("a0a0a0a0-dead-beef-0000-000000000002");

    /// <summary>Ensures the reminder is registered. Called once on startup.</summary>
    [Alias(nameof(EnsureSweeperActiveAsync))]
    ValueTask EnsureSweeperActiveAsync();

    /// <summary>
    /// Run one pass now, in the configured mode.
    /// </summary>
    /// <remarks>
    /// Returns nothing on purpose. What a pass found belongs on <c>/health</c> and in the metrics, where
    /// an operator can read it without holding a grain reference — and a return type would have to be an
    /// Orleans-serialisable copy of the report, which is a second definition of the same thing to keep
    /// in step with the first.
    /// </remarks>
    [Alias(nameof(RunSweepAsync))]
    ValueTask RunSweepAsync();
}
