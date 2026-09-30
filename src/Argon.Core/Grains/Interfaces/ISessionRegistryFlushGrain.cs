namespace Argon.Grains.Interfaces;

// One activation per cluster: moves the signed-in sessions registry from its Redis transit into the
// database on a reminder, and sweeps devices nobody has heard from.
[Alias($"Argon.Grains.Interfaces.{nameof(ISessionRegistryFlushGrain)}")]
public interface ISessionRegistryFlushGrain : IGrainWithGuidKey
{
    static readonly Guid SingletonId = Guid.Parse("a0a0a0a0-dead-beef-0000-000000000003");

    [Alias(nameof(EnsureActiveAsync))]
    ValueTask EnsureActiveAsync();

    // Manual triggers, for tests. Both return how many records they handled.
    [Alias(nameof(RunFlushAsync))]
    ValueTask<int> RunFlushAsync();

    [Alias(nameof(RunSweepAsync))]
    ValueTask<int> RunSweepAsync();
}
