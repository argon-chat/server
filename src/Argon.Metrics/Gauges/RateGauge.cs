namespace Argon.Metrics.Gauges;

public class RateGauge(IMetricsCollector collector, MeasurementId measurement, IDictionary<string, string>? tags = null)
{
    private long     _lastValue = 0;
    private DateTime _lastTime  = DateTime.UtcNow;

    public async Task ObserveAsync(long currentTotal)
    {
        var now     = DateTime.UtcNow;
        var elapsed = (now - _lastTime).TotalSeconds;
        if (elapsed <= 0)
            return;

        var delta = currentTotal - _lastValue;
        var rate  = delta / elapsed;

        _lastValue = currentTotal;
        _lastTime  = now;

        await collector.ObserveAsync(measurement, rate, tags);
    }
}