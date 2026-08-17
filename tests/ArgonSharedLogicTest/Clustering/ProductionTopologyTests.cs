namespace ArgonSharedLogicTest.Clustering;

using Argon.Api.Clustering;
using Argon.Features.Clustering;

[TestFixture]
public class ProductionTopologyTests
{
    private static ClusterScanScope Scope()
        => new()
        {
            Assemblies = [typeof(CoreRole).Assembly, typeof(IArgonRole).Assembly]
        };

    private static ArgonClusterCatalog Catalog()
        => ArgonClusterCatalog.Build(Scope());

    [Test]
    public void The_distributed_topology_validates()
    {
        var scope   = Scope();
        var catalog = ArgonClusterCatalog.Build(scope);
        var index   = GrainTypeIndex.Build(scope);

        var report = ClusterValidator.Validate(catalog, catalog.Topologies["distributed"], index,
            new IlGrainGraphScanner(scope, index));

        Assert.That(report.Errors, Is.Empty,
            string.Join(Environment.NewLine, report.Errors.Select(e => e.ToString())));
    }

    [Test]
    public void Every_grain_class_is_hosted_by_exactly_one_role()
    {
        var scope   = Scope();
        var catalog = ArgonClusterCatalog.Build(scope);
        var index   = GrainTypeIndex.Build(scope);

        var hosts = catalog.Roles.Values
           .SelectMany(r => r.HostedGrains.Select(g => (Grain: g, Role: r.Id)))
           .GroupBy(x => x.Grain)
           .ToDictionary(g => g.Key, g => g.Select(x => x.Role).ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(hosts.Count, Is.EqualTo(index.Classes.Count),
                "a grain class exists that no role hosts, or a role hosts something that is not a grain");
            Assert.That(hosts.Where(p => p.Value.Length > 1).Select(p => p.Key.Name), Is.Empty,
                "hosting one grain on several roles is legal — it is how a stateless worker regains " +
                "locality — but the current decomposition assigns each grain exactly once, so a " +
                "duplicate here is a mistake rather than a decision");
        });
    }

    [Test]
    public void No_role_has_a_broken_feature_graph()
    {
        foreach (var role in Catalog().Roles.Values)
            Assert.That(role.Features.HasErrors, Is.False,
                $"role '{role.Id}': {string.Join("; ", role.Features.Diagnostics)}");
    }

    [Test]
    public void Bot_path_token_rewrite_is_configured_before_routing()
    {
        var order = Catalog().Require(ArgonRoleId.BotApi).Features.Ordered.Select(f => f.Name).ToList();

        Assert.That(order, Does.Contain("bot-path-token"));
        Assert.That(order.IndexOf("bot-path-token"), Is.LessThan(order.IndexOf("routing")));
    }

    [Test]
    public void Endpoint_features_come_after_routing()
    {
        var catalog = Catalog();

        Assert.Multiple(() =>
        {
            foreach (var (role, endpoint) in new[]
                     {
                         (ArgonRoleId.BotApi, "bot-api"),
                         (ArgonRoleId.EntryPoint, "ion"),
                         (ArgonRoleId.EntryPoint, "app-hub"),
                         (ArgonRoleId.Admin, "admin-console")
                     })
            {
                var order = catalog.Require(role).Features.Ordered.Select(f => f.Name).ToList();
                Assert.That(order.IndexOf(endpoint), Is.GreaterThan(order.IndexOf("routing")),
                    $"'{endpoint}' maps endpoints, so it must run after the routing spine in role '{role}'");
            }
        });
    }

    [Test]
    public void Transitive_feature_requirements_are_pulled_in()
    {
        var order = Catalog().Require(ArgonRoleId.BotApi).Features.Ordered.Select(f => f.Name).ToList();

        Assert.That(order, Does.Contain("jwt").And.Contain("cache").And.Contain("vault"),
            "BotApiRole declares none of these; they arrive through bot-api -> argon-authorization -> jwt -> vault");
        Assert.That(order.IndexOf("vault"), Is.LessThan(order.IndexOf("jwt")));
        Assert.That(order.IndexOf("jwt"), Is.LessThan(order.IndexOf("argon-authorization")));
    }

    [Test]
    public void Only_the_moderation_role_enables_the_onnx_classifier()
    {
        var owners = Catalog().Roles.Values
           .Where(r => r.Features.Ordered.Any(f => f.Name == "content-moderation"))
           .Select(r => r.Id.Value)
           .ToArray();

        Assert.That(owners, Is.EqualTo(new[] { "moderation" }),
            "the models are resident for the process lifetime; linking the feature anywhere else " +
            "costs that memory in every replica of that role");
    }

    /// <summary>
    /// Composing a role must not force the analysis that reads IL.
    /// </summary>
    /// <remarks>
    /// Resolving a metadata token makes the CLR load the assembly it points at, so a pass that
    /// reads IL during composition loads every heavy dependency in the tree on every start —
    /// measured at the time: 135 assemblies with the ONNX runtime among them, against 77 without.
    /// Starting a role needs the hosted grains and the features and nothing else; only the
    /// diagnostic commands ask for call roots. This is the test that keeps it that way.
    /// </remarks>
    [Test]
    public void Composing_a_role_does_not_force_the_call_graph_analysis()
    {
        foreach (var role in Catalog().Roles.Values)
            Assert.That(role.LazyCallRoots.IsValueCreated, Is.False,
                $"role '{role.Id}' evaluated its call roots during composition");
    }

    [Test]
    public void Asking_for_call_roots_still_produces_them()
    {
        Assert.That(Catalog().Require(ArgonRoleId.EntryPoint).CallRoots, Is.Not.Empty);
    }

    [Test]
    public void Roles_that_host_remindable_grains_declare_reminders()
    {
        var scope = Scope();
        var index = GrainTypeIndex.Build(scope);

        foreach (var role in ArgonClusterCatalog.Build(scope).Roles.Values)
        {
            var remindable = role.HostedGrains.Where(g => index.Info(g)?.IsRemindable is true).Select(g => g.Name).ToArray();
            if (remindable.Length > 0)
                Assert.That(role.UsesReminders, Is.True,
                    $"role '{role.Id}' hosts {string.Join(", ", remindable)}");
        }
    }
}
