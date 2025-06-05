namespace Argon.Metrics;

using InfluxDB3.Client.Write;

public class InfluxMetricsCollector(IPointBuffer writer) : IMetricsCollector
{
    private static PointData BuildPoint(MeasurementId measurement, IDictionary<string, string>? tags)
    {
        var point = PointData.Measurement(measurement.key);
        if (tags == null) return point;
        foreach (var tag in tags)
            point.SetTag(tag.Key, tag.Value);
        return point;
    }

    public async Task CountAsync(MeasurementId measurement, long value = 1, IDictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetIntegerField("value", value)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }

    public Task CountAsync(MeasurementId measurement, IDictionary<string, string> tags)
        => CountAsync(measurement, 1, tags);


    public async Task ObserveAsync(MeasurementId measurement, double value, IDictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetDoubleField("value", value)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }

    public async Task DurationAsync(MeasurementId measurement, TimeSpan duration, IDictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetDoubleField("duration_ms", duration.TotalMilliseconds)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }
}