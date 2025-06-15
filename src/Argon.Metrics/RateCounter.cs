namespace Argon.Metrics;

public class GlobalCounter(MeasurementId measurement, bool canBeLessZero = true)
{
    private long count_1 = 0;
    private ulong count_2 = 0;

    public void Increment(long value = 1)
    {
        if (canBeLessZero)
            Interlocked.Add(ref count_1, value);
        else
            Interlocked.Add(ref count_2, (ulong)value);
    }

    public void Decrement()
    {
        if (canBeLessZero)
            Interlocked.Decrement(ref count_1);
        else
            Interlocked.Decrement(ref count_2);
    }

    public Task ReportAsync(IMetricsCollector collector)
        => canBeLessZero ? collector.CountAsync(measurement, count_1) : collector.CountAsync(measurement, (long)count_2);
}

public class RateCounter(
    IMetricsCollector collector,
    MeasurementId measurement,
    Dictionary<string, string>? tags = null)
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

    private int current_times;

    public async Task FlushAsync(int times)
    {
        current_times++;

        if (current_times < times) return;

        current_times = 0;

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