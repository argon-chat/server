namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Aegis;
using Argon.Features.Clustering;

/// <summary>
/// Whether the browser will show the operator step-up the session it is being asked about.
/// </summary>
/// <remarks>
/// <para>The step-up runs on a host of its own — requiring a client certificate is a per-host TLS
/// setting — and a session cookie with no <c>Domain</c> belongs to the host that issued it. Put those
/// together and the browser sends nothing to the step-up, which answers <c>not_authenticated</c>
/// before it has looked at the card. Everything in the log is about sessions and none of it is about
/// the certificate, so the reading is that smart cards are broken.</para>
///
/// <para>It happened by a deployment moving to a configuration blob that had never carried the
/// session section: every value in it became a default, the cookie lost its domain, and nothing
/// reported anything, because an absent section is not a wrong one.</para>
/// </remarks>
[TestFixture]
public class OperatorStepUpSessionTests
{
    private static IReadOnlyList<ClusterDiagnostic> Validate(string? cookieDomain)
        => FeatureConfigurationValidator
          .Validate(ConfigurationFixtures.Role<StepUpOptionsRole>(),
                    ConfigurationFixtures.From(
                        ($"{StepUpOptionsFeature.Section}:CertificateHeader", "X-Forwarded-Tls-Client-Cert"),
                        ($"{StepUpOptionsFeature.Section}:VerificationLifetime", "00:10:00"),
                        ($"{AegisSessionOptions.SectionName}:CookieDomain", cookieDomain)))
          .Diagnostics;

    /// <summary>The shape production was left in: no session section at all.</summary>
    [Test]
    public void A_host_only_session_cookie_is_refused_where_the_step_up_runs()
    {
        var diagnostics = Validate(null);

        Assert.That(diagnostics.Select(d => d.Message), Has.Some.Contains("CookieDomain"),
            "the step-up was accepted silently with a session cookie it can never receive, which is "
          + "the whole of the outage: the certificate is never reached");
    }

    [Test]
    public void A_cookie_covering_both_hosts_is_accepted()
        => Assert.That(Validate(".argon.gl").Select(d => d.Message),
            Has.None.Contains("CookieDomain"));

    /// <summary>
    /// The narrower domain works too, and is what a deployment should prefer.
    /// </summary>
    /// <remarks>
    /// A <c>Domain</c> attribute already covers subdomains, so naming the widget's own host reaches
    /// the step-up beside it without handing the identity server's session to every other host in the
    /// zone.
    /// </remarks>
    [Test]
    public void The_narrowest_domain_that_reaches_the_step_up_is_accepted()
        => Assert.That(Validate("aegis.argon.gl").Select(d => d.Message),
            Has.None.Contains("CookieDomain"));
}
