namespace Argon.Services;

using Metrics;
using Metrics.Gauges;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using StackExchange.Redis;
using static Metrics.MeasurementId;

public class RedisConnectionPool(
    IConfiguration cfg,
    ILogger<RedisConnectionPool> logger,
    IOptions<RedisConnectionPoolOptions> options,
    IMetricsCollector metrics) : BackgroundService, IRedisPoolConnections
{
    private readonly ConcurrentBag<IConnectionMultiplexer> ConnectionPool = new();

    private readonly PeriodicTimer timer = new(TimeSpan.FromMinutes(2));

    private const uint MaxAllowedSize = 2000;

    private       ulong Taken;
    private       ulong Allocated;
    private       ulong DefaultSize = 16;
    private       ulong MaxSizeOveruseCounter;
    private const ulong MaxOveruseThreshold = 5;

    private readonly EmaGauge             takenGauge          = new(metrics, RedisMetrics.PoolTaken);
    private readonly DeltaGauge           allocatedDelta      = new(metrics, RedisMetrics.PoolAllocated);
    private readonly CountPerTagGauge     cleanupErrorCounter = new(metrics, RedisMetrics.PoolCleanupError);
    private readonly HistogramBucketGauge cleanupHistogram    = new(metrics, RedisMetrics.PoolCleanup, [1, 2, 5, 10, 20]);

    public ConnectionScope Rent()
    {
        Interlocked.Increment(ref Taken);

        var currentAllocated = Interlocked.Read(ref Allocated);
        if (currentAllocated > Interlocked.Read(ref DefaultSize))
        {
            var count = Interlocked.Increment(ref MaxSizeOveruseCounter);
            if (count >= MaxOveruseThreshold)
            {
                TryIncreaseMaxSize();
                Interlocked.Exchange(ref MaxSizeOveruseCounter, 0);
            }
        }

        return ConnectionPool.TryTake(out var multiplexer)
            ? new ConnectionScope(multiplexer, this)
            : new ConnectionScope(EnsureNew(), this);
    }

    public void Return(IConnectionMultiplexer connection)
    {
        Interlocked.Decrement(ref Taken);
        ConnectionPool.Add(connection);
    }


    private IConnectionMultiplexer EnsureNew()
    {
        var value = Interlocked.Increment(ref Allocated);
        _ = allocatedDelta.ObserveAsync(value);
        return ConnectionMultiplexer.Connect(cfg.GetConnectionString("cache")!);
    }

    private void Populate()
    {
        foreach (var _ in Enumerable.Range(0, (int)Math.Max(6, (int)Interlocked.Read(ref DefaultSize) / 2u)).Select(_ => EnsureNew()))
        {
        }
    }

    private async Task Cleanup()
    {
        var allocated = Interlocked.Read(ref Allocated);
        var taken     = Interlocked.Read(ref Taken);
        var maxSize   = Interlocked.Read(ref DefaultSize);
        var excess    = allocated - taken;

        if (excess <= maxSize) return;

        var rmRequired = excess - maxSize;
        logger.LogInformation("Cleaning up {Count} excess redis connections", rmRequired);

        var removed = 0;

        while (rmRequired > 0 && ConnectionPool.TryTake(out var mux))
        {
            try
            {
                await mux.CloseAsync();
                await mux.DisposeAsync();
                Interlocked.Decrement(ref Allocated);
                rmRequired--;
                removed++;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to clean up Redis connection.");
                _ = cleanupErrorCounter.CountAsync("type", e.GetType().Name);
            }
        }

        _ = cleanupHistogram.ObserveAsync(removed);
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Interlocked.Exchange(ref DefaultSize, options.Value.MaxSize);
        Populate();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var allocated = Interlocked.Read(ref Allocated);
                var taken     = Interlocked.Read(ref Taken);

                logger.LogDebug("Cleaning up redis pool call, allocated: {allocated}, taken: {taken}", allocated, taken);

                _ = takenGauge.ObserveAsync(taken);

                await Cleanup();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Redis cleanup service stopping...");
        }
    }

    private DateTime lastScaleUp = DateTime.UtcNow;

    private void TryIncreaseMaxSize()
    {
        var current = Interlocked.Read(ref DefaultSize);

        if (current >= MaxAllowedSize)
        {
            logger.LogCritical("Redis pool reached max allowed size {MaxAllowedSize}. Consider scaling infrastructure or adding replicas!",
                MaxAllowedSize);
            return;
        }

        var now                = DateTime.UtcNow;
        var timeSinceLastScale = now - lastScaleUp;
        if (timeSinceLastScale < TimeSpan.FromMinutes(1))
            return;

        var increment = current < 20 ? 2u : current < 50 ? 5u : 10u;
        var proposed  = Math.Min(MaxAllowedSize, current + increment);

        _ = metrics.CountAsync(RedisMetrics.PoolScaleUp, 1, new()
        {
            ["from"] = current.ToString(),
            ["to"]   = proposed.ToString()
        });

        if (proposed <= current)
            return;
        logger.LogInformation("Auto-scaling Redis pool size from {Old} to {New}", current, proposed);
        Interlocked.Exchange(ref DefaultSize, proposed);
        lastScaleUp = now;
    }
}