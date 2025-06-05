namespace Argon.Metrics;

public class RateCounter(
    IMetricsCollector collector,
    MeasurementId measurement,
    IDictionary<string, string>? tags = null)
{
    private long     _count     = 0;
    private DateTime _lastFlush = DateTime.UtcNow;

    public void Increment(long value = 1)
        => Interlocked.Add(ref _count, value);

    public async Task FlushAsync()
    {
        var now     = DateTime.UtcNow;
        var elapsed = (now - _lastFlush).TotalSeconds;
        var count   = Interlocked.Exchange(ref _count, 0);
        _lastFlush = now;

        if (elapsed > 0)
        {
            var rate = count / elapsed;
            await collector.ObserveAsync(measurement, rate, tags);
        }
    }
}