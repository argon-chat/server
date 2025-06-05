namespace Argon.Metrics.Gauges;

public class MovingAverageGauge(
    IMetricsCollector collector,
    MeasurementId measurement,
    int windowSize = 10,
    IDictionary<string, string>? tags = null)
{
    private readonly Queue<double> _window = new();

    public async Task ObserveAsync(double value)
    {
        _window.Enqueue(value);
        if (_window.Count > windowSize)
            _window.Dequeue();

        var average = _window.Average();
        await collector.ObserveAsync(measurement, average, tags);
    }
}