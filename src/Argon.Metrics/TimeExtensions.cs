namespace Argon.Metrics;

using System.Diagnostics;

public readonly struct MetricTimer(IMetricsCollector collector, MeasurementId name, IDictionary<string, string>? tags = null)
    : IAsyncDisposable
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <summary>
    /// Stops the timer and asynchronously reports the elapsed duration to the metrics collector.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _sw.Stop();
        await collector.DurationAsync(name, _sw.Elapsed, tags);
    }
}

public static class TimeExtensions
{
    /// <summary>
        /// Starts a timer that measures elapsed time and reports the duration to the metrics collector upon asynchronous disposal.
        /// </summary>
        /// <param name="name">The identifier for the measurement.</param>
        /// <param name="tags">Optional tags to associate with the measurement.</param>
        /// <returns>An <see cref="IAsyncDisposable"/> that reports the elapsed duration when disposed.</returns>
        public static IAsyncDisposable StartTimer(this IMetricsCollector collector, MeasurementId name,
        IDictionary<string, string>? tags = null)
        => new MetricTimer(collector, name, tags);

    /// <summary>
    /// Measures the duration of an asynchronous action and reports it to the metrics collector.
    /// </summary>
    /// <param name="name">The measurement identifier for the timed operation.</param>
    /// <param name="action">The asynchronous action to execute and time.</param>
    /// <param name="tags">Optional tags to associate with the measurement.</param>
    /// <returns>A task representing the asynchronous timing and reporting operation.</returns>
    public async static Task TimeAsync(this IMetricsCollector collector, MeasurementId name, Func<Task> action,
        IDictionary<string, string>? tags = null)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();

        await collector.DurationAsync(name, sw.Elapsed, tags);
    }

    /// <summary>
    /// Measures the duration of an asynchronous function and reports it to the metrics collector.
    /// </summary>
    /// <typeparam name="T">The return type of the asynchronous function.</typeparam>
    /// <param name="name">The measurement identifier for the timing event.</param>
    /// <param name="action">The asynchronous function to execute and time.</param>
    /// <param name="tags">Optional tags to associate with the measurement.</param>
    /// <returns>The result of the asynchronous function.</returns>
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