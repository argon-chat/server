namespace Argon.Metrics;

public static class PercentExtensions
{
    public static Task ObservePercentAsync(this IMetricsCollector collector, MeasurementId name, double ratio,
        Dictionary<string, string>? tags = null)
    {
        var percent = Math.Clamp(ratio * 100, 0, 100);
        return collector.ObserveAsync(name, percent, tags);
    }
}