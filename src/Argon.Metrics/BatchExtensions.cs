namespace Argon.Metrics;

public static class BatchExtensions
{
    public static Task ObserveBatchAsync(this IMetricsCollector collector, MeasurementId name, IEnumerable<double> values,
        IDictionary<string, string>? tags = null)
    {
        var tasks = values.Select(v => collector.ObserveAsync(name, v, tags));
        return Task.WhenAll(tasks);
    }
}