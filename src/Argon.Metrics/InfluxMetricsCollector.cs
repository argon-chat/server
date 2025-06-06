namespace Argon.Metrics;

using InfluxDB3.Client.Write;
using Microsoft.Extensions.DependencyInjection;

public class InfluxMetricsCollector(IPointBuffer writer, IServiceProvider provider, IWebHostEnvironment env) : IMetricsCollector
{
    private readonly Lazy<string?> Datacenter = new(() => provider.GetKeyedService<string>("dc"));

    private PointData BuildPoint(MeasurementId measurement, Dictionary<string, string>? tags)
    {
        var point = PointData.Measurement(measurement.key);
        if (tags == null) return point;
        foreach (var tag in tags)
            point.SetTag(tag.Key, tag.Value);

        if (Datacenter.Value is { } dc)
            point.SetTag("dc", dc);
        if (Environment.GetEnvironmentVariable("ARGON_ROLE") is { } role)
            point.SetTag("role", role);
        if (Environment.GetEnvironmentVariable("NODE") is { } node)
            point.SetTag("node", node);
        return point;
    }
     
    public async Task CountAsync(MeasurementId measurement, long value = 1, Dictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetField("value", value)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }

    public Task CountAsync(MeasurementId measurement, Dictionary<string, string> tags)
        => CountAsync(measurement, 1, tags);


    public async Task ObserveAsync(MeasurementId measurement, double value, Dictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetField("value", value)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }

    public async Task DurationAsync(MeasurementId measurement, TimeSpan duration, Dictionary<string, string>? tags = null)
    {
        var point = BuildPoint(measurement, tags)
           .SetField("duration_ms", duration.TotalMilliseconds)
           .SetTimestamp(DateTime.UtcNow);

        writer.Enqueue(point);
    }
}