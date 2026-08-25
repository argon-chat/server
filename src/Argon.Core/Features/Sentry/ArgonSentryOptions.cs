namespace Argon.Features.Sentry;

using Argon.Features.Clustering;

/// <summary>
/// Error reporting. Named to keep out of the way of Sentry's own <c>SentryOptions</c>, which this
/// configures rather than replaces.
/// </summary>
public sealed class ArgonSentryOptions : IValidatableFeatureOptions
{
    public void Validate(IFeatureConfigurationReport report)
    {
        // Not required: no DSN is how a local run and the test host turn reporting off.
        if (!string.IsNullOrWhiteSpace(Dsn))
            report.RequireUri(Dsn, nameof(Dsn), "https", "http");
    }

    /// <summary>
    /// Where events go. Empty disables reporting, which is the right answer for a local run and for
    /// the test host — hence no <c>[Required]</c>.
    /// </summary>
    public string? Dsn { get; set; }

    public bool Debug               { get; set; } = true;
    public bool AutoSessionTracking { get; set; } = true;

    [Range(0d, 1d)]
    public double TracesSampleRate { get; set; } = 1.0;

    [Range(0d, 1d)]
    public double ProfilesSampleRate { get; set; } = 1.0;

    /// <summary>Host the browser tunnels its own events through, so an ad blocker cannot eat them.</summary>
    public string TunnelHost { get; set; } = "sentry.argon.gl";

    /// <summary>Path the tunnel is mapped at.</summary>
    public string TunnelPath { get; set; } = "/k";
}
