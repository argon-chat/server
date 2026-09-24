namespace Argon.Features.Sentry;

using System.Diagnostics.CodeAnalysis;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using global::Sentry;

/// <summary>
/// Forwards <c>System.Diagnostics.Metrics</c> measurements to Sentry as Sentry metrics.
/// </summary>
/// <remarks>
/// <para>The .NET SDK has no bridge of its own yet — <c>SentryOptions</c> mentions a
/// System.Diagnostics.Metrics integration, but no Sentry assembly so much as references
/// <c>MeterListener</c>. Sentry intends to ship one, so this is written to be deleted: it touches
/// nothing but its own options, and the day the SDK grows the integration this whole file goes
/// away along with the <c>Sentry:Metrics</c> section.</para>
///
/// <para><strong>Sentry stores one row per measurement.</strong> That is the fact the rest of this
/// file is shaped around, and the one that is easy to miss: unlike the OTLP exporter next door —
/// which aggregates a meter into a time series and ships a point per interval — every
/// <c>Emit*</c> call here becomes its own record, with its own timestamp and its own attributes.
/// A meter that ticks on a hot path therefore costs a row per tick. <c>aspnetcore.memory_pool.pooled</c>
/// fires on every buffer return; <c>orleans_messaging_sent_messages_size</c> on every message
/// between silos. Forwarded one to one across a handful of hosts that is tens of millions of rows a
/// day — which is how this bridge once put 100 GB into Sentry in a fortnight while the same numbers
/// sat correctly aggregated in VictoriaMetrics, where they were already being read.</para>
///
/// <para>The obvious response to that is to forward fewer meters, and it was the first one tried
/// here. It works and it is the wrong shape, because the meters are wanted — the question a metric
/// store is for is which role is behaving differently, and that question needs the platform's
/// instruments, not only the product's. What the traffic actually looks like is that <b>the volume
/// is repetition, not variety</b>: the largest producer of all, <c>memory_pool.pooled</c>, was fifty
/// thousand rows a quarter hour across a single series. Repetition compresses. So each kind of
/// instrument is bounded by what can be done to it without losing what it says:</para>
/// <list type="number">
///   <item><description><b>Counters</b> — deltas summed per series, one row per window. Lossless: a
///     Sentry counter increments by a delta, so a window's sum is its increment. Two thirds of the
///     volume, down by 95%.</description></item>
///   <item><description><b>Gauges</b> — already one read per window; nothing to do.</description></item>
///   <item><description><b>Histograms</b> — cannot be summed, so sampled uniformly at a rate the
///     bridge derives from the rate it observes, with the rate attached to what it keeps. Uniformly
///     rather than by a cap, because a cap drops the tail of a window and the tail is the
///     percentile.</description></item>
/// </list>
///
/// <para>Which meters and which instruments are still configurable, and are still the right answer
/// for an instrument that is genuinely unwanted. They are no longer how this is kept affordable.</para>
/// </remarks>
[ExcludeFromCodeCoverage(Justification = "Telemetry plumbing; exercised only against a live collector.")]
public sealed class SentryMeterBridge(
    IOptions<ArgonSentryOptions> options,
    ILogger<SentryMeterBridge>   logger) : IHostedService, IDisposable
{
    private readonly SentryMetricsOptions settings = options.Value.Metrics;

    /// <summary>
    /// Counter deltas waiting for the next flush, keyed by series — the metric name plus its
    /// attributes. Concurrent because measurement callbacks run on whatever thread recorded them.
    /// </summary>
    private readonly ConcurrentDictionary<string, CounterSeries> pending = new(StringComparer.Ordinal);

    /// <summary>Histogram series being sampled, each holding its rate and what it has seen.</summary>
    private readonly ConcurrentDictionary<string, DistributionSeries> sampled = new(StringComparer.Ordinal);

    /// <summary>
    /// Series refused for cardinality, so a map that has stopped growing never does it silently.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> droppedThisWindow = new(StringComparer.Ordinal);

    private MeterListener? listener;
    private PeriodicTimer? flushTimer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return Task.CompletedTask;

        listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (!IsForwarded(instrument))
                    return;

                self.EnableMeasurementEvents(instrument);
            }
        };

        // One callback per numeric type the instrument APIs accept. There is no generic catch-all:
        // MeterListener dispatches on the measurement's own type, and an instrument whose type has
        // no callback registered is simply never delivered.
        listener.SetMeasurementEventCallback<byte>((i, m, t, _) => Record(i, m, t));
        listener.SetMeasurementEventCallback<short>((i, m, t, _) => Record(i, m, t));
        listener.SetMeasurementEventCallback<int>((i, m, t, _) => Record(i, m, t));
        listener.SetMeasurementEventCallback<long>((i, m, t, _) => Record(i, m, t));
        listener.SetMeasurementEventCallback<float>((i, m, t, _) => Record(i, m, t));
        listener.SetMeasurementEventCallback<double>((i, m, t, _) => Record(i, m, t));
        listener.SetMeasurementEventCallback<decimal>((i, m, t, _) => Record(i, (double)m, t));

        listener.Start();

        // One timer, one window, three jobs: read the observable instruments (they push nothing and
        // are only worth what they were last asked), drain the accumulated counters, and revise the
        // histogram sample rates from what the last window saw. Keeping them on the same tick makes
        // this interval the resolution of everything the bridge reports, which is one number to
        // reason about rather than three.
        flushTimer = new PeriodicTimer(settings.ObservableInterval);
        _          = FlushOnEveryTickAsync(flushTimer);

        logger.LogInformation(
            "Sentry meter bridge listening to {Meters} (denying {Denied}), flushing every {Interval}; "
          + "counters are summed per series, histograms sampled towards {Samples} per series per flush",
            string.Join(", ", settings.Effective),
            settings.EffectiveDeniedInstruments.Count == 0
                ? "nothing"
                : string.Join(", ", settings.EffectiveDeniedInstruments),
            settings.ObservableInterval,
            settings.DistributionSamplesPerFlush);

        return Task.CompletedTask;
    }

    private async Task FlushOnEveryTickAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                Flush();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Sentry meter bridge flush failed; the next tick will try again");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Drain before tearing down: the deltas accumulated since the last tick are real
        // measurements, and a rolling deployment would otherwise lose a window of them per host.
        if (listener is not null)
            DrainCounters();

        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        flushTimer?.Dispose();
        flushTimer = null;

        listener?.Dispose();
        listener = null;
    }

    /// <summary>
    /// Whether a meter's measurements are forwarded. A configured name ending in <c>.</c> or
    /// <c>*</c> matches by prefix; anything else has to match exactly, so <c>Argon</c> does not
    /// quietly take <c>ArgonSomethingElse</c> with it.
    /// </summary>
    internal static bool IsListenedTo(string meterName, IReadOnlyList<string> configured)
        => Matches(meterName, configured);

    /// <summary>
    /// Whether an instrument is refused despite its meter being listened to.
    /// </summary>
    /// <remarks>
    /// Matched against the full <c>meter.instrument</c> name — the same string the metric is stored
    /// under — so a denial reads exactly like the row it exists to prevent. The prefix rule is the
    /// meter rule, which lets <c>Microsoft.AspNetCore.MemoryPool.*</c> deny a family and
    /// <c>System.Net.Http.http.client.open_connections</c> deny one instrument.
    /// </remarks>
    internal static bool IsDenied(string metricName, IReadOnlyList<string> denied)
        => Matches(metricName, denied);

    private static bool Matches(string name, IReadOnlyList<string> patterns)
    {
        foreach (var candidate in patterns)
        {
            if (candidate.Length == 0)
                continue;

            if (candidate[^1] is '*' or '.')
            {
                var prefix = candidate.TrimEnd('*');

                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    return true;

                // `Argon.` should match the family and its root, which is how a reader expects a
                // prefix to behave and how OpenTelemetry's own meter matching behaves.
                if (name.AsSpan().SequenceEqual(prefix.AsSpan().TrimEnd('.')))
                    return true;

                continue;
            }

            if (string.Equals(name, candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The whole admission decision for one instrument: an allowed meter, and not a denied
    /// instrument within it.
    /// </summary>
    internal static bool IsForwarded(Instrument instrument, IReadOnlyList<string> meters, IReadOnlyList<string> denied)
        => IsListenedTo(instrument.Meter.Name, meters) && !IsDenied(MetricName(instrument), denied);

    private bool IsForwarded(Instrument instrument)
        => IsForwarded(instrument, settings.Effective, settings.EffectiveDeniedInstruments);

    /// <summary>
    /// Which kind of Sentry metric an instrument becomes.
    /// </summary>
    /// <remarks>
    /// <para>The observable instruments are the ones worth reading twice. Their callbacks report a
    /// <i>total</i>, not what changed since the last read — so an <c>ObservableCounter</c> forwarded
    /// as a Sentry counter would add the whole running total again at every interval, and the chart
    /// would climb quadratically while looking entirely plausible. Reported as a gauge, the value
    /// means what it says.</para>
    ///
    /// <para>Their non-observable siblings are the opposite: <c>Counter.Add</c> and
    /// <c>UpDownCounter.Add</c> deliver a delta, which is exactly what a Sentry counter increments
    /// by. <c>UpDownCounter</c> stays a counter for that reason even though it can go down — and it
    /// is why summing those deltas over a window and emitting the total once is not an
    /// approximation, but the same arithmetic done earlier.</para>
    /// </remarks>
    internal static SentryMetricType MetricTypeFor(Instrument instrument)
    {
        var type = instrument.GetType();

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Histogram<>))
            return SentryMetricType.Distribution;

        // Every observable instrument derives from ObservableInstrument<T>, which is a firmer test
        // than the class name starting with "Observable" and covers a Gauge<T> that does not.
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(ObservableInstrument<>))
                return SentryMetricType.Gauge;
        }

        return SentryMetricType.Counter;
    }

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        // A measurement callback runs on whatever thread recorded the measurement, which is
        // somebody's request path. Telemetry that throws there would turn a metric into an outage,
        // and for a counter the cost here is now an interlocked add on a dictionary hit rather than
        // an emitter enqueue.
        try
        {
            if (!SentrySdk.IsEnabled)
                return;

            var name    = MetricName(instrument);
            var numeric = Convert.ToDouble(value);
            var unit    = instrument.Unit ?? string.Empty;

            switch (MetricTypeFor(instrument))
            {
                case SentryMetricType.Distribution:
                    EmitDistribution(name, numeric, unit, tags);
                    break;

                case SentryMetricType.Gauge:
                    // Only ever reached from RecordObservableInstruments on the flush tick, so this
                    // is already one emission per series per window and needs no accumulator.
                    SentrySdk.Metrics.EmitGauge(name, numeric, unit, Attributes(tags), null);
                    break;

                default:
                    Accumulate(name, numeric, tags);
                    break;
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Sentry meter bridge dropped a measurement of {Instrument}", instrument.Name);
        }
    }

    /// <summary>
    /// Adds a counter delta to the series it belongs to. Nothing is sent here; <see cref="Flush"/>
    /// sends the total once per window.
    /// </summary>
    /// <remarks>
    /// This is the guard that does the heavy lifting, and it costs nothing in fidelity: a Sentry
    /// counter is incremented by a delta, so the sum of a window's deltas <i>is</i> that window's
    /// increment. What it does cost is timing — every measurement in a window is reported at the
    /// flush rather than when it happened — which is the same trade the OTLP exporter makes at its
    /// own export interval, for the same reason.
    /// </remarks>
    private void Accumulate(string name, double delta, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = SeriesKey(name, tags);

        // A repeat of a known series allocates nothing beyond the key. That asymmetry is the point:
        // the hot path for a counter ticking a thousand times a second is this branch.
        if (pending.TryGetValue(key, out var existing))
        {
            existing.Add(delta);
            return;
        }

        if (pending.Count >= settings.MaxSeries)
        {
            // Cardinality, not volume. A tag carrying a user id or a URL would grow this map without
            // bound and take the process with it, so the map stops growing and the log says which
            // instrument was asking. Series already in it keep accumulating correctly.
            CountDrop(name);
            return;
        }

        // The value overload rather than the factory one: a span cannot be captured by a lambda, and
        // materialising the series here costs an allocation that the miss path was going to pay
        // anyway. Two threads racing a new series lose one of these; neither loses a measurement,
        // because the winner is what Add is called on.
        pending.GetOrAdd(key, new CounterSeries(name, Attributes(tags))).Add(delta);
    }

    /// <summary>
    /// Sends a histogram measurement, or does not, at the rate this series is currently sampled at.
    /// </summary>
    /// <remarks>
    /// <para>A histogram's individual values are the whole point of it — percentiles cannot be
    /// recovered from a sum — so there is nothing to aggregate here the way there is for a counter.
    /// Sampling is the only lever, and it has to be uniform: keeping the first N of a window and
    /// dropping the rest is a cap rather than a sample, and it discards the tail of the window,
    /// which is precisely what a p95 is asking about. That chart draws. It is wrong in the direction
    /// nobody checks.</para>
    ///
    /// <para>The decision is made here rather than by buffering values for the flush, and that is
    /// deliberate: emitting inline keeps the ambient span, so a slow call sampled in still points at
    /// the trace it happened in. Which is the reason to keep a metric in Sentry at all rather than
    /// reading it in the OTLP pipeline.</para>
    /// </remarks>
    private void EmitDistribution(string name, double value, string unit, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = SeriesKey(name, tags);

        if (!sampled.TryGetValue(key, out var series))
        {
            if (sampled.Count >= settings.MaxSeries)
            {
                CountDrop(name);
                return;
            }

            // A series starts unsampled. It takes one window to find out how loud it is, and being
            // briefly complete is the right way round to be wrong.
            series = sampled.GetOrAdd(key, new DistributionSeries());
        }

        if (!series.ShouldSend())
            return;

        var rate       = series.Rate;
        var attributes = Attributes(tags);

        // Only when it is actually being sampled. An attribute that always says 1 is a column in
        // every row of the store forever, and answers a question nobody asked.
        if (rate < 1d)
            attributes.Add(new KeyValuePair<string, object>("argon.sample_rate", rate));

        SentrySdk.Metrics.EmitDistribution(name, value, unit, attributes, null);
    }

    private void CountDrop(string name)
        => droppedThisWindow.AddOrUpdate(name, 1, static (_, n) => n + 1);

    /// <summary>
    /// The rate a series should be sampled at next, given how many measurements it just made.
    /// </summary>
    /// <remarks>
    /// Plain proportional control, deliberately without smoothing: the window is short, and a series
    /// that changes rate should be at its new rate within one window rather than easing towards it
    /// over several. Overshoot lasts one window and costs rows, not correctness.
    /// </remarks>
    internal static double NextSampleRate(long observed, int target)
        => observed <= target ? 1d : (double)target / observed;

    /// <summary>
    /// <c>meter.instrument</c>, which is the hierarchical dotted shape Sentry asks for and already
    /// how these instruments are named everywhere else this server exports them.
    /// </summary>
    internal static string MetricName(Instrument instrument)
        => $"{instrument.Meter.Name}.{instrument.Name}";

    /// <summary>
    /// A stable identity for one counter series: the metric plus its attributes.
    /// </summary>
    /// <remarks>
    /// Attributes are sorted before they are joined. Two call sites tagging the same instrument in
    /// a different order are one series, not two — and without the sort they would silently be two,
    /// each holding part of the count and each looking like a complete answer.
    /// </remarks>
    internal static string SeriesKey(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
            return name;

        var parts = new string[tags.Length];

        for (var i = 0; i < tags.Length; i++)
            parts[i] = $"{tags[i].Key}{RecordSeparator}{tags[i].Value}";

        Array.Sort(parts, StringComparer.Ordinal);

        return $"{name}{UnitSeparator}{string.Join(UnitSeparator, parts)}";
    }

    // ASCII's own separators, because a tag key or value containing one is not a thing that happens
    // — where a comma or an equals sign would let `{a: "b,c"}` and `{a: "b", c: ""}` collide into
    // one series, which is a wrong number that looks like a right one.
    private const char RecordSeparator = '\u001e';
    private const char UnitSeparator   = '\u001f';

    private static List<KeyValuePair<string, object>> Attributes(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var attributes = new List<KeyValuePair<string, object>>(tags.Length);

        foreach (var tag in tags)
        {
            if (tag.Value is { } tagValue)
                attributes.Add(new KeyValuePair<string, object>(tag.Key, tagValue));
        }

        return attributes;
    }

    private void Flush()
    {
        ReadObservableInstruments();
        DrainCounters();
        ResampleDistributions();
        ReportDrops();
    }

    /// <summary>
    /// Sets each histogram series' rate for the coming window from what it did in the last one.
    /// </summary>
    /// <remarks>
    /// A series that went quiet is forgotten rather than pinned at its old rate, so a burst that has
    /// passed does not keep throwing measurements away — and so this map does not accumulate every
    /// series the process has ever produced.
    /// </remarks>
    private void ResampleDistributions()
    {
        foreach (var key in sampled.Keys)
        {
            if (!sampled.TryGetValue(key, out var series))
                continue;

            var observed = series.TakeObserved();

            if (observed == 0)
                sampled.TryRemove(key, out _);
            else
                series.Rate = NextSampleRate(observed, settings.DistributionSamplesPerFlush);
        }
    }

    private void DrainCounters()
    {
        foreach (var key in pending.Keys)
        {
            if (!pending.TryRemove(key, out var series))
                continue;

            var total = series.Exchange();

            // A window in which an UpDownCounter went up and back down nets to zero and is not an
            // increment of anything; sending it would add a row saying nothing happened.
            if (total == 0)
                continue;

            try
            {
                SentrySdk.Metrics.EmitCounter(series.Name, total, series.Attributes, null);
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Sentry meter bridge dropped a counter flush of {Metric}", series.Name);
            }
        }
    }

    private void ReportDrops()
    {
        foreach (var name in droppedThisWindow.Keys)
        {
            if (droppedThisWindow.TryRemove(name, out var dropped) && dropped > 0)
            {
                logger.LogWarning(
                    "Sentry meter bridge refused {Dropped} measurements of {Metric} in one window: it is past "
                  + "Sentry:Metrics:MaxSeries distinct series. Aggregation and sampling both work by "
                  + "recognising a series, so this is a tag carrying something unbounded — an id, a URL, a "
                  + "timestamp. Take the tag off the instrument rather than raising the limit.",
                    dropped, name);
            }
        }
    }

    private void ReadObservableInstruments()
    {
        try
        {
            listener?.RecordObservableInstruments();
        }
        catch (Exception e)
        {
            // One misbehaving callback must not take the timer with it; the next tick tries again.
            logger.LogWarning(e, "Reading observable instruments for Sentry failed");
        }
    }

    /// <summary>One counter series between two flushes.</summary>
    private sealed class CounterSeries(string name, List<KeyValuePair<string, object>> attributes)
    {
        private double sum;

        public string                             Name       { get; } = name;
        public List<KeyValuePair<string, object>> Attributes { get; } = attributes;

        /// <summary>
        /// Interlocked because <c>Counter.Add</c> is callable from any thread at once, and
        /// compare-exchange rather than a lock because the contended case is a retry of an add.
        /// </summary>
        public void Add(double delta)
        {
            double current, updated;

            do
            {
                current = Volatile.Read(ref sum);
                updated = current + delta;
            }
            while (Interlocked.CompareExchange(ref sum, updated, current) != current);
        }

        /// <summary>Takes the accumulated total and resets it, so nothing is counted twice.</summary>
        public double Exchange() => Interlocked.Exchange(ref sum, 0d);
    }

    /// <summary>One histogram series: the rate it is sampled at, and what it has seen this window.</summary>
    private sealed class DistributionSeries
    {
        private long observed;

        /// <summary>
        /// The probability a measurement is sent. Set at the flush and read on the hot path, so it
        /// is written and read as a whole rather than guarded — a measurement landing on either side
        /// of a rate change is sampled at either the old rate or the new one, both of which are
        /// right, and neither of which is worth a lock on a per-measurement path.
        /// </summary>
        public double Rate
        {
            get => Volatile.Read(ref rate);
            set => Volatile.Write(ref rate, value);
        }

        private double rate = 1d;

        /// <summary>
        /// Counts the measurement and says whether to send it.
        /// </summary>
        /// <remarks>
        /// The count is of everything that happened, not of what was sent — it is the denominator
        /// the next rate is computed from, so sampling it would make the rate converge on the wrong
        /// number and keep converging further from it.
        /// </remarks>
        public bool ShouldSend()
        {
            Interlocked.Increment(ref observed);

            var current = Rate;

            return current >= 1d || Random.Shared.NextDouble() < current;
        }

        /// <summary>Takes the window's count and resets it.</summary>
        public long TakeObserved() => Interlocked.Exchange(ref observed, 0L);
    }
}
