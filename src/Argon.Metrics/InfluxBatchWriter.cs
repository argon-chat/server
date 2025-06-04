namespace Argon.Metrics;

using System.Collections.Concurrent;
using InfluxDB.Client;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Options;

public class InfluxBatchWriter(Lazy<InfluxDBClient> client, IOptions<InfluxDbOptions> options, ILogger<IPointBuffer> logger)
    : BackgroundService, IPointBuffer
{
    private readonly InfluxDbOptions            _options       = options.Value;
    private readonly ConcurrentQueue<PointData> _buffer        = new();
    private readonly TimeSpan                   _flushInterval = TimeSpan.FromSeconds(5);

    public void Enqueue(PointData point) => _buffer.Enqueue(point);

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_flushInterval, stoppingToken);

            if (!_options.IsEnabled) continue;

            var list = new List<PointData>();
            while (_buffer.TryDequeue(out var point))
                list.Add(point);

            if (list.Count <= 0)
                continue;

            try
            {
                var writer = client.Value.GetWriteApiAsync();
                await writer.WritePointsAsync(list, _options.Bucket, _options.Org, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed write metrics");
                foreach (var point in list)
                    _buffer.Enqueue(point);
            }
        }
    }
}