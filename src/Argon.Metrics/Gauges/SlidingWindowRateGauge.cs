namespace Argon.Metrics.Gauges;

/// <example>
///public class EventRateService(IMetricsCollector metrics)
///{
///    private readonly SlidingWindowRateGauge _rate = new(metrics, MeasurementId.HttpRequests, TimeSpan.FromSeconds(60), new Dictionary&lt;string, string&gt;
///    {
///        ["source"] = "gateway"
///    });
///    public void OnEventProcessed()
///        => _rate.Add();
///    public async Task FlushAsync() => await _rate.FlushAsync();
///}
/// </example>
public class SlidingWindowRateGauge(
    IMetricsCollector collector,
    MeasurementId measurement,
    TimeSpan window, 
    Dictionary<string, string>? tags = null)
{
    private readonly LinkedList<(DateTime timestamp, int count)> _events = new();

    public void Add(int count = 1)
    {
        var now = DateTime.UtcNow;
        _events.AddLast((now, count));
        Cleanup(now);
    }

    public async Task FlushAsync()
    {
        var now = DateTime.UtcNow;
        Cleanup(now);

        var total = _events.Sum(x => x.count);
        var rate  = total / window.TotalSeconds;

        await collector.ObserveAsync(measurement, rate, tags);
    }

    private void Cleanup(DateTime now)
    {
        while (_events.Count > 0 && (now - _events.First!.Value.timestamp) > window)
            _events.RemoveFirst();
    }
}