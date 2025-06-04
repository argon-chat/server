namespace Argon.Metrics;

using System.Diagnostics;

public static class LatencyBucketExtensions
{
    /// <summary>
    /// Measures the latency of an asynchronous action, categorizes it into a latency bucket, and records a count metric with the corresponding bucket tag.
    /// </summary>
    /// <param name="name">The identifier for the metric to record.</param>
    /// <param name="action">The asynchronous action whose latency is measured.</param>
    /// <param name="tags">Optional tags to associate with the metric; the "latency_bucket" tag will be added or overwritten.</param>
    /// <remarks>
    /// The latency is bucketed as "&lt;50ms", "&lt;200ms", "&lt;1s", or "slow" based on the elapsed time.
    /// </remarks>
    public async static Task ObserveLatencyBucketAsync(this IMetricsCollector metrics, MeasurementId name,
        Func<Task> action, IDictionary<string, string>? tags = null)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();

        var elapsedMs = sw.Elapsed.TotalMilliseconds;
        var bucket = elapsedMs switch
        {
            < 50   => "<50ms",
            < 200  => "<200ms",
            < 1000 => "<1s",
            _      => "slow"
        };

        var finalTags = tags == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(tags);

        finalTags["latency_bucket"] = bucket;

        await metrics.CountAsync(name, 1, finalTags);
    }
}