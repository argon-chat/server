namespace Argon.Metrics;

using InfluxDB3.Client.Write;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

public class InfluxMetricsCollector(IPointBuffer writer, IServiceProvider provider, IWebHostEnvironment env) : IMetricsCollector
{
    private readonly Lazy<string?> Datacenter = new(() => provider.GetKeyedService<string>("dc"));

    private PointData BuildPoint(MeasurementId measurement, Dictionary<string, string>? tags)
    {
        var point = PointData.Measurement(measurement.key);
        if (tags != null) foreach (var tag in tags)
            point.SetField(tag.Key, tag.Value);

        if (Datacenter.Value is { } dc)
            point.SetField("dc", dc);
        if (Environment.GetEnvironmentVariable("ARGON_ROLE") is { } role)
            point.SetField("role", role);
        if (Environment.GetEnvironmentVariable("NODE") is { } node)
            point.SetField("node", node);
        return point;
    }
     
    public async Task CountAsync(MeasurementId measurement, long value = 1, Dictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetDoubleField("value", value)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }

    public Task CountAsync(MeasurementId measurement, Dictionary<string, string> tags)
        => CountAsync(measurement, 1, tags);


    public async Task ObserveAsync(MeasurementId measurement, double value, Dictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetDoubleField("value", value)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }

    public async Task DurationAsync(MeasurementId measurement, TimeSpan duration, Dictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetDoubleField("duration_ms", duration.TotalMilliseconds)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }

    public async Task DurationAsync(MeasurementId measurement, TimeSpan duration, string scope, string operation)
    {
        var point = BuildPoint(measurement, [])
           .SetDoubleField("duration_ms", duration.TotalMilliseconds)
           .SetStringField("scope", scope)
           .SetStringField("operation", operation)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }
}