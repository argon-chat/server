namespace Argon.Metrics.Gauges;


// var trigger = new ConditionTriggerGauge(metrics, MeasurementId.Exceptions, val => val > 500, new() { ["stage"] = "auth" });
// await trigger.ObserveAsync(530);
public class ConditionTriggerGauge(
    IMetricsCollector collector,
    MeasurementId measurement,
    Func<double, bool> condition,
    IDictionary<string, string>? tags = null)
{
    public Task ObserveAsync(double value)
        => condition(value) ? collector.CountAsync(measurement, 1, tags) : Task.CompletedTask;
}