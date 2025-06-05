namespace Argon.Metrics.Gauges;

public class EmaGauge(IMetricsCollector collector, MeasurementId measurement, double alpha = 0.2, IDictionary<string, string>? tags = null)
{
    private double? _ema;

    public async Task ObserveAsync(double value)
    {
        _ema = _ema.HasValue
            ? alpha * value + (1 - alpha) * _ema.Value
            : value;

        await collector.ObserveAsync(measurement, _ema.Value, tags);
    }
}