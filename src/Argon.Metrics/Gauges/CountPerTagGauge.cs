namespace Argon.Metrics.Gauges;

public class CountPerTagGauge(IMetricsCollector collector, MeasurementId measurement)
{
    public Task CountAsync(string tagKey, string tagValue)
        => collector.CountAsync(measurement, 1, new Dictionary<string, string>
        {
            [tagKey] = tagValue
        });
}