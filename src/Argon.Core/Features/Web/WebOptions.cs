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
