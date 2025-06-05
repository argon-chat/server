namespace Argon.Metrics.Gauges;

// var threshold = new ThresholdWatchGauge(metrics, MeasurementId.Dotnet.Gc.TotalMemory, 512, triggerAbove: true);
// await threshold.ObserveAsync(600);
public class ThresholdWatchGauge(
    IMetricsCollector collector,
    MeasurementId measurement,
    double threshold,
    bool triggerAbove,
    IDictionary<string, string>? tags = null)
{
    private bool _lastTriggered = false;

    public async Task ObserveAsync(double value)
    {
        var triggered = triggerAbove ? value > threshold : value < threshold;

        if (triggered && !_lastTriggered)
        {
            await collector.CountAsync(measurement, 1, tags);
        }

        _lastTriggered = triggered;
    }
}