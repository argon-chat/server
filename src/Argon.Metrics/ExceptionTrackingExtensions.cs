namespace Argon.Metrics;

public static class ExceptionTrackingExtensions
{
    /// <summary>
    /// Asynchronously records an exception occurrence as a metric, tagging it with the exception type and optional component name.
    /// </summary>
    /// <param name="ex">The exception to be tracked.</param>
    /// <param name="component">An optional component name to associate with the exception metric.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task TrackExceptionAsync(this IMetricsCollector collector, Exception ex,
        string? component = null)
    {
        var tags = new Dictionary<string, string>
        {
            ["exception"] = ex.GetType().Name
        };

        if (component != null)
            tags["component"] = component;

        return collector.CountAsync(MeasurementId.Exceptions, 1, tags);
    }
}