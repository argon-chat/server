namespace Argon.Metrics.Gauges;

/// <example>
/// var p = new PercentileEstimatorGauge(metrics, MeasurementId.HttpRequests);
/// await p.ObserveAsync(120);
/// await p.ObserveAsync(180);
/// await p.ObserveAsync(220);
/// public class LatencyTracker(IMetricsCollector metrics)
///{
///    private readonly PercentileEstimatorGauge _latency = new(metrics, MeasurementId.HttpRequests, tags: new Dictionary{string, string}
///    {
///        ["endpoint"] = "/api/user",
///        ["method"]   = "GET"
///    });
///    public async Task RecordAsync(TimeSpan elapsed)
///        => await _latency.ObserveAsync(elapsed.TotalMilliseconds);
///}
/// </example>
public class PercentileEstimatorGauge(
    IMetricsCollector collector,
    MeasurementId measurement,
    int maxSamples = 500,
    IDictionary<string, string>? tags = null)
{
    private readonly List<double> _samples = new();

    public async Task ObserveAsync(double value)
    {
        _samples.Add(value);
        if (_samples.Count > maxSamples)
            _samples.RemoveAt(0);

        var sorted = _samples.Order().ToArray();

        var p50 = sorted[(int)(sorted.Length * 0.50)];
        var p90 = sorted[(int)(sorted.Length * 0.90)];
        var p99 = sorted[(int)(sorted.Length * 0.99)];

        await collector.ObserveAsync(measurement, p50, MergeTags("quantile", "p50"));
        await collector.ObserveAsync(measurement, p90, MergeTags("quantile", "p90"));
        await collector.ObserveAsync(measurement, p99, MergeTags("quantile", "p99"));
    }

    private IDictionary<string, string> MergeTags(string key, string value)
    {
        var tags1 = tags is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(tags);

        tags1[key] = value;
        return tags1;
    }
}