namespace Argon.Metrics;

using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;

public class InfluxMetricsCollector(IPointBuffer writer) : IMetricsCollector
{
    /// <summary>
    /// Constructs an InfluxDB <c>PointData</c> object for a given measurement, setting the measurement name, current UTC timestamp with nanosecond precision, an optional set of tags, and a single field with its value.
    /// </summary>
    /// <param name="measurement">The measurement identifier.</param>
    /// <param name="field">The name of the field to set.</param>
    /// <param name="value">The value to assign to the field.</param>
    /// <param name="tags">Optional tags to associate with the point.</param>
    /// <returns>A <c>PointData</c> instance representing the metric point.</returns>
    private static PointData CreatePoint(MeasurementId measurement, string field, object value, IDictionary<string, string>? tags)
    {
        var point = PointData.Measurement(measurement.key)
           .Timestamp(DateTime.UtcNow, WritePrecision.Ns);

        if (tags != null)
            point = tags.Aggregate(point, (current, tag) => current.Tag(tag.Key, tag.Value));

        point = point.Field(field, value);
        return point;
    }

    /// <summary>
    /// Enqueues a count metric point for the specified measurement with an optional value and tags.
    /// </summary>
    /// <param name="measurement">The measurement identifier for the metric.</param>
    /// <param name="value">The count value to record. Defaults to 1.</param>
    /// <param name="tags">Optional tags to associate with the metric point.</param>
    /// <returns>A completed task.</returns>
    public Task CountAsync(MeasurementId measurement, long value = 1, IDictionary<string, string>? tags = null)
    {
        writer.Enqueue(CreatePoint(measurement, "count", value, tags));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues an observed value metric for the specified measurement with optional tags.
    /// </summary>
    /// <param name="measurement">The measurement identifier.</param>
    /// <param name="value">The observed value to record.</param>
    /// <param name="tags">Optional tags to associate with the metric.</param>
    /// <returns>A completed task.</returns>
    public Task ObserveAsync(MeasurementId measurement, double value, IDictionary<string, string>? tags = null)
    {
        writer.Enqueue(CreatePoint(measurement, "value", value, tags));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues a duration metric point with the specified measurement and optional tags, recording the duration in milliseconds.
    /// </summary>
    /// <param name="measurement">The measurement identifier for the metric.</param>
    /// <param name="duration">The duration to record.</param>
    /// <param name="tags">Optional tags to associate with the metric point.</param>
    /// <returns>A completed task.</returns>
    public Task DurationAsync(MeasurementId measurement, TimeSpan duration, IDictionary<string, string>? tags = null)
    {
        writer.Enqueue(CreatePoint(measurement, "duration_ms", duration.TotalMilliseconds, tags));
        return Task.CompletedTask;
    }
}