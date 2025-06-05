namespace Argon.Metrics.Gauges;

public class DeltaGauge(IMetricsCollector collector, MeasurementId measurement, IDictionary<string, string>? tags = null)
{
    private double? _previous;

    public async Task ObserveAsync(double current)
    {
        if (_previous.HasValue)
        {
            var delta = current - _previous.Value;
            await collector.ObserveAsync(measurement, delta, tags);
        }

        _previous = current;
    }
}