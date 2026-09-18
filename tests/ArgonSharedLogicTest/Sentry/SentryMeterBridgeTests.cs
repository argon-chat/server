namespace ArgonSharedLogicTest.Sentry;

using System.Diagnostics.Metrics;
using Argon.Features.Sentry;
using global::Sentry;

/// <summary>
/// The decisions the meter bridge makes before it sends anything: which meters it listens to, which
/// instruments it refuses inside them, what kind of Sentry metric an instrument becomes, and which
/// measurements are one series.
/// </summary>
/// <remarks>
/// <para>All of them are silent when wrong, which is the only reason this fixture is worth its
/// length. A matching rule that is too loose forwards the whole process's instrumentation to a paid
/// endpoint and nothing says so — that one has happened, at 100 GB. A metric type that is wrong
/// produces a chart that is plausible and false. A series key that is too coarse merges two things;
/// one that is too fine splits a count in half and shows you either half as if it were the whole.</para>
///
/// <para>None of it needs Sentry, a network or a host to check, which is why these are static
/// functions and why this fixture is in the fast suite.</para>
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

    /// <summary>
    /// The defaults forward the platform's meters and deny nothing.
    /// </summary>
    /// <remarks>
    /// Asserted rather than described, because this list was cut back to <c>Argon</c> and <c>Ion</c>
    /// once, to stop a hundred gigabytes, and that was the wrong lever — the meters are wanted, and
    /// what makes them affordable is the aggregation and sampling below, not a shorter list.
    /// Narrowing it again should have to edit a test that says so.
    /// </remarks>
    [Test]
    public void The_defaults_forward_the_platform_and_deny_nothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SentryMetricsOptions.DefaultMeters,
                Is.EqualTo(new[]
                {
                    "Argon", "Ion", "Microsoft.Orleans.*", "Microsoft.AspNetCore.*",
                    "System.Runtime", "System.Net.Http"
                }));
            Assert.That(SentryMetricsOptions.DefaultDeniedInstruments, Is.Empty);
        });
    }

    /// <summary>
    /// A denied instrument is refused even though its meter is listened to, which is the whole
    /// reason the denylist exists as a separate control: <c>Microsoft.AspNetCore.*</c> is a
    /// reasonable thing to ask for and brings the memory pool with it.
    /// </summary>
    [Test]
    public void A_denied_instrument_is_refused_inside_an_allowed_meter()
    {
        var pool   = new Meter("Microsoft.AspNetCore.MemoryPool");
        var meters = new[] { "Microsoft.AspNetCore.*" };
        var denied = new[] { "Microsoft.AspNetCore.MemoryPool.*" };

        Assert.Multiple(() =>
        {
            Assert.That(SentryMeterBridge.IsListenedTo(pool.Name, meters), Is.True,
                "the meter is allowed");
            Assert.That(
                SentryMeterBridge.IsForwarded(pool.CreateCounter<long>("aspnetcore.memory_pool.pooled"),
                    meters, denied),
                Is.False,
                "and the instrument inside it is still refused");
        });
    }

    /// <summary>
    /// A family needs its <c>*</c>, and this is the test that found out.
    /// </summary>
    /// <remarks>
    /// Only <c>.</c> and <c>*</c> open a prefix. <c>orleans_messaging_</c> reads like a prefix —
    /// that is how the instruments in it are named — but is matched exactly, against an instrument
    /// no meter publishes, and so denies nothing at all while looking like a denylist entry.
    /// </remarks>
    [Test]
    public void An_underscore_does_not_open_a_prefix()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SentryMeterBridge.IsDenied("Microsoft.Orleans.orleans_messaging_pings_sent",
                    ["Microsoft.Orleans.orleans_messaging_"]),
                Is.False,
                "which is why the entry that means this has to be written with a star");
            Assert.That(
                SentryMeterBridge.IsDenied("Microsoft.Orleans.orleans_messaging_pings_sent",
                    ["Microsoft.Orleans.orleans_messaging_*"]),
                Is.True);
        });
    }

    /// <summary>A series quieter than the target is not sampled at all.</summary>
    [TestCase(0L)]
    [TestCase(1L)]
    [TestCase(31L)]
    [TestCase(32L)]
    public void A_series_within_the_target_is_sent_whole(long observed)
        => Assert.That(SentryMeterBridge.NextSampleRate(observed, 32), Is.EqualTo(1d));

    /// <summary>
    /// A loud series is sampled to land on the target, which is the arithmetic the whole scheme
    /// rests on: rate times observed is what gets sent.
    /// </summary>
    [TestCase(64L, 32, 0.5)]
    [TestCase(320L, 32, 0.1)]
    [TestCase(33140L, 32, 32d / 33140d)]
    public void A_loud_series_is_sampled_towards_the_target(long observed, int target, double expected)
    {
        var rate = SentryMeterBridge.NextSampleRate(observed, target);

        Assert.Multiple(() =>
        {
            Assert.That(rate, Is.EqualTo(expected).Within(1e-12));
            Assert.That(rate * observed, Is.EqualTo(target).Within(1e-9),
                "the point of the rate is that this is the target");
        });
    }

    /// <summary>
    /// The rate never exceeds one, however the numbers come out.
    /// </summary>
    /// <remarks>
    /// A rate above one would read as "send more than happened", and the hot path compares against
    /// it with a random draw — so it has to be a probability, not a ratio that happens to usually be
    /// one.
    /// </remarks>
    [Test]
    public void A_sample_rate_is_a_probability()
    {
        foreach (var observed in new[] { 0L, 1L, 5L, 100L, 1_000_000L })
        {
            var rate = SentryMeterBridge.NextSampleRate(observed, 32);

            Assert.That(rate, Is.GreaterThan(0d).And.LessThanOrEqualTo(1d), $"observed {observed}");
        }
    }

    /// <summary>
    /// Two call sites tagging one instrument in a different order are one series.
    /// </summary>
    /// <remarks>
    /// Without the sort they are two, each holding part of the count, each looking like a complete
    /// answer — the kind of wrong number that never announces itself. This is the only place the
    /// aggregation can silently lose arithmetic, so it is the one worth a test.
    /// </remarks>
    [Test]
    public void A_series_is_identified_by_its_attributes_whatever_order_they_arrive_in()
    {
        KeyValuePair<string, object?>[] forwards = [new("result", "ok"), new("mode", "channel")];
        KeyValuePair<string, object?>[] backwards = [new("mode", "channel"), new("result", "ok")];

        Assert.That(SentryMeterBridge.SeriesKey("argon.call.join", forwards),
            Is.EqualTo(SentryMeterBridge.SeriesKey("argon.call.join", backwards)));
    }

    /// <summary>
    /// And two genuinely different attribute sets are not one series, however the separator is
    /// chosen — the reason it is a control character rather than a comma.
    /// </summary>
    [Test]
    public void Attribute_sets_that_differ_are_different_series()
    {
        KeyValuePair<string, object?>[] together = [new("a", "b,c")];
        KeyValuePair<string, object?>[] apart = [new("a", "b"), new("c", "")];

        Assert.That(SentryMeterBridge.SeriesKey("argon.metric", together),
            Is.Not.EqualTo(SentryMeterBridge.SeriesKey("argon.metric", apart)));
    }

    /// <summary>An untagged counter is one series, keyed by nothing but its name.</summary>
    [Test]
    public void An_untagged_measurement_is_keyed_by_its_name_alone()
        => Assert.That(SentryMeterBridge.SeriesKey("argon.metric", []), Is.EqualTo("argon.metric"));
}
