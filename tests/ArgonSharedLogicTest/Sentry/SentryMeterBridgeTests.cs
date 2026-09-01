namespace ArgonSharedLogicTest.Sentry;

using System.Diagnostics.Metrics;
using Argon.Features.Sentry;
using global::Sentry;

/// <summary>
/// The two decisions the meter bridge makes before it sends anything: which meters it listens to,
/// and what kind of Sentry metric an instrument becomes.
/// </summary>
/// <remarks>
/// Both are silent when wrong. A matching rule that is too loose forwards the whole process's
/// instrumentation to a paid endpoint and nothing says so; a metric type that is wrong produces a
/// chart that is plausible and false. Neither needs Sentry, a network or a host to check, which is
/// why they are static and why this fixture is in the fast suite.
/// </remarks>
[TestFixture]
public class SentryMeterBridgeTests
{
    private static readonly string[] Configured =
    [
        "Argon",
        "Ion",
        "Microsoft.Orleans.*",
        "System.Runtime"
    ];

    [TestCase("Argon")]
    [TestCase("Ion")]
    [TestCase("System.Runtime")]
    public void An_exactly_named_meter_is_listened_to(string meter)
        => Assert.That(SentryMeterBridge.IsListenedTo(meter, Configured), Is.True);

    /// <summary>
    /// The reason exact names are matched exactly. <c>StartsWith</c> on every entry would take this
    /// with it, and a third-party meter is exactly the kind of thing nobody notices forwarding.
    /// </summary>
    [Test]
    public void A_meter_that_merely_starts_with_a_configured_name_is_not()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SentryMeterBridge.IsListenedTo("ArgonSomethingElse", Configured), Is.False);
            Assert.That(SentryMeterBridge.IsListenedTo("System.Runtime.Extra", Configured), Is.False);
        });
    }

    [TestCase("Microsoft.Orleans.Directory")]
    [TestCase("Microsoft.Orleans.Scheduler.Something")]
    public void A_starred_name_takes_the_family(string meter)
        => Assert.That(SentryMeterBridge.IsListenedTo(meter, Configured), Is.True);

    /// <summary>A prefix covers the root it is a prefix of; <c>Microsoft.Orleans</c> is Orleans.</summary>
    [Test]
    public void A_starred_name_also_takes_the_root_it_names()
        => Assert.That(SentryMeterBridge.IsListenedTo("Microsoft.Orleans", Configured), Is.True);

    [Test]
    public void An_unrelated_meter_is_left_alone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SentryMeterBridge.IsListenedTo("Npgsql", Configured), Is.False);
            Assert.That(SentryMeterBridge.IsListenedTo("Microsoft.AspNetCore.Hosting", Configured), Is.False);
            Assert.That(SentryMeterBridge.IsListenedTo("", Configured), Is.False);
        });
    }

    [Test]
    public void An_empty_configuration_listens_to_nothing()
        => Assert.That(SentryMeterBridge.IsListenedTo("Argon", []), Is.False);

    /// <summary>
    /// Configuration binding appends to a collection that already has items rather than replacing
    /// it. A default list on the property itself would therefore have meant that naming three
    /// meters in appsettings.json got those three <i>plus</i> the six defaults, and that narrowing
    /// the set was impossible — which is exactly what it did before this was noticed, with twelve
    /// entries bound from six.
    /// </summary>
    [Test]
    public void Naming_meters_replaces_the_defaults_rather_than_adding_to_them()
    {
        var configured = new SentryMetricsOptions { Meters = { "OnlyThis" } };

        Assert.Multiple(() =>
        {
            Assert.That(configured.Effective, Is.EqualTo(new[] { "OnlyThis" }));
            Assert.That(new SentryMetricsOptions().Effective,
                Is.EqualTo(SentryMetricsOptions.DefaultMeters),
                "naming none is what asks for the defaults");
        });
    }

    private static readonly Meter Meter = new("Argon.Test.Bridge");

    /// <summary>A histogram is a distribution: the point of it is the shape, not the total.</summary>
    [Test]
    public void A_histogram_becomes_a_distribution()
        => Assert.That(SentryMeterBridge.MetricTypeFor(Meter.CreateHistogram<double>("latency")),
            Is.EqualTo(SentryMetricType.Distribution));

    /// <summary>
    /// Add() hands over a delta, and a Sentry counter increments by what it is given, so the two
    /// mean the same thing. UpDownCounter included: it reports a delta too, negative or not.
    /// </summary>
    [Test]
    public void A_counter_and_an_up_down_counter_become_counters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SentryMeterBridge.MetricTypeFor(Meter.CreateCounter<long>("requests")),
                Is.EqualTo(SentryMetricType.Counter));
            Assert.That(SentryMeterBridge.MetricTypeFor(Meter.CreateUpDownCounter<long>("connections")),
                Is.EqualTo(SentryMetricType.Counter));
        });
    }

    /// <summary>
    /// The one that would have been wrong and looked right.
    /// </summary>
    /// <remarks>
    /// An observable instrument's callback reports the running total, not what changed since the
    /// last read. Forwarded as a counter it would add that whole total again at every interval, and
    /// the chart would climb quadratically while remaining entirely believable. As a gauge the
    /// value means what it says.
    /// </remarks>
    [Test]
    public void Every_observable_instrument_becomes_a_gauge()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SentryMeterBridge.MetricTypeFor(Meter.CreateObservableCounter("uptime", () => 1L)),
                Is.EqualTo(SentryMetricType.Gauge));
            Assert.That(SentryMeterBridge.MetricTypeFor(Meter.CreateObservableUpDownCounter("queue", () => 1L)),
                Is.EqualTo(SentryMetricType.Gauge));
            Assert.That(SentryMeterBridge.MetricTypeFor(Meter.CreateObservableGauge("heap", () => 1L)),
                Is.EqualTo(SentryMetricType.Gauge));
        });
    }

    /// <summary>The dotted shape Sentry asks for, and the one these instruments already export as.</summary>
    [Test]
    public void A_metric_is_named_after_its_meter_and_instrument()
        => Assert.That(SentryMeterBridge.MetricName(Meter.CreateCounter<long>("messages.sent")),
            Is.EqualTo("Argon.Test.Bridge.messages.sent"));
}
