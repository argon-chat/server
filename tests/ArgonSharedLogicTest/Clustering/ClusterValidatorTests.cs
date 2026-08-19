namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;

[TestFixture]
public class ClusterValidatorTests
{
    private static readonly Type[] CoreGrains =
    [
        typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain),
        typeof(OrphanGrain), typeof(StatefulGrain)
    ];

    private static ValidationReport Validate(
        ClusterScanScope          scope,
        string                    topology,
        ClusterValidationOptions? options = null)
    {
        var catalog = ArgonClusterCatalog.Build(scope);
        var index   = GrainTypeIndex.Build(scope);
        var scanner = new IlGrainGraphScanner(scope, index);

        Assert.That(catalog.Topologies.ContainsKey(topology), Is.True,
            $"topology '{topology}' was not discovered; known: {string.Join(", ", catalog.Topologies.Keys)}");

        return ClusterValidator.Validate(catalog, index: index, topology: catalog.Topologies[topology],
            graphSource: scanner, options: options);
    }

    private static IEnumerable<string> Codes(ValidationReport report)
        => report.Diagnostics.Select(d => d.Code);

    // ── the happy path ──────────────────────────────────────────────────────────────────────

    [Test]
    public void A_single_role_hosting_everything_validates_clean()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.Healthy), CoreGrains), "healthy");

        Assert.That(report.Errors, Is.Empty, string.Join(Environment.NewLine, report.Errors));
        Assert.That(report.Warnings, Is.Empty, string.Join(Environment.NewLine, report.Warnings));
        Assert.That(report.IsValid, Is.True);
    }

    [Test]
    public void A_split_that_covers_every_grain_has_no_errors()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.Split), CoreGrains), "split");

        Assert.That(report.Errors, Is.Empty, string.Join(Environment.NewLine, report.Errors));
    }

    // ── E1: a call nothing satisfies ────────────────────────────────────────────────────────

    [Test]
    public void E1_names_a_dead_interface_distinctly_from_an_unhosted_one()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.DeadInterface), typeof(DeltaGrain)), "dead");

        var e1 = report.Errors.Where(d => d.Code == "E1").ToArray();

        Assert.That(e1, Is.Not.Empty, "DeltaGrain calls IGhostGrain, which nothing implements");
        Assert.That(e1.Select(d => d.Message), Has.Some.Contains("dead interface"));
        Assert.That(e1.Select(d => d.Target), Does.Contain(nameof(IGhostGrain)));
    }

    [Test]
    public void E1_fires_when_a_call_leaves_the_topology()
    {
        // 'alpha' alone: nothing hosts IBetaGrain, which AlphaGrain calls.
        var scope   = Scenario.Scope(typeof(Scenario.Split), CoreGrains);
        var catalog = ArgonClusterCatalog.Build(scope);
        var index   = GrainTypeIndex.Build(scope);

        var partial = new TopologyDescriptor
        {
            Name         = "alpha-only",
            TopologyType = typeof(Scenario.Split.Topology),
            Roles        = [new ArgonRoleId("alpha")]
        };

        var report = ClusterValidator.Validate(catalog, partial, index, new IlGrainGraphScanner(scope, index));

        Assert.That(report.Errors.Where(d => d.Code == "E1").Select(d => d.Target),
            Does.Contain(nameof(IBetaGrain)));
    }

    // ── E2: orphan grains ───────────────────────────────────────────────────────────────────

    [Test]
    public void E2_reports_a_grain_no_role_hosts()
    {
        var scope   = Scenario.Scope(typeof(Scenario.DeadInterface), typeof(DeltaGrain), typeof(OrphanGrain));
        var report  = Validate(scope, "dead");

        Assert.That(report.Errors.Where(d => d.Code == "E2").Select(d => d.Target),
            Does.Contain(nameof(OrphanGrain)));
    }

    [Test]
    public void E2_can_be_switched_off()
    {
        var scope  = Scenario.Scope(typeof(Scenario.DeadInterface), typeof(DeltaGrain), typeof(OrphanGrain));
        var report = Validate(scope, "dead", new ClusterValidationOptions { RequireEveryGrainHosted = false });

        Assert.That(Codes(report), Does.Not.Contain("E2"));
    }

    // ── E3/E5/E6 ────────────────────────────────────────────────────────────────────────────

    [Test]
    public void E3_E5_and_E6_catch_the_misconfigured_roles()
    {
        var report = Validate(
            Scenario.Scope(typeof(Scenario.Misconfigured), typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain)),
            "misconfigured");

        Assert.Multiple(() =>
        {
            Assert.That(Codes(report), Does.Contain("E3"), "GammaGrain is IRemindable on a role without reminders");
            Assert.That(Codes(report), Does.Contain("E5"), "bad-silo starts IBetaGrain but does not host it");
            Assert.That(Codes(report), Does.Contain("E6"), "bad-client is a client and cannot host AlphaGrain");
        });
    }

    // ── E10: a client needs a gateway to connect to ─────────────────────────────────────────

    /// <summary>
    /// A topology whose clients have nowhere to connect.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical: it is what shipped. <c>ExposesClusterGateway</c> defaults to false
    /// and no silo role overrode it, so the distributed topology had client roles and no gateway.
    /// Nothing fails at startup — a silo with proxy port 0 simply never appears in a gateway list,
    /// because every provider filters on <c>ProxyPort != 0</c> — and the client retries forever
    /// against an empty list.
    /// </remarks>
    [Test]
    public void E10_fires_when_a_topology_has_clients_and_no_gateway()
    {
        var report = Validate(
            Scenario.Scope(typeof(Scenario.Misconfigured), typeof(AlphaGrain), typeof(BetaGrain), typeof(GammaGrain)),
            "misconfigured");

        Assert.That(Codes(report), Does.Contain("E10"),
            "bad-client is a client role and no silo role in the topology exposes a gateway");
    }

    /// <summary>
    /// Silos alone need no gateway, and saying otherwise would make the rule noise.
    /// </summary>
    /// <remarks>
    /// A gateway exists for clients. A topology of silos talking only to each other has nothing to
    /// connect through and nothing missing.
    /// </remarks>
    [Test]
    public void E10_stays_quiet_for_a_topology_with_no_clients()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.Healthy), CoreGrains), "healthy");

        Assert.That(Codes(report), Does.Not.Contain("E10"));
    }

    // ── E4: storage providers ───────────────────────────────────────────────────────────────

    [Test]
    public void E4_fires_only_once_the_provider_list_is_known()
    {
        var scope = Scenario.Scope(typeof(Scenario.Healthy), CoreGrains);

        var unconfigured = Validate(scope, "healthy");
        var configured   = Validate(scope, "healthy", new ClusterValidationOptions
        {
            StorageProviders = new HashSet<string> { "Default" }
        });

        Assert.Multiple(() =>
        {
            Assert.That(Codes(unconfigured), Does.Not.Contain("E4"),
                "with no provider list there is nothing to check against");
            Assert.That(configured.Errors.Where(d => d.Code == "E4").Select(d => d.Target),
                Does.Contain(nameof(StatefulGrain)));
        });
    }

    // ── E7: dynamic dispatch ────────────────────────────────────────────────────────────────

    [Test]
    public void E7_reports_an_unresolvable_call_site_and_AllowUnresolved_waives_it()
    {
        var scope   = Scenario.Scope(typeof(Scenario.Dynamic), typeof(GammaGrain), typeof(DynamicService));
        var catalog = ArgonClusterCatalog.Build(scope);
        var index   = GrainTypeIndex.Build(scope);
        var scanner = new IlGrainGraphScanner(scope, index);

        ValidationReport Run(string role) => ClusterValidator.Validate(catalog,
            new TopologyDescriptor
            {
                Name         = role,
                TopologyType = typeof(Scenario.Dynamic),
                Roles        = [new ArgonRoleId(role)]
            }, index, scanner);

        Assert.Multiple(() =>
        {
            Assert.That(Codes(Run("dynamic")), Does.Contain("E7"));
            Assert.That(Codes(Run("dynamic-waived")), Does.Not.Contain("E7"),
                "AllowUnresolved<DynamicService> waives the site the reviewer already covered with AddDynamicRef");
        });
    }

    // ── W1/W2: cross-role costs ─────────────────────────────────────────────────────────────

    [Test]
    public void W1_flags_a_stateless_worker_reached_across_a_role_boundary()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.Split), CoreGrains), "split");

        var w1 = report.Warnings.Where(d => d.Code == "W1").ToArray();

        Assert.That(w1.Select(d => d.Target), Does.Contain(nameof(IBetaGrain)));
        Assert.That(w1.Select(d => d.Message), Has.Some.Contains("loses worker locality"));
    }

    [Test]
    public void AcceptRemote_silences_W1_but_leaves_the_hot_edge_visible()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.SplitQuiet), CoreGrains), "split-quiet");

        Assert.Multiple(() =>
        {
            Assert.That(Codes(report), Does.Not.Contain("W1"), "the trade was recorded with a reason");
            Assert.That(Codes(report), Does.Contain("W2"),
                "accepting the worker hop does not make a 4-site edge stop crossing the boundary");
        });
    }

    [Test]
    public void W2_flags_a_heavy_edge_crossing_a_role_boundary()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.Split), CoreGrains), "split");

        var w2 = report.Warnings.Where(d => d.Code == "W2").ToArray();

        Assert.That(w2.Select(d => d.Target), Does.Contain(nameof(AlphaGrain)));
        Assert.That(w2.Select(d => d.Message), Has.Some.Contains("4 call sites"));
    }

    [Test]
    public void W2_respects_the_configured_threshold()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.Split), CoreGrains), "split",
            new ClusterValidationOptions { HotEdgeThreshold = 5 });

        Assert.That(Codes(report), Does.Not.Contain("W2"), "a 4-site edge is below a threshold of 5");
    }

    [Test]
    public void A_co_hosted_heavy_edge_is_not_a_warning()
    {
        var report = Validate(Scenario.Scope(typeof(Scenario.Healthy), CoreGrains), "healthy");

        Assert.That(Codes(report), Does.Not.Contain("W2"),
            "the same 4-site edge is free when both ends live in one role — that is the whole argument " +
            "for replicating a dense component instead of cutting it");
    }
}
