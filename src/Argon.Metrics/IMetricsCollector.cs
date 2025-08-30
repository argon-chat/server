namespace Argon.Metrics;

public interface IMetricsCollector
{
    Task CountAsync(MeasurementId measurement, long value = 1, Dictionary<string, string>? tags = null);
    Task CountAsync(MeasurementId measurement, Dictionary<string, string> tags);
    Task CountExactAsync(MeasurementId measurement, long value = 1, DateTime? timestamp = null);


    Task ObserveAsync(MeasurementId measurement, double value, Dictionary<string, string>? tags = null);
    Task DurationAsync(MeasurementId measurement, TimeSpan duration, Dictionary<string, string>? tags = null);
    Task DurationAsync(MeasurementId measurement, TimeSpan duration, string scope, string operation);

    ILogger<IMetricsCollector> Logger { get; }
}