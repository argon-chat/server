namespace Argon.Metrics;

public static class GaugeExtensions
{
    public static Task GaugeAsync(this IMetricsCollector collector, MeasurementId name, double value,
        IDictionary<string, string>? tags = null)
        => collector.ObserveAsync(name, value, tags);

    public async static Task WithinGaugeAsync(this IMetricsCollector metrics, MeasurementId name,
        Func<Task> action, IDictionary<string, string>? tags = null)
    {
        await metrics.GaugeAsync(name, 1, tags);
        try
        {
            await action();
        }
        finally
        {
            await metrics.GaugeAsync(name, 0, tags);
        }
    }
}