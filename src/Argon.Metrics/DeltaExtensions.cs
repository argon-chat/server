namespace Argon.Metrics;

public static class DeltaExtensions
{
    /// <summary>
    /// Asynchronously records the difference between two values as a metric observation.
    /// </summary>
    /// <param name="name">The identifier for the metric measurement.</param>
    /// <param name="before">The initial value to compare.</param>
    /// <param name="after">The final value to compare.</param>
    /// <param name="tags">Optional tags to associate with the metric observation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task ObserveDeltaAsync(this IMetricsCollector collector, MeasurementId name, double before, double after,
        IDictionary<string, string>? tags = null)
    {
        var delta = after - before;
        return collector.ObserveAsync(name, delta, tags);
    }
}