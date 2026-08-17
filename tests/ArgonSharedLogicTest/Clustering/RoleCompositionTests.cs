namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

[TestFixture]
public class RoleCompositionTests
{
    [SetUp]
    public void Reset()
        => Scenario.Configured.Clear();

    // ── Include<> ───────────────────────────────────────────────────────────────────────────

    [Test]
    public void Include_flattens_grains_features_and_records_the_provenance()
    {
        var scope   = Scenario.Scope(typeof(Scenario.Composed), typeof(BetaGrain), typeof(GammaGrain));
        var catalog = ArgonClusterCatalog.Build(scope);

        var trunk = catalog.Require(new ArgonRoleId("trunk"));

        Assert.Multiple(() =>
        {
            Assert.That(trunk.HostedGrains, Is.EquivalentTo(new[] { typeof(BetaGrain), typeof(GammaGrain) }));
            Assert.That(trunk.IncludedRoles, Does.Contain(typeof(Scenario.Composed.LeafRole)));
            Assert.That(trunk.Features.Ordered.Select(f => f.Name), Does.Contain("storage"),
                "the included role's feature comes along");
            Assert.That(trunk.UsesReminders, Is.True,
                "flags come from the including role only, not from what it includes");
        });
    }

    [Test]
    public void An_include_cycle_is_reported_rather_than_hanging()
    {
        var catalog = ArgonClusterCatalog.Build(Scenario.Scope(typeof(Scenario.CyclicRoles)));

        var left = catalog.Require(new ArgonRoleId("left"));

        Assert.That(left.Diagnostics.Select(d => d.Code), Does.Contain("E9"));
        Assert.That(left.Diagnostics.Select(d => d.Message), Has.Some.Contains("include cycle"));
    }

    [Test]
    public void Two_roles_claiming_one_id_is_an_error()
    {
        var catalog = ArgonClusterCatalog.Build(Scenario.Scope(typeof(Scenario.DuplicateIds)));

        Assert.That(catalog.Diagnostics.Select(d => d.Code), Does.Contain("E9"));
        Assert.That(catalog.Diagnostics.Select(d => d.Message), Has.Some.Contains("duplicate role id"));
    }

    // ── feature graph ───────────────────────────────────────────────────────────────────────

    [Test]
    public void Features_are_pulled_in_transitively_and_ordered_by_dependency()
    {
        var graph = FeatureGraph.Build([typeof(Scenario.ApiFeature)], new ArgonRoleId("test"));

        Assert.That(graph.HasErrors, Is.False);
        Assert.That(graph.Ordered.Select(f => f.Name), Is.EqualTo(new[] { "storage", "auth", "api" }),
            "api requires auth, auth requires storage — only storage has no unmet dependency to start from");
    }

    [Test]
    public void A_feature_cycle_is_an_E8_rather_than_a_stack_overflow()
    {
        var graph = FeatureGraph.Build([typeof(Scenario.LoopAFeature)], new ArgonRoleId("test"));

        Assert.That(graph.Diagnostics.Select(d => d.Code), Does.Contain("E8"));
        Assert.That(graph.Diagnostics.Select(d => d.Message), Has.Some.Contains("cycle"));
    }

    [Test]
    public void Conflicting_features_enabled_together_are_an_E8()
    {
        var graph = FeatureGraph.Build(
            [typeof(Scenario.AuthFeature), typeof(Scenario.LegacyAuthFeature)], new ArgonRoleId("test"));

        Assert.That(graph.Diagnostics.Select(d => d.Message), Has.Some.Contains("conflict"));
    }

    [Test]
    public void A_feature_conflict_that_is_not_enabled_is_not_reported()
    {
        var graph = FeatureGraph.Build([typeof(Scenario.AuthFeature)], new ArgonRoleId("test"));

        Assert.That(graph.HasErrors, Is.False,
            "AuthFeature is only in conflict with LegacyAuthFeature when both are enabled");
    }

    [Test]
    public void A_feature_contributes_its_analysis_roots_to_the_enabling_role()
    {
        var scope = Scenario.Scope(typeof(Scenario.Composed),
            typeof(BetaGrain), typeof(GammaGrain),
            typeof(IndirectService), typeof(ICallHelper), typeof(CallHelper));

        var trunk = ArgonClusterCatalog.Build(scope).Require(new ArgonRoleId("trunk"));

        Assert.That(trunk.CallRoots, Does.Contain(typeof(IndirectService)),
            "ApiFeature declares GrainRoots(g => g.AddCallRoot<IndirectService>())");
    }

    // ── Orleans wiring ──────────────────────────────────────────────────────────────────────

    [Test]
    public void UseArgonGrainTypes_removes_our_unhosted_grains_and_keeps_the_runtime_s()
    {
        var scope = Scenario.Scope(typeof(Scenario.Composed), typeof(BetaGrain), typeof(GammaGrain));
        var trunk = ArgonClusterCatalog.Build(scope).Require(new ArgonRoleId("trunk"));

        var services = new ServiceCollection();
        services.Configure<GrainTypeOptions>(o =>
        {
            // What the default provider would produce: our grains, hosted and not, alongside
            // Orleans' own system grains.
            o.Classes.Add(typeof(BetaGrain));
            o.Classes.Add(typeof(GammaGrain));
            o.Classes.Add(typeof(AlphaGrain));                 // ours, not hosted by trunk
            o.Classes.Add(RuntimeProvidedGrain);
        });

        new StubSiloBuilder(services).UseArgonGrainTypes(trunk);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<GrainTypeOptions>>().Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.Classes, Does.Contain(typeof(BetaGrain)).And.Contain(typeof(GammaGrain)));
            Assert.That(options.Classes, Does.Not.Contain(typeof(AlphaGrain)),
                "a grain of ours that this role does not host must go");
            Assert.That(options.Classes, Does.Contain(RuntimeProvidedGrain),
                "Orleans' own system grains must survive — a silo without ManagementGrain refuses to start, " +
                "which is what Clear()-and-re-add did and why the documented snippet does not work");
        });
    }

    [Test]
    public void UseArgonGrainTypes_refuses_a_client_role()
    {
        var scope  = Scenario.Scope(typeof(Scenario.Misconfigured), typeof(AlphaGrain));
        var client = ArgonClusterCatalog.Build(scope).Require(new ArgonRoleId("bad-client"));

        Assert.That(() => new StubSiloBuilder(new ServiceCollection()).UseArgonGrainTypes(client),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("hosts no grains"));
    }

    /// <summary>
    /// Stands in for a grain class the Orleans runtime contributes rather than the product —
    /// <c>ManagementGrain</c> and friends are internal, and the filter keys off the assembly name,
    /// so any Orleans type is a faithful stand-in.
    /// </summary>
    private static readonly Type RuntimeProvidedGrain = typeof(Grain);

    private sealed class StubSiloBuilder(IServiceCollection services) : ISiloBuilder
    {
        public IServiceCollection Services      => services;
        public IConfiguration     Configuration => new ConfigurationBuilder().Build();
    }
}
