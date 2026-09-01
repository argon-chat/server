namespace Argon.Features.Sentry;

using Argon.Features.Clustering;

/// <summary>
/// Error reporting. Named to keep out of the way of Sentry's own <c>SentryOptions</c>, which this
/// configures rather than replaces.
/// </summary>
/// <remarks>
/// <para>Everything here is bound from the <c>Sentry</c> section, and so is everything Sentry's own
/// SDK understands — <c>Sentry.AspNetCore</c> binds that same section into <c>SentryAspNetCoreOptions</c>
/// by convention. So a knob this class does not model is still reachable from <c>appsettings.json</c>
/// by its Sentry name; what this class adds is a default Argon has an opinion about, a validation
/// rule, or a setting Sentry has no equivalent for.</para>
///
/// <para>Two settings are read here and applied nowhere near this file: <see cref="TunnelHost"/>
/// and <see cref="TunnelPath"/> belong to the browser tunnel, which is a feature of its own.</para>
/// </remarks>
public sealed class ArgonSentryOptions : IValidatableFeatureOptions
{
    public void Validate(IFeatureConfigurationReport report)
    {
        // Not required: no DSN is how a local run and the test host turn reporting off.
        if (!string.IsNullOrWhiteSpace(Dsn))
            report.RequireUri(Dsn, nameof(Dsn), "https", "http");

        if (Metrics.Enabled)
        {
            report.Require(Metrics.Meters.Count > 0, $"{nameof(Metrics)}:{nameof(Metrics.Meters)}",
                "is empty, so the bridge would listen to nothing and the setting is a lie");

            report.Require(Metrics.ObservableInterval > TimeSpan.Zero,
                $"{nameof(Metrics)}:{nameof(Metrics.ObservableInterval)}",
                "must be positive; observable instruments are only read when something asks them to");

            report.Prefer(!string.IsNullOrWhiteSpace(Dsn), $"{nameof(Metrics)}:{nameof(Metrics.Enabled)}",
                "is on with no DSN, so measurements are collected and then dropped");
        }
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

    /// <summary>
    /// What fraction of error events are kept. Distinct from <see cref="TracesSampleRate"/>, which
    /// is about performance data; this one throws away errors and is almost never what you want
    /// below 1.
    /// </summary>
    [Range(0d, 1d)]
    public double SampleRate { get; set; } = 1.0;

    /// <summary>
    /// Which deployment this is. Empty lets Sentry work it out from <c>ASPNETCORE_ENVIRONMENT</c>
    /// or <c>SENTRY_ENVIRONMENT</c>, which is usually right.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// Which build this is. Empty means the running version, which is what makes an event point at
    /// a commit rather than at "production".
    /// </summary>
    public string? Release { get; set; }

    /// <summary>
    /// Send cookies, claims and the caller's IP with events. Off, and worth leaving off: the
    /// requests this server handles carry credentials.
    /// </summary>
    public bool SendDefaultPii { get; set; }

    public bool AttachStacktrace { get; set; } = true;

    public int MaxBreadcrumbs { get; set; } = 100;

    /// <summary>
    /// Send log entries to Sentry as structured logs, not only as breadcrumbs on an event.
    /// </summary>
    /// <remarks>
    /// Off in the SDK by default. Which entries travel is the ordinary logging question and is
    /// answered by <c>Sentry:MinimumEventLevel</c> and <c>Sentry:MinimumBreadcrumbLevel</c>, which
    /// Sentry binds from this same section — turning this on without looking at those sends more
    /// than anyone wants. Serilog is installed as a provider rather than in place of the logging
    /// pipeline, so Sentry's provider sees the same entries it does.
    /// </remarks>
    public bool EnableLogs { get; set; }

    /// <summary>Turning <c>System.Diagnostics.Metrics</c> measurements into Sentry metrics.</summary>
    public SentryMetricsOptions Metrics { get; set; } = new();

    /// <summary>Host the browser tunnels its own events through, so an ad blocker cannot eat them.</summary>
    public string TunnelHost { get; set; } = "sentry.argon.gl";

    /// <summary>Path the tunnel is mapped at.</summary>
    public string TunnelPath { get; set; } = "/k";
}

/// <summary>
/// The <c>System.Diagnostics.Metrics</c> to Sentry bridge.
/// </summary>
/// <remarks>
/// Off by default and named meters only. Everything this process runs on publishes instruments —
/// ASP.NET Core, the runtime, Orleans, EF Core — and forwarding all of them would send Sentry a
/// volume of measurements nobody asked for and then bill for it. The meters worth naming here are
/// the product's own.
/// </remarks>
public sealed class SentryMetricsOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Meter names to forward. A name ending in <c>.</c> or <c>*</c> matches by prefix, so
    /// <c>Microsoft.AspNetCore.*</c> takes the family; anything else must match exactly.
    /// </summary>
    public List<string> Meters { get; set; } =
    [
        "Argon",                 // the product's own instruments
        "Ion",                   // the RPC transport
        "Microsoft.Orleans.*",   // grain calls, activations, the directory, the scheduler
        "Microsoft.AspNetCore.*",// requests, routing, rate limiting, Kestrel
        "System.Runtime",        // GC, threadpool, exceptions
        "System.Net.Http"        // what this process calls out to
    ];

    /// <summary>
    /// How often observable instruments — gauges and the like — are read.
    /// </summary>
    /// <remarks>
    /// They have no callback of their own: nothing observes an <c>ObservableGauge</c> until someone
    /// calls <c>RecordObservableInstruments</c>, so this interval is the resolution of every gauge
    /// the bridge reports. Counters and histograms are unaffected; those push.
    /// </remarks>
    public TimeSpan ObservableInterval { get; set; } = TimeSpan.FromSeconds(30);
}
