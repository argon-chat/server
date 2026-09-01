namespace Argon.Features.Sentry;

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
/// <para>Only the meters named in configuration are listened to. Everything in the process
/// publishes instruments and forwarding all of them would send Sentry a volume nobody asked for.</para>
/// </remarks>
public sealed class SentryMeterBridge(
    IOptions<ArgonSentryOptions> options,
    ILogger<SentryMeterBridge>   logger) : IHostedService, IDisposable
{
    private readonly SentryMetricsOptions settings = options.Value.Metrics;

    private MeterListener? listener;
    private Timer?         observableTimer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return Task.CompletedTask;

        listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (!IsListenedTo(instrument.Meter.Name))
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

        // Observable instruments push nothing; they are read when asked. This is that asking, and
        // its interval is the resolution of every gauge the bridge reports.
        observableTimer = new Timer(
            _ => ReadObservableInstruments(),
            null, settings.ObservableInterval, settings.ObservableInterval);

        logger.LogInformation(
            "Sentry meter bridge listening to {Meters}, reading observable instruments every {Interval}",
            string.Join(", ", settings.Meters), settings.ObservableInterval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        observableTimer?.Dispose();
        observableTimer = null;

        listener?.Dispose();
        listener = null;
    }

    /// <summary>
    /// Whether a meter's measurements are forwarded. A configured name ending in <c>.</c> or
    /// <c>*</c> matches by prefix; anything else has to match exactly, so <c>Argon</c> does not
    /// quietly take <c>ArgonSomethingElse</c> with it.
    /// </summary>
    internal static bool IsListenedTo(string meterName, IReadOnlyList<string> configured)
    {
        foreach (var candidate in configured)
        {
            if (candidate.Length == 0)
                continue;

            if (candidate[^1] is '*' or '.')
            {
                var prefix = candidate.TrimEnd('*');

                if (meterName.StartsWith(prefix, StringComparison.Ordinal))
                    return true;

                // `Argon.` should match the family and its root, which is how a reader expects a
                // prefix to behave and how OpenTelemetry's own meter matching behaves.
                if (meterName.AsSpan().SequenceEqual(prefix.AsSpan().TrimEnd('.')))
                    return true;

                continue;
            }

            if (string.Equals(meterName, candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool IsListenedTo(string meterName) => IsListenedTo(meterName, settings.Meters);

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
    /// by. <c>UpDownCounter</c> stays a counter for that reason even though it can go down.</para>
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
        // and Sentry's emitter buffers rather than sends, so the cost here is an enqueue.
        try
        {
            if (!SentrySdk.IsEnabled)
                return;

            var attributes = new List<KeyValuePair<string, object>>(tags.Length + 1);

            foreach (var tag in tags)
            {
                if (tag.Value is { } tagValue)
                    attributes.Add(new KeyValuePair<string, object>(tag.Key, tagValue));
            }

            var name = MetricName(instrument);
            var unit = instrument.Unit ?? string.Empty;

            switch (MetricTypeFor(instrument))
            {
                case SentryMetricType.Distribution:
                    SentrySdk.Metrics.EmitDistribution(name, value, unit, attributes, null);
                    break;
                case SentryMetricType.Gauge:
                    SentrySdk.Metrics.EmitGauge(name, value, unit, attributes, null);
                    break;
                default:
                    // No unit on a counter: the emitter takes none, because a count of things has
                    // no unit beyond the thing.
                    SentrySdk.Metrics.EmitCounter(name, value, attributes, null);
                    break;
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Sentry meter bridge dropped a measurement of {Instrument}", instrument.Name);
        }
    }

    /// <summary>
    /// <c>meter.instrument</c>, which is the hierarchical dotted shape Sentry asks for and already
    /// how these instruments are named everywhere else this server exports them.
    /// </summary>
    internal static string MetricName(Instrument instrument)
        => $"{instrument.Meter.Name}.{instrument.Name}";

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
}
