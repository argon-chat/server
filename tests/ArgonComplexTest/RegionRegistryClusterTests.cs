namespace ArgonComplexTest;

using Argon.Core.Entities.Data;
using Argon.Features.Clustering;
using Argon.Features.Clustering.Regions;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using System.Diagnostics;

/// <summary>
/// Calling grains in another region, which is one Orleans cluster calling into a different one.
/// </summary>
/// <remarks>
/// <para>Orleans has no multi-cluster support — it was removed in 4.0 — so the only way one region
/// reaches another is an ordinary Orleans client pointed at the other cluster's gateway. That client
/// needs a service provider of its own, because <c>AddOrleansClient</c> builds one and a child
/// provider cannot see a parent, and everything hard about this lives in that seam: which services
/// the child needs, whether the grain interfaces resolve across it, and what happens to this process
/// when the far cluster is not there.</para>
///
/// <para>The unit fixture pins the parts that can be checked without a network. This one is the only
/// place the actual claim is tested: that a client built this way reaches a <em>different</em>
/// cluster and can call a real grain on it. Two silos, two cluster ids, one Redis — which is also
/// what proves the cluster id is what separates them.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class RegionRegistryClusterTests : TestBase
{
    // Clear of RoleStartupTests (from 21111) and GrainMigrationTests (22111, 22131).
    private const int HerePort  = 23111;
    private const int TherePort = 23131;

    private const string Here  = "region-a";
    private const string There = "region-b";

    // Zero for the local one, deliberately: the registry refuses to start when the process mints for a
    // different index than its own region is configured with, and the test process mints for zero
    // because nothing called UseRegion. Matching it here buys the assertion without stamping a static
    // that the rest of the suite is reading in parallel.
    private const int HereIndex  = 0;
    private const int ThereIndex = 1;

    /// <summary>The cutover these identifiers are read against.</summary>
    /// <remarks>
    /// In the past, so that an identifier minted during the test is after it and carries its region.
    /// Anything older answers with the original region, which is the production database's whole
    /// content and is the behaviour <see cref="ArgonIdTests"/> covers; here the point is the opposite
    /// case.
    /// </remarks>
    private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private RoleHost here  = null!;
    private RoleHost there = null!;

    private ArgonRegionRegistry registry = null!;

    [OneTimeSetUp]
    public async Task StartTwoClusters()
    {
        var settings = ArgonTestEnvironment.Instance.Host.Settings;

        // Started one after the other: both run the database warm-up on the way up, and two of those
        // racing on one schema is a flake with nothing to do with what is being tested.
        here = new RoleHost(settings, IntegrationTestRole.Id, HerePort, $"argon-test-{Here}");
        _ = here.Services.GetRequiredService<IGrainFactory>();

        there = new RoleHost(settings, IntegrationTestRole.Id, TherePort, $"argon-test-{There}");
        _ = there.Services.GetRequiredService<IGrainFactory>();

        // Stamped before the registry starts, because the registry refuses to start when the process
        // and its configuration disagree about either the index or the epoch — and it is right to: the
        // routing gate on the request path reads these statics while RegionOf reads the options, so a
        // divergence sends calls to the wrong cluster. Put back in Stop, since this fixture is
        // NonParallelizable but the rest of the assembly is not.
        ArgonId.UseRegion(HereIndex, Epoch);

        registry = new ArgonRegionRegistry(
            Options.Create(new ArgonRegionOptions
            {
                Self = Here,

                // Set, where this fixture used to leave them at their defaults, and the difference is
                // what makes routing testable at all. RegionOf reads an index out of the identifier and
                // maps it back to a name, so without distinct indices every identifier answers with the
                // same region and TryGetClientFor cannot be wrong. The epoch is required beside them:
                // it is the cutover before which an identifier belongs to the original region, and with
                // none set every identifier predates everything.
                IdEpoch = Epoch,

                Nodes = new(StringComparer.OrdinalIgnoreCase)
                {
                    [Here] = new()
                    {
                        Zone      = "test",
                        Index     = HereIndex,
                        Gateway   = GatewayOf(here),
                        ClusterId = $"argon-test-{Here}"
                    },
                    [There] = new()
                    {
                        Zone      = "test",
                        Index     = ThereIndex,
                        Gateway   = GatewayOf(there),

                        // The cluster id is the only identifier this has to say out loud. The service
                        // id is a constant now, so client and silo agree without being told — which is
                        // the point of it naming the service rather than the deployment.
                        ClusterId = $"argon-test-{There}"
                    },
                    // Configured, never started, nothing listening. The control for every assertion
                    // about a region that is not there.
                    ["region-gone"] = new()
                    {
                        Zone      = "test",
                        Gateway   = "127.0.0.1:23999",
                        ClusterId = "argon-test-region-gone"
                    }
                }
            }),
            here.Services,
            NullLogger<ArgonRegionRegistry>.Instance);

        await registry.StartAsync(CancellationToken.None);
        await WaitForOnlineAsync(There, TimeSpan.FromMinutes(1));
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (registry is not null)
        {
            // StopAsync disposes the peer clients and their containers; the explicit dispose is what
            // the analyzer wants to see and is a no-op the second time.
            await registry.StopAsync(CancellationToken.None);
            await registry.DisposeAsync();
        }

        here?.Dispose();
        there?.Dispose();

        ArgonId.UseRegion(ArgonId.OriginalRegionIndex);
    }

    /// <summary>
    /// The address a running silo actually advertises its gateway on.
    /// </summary>
    /// <remarks>
    /// Not loopback, and this fixture assumed it was and failed for it. <c>ConfigureEndpoints</c>
    /// resolves the machine's own address, so the gateway listens on that and refuses a connection to
    /// 127.0.0.1 on the same port. Asking the silo removes the guess — and it is the same question a
    /// deployment answers with a service name.
    /// </remarks>
    private static string GatewayOf(RoleHost host)
    {
        var endpoint = host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress.Endpoint;
        return $"{endpoint.Address}:{endpoint.Port}";
    }

    private async Task WaitForOnlineAsync(string region, TimeSpan within)
    {
        var started = Stopwatch.GetTimestamp();

        while (registry.StatusOf(region) != RegionStatus.Online)
        {
            if (Stopwatch.GetElapsedTime(started) > within)
                Assert.Fail($"region '{region}' never came online (last status {registry.StatusOf(region)})");

            await Task.Delay(200);
        }
    }

    /// <summary>
    /// The client reaches the other cluster, and it is genuinely the other one.
    /// </summary>
    /// <remarks>
    /// <c>IManagementGrain</c> answers with the silos of whichever cluster served the call, so the
    /// silo port in the reply is the assertion: getting <c>HerePort</c> back would mean the client
    /// had connected to the local cluster and every other test here would pass while proving nothing.
    /// The two clusters share a Redis and differ only by cluster id, so this is also what shows the
    /// cluster id is what separates them.
    /// </remarks>
    [Test, Order(1)]
    public async Task A_client_for_another_region_reaches_that_regions_silos()
    {
        var remote = registry.GetClient(There);
        var hosts  = await remote.GetGrain<IManagementGrain>(0).GetHosts(onlyActive: true);

        var ports = hosts.Keys.Select(s => s.Endpoint.Port).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ports, Does.Contain(TherePort), "the call did not land on the other cluster");
            Assert.That(ports, Does.Not.Contain(HerePort), "the call landed on the local cluster");
        });
    }

    /// <summary>
    /// A real grain, with Argon types in both directions.
    /// </summary>
    /// <remarks>
    /// The management grain proves a connection; it proves nothing about whether this process and the
    /// far silo agree on what <c>IFeatureFlagGrain</c> is. That agreement comes from the type
    /// manifest, which the client's own container has to acquire — the whole reason
    /// <c>IConfigureOptions&lt;TypeManifestOptions&gt;</c> is copied across. A mismatch here is not a
    /// compile error and not a startup error; it is a call that fails at the far end.
    /// </remarks>
    [Test, Order(2)]
    public async Task A_grain_call_crosses_the_boundary_with_argon_types_intact()
    {
        var remote = registry.GetClient(There);

        var result = await remote.GetGrain<IFeatureFlagGrain>(Guid.Empty)
           .EvaluateAsync("argon.test.flag-that-does-not-exist", FeatureFlagEvaluationContext.ForUser(Guid.NewGuid()));

        // What it answers does not matter. That it answered, with a type this assembly and the far
        // silo both understand, is the whole test.
        Assert.That(result, Is.Not.Null);
    }

    /// <summary>
    /// An identifier minted by the other region resolves to the other region's cluster.
    /// </summary>
    /// <remarks>
    /// The decision the routing seam makes, against two real clusters rather than a fake. Asserted by
    /// reference on the client, because the two are indistinguishable by anything else they say — and
    /// then again, below, by asking a grain which silo answered.
    /// </remarks>
    [Test, Order(3)]
    public void An_identifier_from_another_region_resolves_to_that_regions_cluster()
    {
        var mine  = ArgonId.Create(HereIndex);
        var yours = ArgonId.Create(ThereIndex);

        Assert.Multiple(() =>
        {
            Assert.That(registry.RegionOf(mine), Is.EqualTo(Here));
            Assert.That(registry.RegionOf(yours), Is.EqualTo(There));

            Assert.That(registry.TryGetClientFor(yours, out var owner), Is.True);
            Assert.That(owner, Is.SameAs(registry.GetClient(There)),
                "a foreign identifier handed back the local cluster, which is the failure that looks "
              + "like a working deployment and writes to the wrong database");
        });
    }

    /// <summary>
    /// And a call placed through that client is genuinely served by the far cluster.
    /// </summary>
    /// <remarks>
    /// <para>The reference assertion above proves the decision; this proves the consequence. It is the
    /// same trick the connectivity test uses — <c>IManagementGrain</c> answers with the silos of
    /// whichever cluster served the call, so the port in the reply is the assertion. Getting
    /// <see cref="HerePort"/> back would mean the routing had quietly kept the call at home.</para>
    ///
    /// <para>This is what the seam in <c>ServiceEx.GetGrain</c> does, with the identifier decode in
    /// front of it. It is asserted here rather than there because the seam needs an Ion request context
    /// and this needs two clusters, and the two are not worth standing up together to prove the same
    /// sentence twice.</para>
    /// </remarks>
    [Test, Order(4)]
    public async Task A_call_routed_by_identifier_is_served_by_the_owning_cluster()
    {
        Assert.That(registry.TryGetClientFor(ArgonId.Create(ThereIndex), out var owner), Is.True);

        var silos = await owner!.GetGrain<IManagementGrain>(0).GetHosts(true);

        Assert.That(silos.Keys.Select(silo => silo.Endpoint.Port), Does.Contain(TherePort),
            "the call was answered by the local cluster, so the identifier decided nothing");
    }

    [Test, Order(5)]
    public void The_local_region_resolves_to_the_local_cluster()
    {
        Assert.Multiple(() =>
        {
            Assert.That(registry.StatusOf(Here), Is.EqualTo(RegionStatus.Online));
            Assert.That(registry.GetClient(Here),
                Is.SameAs(here.Services.GetRequiredService<IClusterClient>()));
        });
    }

    /// <summary>
    /// A region that is not there hands out nothing, and never did.
    /// </summary>
    /// <remarks>
    /// This is the failure the old cluster-client path got wrong in both directions: it took the
    /// process down when a region would not connect, and once past that it would have handed out a
    /// client that accepts calls and lets them time out. Refusing here is what lets a caller route
    /// somewhere else instead of waiting.
    /// </remarks>
    [Test, Order(6)]
    public void A_region_that_never_connected_is_refused_rather_than_handed_out()
    {
        Assert.Multiple(() =>
        {
            Assert.That(registry.StatusOf("region-gone"), Is.Not.EqualTo(RegionStatus.Online));
            Assert.That(registry.TryGetClient("region-gone", out _), Is.False);
            Assert.That(() => registry.GetClient("region-gone"), Throws.TypeOf<RegionUnavailableException>());
        });
    }

    /// <summary>
    /// And the process it could not reach is still running.
    /// </summary>
    /// <remarks>
    /// Stated as a test because it is the entire point. <c>OutsideRuntimeClient.StartAsync</c> throws
    /// when the retry filter gives up, and the filter this replaces gave up on anything that was not
    /// <c>SiloUnavailableException</c> — a closed port among them — while the thing awaiting it had no
    /// <c>catch</c>. Everything above this line ran in a process that has been failing to reach
    /// <c>region-gone</c> since setup.
    /// </remarks>
    [Test, Order(5)]
    public async Task An_unreachable_region_does_not_disturb_the_rest()
    {
        Assert.That(registry.StatusOf(There), Is.EqualTo(RegionStatus.Online),
            "the reachable region should be unaffected by the unreachable one");

        var local = registry.GetClient(Here);
        var hosts = await local.GetGrain<IManagementGrain>(0).GetHosts(onlyActive: true);

        Assert.That(hosts, Is.Not.Empty, "the local cluster is still usable");
    }
}
