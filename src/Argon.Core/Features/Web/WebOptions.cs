namespace Argon.Features.Web;

using Argon.Features.Clustering;

/// <summary>
/// Listeners and TLS. Prefixed to stay out of the way of ASP.NET's own <c>Kestrel</c> section, which
/// the host still reads for anything not named here.
/// </summary>
public sealed class ArgonKestrelOptions : IValidatableFeatureOptions
{
    /// <summary>
    /// A certificate named but not present is the failure worth catching here: the process starts,
    /// binds nothing on TLS, and looks healthy to everything except the client trying to connect.
    /// </summary>
    public void Validate(IFeatureConfigurationReport report)
    {
        if (UseLocalhostCertificate)
        {
            report.RequireFile(LocalhostCertificatePath, nameof(LocalhostCertificatePath));
            report.Prefer(!UseFileCertificate, nameof(UseFileCertificate),
                "is set together with useLocalhostCertificate; the localhost certificate wins and this is ignored");
        }
        else if (UseFileCertificate)
        {
            report.RequireFile(CertificatePath, nameof(CertificatePath));
            report.RequireFile(CertificateKeyPath, nameof(CertificateKeyPath));
        }

        RequireSomethingToPresent(report);
    }

    /// <summary>
    /// A TLS listener that can answer a connection, whatever name it arrives under.
    /// </summary>
    /// <remarks>
    /// <para><b>This rule exists because production ran without it.</b> A role was configured with
    /// <c>Kestrel:Endpoints:Https</c> carrying an <c>Sni</c> map keyed on its public host and no
    /// default certificate, and nothing above noticed: neither branch fires when this section names
    /// no certificate at all, and the endpoints section is ASP.NET's rather than this feature's, so
    /// it was never read.</para>
    ///
    /// <para>What that produces is the worst shape a misconfiguration can take. The port binds, the
    /// host logs <c>Now listening on https://…</c>, the pod goes ready and stays ready, and every
    /// single request fails — because the proxy in front reaches the pod by its <i>service</i> name,
    /// never the public one, so the SNI it sends matches nothing and Kestrel has no certificate to
    /// present. It closes the connection without an alert, which the proxy reports as <c>EOF</c> and
    /// the caller sees as <c>502</c>. Nothing in the application logs a word.</para>
    ///
    /// <para>Only fires when an SNI map exists. A role with no HTTPS endpoints is a silo or a pod
    /// behind a proxy that terminates TLS itself, and neither has anything to present.</para>
    /// </remarks>
    private void RequireSomethingToPresent(IFeatureConfigurationReport report)
    {
        if (UseLocalhostCertificate || UseFileCertificate)
            return;

        var https = report.Read<KestrelHttpsSection>("Kestrel:Endpoints:Https");

        if (https.Sni.Count == 0 || https.Certificate is not null)
            return;

        report.Invalid(
            $"Kestrel:Endpoints:Https names certificates for {string.Join(", ", https.Sni.Keys.ToArray())} and no " +
            "default one, and this section configures none either — so a connection whose SNI is not " +
            "one of those names is answered with no certificate at all and dropped mid-handshake. " +
            "The proxy in front connects by the service name rather than the public host, which " +
            "makes that every request. Set useFileCertificate here, or add a default " +
            "Kestrel:Endpoints:Https:Certificate");
    }

    /// <summary>
    /// The port to bind. Unset leaves it to ASP.NET — <c>ASPNETCORE_URLS</c>,
    /// <c>ASPNETCORE_HTTP_PORTS</c>, or the launch profile — which is what a container image wants.
    /// Set it and this is the port, whether or not TLS is configured.
    /// </summary>
    [Range(1, 65535)]
    public int? Port { get; set; }

    /// <summary>Used when a certificate is configured but no port is.</summary>
    public const int DefaultTlsPort = 5002;

    /// <summary>
    /// Serve HTTPS from a local development certificate. Replaces the <c>USE_LOCALHOST_CERTS</c>
    /// variable.
    /// </summary>
    public bool UseLocalhostCertificate { get; set; }

    public string LocalhostCertificatePath     { get; set; } = "localhost.pfx";
    public string LocalhostCertificatePassword { get; set; } = "changeit";

    /// <summary>
    /// Read the certificate off disk instead of letting the platform present it. Replaces
    /// <c>LEGACY_CERT_LOADING</c>; a cluster fronted by a terminating proxy leaves this off.
    /// </summary>
    public bool UseFileCertificate { get; set; }

    public string CertificatePath    { get; set; } = "/etc/tls/tls.crt";
    public string CertificateKeyPath { get; set; } = "/etc/tls/tls.key";
}

/// <summary>
/// As much of ASP.NET's own <c>Kestrel:Endpoints:Https</c> as a rule here needs to read.
/// </summary>
/// <remarks>
/// Not a settings model — nothing binds these to configure anything, and the host reads that section
/// itself. It exists so <see cref="ArgonKestrelOptions"/> can ask the one question the host will not
/// answer until a client is already waiting on the handshake: is there a certificate for a name that
/// is not in the map.
/// </remarks>
internal sealed class KestrelHttpsSection
{
    /// <summary>The certificate used when the SNI map has no entry for the name asked for.</summary>
    public KestrelCertificateSection? Certificate { get; set; }

    public Dictionary<string, KestrelSniSection> Sni { get; set; } = [];
}

internal sealed class KestrelSniSection
{
    public KestrelCertificateSection? Certificate { get; set; }
}

internal sealed class KestrelCertificateSection
{
    public string? Path    { get; set; }
    public string? KeyPath { get; set; }
    public string? Subject { get; set; }
}

/// <summary>Which origins the browser is allowed to call this host from.</summary>
public sealed class ArgonCorsOptions : IValidatableFeatureOptions
{
    /// <summary>
    /// Origins as <c>scheme://host</c>. <c>https://argon.gl</c> also admits every subdomain of
    /// <c>argon.gl</c>, which is how the first-party apps are let in without listing each one.
    /// Empty means the compiled-in list, which is the first-party set.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// An origin that does not parse is silently dropped by the matcher, which turns a typo into a
    /// browser error nobody can explain. Report it here instead.
    /// </summary>
    public void Validate(IFeatureConfigurationReport report)
    {
        foreach (var origin in AllowedOrigins.Where(o => !Uri.TryCreate(o, UriKind.Absolute, out _)))
            report.Require(false, nameof(AllowedOrigins),
                $"contains '{origin}', which is not an absolute origin and will never match");
    }
}

public sealed class ArgonWebSocketOptions
{
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a socket may go unanswered before it is dropped. The default is infinite: a client on
    /// a bad mobile link should survive a long stall, and the keep-alive interval is what actually
    /// detects a dead peer.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.MaxValue;
}

/// <summary>The SignalR event hub the first-party clients hold open.</summary>
public sealed class AppHubOptions
{
    public string Path { get; set; } = "/w";
}

/// <summary>Endpoints the platform around the process talks to, rather than any user.</summary>
public sealed class HostHooksOptions : IValidatableFeatureOptions
{
    /// <summary>Serve the build version at <c>/</c>. Useful; also tells anyone who asks what is deployed.</summary>
    public bool ExposeVersion { get; set; } = true;

    /// <summary>
    /// Accept <c>GET /internal/shutdown</c> from loopback and begin a graceful stop. This is what a
    /// Kubernetes preStop hook calls; without it a pod is stopped by signal instead.
    /// </summary>
    public bool PreStopHook { get; set; } = true;

    /// <summary>
    /// How long a client role stays up, already reporting not-ready, before it stops.
    /// </summary>
    /// <remarks>
    /// <para>Kubernetes removes a pod from its Service by noticing the readiness probe fail and then
    /// reprogramming every node, and none of that is instant: the probe's own period, then the
    /// EndpointSlice update, then kube-proxy or the ingress catching up. Stopping before that lands
    /// means connections are still being routed to a process on its way out — which is the failure
    /// this wait exists to remove, so it has to be longer than that whole chain rather than longer
    /// than any one link.</para>
    ///
    /// <para>Twenty seconds covers a five-second probe period with room for propagation. It is spent
    /// on every deployment of every client pod, so it is also a floor on how long a rollout takes,
    /// and <c>terminationGracePeriodSeconds</c> has to exceed it plus the host's own shutdown
    /// timeout. Zero disables the wait, which is what a single-instance deployment with no Service
    /// in front of it wants.</para>
    ///
    /// <para>Nothing on a silo reads it — a silo's pre-stop hook blocks on the drain instead, and the
    /// drain flipping readiness is what starts the same clock there.</para>
    /// </remarks>
    public TimeSpan PreStopLeadTime { get; set; } = TimeSpan.FromSeconds(20);

    public void Validate(IFeatureConfigurationReport report)
        => report.RequireRange(PreStopLeadTime, TimeSpan.Zero, TimeSpan.FromMinutes(2),
            nameof(PreStopLeadTime));
}
