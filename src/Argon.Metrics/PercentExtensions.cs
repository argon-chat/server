namespace Argon.Metrics;

public static class PercentExtensions
{
    /// <summary>
    /// Records a percentage value, derived from a ratio, as a metric observation.
    /// </summary>
    /// <param name="name">The identifier for the metric measurement.</param>
    /// <param name="ratio">A value between 0.0 and 1.0 representing the ratio to be converted to a percentage.</param>
    /// <param name="tags">Optional key-value pairs to associate with the metric observation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task ObservePercentAsync(this IMetricsCollector collector, MeasurementId name, double ratio,
        IDictionary<string, string>? tags = null)
    {
        var percent = Math.Clamp(ratio * 100, 0, 100);
        return collector.ObserveAsync(name, percent, tags);
    }
}