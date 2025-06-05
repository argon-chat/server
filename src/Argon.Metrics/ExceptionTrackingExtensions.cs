namespace Argon.Metrics;

public static class ExceptionTrackingExtensions
{
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