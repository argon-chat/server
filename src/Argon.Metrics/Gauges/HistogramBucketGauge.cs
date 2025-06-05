namespace Argon.Metrics.Gauges;


//public class HttpTimingRecorder(IMetricsCollector metrics)
//{
//    private readonly HistogramBucketGauge _histogram = new(metrics, MeasurementId.HttpRequests, [50, 100, 200, 500, 1000], new Dictionary<string, string>
//    {
//        ["endpoint"] = "/api/data"
//    });

//    public async Task RecordAsync(TimeSpan elapsed)
//        => await _histogram.ObserveAsync(elapsed.TotalMilliseconds);
//}
public class HistogramBucketGauge(
    IMetricsCollector collector,
    MeasurementId measurement,
    double[] buckets,
    IDictionary<string, string>? baseTags = null)
{
    private readonly Lazy<double[]> _buckets = new(() => buckets.OrderBy(x => x).ToArray());

    public async Task ObserveAsync(double value)
    {
        var b = _buckets.Value;
        var bucketLabel = value <= b[0]
            ? $"<= {b[0]}"
            : value > b[^1]
                ? $"> {b[^1]}"
                : $"<={b.First(x => value <= x)}";

        var tags = baseTags is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(baseTags);

        tags["bucket"] = bucketLabel;

        await collector.CountAsync(measurement, 1, tags);
    }
}