namespace Argon.Metrics.Gauges;


// var limiter = new BurstLimiterGauge(metrics, MeasurementId.Exceptions, 100, TimeSpan.FromSeconds(10));
// await limiter.TryTriggerAsync();
public class BurstLimiterGauge(
    IMetricsCollector collector,
    MeasurementId measurement,
    int thresholdPerWindow,
    TimeSpan window,
    IDictionary<string, string>? tags = null)
{
    private int      _count       = 0;
    private DateTime _windowStart = DateTime.UtcNow;

    public async Task TryTriggerAsync()
    {
        var now = DateTime.UtcNow;
        if (now - _windowStart > window)
        {
            _count       = 0;
            _windowStart = now;
        }

        _count++;

        if (_count > thresholdPerWindow)
        {
            await collector.CountAsync(measurement, 1, tags);
            _count       = 0;
            _windowStart = now;
        }
    }
}