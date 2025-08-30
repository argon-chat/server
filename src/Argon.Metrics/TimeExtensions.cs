namespace Argon.Metrics;

using System.Diagnostics;

public readonly struct MetricTimer(IMetricsCollector collector, MeasurementId name, Dictionary<string, string>? tags = null)
    : IAsyncDisposable
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public async ValueTask DisposeAsync()
    {
        _sw.Stop();
        collector.Logger.LogInformation("Timing for '{metricKey}' elapsed: {elapsed:##,###} ms", name, _sw.Elapsed.TotalMilliseconds);
        await collector.DurationAsync(name, _sw.Elapsed, tags);
    }
}

public static class TimeExtensions
{
    public static IAsyncDisposable StartTimer(this IMetricsCollector collector, MeasurementId name,
        Dictionary<string, string>? tags = null)
        => new MetricTimer(collector, name, tags);

    public async static Task TimeAsync(this IMetricsCollector collector, MeasurementId name, Func<Task> action,
        Dictionary<string, string>? tags = null)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();

        await collector.DurationAsync(name, sw.Elapsed, tags);
    }

    public async static Task TimeAsync(this IMetricsCollector collector, MeasurementId name, string scope, string operation, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();

        await collector.DurationAsync(name, sw.Elapsed, scope, operation);
    }

    public async static Task<T> TimeAsync<T>(this IMetricsCollector collector, MeasurementId name, Func<Task<T>> action,
        Dictionary<string, string>? tags = null)
    {
        var sw     = Stopwatch.StartNew();
        var result = await action();
        sw.Stop();

        await collector.DurationAsync(name, sw.Elapsed, tags);
        return result;
    }
}