namespace Argon.Metrics;

using InfluxDB3.Client;
using InfluxDB3.Client.Write;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;

public class InfluxBatchWriter(Lazy<InfluxDBClient> client, IOptions<InfluxDbOptions> options, ILogger<IPointBuffer> logger)
    : BackgroundService, IPointBuffer
{
    private readonly InfluxDbOptions            _options       = options.Value;
    private readonly ConcurrentQueue<PointData> _buffer        = new();
    private readonly TimeSpan                   _flushInterval = TimeSpan.FromSeconds(5);
    private const    int                        MaxBufferSize  = 10000;

    public void Enqueue(PointData point)
    {
        if (!_options.IsEnabled) return;
        _buffer.Enqueue(point);
    }

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
                await client.Value.WritePointsAsync(
                    points: list,
                    database: _options.Database,
                    cancellationToken: stoppingToken
                );
            }
            catch (OutOfMemoryException e)
            {
                logger.LogCritical(e, "failed write metrics, current queue: {count}", _buffer.Count);
                logger.LogCritical("metrics buffer out of memory — dropping all point");
                list.Clear();
                _buffer.Clear();
                GC.Collect();
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed write metrics, current queue: {count}", _buffer.Count);

                if (_buffer.Count >= MaxBufferSize)
                {
                    logger.LogWarning("metrics buffer overflow — dropping oldest point");
                    foreach (var point in list.Skip(Math.Abs(_buffer.Count - MaxBufferSize)))
                        _buffer.Enqueue(point);
                }
                else
                    foreach (var point in list)
                        _buffer.Enqueue(point);
            }
        }
    }
}