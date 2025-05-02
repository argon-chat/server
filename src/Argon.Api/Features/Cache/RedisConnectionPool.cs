namespace Argon.Services;

using StackExchange.Redis;

public class RedisConnectionPool(IConfiguration cfg, ILogger<RedisConnectionPool> logger, IOptions<RedisConnectionPoolOptions> options) : BackgroundService, IRedisPoolConnections
{
    private readonly ConcurrentBag<IConnectionMultiplexer> ConnectionPool = new();

    private readonly PeriodicTimer timer = new(TimeSpan.FromMinutes(2));

    private const uint MaxAllowedSize = 2000;

    private       ulong Taken;
    private       ulong Allocated;
    private       ulong DefaultSize = 16;
    private       ulong MaxSizeOveruseCounter;
    private const ulong MaxOveruseThreshold = 5;

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
        Interlocked.Increment(ref Allocated);
        return ConnectionMultiplexer.Connect(cfg.GetConnectionString("cache")!);
    }

    private void Populate()
    {
        foreach (var _ in Enumerable.Range(0, (int)Math.Max(6, (int)Interlocked.Read(ref DefaultSize) / 2u)).Select(_ => EnsureNew())) { }
    }

    private async Task Cleanup()
    {
        var (allocated, taken) = (Interlocked.Read(ref Allocated), Interlocked.Read(ref Taken));
        var maxSize = Interlocked.Read(ref DefaultSize);
        var excess  = allocated - taken;
        if (excess <= maxSize) return;

        var rmRequired = excess - maxSize;
        logger.LogWarning("Cleaning up {Count} excess redis connections", rmRequired);

        while (rmRequired > 0 && ConnectionPool.TryTake(out var mux))
        {
            try
            {
                await mux.CloseAsync();
                await mux.DisposeAsync();
                Interlocked.Decrement(ref Allocated);
                rmRequired--;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to clean up Redis connection.");
            }
        }
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Interlocked.Exchange(ref DefaultSize, options.Value.MaxSize);
        Populate();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var (allocated, taken) = (Interlocked.Read(ref Allocated), Interlocked.Read(ref Taken));
                logger.LogInformation("Cleaning up redis pool call, currently allocated: {connectionAllocated}, in use: {connectionTaken}", allocated, taken);
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
            logger.LogCritical("Redis pool reached max allowed size {MaxAllowedSize}. Consider scaling infrastructure or adding replicas!", MaxAllowedSize);
            return;
        }

        var now                = DateTime.UtcNow;
        var timeSinceLastScale = now - lastScaleUp;
        if (timeSinceLastScale < TimeSpan.FromMinutes(1))
            return;

        var increment = current < 20 ? 2u : current < 50 ? 5u : 10u;
        var proposed  = Math.Min(MaxAllowedSize, current + increment);

        if (proposed <= current) 
            return;
        logger.LogWarning("Auto-scaling Redis pool size from {Old} to {New}", current, proposed);
        Interlocked.Exchange(ref DefaultSize, proposed);
        lastScaleUp = now;
    }
}