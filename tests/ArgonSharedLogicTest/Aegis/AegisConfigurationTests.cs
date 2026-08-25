namespace ArgonSharedLogicTest.Aegis;

using Argon.Api.Clustering;
using Argon.Features.Aegis;
using Argon.Features.Clustering;
using Microsoft.Extensions.Configuration;

/// <summary>
/// The identity server's settings, checked before it can serve a single sign-in.
/// </summary>
/// <remarks>
/// This is the role the whole internet reaches, and most of what can go wrong with it goes wrong
/// quietly: a session that outlives its cookie, a step-up window long enough to be reusable, a
/// provider with no scopes to grant. None of those fail a request in a way anyone would notice —
/// they fail the property the setting existed to hold. So they fail the start-up instead.
/// </remarks>
[TestFixture]
public class AegisConfigurationTests
{
    private static RoleDescriptor Aegis()
        => ArgonClusterCatalog.Build(new ClusterScanScope
        {
            Assemblies = [typeof(AegisRole).Assembly, typeof(IArgonRole).Assembly]
        }).Require(ArgonRoleId.Aegis);

    /// <summary>
    /// The shipped <c>appsettings.json</c> with the values under test layered over it.
    /// </summary>
    /// <remarks>
    /// On the shipped file rather than on nothing, because the role enables more than the identity
    /// server's own features — Redis, JWT and the rest all have rules of their own, and a bare
    /// configuration would fail on those instead, which says nothing about the rule being exercised.
    /// </remarks>
    private static FeatureConfigurationReportSet Validate(params (string Key, string? Value)[] values)
        => FeatureConfigurationValidator.Validate(Aegis(),
            new ConfigurationBuilder()
               .AddJsonFile(Path.Combine(TestContext.CurrentContext.TestDirectory, "appsettings.json"), optional: false)
               .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
               .Build());

    /// <summary>
    /// Every one of these has a default that works, so a section a deployment never writes must not
    /// be the thing that stops the role starting.
    /// </summary>
    [Test]
    public void An_absent_section_is_not_an_error()
    {
        var report = Validate(("Aegis:host", "aegis.argon.gl"));

        Assert.That(report.IsValid, Is.True,
            string.Join(Environment.NewLine, report.Errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// The issuer, the audiences and the redirects this server emits are all built from the request's
    /// host, so leaving it unpinned is worth saying out loud — but it is what a developer running on
    /// localhost wants, so it is a warning rather than a refusal.
    /// </summary>
    [Test]
    public void An_unpinned_host_is_a_warning_and_not_a_refusal()
    {
        var report = Validate(("Aegis:host", ""));

        Assert.Multiple(() =>
        {
            Assert.That(report.IsValid, Is.True);
            Assert.That(report.Warnings.Select(w => w.Target), Does.Contain("Aegis:host"));
        });
    }

    [Test]
    public void A_static_root_that_does_not_exist_is_refused()
    {
        var report = Validate(
            ("Aegis:host", "aegis.argon.gl"),
            ("Aegis:staticRoot", Path.Combine(Path.GetTempPath(), "no-widget-here-" + Guid.NewGuid().ToString("N"))));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("Aegis:staticRoot"));
    }

    /// <summary>
    /// The cookie's own expiry is what a returning browser is measured against; a ticket that
    /// outlives it is a session silently thrown away while it was still good.
    /// </summary>
    [Test]
    public void A_session_may_not_outlive_the_cookie_carrying_it()
    {
        var report = Validate(
            ("AegisSession:lifetime", "7.00:00:00"),
            ("AegisSession:rememberFor", "1.00:00:00"));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("AegisSession:rememberFor"));
    }

    /// <summary>
    /// A request carries its scopes space-separated, so a scope with a space in it is one no client
    /// could ever ask for.
    /// </summary>
    [Test]
    public void A_scope_that_could_never_be_asked_for_is_refused()
    {
        var report = Validate(("OpenId:scopes:0", "user read"));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("OpenId:scopes"));
    }

    /// <summary>
    /// Configuration appends to the shipped list rather than replacing it, which is the trap: a
    /// deployment writing out the scopes it wants gets them twice and no other sign of it.
    /// </summary>
    [Test]
    public void Listing_a_scope_twice_is_refused()
    {
        var report = Validate(("OpenId:scopes:0", ArgonScopes.Email));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("OpenId:scopes"));
    }

    [Test]
    public void An_endpoint_that_is_not_a_rooted_path_is_refused()
    {
        var report = Validate(("OpenId:tokenEndpoint", "connect/token"));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("OpenId:tokenEndpoint"));
    }

    /// <summary>
    /// The widget cannot keep a secret, so an intercepted authorization code would otherwise be
    /// redeemable by whoever intercepted it.
    /// </summary>
    [Test]
    public void Turning_off_proof_key_is_a_warning()
    {
        var report = Validate(("OpenId:requireProofKeyForCodeExchange", "false"));

        Assert.Multiple(() =>
        {
            Assert.That(report.IsValid, Is.True);
            Assert.That(report.Warnings.Select(w => w.Target),
                Does.Contain("OpenId:requireProofKeyForCodeExchange"));
        });
    }

    /// <summary>
    /// A step-up window measured in hours stops being a step-up: the point is that it covers the gap
    /// between touching the key and finishing the flow in front of it.
    /// </summary>
    [Test]
    public void An_operator_step_up_that_lasts_all_day_is_refused()
    {
        var report = Validate(("OperatorMutualTls:verificationLifetime", "08:00:00"));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("OperatorMutualTls:verificationLifetime"));
    }

    /// <summary>
    /// Forwarded headers are attacker-written until a trusted hop is named. With no hop named, every
    /// request would appear to come from the proxy — which is safe, and also means the anonymous rate
    /// limits count every visitor as the same one.
    /// </summary>
    [Test]
    public void Trusting_no_proxy_at_all_is_refused()
    {
        var report = Validate(("ForwardedHeaders:knownNetworks:0", null));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("ForwardedHeaders:knownNetworks"));
    }

    [Test]
    public void A_known_network_that_is_not_a_cidr_range_is_refused()
    {
        var report = Validate(("ForwardedHeaders:knownNetworks:0", "10.42.0.0"));

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("ForwardedHeaders:knownNetworks"));
    }
}
