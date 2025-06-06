namespace Argon.Metrics;

public static class DeltaExtensions
{
    public static Task ObserveDeltaAsync(this IMetricsCollector collector, MeasurementId name, double before, double after,
        Dictionary<string, string>? tags = null)
    {
        var delta = after - before;
        return collector.ObserveAsync(name, delta, tags);
    }
}