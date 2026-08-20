namespace ArgonSharedLogicTest;

using Argon.Api.Clustering;
using Argon.Features.Clustering;
using Argon.Features.Clustering.Regions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Runtime.CompilerServices;

/// <summary>
/// The rules a region list has to hold before a process will start against it.
/// </summary>
/// <remarks>
/// Every one of these is a mistake that does not fail — it connects to something. A cluster id
/// shared by two regions is one cluster to Orleans; a <c>Self</c> that names no configured region
/// leaves the process unable to say what is local; a gateway that is not <c>host:port</c> is a
/// client with nowhere to go. None of them throws at runtime, so they have to be caught here.
/// </remarks>
[TestFixture]
public class RegionOptionsRulesTests
{
    private const string Section = ArgonRegionOptions.SectionName;

    private static RoleDescriptor EntryPoint()
        => ArgonClusterCatalog.Build(new ClusterScanScope
        {
            Assemblies = [typeof(EntryPointRole).Assembly, typeof(IArgonRole).Assembly]
        }).Require(ArgonRoleId.EntryPoint);

    private static (string[] Errors, string[] Warnings) Validate(params (string Key, string? Value)[] values)
    {
        var report = FeatureConfigurationValidator.Validate(EntryPoint(),
            new ConfigurationBuilder()
               .AddJsonFile(Path.Combine(TestContext.CurrentContext.TestDirectory, "appsettings.json"), optional: false)
               .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
               .Build());

        return (Mine(report.Errors), Mine(report.Warnings));
    }

    private static string[] Mine(IEnumerable<ClusterDiagnostic> diagnostics)
        => diagnostics
           .Where(d => d.Target?.StartsWith(Section, StringComparison.Ordinal) is true
                    || d.ToString().Contains("cluster id", StringComparison.Ordinal))
           .Select(d => d.ToString())
           .ToArray();

    /// <summary>
    /// The cluster id this process runs as when nothing configures one.
    /// </summary>
    /// <remarks>
    /// The local region's entry has to name it: a process whose region list disagrees with its own
    /// cluster identity would dial itself as if it were somewhere else.
    /// </remarks>
    private const string LocalCluster = ArgonClusterEndpoints.DefaultClusterId;

    private static (string Key, string? Value)[] Region(string name, string zone, string gateway, string clusterId)
        =>
        [
            ($"{Section}:Nodes:{name}:Zone", zone),
            ($"{Section}:Nodes:{name}:Gateway", gateway),
            ($"{Section}:Nodes:{name}:ClusterId", clusterId)
        ];

    /// <summary>
    /// One region is a deployment, not a misconfiguration.
    /// </summary>
    /// <remarks>
    /// The section is absent everywhere today and will stay absent in most deployments forever. A
    /// warning here would be one every developer learns to scroll past, which is how the ones that
    /// matter get scrolled past too.
    /// </remarks>
    [Test]
    public void No_regions_configured_is_not_a_finding()
    {
        var (errors, warnings) = Validate();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(warnings, Is.Empty);
        });
    }

    [Test]
    public void A_complete_two_region_deployment_passes()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            ($"{Section}:IdEpoch", "2026-01-01T00:00:00Z"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", LocalCluster),
            .. Region("ru-b", "ru", "gw.ru-b.internal:30000", "argon-ru-b"),
            ($"{Section}:Nodes:ru-b:Index", "1")
        ]);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void A_process_that_is_not_in_the_deployment_it_describes_is_rejected()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "eu-a"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", LocalCluster)
        ]);

        Assert.That(errors, Has.Some.Contains("eu-a"));
    }

    /// <summary>
    /// The one that looks like it works.
    /// </summary>
    /// <remarks>
    /// Two regions with one cluster id are one cluster as far as Orleans is concerned. A client aimed
    /// at either reaches whichever silo answered, calls succeed, and the deployment is silently
    /// single-region.
    /// </remarks>
    [Test]
    public void Two_regions_sharing_a_cluster_id_are_rejected()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", "argon-shared"),
            .. Region("ru-b", "ru", "gw.ru-b.internal:30000", "argon-shared")
        ]);

        Assert.That(errors, Has.Some.Contains("argon-shared"));
    }

    [Test]
    public void A_region_without_a_zone_or_a_gateway_is_rejected()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", LocalCluster),
            ($"{Section}:Nodes:ru-b:Zone", null),
            ($"{Section}:Nodes:ru-b:Gateway", null)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("Zone"));
            Assert.That(errors, Has.Some.Contains("Gateway"));
        });
    }

    [Test]
    public void A_gateway_that_is_not_host_and_port_is_rejected()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", LocalCluster),
            .. Region("ru-b", "ru", "gw.ru-b.internal", "argon-ru-b")
        ]);

        Assert.That(errors, Has.Some.Contains("host:port"));
    }

    /// <summary>
    /// A process whose region list disagrees with its own cluster identity.
    /// </summary>
    /// <remarks>
    /// The two values live in different sections, written for different reasons, and nothing at
    /// runtime notices: the local region would be treated as reachable-through-a-client rather than
    /// as here, and the client would wait for a gateway that will not have it.
    /// </remarks>
    [Test]
    public void The_local_region_must_name_the_cluster_this_process_runs_as()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", "argon-somewhere-else")
        ]);

        Assert.That(errors, Has.Some.Contains(ArgonClusterEndpoints.DefaultClusterId));
    }

    /// <summary>
    /// And the service id has to be the one everything else uses.
    /// </summary>
    /// <remarks>
    /// Orleans keys grain storage and reminders on it, so a region that disagrees is a region whose
    /// state nothing else can address. It is the mistake the old default made on its own, by deriving
    /// the service id from the datacenter.
    /// </remarks>
    [Test]
    public void The_local_region_must_agree_about_the_service_id()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", LocalCluster),
            ($"{Section}:Nodes:ru-a:ServiceId", "argon-region-ru-a")
        ]);

        Assert.That(errors, Has.Some.Contains("service id"));
    }

    /// <summary>
    /// Two regions and no cutover means every identifier reads as belonging to the first one.
    /// </summary>
    /// <remarks>
    /// Including the ones the second region is minting right now, which is the part that makes it
    /// dangerous rather than merely wrong: the second region would hand out identifiers it then
    /// routes to the first.
    /// </remarks>
    [Test]
    public void A_second_region_without_a_cutover_is_rejected()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", LocalCluster),
            .. Region("ru-b", "ru", "gw.ru-b.internal:30000", "argon-ru-b"),
            ($"{Section}:Nodes:ru-b:Index", "1")
        ]);

        Assert.That(errors, Has.Some.Contains("Epoch"));
    }

    [Test]
    public void Two_regions_on_one_index_are_rejected()
    {
        var (errors, _) = Validate([
            ($"{Section}:Self", "ru-a"),
            ($"{Section}:IdEpoch", "2026-01-01T00:00:00Z"),
            .. Region("ru-a", "ru", "gw.ru-a.internal:30000", LocalCluster),
            .. Region("ru-b", "ru", "gw.ru-b.internal:30000", "argon-ru-b")
        ]);

        Assert.That(errors, Has.Some.Contains("share index"));
    }

    /// <summary>
    /// A mistyped cutover stops the process instead of quietly disabling itself.
    /// </summary>
    /// <remarks>
    /// Returning null for an unreadable value means "tagging has not begun", which reads every
    /// identifier as belonging to the original region. That is the right answer for a deployment that
    /// set nothing and the worst possible one for a deployment that set the epoch and fat-fingered it.
    /// </remarks>
    [Test]
    public void A_cutover_that_is_not_a_timestamp_is_refused()
    {
        var configuration = new ConfigurationBuilder()
           .AddInMemoryCollection([new KeyValuePair<string, string?>($"{Section}:IdEpoch", "yesterday")])
           .Build();

        Assert.That(() => ArgonRegionOptions.EpochOf(configuration),
            Throws.TypeOf<InvalidOperationException>());
    }

    /// <summary>
    /// And a cutover without a zone is read as UTC rather than as the pod's local time.
    /// </summary>
    /// <remarks>
    /// The first version used the ambient culture and no styles. Two regions in different time zones
    /// would then disagree about the cutover by hours, and each would read a band of the other's
    /// identifiers as pre-epoch — that is, as belonging to the original region.
    /// </remarks>
    [Test]
    public void A_cutover_without_a_zone_is_utc()
        => Assert.That(
            ArgonRegionOptions.EpochOf(new ConfigurationBuilder()
               .AddInMemoryCollection([new KeyValuePair<string, string?>($"{Section}:IdEpoch", "2026-01-01T00:00:00")])
               .Build()),
            Is.EqualTo(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    [Test]
    public void No_cutover_configured_is_not_an_error()
        => Assert.That(ArgonRegionOptions.EpochOf(new ConfigurationBuilder().Build()), Is.Null);

    [TestCase("host:30000", true)]
    [TestCase("10.0.0.1:1", true)]
    [TestCase("host", false)]
    [TestCase("host:", false)]
    [TestCase(":30000", false)]
    [TestCase("host:0", false)]
    [TestCase("host:70000", false)]
    [TestCase("host:abc", false)]
    [TestCase(null, false)]
    public void Gateway_parsing(string? value, bool expected)
        => Assert.That(ArgonRegionNode.TryParseGateway(value, out _, out _), Is.EqualTo(expected));
}

/// <summary>
/// What the registry does when a region is not there, which is most of what it is for.
/// </summary>
[TestFixture]
public class RegionRegistryTests
{
    /// <summary>
    /// A host provider with a logger factory and nothing else.
    /// </summary>
    /// <remarks>
    /// The logger factory is the one service a peer client borrows, so that it logs where the rest of
    /// the process does. Everything else it needs it registers itself, and this fixture holding
    /// nothing else is what demonstrates that.
    /// </remarks>
    private static IServiceProvider Host()
        => new ServiceCollection().AddLogging().BuildServiceProvider();

    private static ArgonRegionRegistry Registry(ArgonRegionOptions options, IServiceProvider? host = null)
        => new(Options.Create(options), host ?? Host(), NullLogger<ArgonRegionRegistry>.Instance);

    [Test]
    public void With_nothing_configured_there_is_one_region_and_it_is_here()
    {
        var registry = Registry(new ArgonRegionOptions { Self = "ru-a" });

        Assert.Multiple(() =>
        {
            Assert.That(registry.Self, Is.EqualTo("ru-a"));
            Assert.That(registry.Regions, Is.EquivalentTo(new[] { "ru-a" }));
            Assert.That(registry.IsLocal("RU-A"), Is.True, "region names are not case sensitive");
            Assert.That(registry.StatusOf("ru-a"), Is.EqualTo(RegionStatus.Online));
        });
    }

    /// <summary>
    /// A region nobody configured is offline, not an exception.
    /// </summary>
    /// <remarks>
    /// Routing will ask about regions it read out of an id, and an id can outlive the configuration
    /// that knew about its region. Answering "offline" lets the caller fall back; throwing would
    /// make an unknown region a different failure from an unreachable one for no benefit.
    /// </remarks>
    [Test]
    public void An_unknown_region_is_offline()
    {
        var registry = Registry(new ArgonRegionOptions { Self = "ru-a" });

        Assert.Multiple(() =>
        {
            Assert.That(registry.StatusOf("eu-a"), Is.EqualTo(RegionStatus.Offline));
            Assert.That(registry.TryGetClient("eu-a", out _), Is.False);
            Assert.That(() => registry.GetClient("eu-a"), Throws.TypeOf<RegionUnavailableException>());
        });
    }

    [Test]
    public void Zones_group_the_regions_a_caller_may_fall_back_to()
    {
        var registry = Registry(new ArgonRegionOptions
        {
            Self  = "ru-a",
            Nodes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ru-a"] = new() { Zone = "ru", Gateway = "127.0.0.1:30000", ClusterId = "argon-ru-a" },
                ["ru-b"] = new() { Zone = "ru", Gateway = "127.0.0.1:30010", ClusterId = "argon-ru-b" },
                ["eu-a"] = new() { Zone = "eu", Gateway = "127.0.0.1:30020", ClusterId = "argon-eu-a" }
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(registry.ZoneOf("ru-b"), Is.EqualTo("ru"));

            // Itself excluded: this list is "where else could this go", and here is not elsewhere.
            Assert.That(registry.PeersInZone("ru"), Is.EquivalentTo(new[] { "ru-b" }));

            // The boundary residency draws. Falling back across it is the thing that must not happen.
            Assert.That(registry.PeersInZone("eu"), Is.EquivalentTo(new[] { "eu-a" }));
        });
    }

    /// <summary>
    /// A configured peer starts out unusable, and building it does not touch the network.
    /// </summary>
    /// <remarks>
    /// <para>This is the test for the claim the whole design rests on: an Orleans client can be built
    /// on a service collection of its own, given nothing but a logger factory, because
    /// <c>AddOrleansClient</c> registers logging and the serializer itself. If that were false this
    /// would throw while resolving <c>IClusterClient</c>, and the alternative would be proxying host
    /// services into the child container one runtime failure at a time.</para>
    ///
    /// <para>It also pins the status: a peer that has never connected reads <c>Connecting</c> and
    /// hands out no client. An Orleans client that has not connected does not refuse calls, it
    /// accepts them and lets them time out, so the gate has to be here.</para>
    /// </remarks>
    [Test]
    public void A_peer_is_built_without_connecting_and_hands_out_no_client_until_it_does()
    {
        var registry = Registry(new ArgonRegionOptions
        {
            Self  = "ru-a",
            Nodes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ru-a"] = new() { Zone = "ru", Gateway = "127.0.0.1:30000", ClusterId = "argon-ru-a" },
                ["ru-b"] = new() { Zone = "ru", Gateway = "127.0.0.1:39999", ClusterId = "argon-ru-b" }
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(registry.StatusOf("ru-b"), Is.EqualTo(RegionStatus.Connecting));
            Assert.That(registry.TryGetClient("ru-b", out _), Is.False);
        });
    }

    [Test]
    public async Task Starting_with_an_unreachable_peer_returns_immediately()
    {
        var registry = Registry(new ArgonRegionOptions
        {
            Self  = "ru-a",
            Nodes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ru-a"] = new() { Zone = "ru", Gateway = "127.0.0.1:30000", ClusterId = "argon-ru-a" },
                // Nothing listens here, and nothing ever will.
                ["ru-b"] = new() { Zone = "ru", Gateway = "127.0.0.1:39998", ClusterId = "argon-ru-b" }
            }
        });

        var started = System.Diagnostics.Stopwatch.StartNew();
        await registry.StartAsync(CancellationToken.None);
        started.Stop();

        // The point of the whole class: host startup does not wait on a region, so a region that is
        // down cannot keep this one from booting.
        Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));

        await registry.StopAsync(CancellationToken.None);
    }
}

/// <summary>
/// Turning an identifier into the region that owns it, which is the whole reason the region is in
/// the identifier.
/// </summary>
[TestFixture]
public class RegionRoutingTests
{
    private static readonly DateTimeOffset Cutover = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ArgonRegionOptions TwoRegions() => new()
    {
        Self    = "ru-a",
        IdEpoch = Cutover,
        Nodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ru-a"] = new() { Zone = "ru", Gateway = "127.0.0.1:30000", ClusterId = "argon-ru-a", Index = 0 },
            ["ru-b"] = new() { Zone = "ru", Gateway = "127.0.0.1:30010", ClusterId = "argon-ru-b", Index = 1 }
        }
    };

    private static ArgonRegionRegistry Registry(ArgonRegionOptions options)
        => new(Options.Create(options), new ServiceCollection().AddLogging().BuildServiceProvider(),
            NullLogger<ArgonRegionRegistry>.Instance);

    /// <summary>
    /// With one region the identifier is never read, and that is what keeps every deployment that
    /// has not split working without an epoch, a migration or a thought.
    /// </summary>
    [Test]
    public void With_one_region_everything_is_here()
    {
        var registry = Registry(new ArgonRegionOptions { Self = "ru-a" });

        Assert.Multiple(() =>
        {
            Assert.That(registry.RegionOf(ArgonId.Create(7)), Is.EqualTo("ru-a"));
            Assert.That(registry.RegionOf(Guid.NewGuid()), Is.EqualTo("ru-a"));
            Assert.That(registry.RegionOf(Guid.Empty), Is.EqualTo("ru-a"));
        });
    }

    [Test]
    public void An_identifier_names_the_region_that_minted_it()
    {
        var registry = Registry(TwoRegions());

        Assert.Multiple(() =>
        {
            Assert.That(registry.RegionOf(ArgonId.Create(0)), Is.EqualTo("ru-a"));
            Assert.That(registry.RegionOf(ArgonId.Create(1)), Is.EqualTo("ru-b"));
        });
    }

    /// <summary>
    /// The porting guarantee, end to end.
    /// </summary>
    /// <remarks>
    /// Rows that existed before any of this was written keep resolving to the region that made them,
    /// with nothing backfilled and no column added. The identifiers already carried the one fact
    /// needed to decide: when they were made.
    /// </remarks>
    [Test]
    public void Everything_from_before_the_cutover_stays_with_the_original_region()
    {
        var registry = Registry(TwoRegions());

        // What production holds today: v7 spaces with a random rand_a, and v4 users and channels.
        for (var i = 0; i < 200; i++)
        {
            Assert.That(registry.RegionOf(Guid.CreateVersion7(Cutover.AddDays(-1))), Is.EqualTo("ru-a"));
            Assert.That(registry.RegionOf(Guid.NewGuid()), Is.EqualTo("ru-a"));
        }
    }

    /// <summary>
    /// A region that was configured once, minted identifiers, and has since been removed.
    /// </summary>
    /// <remarks>
    /// A configuration mistake rather than a data one, and the only case where routing refuses. It is
    /// deliberately not folded into the original region: those identifiers belong somewhere, and
    /// answering "here" would have two regions both claiming them.
    /// </remarks>
    [Test]
    public void An_identifier_from_a_region_that_is_gone_is_refused()
    {
        var registry = Registry(TwoRegions());

        Assert.That(() => registry.RegionOf(ArgonId.Create(9)),
            Throws.TypeOf<UnroutableIdException>());
    }

    /// <summary>
    /// A process whose stamp disagrees with its own configuration does not start.
    /// </summary>
    /// <remarks>
    /// Every identifier it minted would name the wrong region permanently, and nothing downstream
    /// could tell — the identifier is well formed and names a real region, just not the one that made
    /// it.
    /// </remarks>
    [Test]
    public async Task A_process_that_would_mint_for_the_wrong_region_refuses_to_start()
    {
        // Self is ru-b (index 1) while the process still stamps the original region.
        var options = TwoRegions();
        options.Self = "ru-b";

        var registry = Registry(options);

        Assert.That(async () => await registry.StartAsync(CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().And.Message.Contains("wrong region"));

        await registry.DisposeAsync();
    }
}

/// <summary>
/// The filter that decides whether an unreachable region is an inconvenience or an outage.
/// </summary>
/// <remarks>
/// <c>OutsideRuntimeClient.StartAsync</c> rethrows the moment this answers false. The filter it
/// replaces retried <c>SiloUnavailableException</c> and returned false for everything else, so a
/// region that resolved but refused the connection — wrong port, closed gateway, network policy —
/// threw out of a <c>StartAsync</c> nobody was catching.
/// </remarks>
[TestFixture]
public class RegionConnectionRetryFilterTests
{
    [Test]
    public async Task Every_failure_is_retried_whatever_it_was()
    {
        var filter = new RegionConnectionRetryFilter("eu-a", TimeSpan.FromMilliseconds(20), NullLogger.Instance);

        Assert.Multiple(async () =>
        {
            Assert.That(await filter.ShouldRetryConnectionAttempt(
                new SiloUnavailableException("no silo"), CancellationToken.None), Is.True);

            // The case the old filter gave up on.
            Assert.That(await filter.ShouldRetryConnectionAttempt(
                new System.Net.Sockets.SocketException(), CancellationToken.None), Is.True);

            Assert.That(await filter.ShouldRetryConnectionAttempt(
                new InvalidOperationException("anything at all"), CancellationToken.None), Is.True);
        });
    }

    [Test]
    public async Task Cancellation_is_the_only_way_out()
    {
        var filter = new RegionConnectionRetryFilter("eu-a", TimeSpan.FromMilliseconds(20), NullLogger.Instance);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.That(await filter.ShouldRetryConnectionAttempt(new Exception(), cancelled.Token), Is.False);
    }

    [Test]
    public void Backoff_grows_stays_under_the_cap_and_is_never_zero()
    {
        var max = TimeSpan.FromSeconds(30);

        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var delay = RegionConnectionRetryFilter.Backoff(attempt, max);

            Assert.That(delay, Is.GreaterThan(TimeSpan.Zero), $"attempt {attempt}");
            Assert.That(delay, Is.LessThanOrEqualTo(max), $"attempt {attempt}");
        }

        // Jittered, so growth is asserted between the floors rather than sample to sample: every
        // process in a region retries the same peer, and without jitter they all return at once.
        Assert.That(RegionConnectionRetryFilter.Backoff(1, max),
            Is.LessThan(RegionConnectionRetryFilter.Backoff(8, max)));
    }
}

/// <summary>
/// Finding another region's gateways without reaching into its membership store.
/// </summary>
/// <remarks>
/// Redis clustering would have meant every region's processes connecting to every other region's
/// membership store. Gateways are the only thing a region has to expose to clients anyway.
/// </remarks>
[TestFixture]
public class RegionGatewayListProviderTests
{
    private static RegionGatewayListProvider Provider(string host, int port)
        => new("eu-a", host, port, TimeSpan.FromSeconds(30), NullLogger.Instance);

    [Test]
    public async Task A_literal_address_needs_no_resolver()
    {
        var gateways = await Provider("10.1.2.3", 30000).GetGateways();

        Assert.Multiple(() =>
        {
            Assert.That(gateways, Has.Count.EqualTo(1));
            Assert.That(gateways[0].Scheme, Is.EqualTo("gwy.tcp"));
            Assert.That(gateways[0].ToIPEndPoint(), Is.EqualTo(new IPEndPoint(IPAddress.Parse("10.1.2.3"), 30000)));
        });
    }

    /// <summary>
    /// A name that does not resolve is a region with no gateway up, not an error.
    /// </summary>
    /// <remarks>
    /// Orleans calls this again every refresh period, so throwing would put a stack trace on a timer
    /// for as long as the far side is down — and the caller already handles an empty list, because an
    /// empty list is also what a region mid-deployment returns.
    /// </remarks>
    [Test]
    public async Task A_name_that_does_not_resolve_returns_nothing_and_does_not_throw()
    {
        var gateways = await Provider("no-such-host.invalid", 30000).GetGateways();

        Assert.That(gateways, Is.Empty);
    }

    [Test]
    public void The_refresh_period_is_what_was_configured()
        => Assert.That(Provider("10.1.2.3", 30000).MaxStaleness, Is.EqualTo(TimeSpan.FromSeconds(30)));
}

/// <summary>
/// The merge rule, which is the part that has to be right whatever the transport does.
/// </summary>
/// <remarks>
/// Reachability is a property of the pair — can this process reach that region — and intent is a
/// property of the region. Combining them as <c>min</c> is what makes the second signal safe to take
/// from a bus: an announcement can only ever narrow what this process already believes, so a stale,
/// late or lying peer cannot talk anyone into routing at a region that is not there.
/// </remarks>
[TestFixture]
public class RegionAvailabilityTests
{
    /// <summary>
    /// The one that matters: nothing a region says about itself raises it.
    /// </summary>
    /// <remarks>
    /// Every case where the region claims to be taking work while this process cannot reach it. If
    /// any of these answered <c>Online</c>, an announcement — a claim, delivered late, by something
    /// that may since have died — would be enough to send calls into a hole.
    /// </remarks>
    [TestCase(RegionStatus.Offline,    ExpectedResult = RegionStatus.Offline)]
    [TestCase(RegionStatus.Connecting, ExpectedResult = RegionStatus.Connecting)]
    [TestCase(RegionStatus.Draining,   ExpectedResult = RegionStatus.Draining)]
    public RegionStatus An_advertised_active_never_raises_a_region(RegionStatus reachability)
        => RegionAvailability.Merge(reachability, RegionIntent.Active);

    /// <summary>And the other direction is the one an announcement is allowed to move it in.</summary>
    [TestCase(RegionStatus.Online,     ExpectedResult = RegionStatus.Draining)]
    [TestCase(RegionStatus.Connecting, ExpectedResult = RegionStatus.Connecting)]
    [TestCase(RegionStatus.Offline,    ExpectedResult = RegionStatus.Offline)]
    public RegionStatus An_advertised_drain_only_ever_lowers(RegionStatus reachability)
        => RegionAvailability.Merge(reachability, RegionIntent.Draining);

    [Test]
    public void A_reachable_region_that_says_nothing_is_online()
        => Assert.That(RegionAvailability.Merge(RegionStatus.Online, RegionIntent.Active),
            Is.EqualTo(RegionStatus.Online));

    /// <summary>
    /// Draining is usable and does not accept new work, and that is the entire distinction.
    /// </summary>
    /// <remarks>
    /// A drain that stopped calls to the activations already living there would be the outage the
    /// drain exists to avoid. What stops is choosing it for anything that is not there yet.
    /// </remarks>
    [Test]
    public void Draining_still_serves_what_is_already_homed_there()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RegionStatus.Draining.IsUsable(), Is.True);
            Assert.That(RegionStatus.Draining.AcceptsNewWork(), Is.False);

            Assert.That(RegionStatus.Online.IsUsable(), Is.True);
            Assert.That(RegionStatus.Online.AcceptsNewWork(), Is.True);

            Assert.That(RegionStatus.Connecting.IsUsable(), Is.False);
            Assert.That(RegionStatus.Offline.IsUsable(), Is.False);
        });
    }

    /// <summary>The scale <c>min</c> is taken on, written out so a renumbered enum cannot move it.</summary>
    [Test]
    public void Less_usable_is_ordered_the_way_routing_means_it()
        => Assert.That(
            new[] { RegionStatus.Online, RegionStatus.Draining, RegionStatus.Connecting, RegionStatus.Offline }
               .Select(status => status.Rank()),
            Is.Ordered.Descending);
}

/// <summary>A channel that keeps what was published and delivers nothing.</summary>
internal sealed class RecordingIntentChannel : IRegionIntentChannel
{
    public List<RegionIntentAnnouncement> Published { get; } = [];

    public ValueTask PublishAsync(RegionIntentAnnouncement announcement, CancellationToken ct = default)
    {
        Published.Add(announcement);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<RegionIntentAnnouncement> ListenAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// What a region says about itself, and what a process does with what it hears.
/// </summary>
[TestFixture]
public class RegionIntentTests
{
    private static (RegionIntents Intents, RecordingIntentChannel Channel) Intents(string self = "ru-a")
    {
        var channel = new RecordingIntentChannel();

        return (new RegionIntents(Options.Create(new ArgonRegionOptions { Self = self }), channel,
            NullLogger<RegionIntents>.Instance), channel);
    }

    [Test]
    public void A_region_nobody_has_said_anything_about_is_active()
    {
        var (intents, _) = Intents();

        Assert.Multiple(() =>
        {
            Assert.That(intents.Local, Is.EqualTo(RegionIntent.Active));
            Assert.That(intents.IntentOf("eu-a"), Is.EqualTo(RegionIntent.Active));

            // And it says nothing, so a process that restarted mid-drain cannot announce its region
            // back into service on the strength of a default.
            Assert.That(intents.Declaration, Is.Null);
        });
    }

    [Test]
    public async Task Declaring_takes_effect_here_before_it_is_published()
    {
        var (intents, channel) = Intents();

        await intents.DeclareAsync(RegionIntent.Draining);

        Assert.Multiple(() =>
        {
            Assert.That(intents.Local, Is.EqualTo(RegionIntent.Draining));
            Assert.That(channel.Published, Has.Count.EqualTo(1));
            Assert.That(channel.Published[0].Region, Is.EqualTo("ru-a"));
            Assert.That(channel.Published[0].Intent, Is.EqualTo(RegionIntent.Draining));
        });
    }

    /// <summary>
    /// A declaration reaches the rest of its own region, not only the other ones.
    /// </summary>
    /// <remarks>
    /// A drain is a property of the region. If a process ignored announcements about itself, "is
    /// this region draining" would answer differently depending on which pod the operator happened
    /// to reach, and the twenty that were not asked would keep repeating the old answer.
    /// </remarks>
    [Test]
    public void A_process_adopts_what_its_own_region_announced()
    {
        var (intents, _) = Intents();

        Assert.That(intents.Record(
            new RegionIntentAnnouncement("ru-a", RegionIntent.Draining, DateTimeOffset.UtcNow)), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(intents.Local, Is.EqualTo(RegionIntent.Draining));

            // Adopted, therefore repeated: this process now carries the declaration for the ones
            // that start after it.
            Assert.That(intents.Declaration, Is.Not.Null);
            Assert.That(intents.Declaration!.Intent, Is.EqualTo(RegionIntent.Draining));
        });
    }

    /// <summary>
    /// A repeat carries the instant of the declaration, so it cannot beat a newer decision.
    /// </summary>
    /// <remarks>
    /// Every process of a region repeats its region's declaration on its own timer. If ordering were
    /// by arrival, one process that had not yet heard the un-drain would push the region back into
    /// maintenance on every tick.
    /// </remarks>
    [Test]
    public void An_older_declaration_does_not_overrule_a_newer_one()
    {
        var (intents, _) = Intents();
        var declaredAt   = DateTimeOffset.UtcNow;

        intents.Record(new RegionIntentAnnouncement("eu-a", RegionIntent.Draining, declaredAt));

        var applied = intents.Record(
            new RegionIntentAnnouncement("eu-a", RegionIntent.Active, declaredAt.AddSeconds(-30)));

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.False);
            Assert.That(intents.IntentOf("eu-a"), Is.EqualTo(RegionIntent.Draining));
        });
    }

    [Test]
    public void The_newer_declaration_wins_in_both_directions()
    {
        var (intents, _) = Intents();
        var declaredAt   = DateTimeOffset.UtcNow;

        intents.Record(new RegionIntentAnnouncement("eu-a", RegionIntent.Draining, declaredAt));
        intents.Record(new RegionIntentAnnouncement("eu-a", RegionIntent.Active, declaredAt.AddSeconds(1)));

        Assert.That(intents.IntentOf("eu-a"), Is.EqualTo(RegionIntent.Active));
    }

    /// <summary>Two declarations stamped the same instant is a clock, not a decision.</summary>
    [Test]
    public void A_tie_goes_to_the_more_restrictive_answer()
    {
        var (intents, _) = Intents();
        var declaredAt   = DateTimeOffset.UtcNow;

        intents.Record(new RegionIntentAnnouncement("eu-a", RegionIntent.Active, declaredAt));
        intents.Record(new RegionIntentAnnouncement("eu-a", RegionIntent.Draining, declaredAt));

        Assert.That(intents.IntentOf("eu-a"), Is.EqualTo(RegionIntent.Draining));
    }

    [Test]
    public void Region_names_are_not_case_sensitive_here_either()
    {
        var (intents, _) = Intents();

        intents.Record(new RegionIntentAnnouncement("EU-A", RegionIntent.Draining, DateTimeOffset.UtcNow));

        Assert.That(intents.IntentOf("eu-a"), Is.EqualTo(RegionIntent.Draining));
    }

    /// <summary>A subject per region, and the same one however the name was spelled.</summary>
    [Test]
    public void Every_region_declares_on_its_own_subject()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NatsRegionIntentChannel.SubjectFor("ru-a"), Is.EqualTo("argon.regions.ru-a.intent"));
            Assert.That(NatsRegionIntentChannel.SubjectFor("RU-A"), Is.EqualTo("argon.regions.ru-a.intent"));
            Assert.That(NatsRegionIntentChannel.SubjectFor("ru-b"),
                Is.Not.EqualTo(NatsRegionIntentChannel.SubjectFor("ru-a")));

            // A dot would otherwise add a level to the subject and stop matching the wildcard
            // everyone subscribes to.
            Assert.That(NatsRegionIntentChannel.SubjectFor("ru.a"), Is.EqualTo("argon.regions.ru_a.intent"));
            Assert.That(NatsRegionIntentChannel.AllSubject, Is.EqualTo("argon.regions.*.intent"));
        });
    }
}

/// <summary>
/// The registry's merged answer: what it can reach, narrowed by what it has been told.
/// </summary>
[TestFixture]
public class RegionMergedStatusTests
{
    private static IServiceProvider Host()
        => new ServiceCollection().AddLogging().BuildServiceProvider();

    private static (ArgonRegionRegistry Registry, IRegionIntents Intents) Deployment(ArgonRegionOptions options)
    {
        var intents = new RegionIntents(Options.Create(options), new RecordingIntentChannel(),
            NullLogger<RegionIntents>.Instance);

        return (new ArgonRegionRegistry(Options.Create(options), Host(),
            NullLogger<ArgonRegionRegistry>.Instance, intents), intents);
    }

    private static ArgonRegionOptions OneRegion() => new() { Self = "ru-a" };

    private static ArgonRegionOptions TwoRegions() => new()
    {
        Self    = "ru-a",
        IdEpoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Nodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ru-a"] = new() { Zone = "ru", Gateway = "127.0.0.1:30000", ClusterId = "argon-ru-a", Index = 0 },
            // Configured, never started, nothing listening: reads Connecting and stays there.
            ["ru-b"] = new() { Zone = "ru", Gateway = "127.0.0.1:39997", ClusterId = "argon-ru-b", Index = 1 }
        }
    };

    /// <summary>
    /// The thing this exists to make false: that a process cannot report its own region as anything
    /// but healthy.
    /// </summary>
    [Test]
    public async Task A_region_can_say_it_is_draining_about_itself()
    {
        var (registry, _) = Deployment(OneRegion());

        Assert.That(registry.StatusOf("ru-a"), Is.EqualTo(RegionStatus.Online), "before it says anything");

        await registry.DeclareAsync(RegionIntent.Draining);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Intent, Is.EqualTo(RegionIntent.Draining));
            Assert.That(registry.StatusOf("ru-a"), Is.EqualTo(RegionStatus.Draining));

            // Still serving what is homed here — the drain is about placement, not about calls.
            Assert.That(registry.StatusOf("ru-a").IsUsable(), Is.True);
            Assert.That(registry.AcceptsNewWork("ru-a"), Is.False);
        });
    }

    [Test]
    public async Task And_can_say_it_is_taking_work_again()
    {
        var (registry, _) = Deployment(OneRegion());

        await registry.DeclareAsync(RegionIntent.Draining);
        await registry.DeclareAsync(RegionIntent.Active);

        Assert.That(registry.StatusOf("ru-a"), Is.EqualTo(RegionStatus.Online));
    }

    /// <summary>
    /// An advertised healthy status does not rescue a region this process cannot reach.
    /// </summary>
    /// <remarks>
    /// The peer here is configured and has never connected, so local reachability says
    /// <c>Connecting</c>. It then announces — correctly, as far as it knows — that it is taking
    /// work. Reachability is a property of the pair and the peer holds neither half of this one, so
    /// the announcement changes nothing: the minimum of the two is still <c>Connecting</c>, and the
    /// caller is still told to route somewhere else.
    /// </remarks>
    [Test]
    public void An_unreachable_peer_is_not_rescued_by_announcing_that_it_is_fine()
    {
        var (registry, intents) = Deployment(TwoRegions());

        Assert.That(registry.StatusOf("ru-b"), Is.EqualTo(RegionStatus.Connecting), "nothing is listening there");

        intents.Record(new RegionIntentAnnouncement("ru-b", RegionIntent.Active, DateTimeOffset.UtcNow));

        Assert.Multiple(() =>
        {
            Assert.That(registry.StatusOf("ru-b"), Is.EqualTo(RegionStatus.Connecting));
            Assert.That(registry.TryGetClient("ru-b", out _), Is.False);
            Assert.That(registry.AcceptsNewWork("ru-b"), Is.False);
        });
    }

    /// <summary>The same, for a region no configuration claims.</summary>
    [Test]
    public void An_unknown_region_stays_offline_however_loudly_it_advertises()
    {
        var (registry, intents) = Deployment(OneRegion());

        intents.Record(new RegionIntentAnnouncement("eu-a", RegionIntent.Active, DateTimeOffset.UtcNow));

        Assert.Multiple(() =>
        {
            Assert.That(registry.StatusOf("eu-a"), Is.EqualTo(RegionStatus.Offline));
            Assert.That(registry.TryGetClient("eu-a", out _), Is.False);
            Assert.That(() => registry.GetClient("eu-a"), Throws.TypeOf<RegionUnavailableException>());
        });
    }

    /// <summary>
    /// A drain announced about an unreachable region does not upgrade it to "reachable but busy".
    /// </summary>
    /// <remarks>
    /// The same rule in the other direction: draining outranks connecting, and taking the minimum is
    /// what keeps a region that is not answering from being described as one that is.
    /// </remarks>
    [Test]
    public void A_drain_announced_about_an_unreachable_region_does_not_raise_it_either()
    {
        var (registry, intents) = Deployment(TwoRegions());

        intents.Record(new RegionIntentAnnouncement("ru-b", RegionIntent.Draining, DateTimeOffset.UtcNow));

        Assert.That(registry.StatusOf("ru-b"), Is.EqualTo(RegionStatus.Connecting));
    }

    /// <summary>A registry with nothing to listen to behaves as it did before there was a channel.</summary>
    [Test]
    public void Without_anything_to_listen_to_every_region_is_taken_to_be_active()
    {
        var registry = new ArgonRegionRegistry(Options.Create(OneRegion()), Host(),
            NullLogger<ArgonRegionRegistry>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Intent, Is.EqualTo(RegionIntent.Active));
            Assert.That(registry.StatusOf("ru-a"), Is.EqualTo(RegionStatus.Online));
            Assert.That(registry.StatusOf("eu-a"), Is.EqualTo(RegionStatus.Offline));
        });
    }
}
