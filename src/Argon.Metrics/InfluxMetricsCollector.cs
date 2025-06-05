namespace Argon.Metrics;

using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;

public class InfluxMetricsCollector(IPointBuffer writer) : IMetricsCollector
{
    private static PointData CreatePoint(MeasurementId measurement, string field, object value, IDictionary<string, string>? tags)
    {
        var point = PointData.Measurement(measurement.key)
           .Timestamp(DateTime.UtcNow, WritePrecision.Ns);

        if (tags != null)
            point = tags.Aggregate(point, (current, tag) => current.Tag(tag.Key, tag.Value));

        point = point.Field(field, value);
        return point;
    }

    public Task CountAsync(MeasurementId measurement, long value = 1, IDictionary<string, string>? tags = null)
    {
        writer.Enqueue(CreatePoint(measurement, "count", value, tags));
        return Task.CompletedTask;
    }

    public Task ObserveAsync(MeasurementId measurement, double value, IDictionary<string, string>? tags = null)
    {
        writer.Enqueue(CreatePoint(measurement, "value", value, tags));
        return Task.CompletedTask;
    }

    public Task DurationAsync(MeasurementId measurement, TimeSpan duration, IDictionary<string, string>? tags = null)
    {
        writer.Enqueue(CreatePoint(measurement, "duration_ms", duration.TotalMilliseconds, tags));
        return Task.CompletedTask;
    }
}