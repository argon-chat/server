namespace Argon.Metrics.Gauges;

//public class ChannelLoadReporter(IMetricsCollector metrics)
//{
//    private readonly MultiGaugeGroup _group = new(metrics, MeasurementId.ActiveUsers, "channel");

//    public async Task ReportAsync(IDictionary<string, int> channelUsers)
//    {
//        var values = channelUsers.ToDictionary(kv => kv.Key, kv => (double)kv.Value);
//        await _group.ObserveAsync(values);
//    }
//}

public class MultiGaugeGroup(
    IMetricsCollector collector,
    MeasurementId measurement,
    string tagKey,
    IDictionary<string, string>? baseTags = null)
{
    public Task ObserveAsync(Dictionary<string, double> values)
    {
        var tasks = new List<Task>(values.Count);
        foreach (var (key, val) in values)
        {
            var tags = baseTags is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(baseTags);

            tags[tagKey] = key;

            tasks.Add(collector.ObserveAsync(measurement, val, tags));
        }

        return Task.WhenAll(tasks);
    }
}