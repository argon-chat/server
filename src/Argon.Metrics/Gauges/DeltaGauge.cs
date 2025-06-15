namespace Argon.Metrics.Gauges;


/*
   SELECT mean("value") AS "delta"
   FROM "delta_gauge"
   WHERE $timeFilter
   GROUP BY time($__interval), "tag1"
   FILL(null)
 */
public class DeltaGauge(IMetricsCollector collector, MeasurementId measurement, Dictionary<string, string>? tags = null)
{
    private double? _previous;

    public async Task ObserveAsync(double current)
    {
        if (_previous.HasValue)
        {
            var delta = current - _previous.Value;
            await collector.ObserveAsync(measurement, delta, tags);
        }

        _previous = current;
    }
}

public class DeltaGaugeGlobal(MeasurementId measurement, Dictionary<string, string>? tags = null)
{
    private double? _previous;

    public async Task ObserveAsync(IMetricsCollector collector, double current)
    {
        if (_previous.HasValue)
        {
            var delta = current - _previous.Value;
            await collector.ObserveAsync(measurement, delta, tags);
        }

        _previous = current;
    }
}
