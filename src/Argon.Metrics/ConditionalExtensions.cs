namespace Argon.Metrics;

public static class ConditionalExtensions
{
    public async static Task CountIfAsync(this IMetricsCollector collector, MeasurementId name, Func<bool> condition,
        long value = 1, IDictionary<string, string>? tags = null)
    {
        if (condition())
            await collector.CountAsync(name, value, tags);
    }

    public async static Task CountIfAsync(this IMetricsCollector collector, MeasurementId name, Func<Task<bool>> condition,
        long value = 1, IDictionary<string, string>? tags = null)
    {
        if (await condition())
            await collector.CountAsync(name, value, tags);
    }
}