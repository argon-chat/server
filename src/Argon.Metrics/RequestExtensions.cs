namespace Argon.Metrics;

public static class RequestExtensions
{
    /// <summary>
        /// Records the duration of an HTTP request as a metric with associated route, method, and status code tags.
        /// </summary>
        /// <param name="route">The route or endpoint of the HTTP request.</param>
        /// <param name="status">The HTTP status code returned by the request.</param>
        /// <param name="elapsed">The elapsed time for the request.</param>
        /// <param name="method">The HTTP method used for the request. Defaults to "GET".</param>
        /// <returns>A task representing the asynchronous metric recording operation.</returns>
        public static Task MarkRequest(this IMetricsCollector collector, string route, int status, TimeSpan elapsed, string method = "GET")
        => collector.DurationAsync(MeasurementId.HttpRequests, elapsed, new Dictionary<string, string>
        {
            ["route"]  = route,
            ["method"] = method,
            ["status"] = status.ToString()
        });
}