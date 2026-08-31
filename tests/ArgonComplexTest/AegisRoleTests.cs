namespace ArgonComplexTest;

using Argon.Entities;
using Argon.Features.Aegis;
using Argon.Features.Clustering;
using Argon.Features.Jwt;
using ArgonComplexTest.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Orleans;

/// <summary>
/// What the identity server is, before anything signs into it.
/// </summary>
/// <remarks>
/// Every property here is one that fails silently. A database connection creeping onto the role
/// changes nothing until somebody writes a query against it; a controller from another role being
/// mapped changes nothing until somebody finds the route; the provider still starts if it signs
/// tokens with the throwaway key it was seeded with, and the tokens still look like tokens — they
/// just cannot be verified by anything holding Argon's public key, and only after a restart, because
/// the throwaway key is regenerated each time.
/// </remarks>
[TestFixture]
public class AegisRoleTests
{
    private RoleHost host = null!;

    [OneTimeSetUp]
    public void StartTheIdentityServer()
        => host = new RoleHost(ArgonTestEnvironment.Instance.Host.Settings, ArgonRoleId.Aegis,
            siloPort: 0, ArgonClusterEndpoints.DefaultClusterId);

    [OneTimeTearDown]
    public async Task StopTheIdentityServer()
        => await host.DisposeAsync();

    [Test, CancelAfter(300_000)]
    public void It_is_a_client_that_hosts_no_grains_and_opens_no_connection()
    {
        var role = host.Services.GetRequiredService<RoleDescriptor>();

        Assert.Multiple(() =>
        {
            Assert.That(role.Id, Is.EqualTo(ArgonRoleId.Aegis));
            Assert.That(role.IsClient, Is.True);
            Assert.That(role.HostedGrains, Is.Empty);

            Assert.That(host.Services.GetService<IClusterClient>(), Is.Not.Null,
                "everything it reads is a grain call, so it must be able to make one");

            // The reason the lookups went into IIdentityDirectoryGrain rather than staying a
            // repository: this role faces the whole internet, and what it cannot reach it cannot leak.
            Assert.That(host.Services.GetService<IDbContextFactory<ApplicationDbContext>>(), Is.Null,
                "the identity server talks to grains, not to Postgres");

            Assert.That(role.Features.Ordered.Select(f => f.Name), Does.Not.Contain("database"));
            Assert.That(role.Features.Ordered.Select(f => f.Name),
                Does.Contain("aegis").And.Contain("openid").And.Contain("aegis-session"));
        });
    }

    /// <summary>
    /// MVC discovers controllers from the whole assembly, so without narrowing this role would also
    /// answer the entry point's webhook and file routes — endpoints whose services it never
    /// registered, on the host most exposed to the internet.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public void It_maps_its_own_endpoints_and_nobody_elses()
    {
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
           .OfType<RouteEndpoint>()
           .Select(e => "/" + e.RoutePattern.RawText?.TrimStart('/'))
           .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(routes, Does.Contain("/api/auth/scenario"));
            Assert.That(routes, Does.Contain("/api/auth/session/check"));
            Assert.That(routes, Does.Contain("/api/auth/operator/verify"));
            Assert.That(routes, Does.Contain("/connect/token"));

            Assert.That(routes, Does.Not.Contain("/api/xsolla/webhook"));
            Assert.That(routes.Where(r => r.StartsWith("/api/files")), Is.Empty);
        });
    }

    /// <summary>
    /// The provider is registered with throwaway keys because Vault cannot be reached while the
    /// container is still being built; <see cref="AegisSigningKeys"/> is what swaps in the real ones
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// Checked by algorithm rather than by identity, because the signing key is thread-local — the
    /// instance post-configuration stored is not the instance this thread would be handed. The
    /// algorithm comes from the configured key material, so it is the thing an ephemeral RSA
    /// placeholder could not accidentally match.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public void The_provider_signs_with_argons_keys_rather_than_the_placeholders()
    {
        var options = host.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;
        var signing = host.Services.GetRequiredService<WrapperForSignKey>();

        Assert.Multiple(() =>
        {
            Assert.That(options.SigningCredentials, Has.Count.EqualTo(1),
                "the placeholder must have been replaced, not added to");
            Assert.That(options.SigningCredentials[0].Algorithm, Is.EqualTo(signing.Algorithm));

            Assert.That(options.EncryptionCredentials, Has.Count.EqualTo(1));

            // Access tokens are verified by resource servers as ordinary signed JWTs; encrypting them
            // would make the claims unreadable to every one of them.
            Assert.That(options.DisableAccessTokenEncryption, Is.True);
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task Every_response_carries_the_browser_hardening_headers()
    {
        using var client   = AegisClient.For(host);
        using var response = await client.GetAsync("/api/auth/session/check");

        var headers = response.Headers;

        Assert.Multiple(() =>
        {
            Assert.That(headers.GetValues("X-Content-Type-Options"), Does.Contain("nosniff"));
            Assert.That(headers.GetValues("X-Frame-Options"), Does.Contain("DENY"));
            Assert.That(headers.Contains("Content-Security-Policy"), Is.True);
        });
    }

    /// <summary>
    /// A path on the exclusion list is served without a policy, and its neighbours still get one.
    /// </summary>
    /// <remarks>
    /// <para>The exclusion is for anything mounted here whose payloads are not documents — a policy
    /// on those is never anything but an obstacle. Nothing qualifies on this role today, so
    /// <c>CspExcludedPaths</c> ships empty and the fixture configures <c>/k</c> itself. That is
    /// deliberate: an earlier version of this test read <c>/k</c> out of the shipped default, so
    /// emptying the default turned a test of the mechanism into a test of a constant, and it began
    /// failing on a change that had broken nothing.</para>
    ///
    /// <para>Both halves are asserted together because either alone is satisfied by a bug. An
    /// exclusion that swallowed every path would pass the first; one that never matched would pass
    /// the second.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task An_excluded_path_is_served_without_a_content_security_policy()
    {
        using var client = AegisClient.For(host);

        bool HasPolicy(HttpResponseMessage response)
        {
            using (response)
                return response.Headers.Contains("Content-Security-Policy");
        }

        var onExcluded = HasPolicy(await client.GetAsync("/k"));
        var onOrdinary = HasPolicy(await client.GetAsync("/api/auth/session/check"));

        Assert.Multiple(() =>
        {
            Assert.That(onExcluded, Is.False, "a path on the exclusion list was given a policy anyway");
            Assert.That(onOrdinary, Is.True,
                "the exclusion reached a path that is not on the list, which would leave the widget "
              + "itself unprotected");
        });
    }
}
