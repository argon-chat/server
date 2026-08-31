namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;

/// <summary>
/// Whether a TLS listener will have a certificate to present when a client actually arrives.
/// </summary>
/// <remarks>
/// <para>Written after a production outage that every other check passed. The role's configuration
/// carried an <c>Sni</c> map keyed on its public hostname and no default certificate; the port bound,
/// the host logged <c>Now listening on https://…</c>, the pod went ready and stayed ready for hours,
/// and every request returned <c>502</c>. The proxy in front reaches a pod by its service name, so
/// the SNI it offers is never the public one — Kestrel found no entry, had no default, and closed
/// each connection without so much as a TLS alert. Nothing was logged on either side beyond the
/// proxy's <c>EOF</c>.</para>
///
/// <para>What makes it worth a rule rather than a runbook entry is that the misconfiguration is
/// invisible from inside the process. There is no request to fail, no exception to catch, and no
/// health check that would notice: the failure happens below HTTP, before the application is
/// involved at all. Configuration time is the only moment anything can see it.</para>
/// </remarks>
[TestFixture]
public class KestrelCertificateRulesTests
{
    private const string SniHost = "Kestrel:Endpoints:Https:Sni:aegis.argon.gl:Certificate";

    private static IReadOnlyList<ClusterDiagnostic> Validate(params (string Key, string? Value)[] settings)
        => FeatureConfigurationValidator
          .Validate(ConfigurationFixtures.Role<ListenerRole>(), ConfigurationFixtures.From(settings))
          .Diagnostics;

    /// <summary>The shape that was in production, exactly as it was.</summary>
    [Test]
    public void An_sni_map_with_no_default_certificate_is_refused()
    {
        var diagnostics = Validate(
            ($"{SniHost}:Path", "/etc/tls/tls.crt"),
            ($"{SniHost}:KeyPath", "/etc/tls/tls.key"),
            ("Kestrel:Endpoints:Https:Url", "https://*:5002"));

        Assert.That(diagnostics, Is.Not.Empty,
            "a listener that can only answer one hostname passed validation, which is how it reached "
          + "production and answered none");

        Assert.That(diagnostics.Any(d => d.Message.Contains("aegis.argon.gl")), Is.True,
            "the finding has to name the hosts it can answer for, because the whole confusion is that "
          + "the certificate is present and simply never selected");
    }

    /// <summary>A default beside the map is the fix that keeps the map.</summary>
    [Test]
    public void A_default_certificate_beside_the_map_is_accepted()
    {
        var diagnostics = Validate(
            ($"{SniHost}:Path", "/etc/tls/tls.crt"),
            ("Kestrel:Endpoints:Https:Certificate:Path", "/etc/tls/tls.crt"),
            ("Kestrel:Endpoints:Https:Certificate:KeyPath", "/etc/tls/tls.key"));

        Assert.That(diagnostics, Is.Empty);
    }

    /// <summary>And so is the shape every working role uses.</summary>
    [Test]
    public void The_features_own_certificate_covers_the_map()
    {
        var certificate = Path.GetTempFileName();
        var key         = Path.GetTempFileName();

        try
        {
            var diagnostics = Validate(
                ($"{SniHost}:Path", "/etc/tls/tls.crt"),
                ("Kestrel:Argon:Port", "5002"),
                ("Kestrel:Argon:UseFileCertificate", "true"),
                ("Kestrel:Argon:CertificatePath", certificate),
                ("Kestrel:Argon:CertificateKeyPath", key));

            // One certificate for every connection, whatever name it asks for — which is why the
            // roles configured this way were the ones that kept working.
            Assert.That(diagnostics, Is.Empty);
        }
        finally
        {
            File.Delete(certificate);
            File.Delete(key);
        }
    }

    /// <summary>
    /// A role with no HTTPS endpoints is not asked for a certificate.
    /// </summary>
    /// <remarks>
    /// Silos and any pod whose proxy terminates TLS itself serve plain HTTP and have nothing to
    /// present. A rule that demanded one from them would fail every deploy in the cluster.
    /// </remarks>
    [Test]
    public void A_role_that_serves_no_tls_is_left_alone()
        => Assert.That(Validate(("Kestrel:Argon:Port", "5002")), Is.Empty);
}
