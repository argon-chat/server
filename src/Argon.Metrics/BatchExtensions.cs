namespace Argon.Metrics;

public static class BatchExtensions
{
    /// <summary>
    /// Asynchronously observes a batch of metric values using the specified measurement identifier and optional tags.
    /// </summary>
    /// <param name="name">The measurement identifier for the metric.</param>
    /// <param name="values">The collection of values to observe.</param>
    /// <param name="tags">Optional tags to associate with each observation.</param>
    /// <returns>A task that completes when all observations have finished.</returns>
    public static Task ObserveBatchAsync(this IMetricsCollector collector, MeasurementId name, IEnumerable<double> values,
        IDictionary<string, string>? tags = null)
    {
        var tasks = values.Select(v => collector.ObserveAsync(name, v, tags));
        return Task.WhenAll(tasks);
    }
}