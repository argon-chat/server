namespace Argon.Metrics;

using System.Diagnostics;

public static class LatencyBucketExtensions
{
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