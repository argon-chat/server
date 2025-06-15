namespace Argon.Metrics;

using System.Collections.Concurrent;

public interface ICounters
{
    void Increment(MeasurementId measurementId, long count = 1);
    void Decrement(MeasurementId measurementId);

    Task ReportAllAsync();
}
public class CounterStorage(IMetricsCollector collector) : ICounters
{
    private readonly ConcurrentDictionary<MeasurementId, GlobalCounter> counters = new();

    public void Increment(MeasurementId measurementId, long count = 1)
        => counters.GetOrAdd(measurementId, id => new GlobalCounter(id, false)).Increment(count);

    public void Decrement(MeasurementId measurementId)
        => counters.GetOrAdd(measurementId, id => new GlobalCounter(id, false)).Decrement();

    public async Task ReportAllAsync()
        => await Task
           .WhenAll(counters.Keys.Select(counters.GetValueOrDefault)
               .Where(x => x is not null)
               .Select(x => x.ReportAsync(collector)));
}