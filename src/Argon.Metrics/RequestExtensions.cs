namespace Argon.Metrics;

public static class RequestExtensions
{
    public static Task MarkRequest(this IMetricsCollector collector, string route, int status, TimeSpan elapsed, string method = "GET")
        => collector.DurationAsync(MeasurementId.HttpRequests, elapsed, new Dictionary<string, string>
        {
            ["route"]  = route,
            ["method"] = method,
            ["status"] = status.ToString()
        });
}