namespace ArgonSharedLogicTest.Clustering;

using System.Reflection;
using Argon.Features.Clustering;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Which role answers on which URL.
/// </summary>
/// <remarks>
/// <para><c>AddControllers()</c> takes every <see cref="ControllerBase"/> in the loaded assemblies,
/// and the product is one assembly graph — so every role that mapped controllers served all of them.
/// The identity server's <c>api/auth</c>, <c>api/users</c> and <c>api/email</c> were routed on the
/// entrypoint role, which enables no Aegis feature and therefore registers none of the services
/// those controllers are constructed from. The endpoints existed and could only fail: routing found
/// them, activation did not, and the caller got a 500 from a URL that was never meant to be
/// there.</para>
///
/// <para>Ownership is declared now, and these are the two ways declaring it can still go wrong:
/// claiming a controller twice, which puts it on roles nobody intended, and — the case that is
/// invisible without a test — claiming it correctly and having it routed somewhere else anyway.</para>
/// </remarks>
[TestFixture]
public class ControllerOwnershipTests
{
    /// <summary>Forces the product assembly in, so the catalog is the real one. See the sibling fixture.</summary>
    private static readonly Assembly Product = typeof(Argon.Api.Clustering.EntryPointRole).Assembly;

    private static readonly ArgonClusterCatalog Catalog = ArgonClusterCatalog.Build();

    private static IEnumerable<FeatureDefinition> AllFeatures
        => Catalog.Roles.Values.SelectMany(role => role.Features.Ordered).DistinctBy(f => f.FeatureType);

    private static IEnumerable<Type> AllControllers
        => Catalog.Scope.Types()
           .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
           .Where(t => typeof(ControllerBase).IsAssignableFrom(t));

    /// <summary>
    /// One owner each — no controller unclaimed, none claimed twice.
    /// </summary>
    /// <remarks>
    /// The validator reports the unclaimed half as E11 at startup. The other half it cannot see: two
    /// features claiming the same controller is not an error anywhere, it just quietly routes the
    /// thing on the union of their roles, which is the state this whole mechanism exists to end.
    /// </remarks>
    [Test]
    public void Every_controller_is_claimed_by_exactly_one_feature()
    {
        Assert.That(Product, Is.Not.Null);

        var claims = AllFeatures
           .SelectMany(feature => feature.Controllers.Select(c => (Controller: c, Feature: feature.Name)))
           .ToArray();

        var unclaimed = AllControllers
           .Where(c => claims.All(x => x.Controller != c))
           .Select(c => c.Name)
           .OrderBy(n => n)
           .ToArray();

        Assert.That(unclaimed, Is.Empty,
            "no feature claims these controllers, so no role routes them at all: "
          + string.Join(", ", unclaimed));

        var twiceClaimed = claims
           .GroupBy(x => x.Controller)
           .Where(g => g.Select(x => x.Feature).Distinct().Count() > 1)
           .Select(g => $"{g.Key.Name} ({string.Join(", ", g.Select(x => x.Feature).Distinct())})")
           .ToArray();

        Assert.That(twiceClaimed, Is.Empty,
            "these controllers are claimed by more than one feature, so they are routed on the union "
          + "of the roles enabling them rather than where they were meant to be: "
          + string.Join("; ", twiceClaimed));
    }

    /// <summary>
    /// The identity server's endpoints are the identity server's.
    /// </summary>
    /// <remarks>
    /// Named rather than derived from a rule, because this is the regression itself: these four were
    /// answering on the public entrypoint. A general rule would be that a role only routes what it
    /// can construct — true, and already enforced where it matters — but it would pass just as well
    /// on the day someone gives entrypoint the Aegis feature by accident, which is the thing worth
    /// noticing.
    /// </remarks>
    [Test]
    public void The_identity_servers_controllers_are_routed_only_where_the_identity_server_runs()
    {
        var aegisControllers = AllControllers
           .Where(c => c.Namespace?.Contains("Aegis", StringComparison.Ordinal) == true)
           .ToArray();

        Assert.That(aegisControllers, Is.Not.Empty, "the fixture found no Aegis controllers to check");

        foreach (var (roleId, role) in Catalog.Roles.OrderBy(r => r.Key.Value))
        {
            var routed = role.Features.Ordered
               .SelectMany(feature => feature.Controllers)
               .Intersect(aegisControllers)
               .ToArray();

            var runsTheIdentityServer = role.Features.Ordered.Any(f => f.Name == "aegis");

            if (runsTheIdentityServer)
                Assert.That(routed, Is.Not.Empty,
                    $"role '{roleId.Value}' runs the identity server and routes none of its controllers");
            else
                Assert.That(routed, Is.Empty,
                    $"role '{roleId.Value}' does not run the identity server, yet routes "
                  + string.Join(", ", routed.Select(c => c.Name))
                  + " — every request to those fails on a service the role does not register");
        }
    }
}
