namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;

/// <summary>
/// The scanner is the part of the design that can silently be wrong: a missed call site produces a
/// graph that validates clean while production breaks. These tests pin the call shapes that IL
/// makes hard to see.
/// </summary>
[TestFixture]
public class GrainGraphScannerTests
{
    private static (GrainTypeIndex Index, IlGrainGraphScanner Scanner) Build(params Type[] fixtures)
    {
        var scope = Scenario.ScannerScope(fixtures);
        var index = GrainTypeIndex.Build(scope);
        return (index, new IlGrainGraphScanner(scope, index));
    }

    [Test]
    public void Finds_grain_calls_through_async_bodies_lambdas_and_linq()
    {
        var (_, scanner) = Build(typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain));

        var graph = scanner.Analyze([typeof(AlphaGrain)]);

        Assert.That(graph.From(typeof(AlphaGrain)).Interfaces, Does.Contain(typeof(IBetaGrain)),
            "AlphaGrain's calls live in an async state machine, a closure and a LINQ projection — " +
            "none of them visible on the declaring method's own body");
    }

    [Test]
    public void Counts_every_call_site_behind_an_edge()
    {
        var (_, scanner) = Build(typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain));

        var weight = scanner.Analyze([typeof(AlphaGrain)]).From(typeof(AlphaGrain)).WeightOf(typeof(IBetaGrain));

        Assert.That(weight, Is.EqualTo(4), "four distinct GetGrain<IBetaGrain> sites; W2 depends on the count");
    }

    [Test]
    public void Stops_at_the_grain_boundary()
    {
        var (_, scanner) = Build(typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain));

        var reached = scanner.Analyze([typeof(AlphaGrain)]).From(typeof(AlphaGrain)).Interfaces;

        Assert.That(reached, Does.Not.Contain(typeof(IGammaGrain)),
            "BetaGrain calls Gamma, but Beta is a cluster boundary — that edge belongs to Beta's role");
    }

    [Test]
    public void The_boundary_holds_even_when_the_callee_class_is_unknown()
    {
        // BetaGrain is outside the scope, so the grain index cannot recognise it as a boundary.
        // The walk must still stop, because a call through a grain *interface* is never expanded
        // into its implementation — that is the boundary Orleans actually enforces at run time.
        var (index, scanner) = Build(typeof(AlphaGrain), typeof(GammaGrain));

        var reached = scanner.Analyze([typeof(AlphaGrain)]).From(typeof(AlphaGrain)).Interfaces;

        Assert.That(index.Classes.Keys, Does.Not.Contain(typeof(BetaGrain)), "premise: Beta is out of scope");
        Assert.That(reached, Is.EquivalentTo(new[] { typeof(IBetaGrain) }),
            "the stop comes from the interface dispatch rule, not merely from the grain index");
    }

    [Test]
    public void Attributes_its_own_calls_to_a_grain_root()
    {
        var (_, scanner) = Build(typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain));

        var reached = scanner.Analyze([typeof(BetaGrain)]).From(typeof(BetaGrain)).Interfaces;

        Assert.That(reached, Does.Contain(typeof(IGammaGrain)),
            "a grain root must still be walked, even though other roots stop at it");
    }

    [Test]
    public void Follows_interface_dispatch_into_the_implementation()
    {
        var (_, scanner) = Build(
            typeof(GammaGrain), typeof(IndirectService), typeof(ICallHelper), typeof(CallHelper));

        var reached = scanner.Analyze([typeof(IndirectService)]).From(typeof(IndirectService)).Interfaces;

        Assert.That(reached, Does.Contain(typeof(IGammaGrain)),
            "IndirectService only ever calls ICallHelper.Reach; without dispatch expansion the walk " +
            "would stop at the interface declaration, which has no body");
    }

    [Test]
    public void Reports_a_grain_resolved_by_Type_as_unresolved()
    {
        var (_, scanner) = Build(typeof(GammaGrain), typeof(DynamicService));

        var unresolved = scanner.Analyze([typeof(DynamicService)]).Unresolved;

        Assert.That(unresolved, Is.Not.Empty);
        Assert.That(unresolved.Select(u => u.DeclaringType), Does.Contain(typeof(DynamicService)));
    }

    [Test]
    public void Reports_an_open_generic_grain_call_as_unresolved()
    {
        var (_, scanner) = Build(typeof(OpenGenericService));

        var unresolved = scanner.Analyze([typeof(OpenGenericService)]).Unresolved;

        Assert.That(unresolved.Select(u => u.Reason),
            Has.Some.Contains("open generic parameter"),
            "GetGrain<TGrain> inside a generic helper resolves to a type parameter, not a grain");
    }

    [Test]
    public void Reads_grain_placement_and_persistence_metadata()
    {
        var (index, _) = Build(
            typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain), typeof(StatefulGrain));

        Assert.Multiple(() =>
        {
            Assert.That(index.Info(typeof(BetaGrain))!.IsStatelessWorker, Is.True);
            Assert.That(index.Info(typeof(AlphaGrain))!.IsStatelessWorker, Is.False);
            Assert.That(index.Info(typeof(GammaGrain))!.IsRemindable, Is.True);
            Assert.That(index.Info(typeof(AlphaGrain))!.IsRemindable, Is.False);
            Assert.That(index.Info(typeof(StatefulGrain))!.StorageProviders.Select(p => p.StorageName),
                Does.Contain("unknown-provider"));
            Assert.That(index.Implementations(typeof(IGhostGrain)), Is.Empty,
                "nothing implements IGhostGrain — this is what makes E1 able to name a dead interface");
        });
    }
}
