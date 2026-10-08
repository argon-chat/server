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
            foreach (var meter in Metrics.Meters)
                report.Require(!string.IsNullOrWhiteSpace(meter), $"{nameof(Metrics)}:{nameof(Metrics.Meters)}",
                    "contains a blank name, which matches nothing and is more likely a stray comma");

            foreach (var instrument in Metrics.DeniedInstruments)
                report.Require(!string.IsNullOrWhiteSpace(instrument),
                    $"{nameof(Metrics)}:{nameof(Metrics.DeniedInstruments)}",
                    "contains a blank name, which denies nothing and is more likely a stray comma");

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
/// <para>Off by default, named meters only, and — the part that took a hundred gigabytes to
/// learn — <b>bounded by shape rather than by list</b>.</para>
///
/// <para>The cost model is the whole story, and it does not transfer from the OTLP exporter next
/// door. OTLP aggregates a meter into a time series and exports a point per interval, so an
/// instrument's cost there is its <i>cardinality</i>. Sentry stores a row per measurement, so an
/// instrument's cost there is its <i>rate</i> — and the row is about a kilobyte whatever is in it,
/// four fifths of which is the SDK's own release, environment, SDK name and internal timestamps.
/// Trimming attributes therefore buys nothing; only sending fewer rows does.</para>
///
/// <para>The first fix here was to cut the meter list back to the product's own, which worked and
/// was the wrong shape: the meters are wanted. What the measurements actually look like is that
/// cardinality is tiny — the largest producer, <c>aspnetcore.memory_pool.pooled</c>, was fifty
/// thousand rows a quarter hour across <i>one</i> series — so the volume is repetition, not
/// variety, and repetition compresses without losing anything:</para>
/// <list type="bullet">
///   <item><description><b>Counters</b> are summed per series and sent once per window. Lossless:
///     a Sentry counter increments by a delta, so a window's sum is its increment. This alone took
///     the counters, two thirds of everything, down by 95%.</description></item>
///   <item><description><b>Gauges</b> are read once per window already.</description></item>
///   <item><description><b>Histograms</b> cannot be summed — percentiles need the values — so they
///     are sampled instead, at a rate the bridge sets itself from the rate it observes. See
///     <see cref="DistributionSamplesPerFlush"/>.</description></item>
/// </list>
///
/// <para>So the meter list is back to the platform's, and <see cref="DeniedInstruments"/> is empty
/// by default. Both are still here and are the right tool for an instrument that is genuinely
/// unwanted rather than merely loud; neither is load-bearing any more.</para>
/// </remarks>
public sealed class SentryMetricsOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The meters the OTLP exporter already forwards, used when configuration names none.
    /// </summary>
    /// <remarks>
    /// The same list, deliberately, so both pipelines show the same instruments and a chart built on
    /// one can be found on the other. What differs is what it costs to say so, and that is handled
    /// by the shape of the bridge rather than by this list being shorter than that one.
    /// </remarks>
    public static readonly string[] DefaultMeters =
    [
        "Argon",                  // the product's own instruments
        "Ion",                    // the RPC transport
        "Microsoft.Orleans.*",    // grain calls, activations, the directory, the scheduler
        "Microsoft.AspNetCore.*", // requests, routing, rate limiting, Kestrel
        "System.Runtime",         // GC, threadpool, exceptions
        "System.Net.Http"         // what this process calls out to
    ];

    /// <summary>
    /// Instruments never forwarded, whatever the meter list says. Empty: aggregation and sampling
    /// are what make the loud ones affordable, and an instrument nobody wants is rarer than one that
    /// merely ticks a lot.
    /// </summary>
    /// <remarks>
    /// Kept because it is the only answer for an instrument that is unwanted rather than loud, and
    /// because it is the fine control the meter list is not: <c>Microsoft.AspNetCore</c> is Kestrel
    /// and routing and rate limiting, and also the memory pool, and a wildcard cannot tell them
    /// apart.
    ///
    /// <para>A family needs its <c>*</c>. Only <c>.</c> and <c>*</c> end a prefix, so
    /// <c>orleans_messaging_</c> — which reads like a prefix, because that is how these instruments
    /// are named — is an exact match against an instrument no meter publishes, and denies nothing at
    /// all. It denied nothing here until a test said so.</para>
    /// </remarks>
    public static readonly string[] DefaultDeniedInstruments = [];

    /// <summary>
    /// Instruments to refuse, or empty for <see cref="DefaultDeniedInstruments"/>. Matched against
    /// the full <c>meter.instrument</c> name with the same prefix rule as <see cref="Meters"/>.
    /// </summary>
    public List<string> DeniedInstruments { get; set; } = [];

    /// <summary>What the bridge actually refuses.</summary>
    public IReadOnlyList<string> EffectiveDeniedInstruments
        => DeniedInstruments.Count > 0 ? DeniedInstruments : DefaultDeniedInstruments;

    /// <summary>
    /// How many measurements of one histogram series the bridge aims to send per window.
    /// </summary>
    /// <remarks>
    /// <para>A target, not a ceiling, and the distinction is the whole reason this is not a simple
    /// cap. A cap keeps the first N measurements of a window and discards the rest, which is a
    /// biased sample of that window — it throws away the tail, and the tail is exactly what a p95 is
    /// asking about. The chart would still draw. It would be wrong in the direction nobody checks.</para>
    ///
    /// <para>So each series is instead sampled at a rate computed from what that series did in the
    /// previous window: <c>p = target / observed</c>. A quiet series is never sampled at all; a loud
    /// one converges on this many rows; one that suddenly speeds up overshoots for a single window
    /// and then settles. Every kept measurement carries the rate it was kept at, as the
    /// <c>argon.sample_rate</c> attribute, so a count or a sum can be scaled back up — percentiles
    /// need no correction, which is the point of sampling uniformly rather than capping.</para>
    ///
    /// <para>Measured against a real quarter hour of this cluster's production traffic: 64 is
    /// roughly a 3.5x cut overall, 32 a 5x, 8 an 8.5x. 32 keeps hundreds of samples per series per
    /// minute — far more than a percentile needs, and still an order of magnitude below what was
    /// being stored.</para>
    /// </remarks>
    [Range(1, 100_000)]
    public int DistributionSamplesPerFlush { get; set; } = 32;

    /// <summary>
    /// The most distinct series the bridge tracks between two flushes, counters and histograms
    /// together.
    /// </summary>
    /// <remarks>
    /// Cardinality rather than volume, and the one failure neither aggregation nor sampling helps
    /// with: both work by recognising a series as one they have seen, so a tag carrying a user id, a
    /// URL or a timestamp defeats them and grows the tracking map without bound. Production runs at
    /// twenty-one series for its widest instrument, so this is two orders of magnitude of room
    /// before it means anything — and if it ever bites, the fix is the tag, not this number. Series
    /// already tracked keep working; new ones are dropped and logged by name.
    /// </remarks>
    [Range(1, 1_000_000)]
    public int MaxSeries { get; set; } = 2_000;

    /// <summary>
    /// Meter names to forward, or empty for <see cref="DefaultMeters"/>. A name ending in <c>.</c>
    /// or <c>*</c> matches by prefix, so <c>Microsoft.AspNetCore.*</c> takes the family; anything
    /// else must match exactly.
    /// </summary>
    /// <remarks>
    /// Empty rather than pre-populated, and that is not a style choice. Configuration binding
    /// <i>appends</i> to a collection that already has items rather than replacing it, so a default
    /// list here would mean a deployment naming three meters in <c>appsettings.json</c> silently got
    /// those three plus these — and could never narrow the set at all. The default is applied by
    /// <see cref="Effective"/> instead, where "configuration said nothing" and "configuration said
    /// this" stay distinguishable.
    /// </remarks>
    public List<string> Meters { get; set; } = [];

    /// <summary>What the bridge actually listens to.</summary>
    public IReadOnlyList<string> Effective => Meters.Count > 0 ? Meters : DefaultMeters;

    /// <summary>
    /// The bridge's window: how often observable instruments are read, accumulated counters are
    /// sent, and histogram sample rates are revised.
    /// </summary>
    /// <remarks>
    /// <para>Observable instruments have no callback of their own — nothing observes an
    /// <c>ObservableGauge</c> until someone calls <c>RecordObservableInstruments</c> — so this is
    /// the resolution of every gauge the bridge reports.</para>
    ///
    /// <para>It is also the resolution of every counter, which it did not use to be. Counter deltas
    /// are summed per series and sent once per window rather than one row per <c>Add</c>, so this
    /// interval trades how precisely a count is placed in time against how many rows saying so are
    /// stored. Histograms still send as they happen, so that a sampled measurement keeps the span it
    /// was recorded in; for them this is how often the sample rate is revised.</para>
    /// </remarks>
    public TimeSpan ObservableInterval { get; set; } = TimeSpan.FromSeconds(30);
}
