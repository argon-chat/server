namespace Argon.Metrics;

public static class CountExtensions
{
    public static Task CountAsync(this IMetricsCollector collector, MeasurementId name, long value = 1,
        IDictionary<string, string>? tags = null)
        => collector.CountAsync(name, value, tags);

    public static Task IncAsync(this IMetricsCollector collector, MeasurementId name,
        IDictionary<string, string>? tags = null)
        => collector.CountAsync(name, 1, tags);
}