namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

// Grains used to exercise the analyser. They are deliberately shaped around the call patterns that
// are hard to see from IL: async state machines, closures, interface dispatch and open generics.

public interface IAlphaGrain : IGrainWithGuidKey
{
    Task<int> Ping();
}

public interface IBetaGrain : IGrainWithGuidKey
{
    Task<int> Ping();
}

public interface IGammaGrain : IGrainWithGuidKey
{
    Task Ping();
}

public interface IDeltaGrain : IGrainWithGuidKey
{
    Task Ping();
}

/// <summary>Implemented but left out of every role, to exercise E2.</summary>
public interface IOrphanGrain : IGrainWithGuidKey
{
    Task Ping();
}

/// <summary>Declared and called, never implemented — the dead-interface case E1 must catch.</summary>
public interface IGhostGrain : IGrainWithGuidKey
{
    Task Ping();
}

/// <summary>
/// Reaches Beta from four distinct IL call sites — an async body, a closure, a LINQ projection and
/// a plain helper method. Four is exactly the default W2 threshold.
/// </summary>
/// <remarks>
/// Note that weights count <i>call sites</i>, not runtime invocations: the LINQ projection below
/// runs twice but is one site.
/// </remarks>
public class AlphaGrain : Grain, IAlphaGrain
{
    public async Task<int> Ping()
    {
        await GrainFactory.GetGrain<IBetaGrain>(Guid.Empty).Ping();          // site 1: async body

        var run = async () => await GrainFactory.GetGrain<IBetaGrain>(Guid.NewGuid()).Ping();
        await run();                                                          // site 2: closure

        await Task.WhenAll(Enumerable.Range(0, 2)
           .Select(_ => GrainFactory.GetGrain<IBetaGrain>(Guid.NewGuid()).Ping()));   // site 3: lambda

        await Warm();                                                         // site 4: plain method
        return 1;
    }

    private Task<int> Warm()
        => GrainFactory.GetGrain<IBetaGrain>(Guid.NewGuid()).Ping();
}

/// <summary>Calls Gamma. Nothing that roots at <see cref="AlphaGrain"/> may attribute this edge.</summary>
[StatelessWorker]
public class BetaGrain : Grain, IBetaGrain
{
    public async Task<int> Ping()
    {
        await GrainFactory.GetGrain<IGammaGrain>(Guid.Empty).Ping();
        return 2;
    }
}

public class GammaGrain : Grain, IGammaGrain, IRemindable
{
    public Task Ping()
        => Task.CompletedTask;

    public Task ReceiveReminder(string reminderName, TickStatus status)
        => Task.CompletedTask;
}

/// <summary>Reaches Ghost, which nothing implements.</summary>
public class DeltaGrain : Grain, IDeltaGrain
{
    public Task Ping()
        => GrainFactory.GetGrain<IGhostGrain>(Guid.Empty).Ping();
}

public class OrphanGrain : Grain, IOrphanGrain
{
    public Task Ping()
        => Task.CompletedTask;
}

public interface IStatefulGrain : IGrainWithGuidKey
{
    Task Ping();
}

public class StatefulGrain : Grain, IStatefulGrain
{
    public StatefulGrain([PersistentState("stateful-store", "unknown-provider")] IPersistentState<int> state)
        => State = state;

    private IPersistentState<int> State { get; }

    public Task Ping()
        => Task.CompletedTask;
}

// ── indirection the scanner has to see through ──────────────────────────────────────────────

public interface ICallHelper
{
    Task Reach(IGrainFactory factory);
}

public class CallHelper : ICallHelper
{
    public Task Reach(IGrainFactory factory)
        => factory.GetGrain<IGammaGrain>(Guid.Empty).Ping();
}

/// <summary>Root that only ever reaches Gamma through an interface-typed field.</summary>
public class IndirectService(ICallHelper helper)
{
    public Task Run(IGrainFactory factory)
        => helper.Reach(factory);
}

/// <summary>Root whose grain call is hidden behind an open generic — E7 territory.</summary>
public class OpenGenericService
{
    public Task Run<TGrain>(IGrainFactory factory) where TGrain : IGrainWithGuidKey, IGhostGrain
        => factory.GetGrain<TGrain>(Guid.Empty).Ping();

    public void Call(IGrainFactory factory)
        => _ = Run<IGhostGrain>(factory);
}

/// <summary>Root that resolves a grain by <see cref="Type"/> — the other E7 shape.</summary>
public class DynamicService
{
    public object Run(IGrainFactory factory)
        => factory.GetGrain(typeof(IGammaGrain), Guid.Empty);
}
