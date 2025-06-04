namespace Argon.Metrics;

using System.Diagnostics;

public readonly struct MetricTimer(IMetricsCollector collector, MeasurementId name, IDictionary<string, string>? tags = null)
    : IDisposable
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public void Dispose()
    {
        _sw.Stop();
        collector.DurationAsync(name, _sw.Elapsed, tags).GetAwaiter().GetResult();
    }
}

public static class TimeExtensions
{
    public static IDisposable StartTimer(this IMetricsCollector collector, MeasurementId name,
        IDictionary<string, string>? tags = null)
        => new MetricTimer(collector, name, tags);

    public async static Task TimeAsync(this IMetricsCollector collector, MeasurementId name, Func<Task> action,
        IDictionary<string, string>? tags = null)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();

        await collector.DurationAsync(name, sw.Elapsed, tags);
    }

    public async static Task<T> TimeAsync<T>(this IMetricsCollector collector, MeasurementId name, Func<Task<T>> action,
        IDictionary<string, string>? tags = null)
    {
        var sw     = Stopwatch.StartNew();
        var result = await action();
        sw.Stop();

        await collector.DurationAsync(name, sw.Elapsed, tags);
        return result;
    }
}