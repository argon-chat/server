namespace Argon.Services;

using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class RedisConnectionPool(
    IConfiguration cfg,
    ILogger<RedisConnectionPool> logger,
    IOptions<RedisConnectionPoolOptions> options) : BackgroundService, IRedisPoolConnections
{
    private readonly ConcurrentBag<IConnectionMultiplexer> connectionPool = new();
    private readonly PeriodicTimer                         timer          = new(TimeSpan.FromMinutes(2));

    private const uint MaxAllowedSize = 2000;

    private       long     taken;
    private       long     allocated;
    private       long     defaultSize = options.Value.MaxSize;
    private       long     maxSizeOveruseCounter;
    private const long     MaxOveruseThreshold = 5;
    private       DateTime lastScaleUp         = DateTime.UtcNow;

    public ConnectionScope Rent()
    {
        Interlocked.Increment(ref taken);

        var currentAllocated = Interlocked.Read(ref allocated);
        if (currentAllocated > Interlocked.Read(ref defaultSize))
        {
            var count = Interlocked.Increment(ref maxSizeOveruseCounter);
            if (count >= MaxOveruseThreshold)
            {
                TryIncreaseMaxSize();
                Interlocked.Exchange(ref maxSizeOveruseCounter, 0);
            }
        }

        if (connectionPool.TryTake(out var mux))
        {
            if (IsUsable(mux))
            {
                return new ConnectionScope(mux, this);
            }

            _ = DisposeMuxAsync(mux);
            Interlocked.Decrement(ref allocated);

            var fresh = EnsureNew();
            return new ConnectionScope(fresh, this);
        }
        else
        {
            var fresh = EnsureNew();
            return new ConnectionScope(fresh, this);
        }
    }

    internal async Task ReturnAsync(IConnectionMultiplexer connection)
    {
        Interlocked.Decrement(ref taken);

        if (!IsUsable(connection))
        {
            await DisposeMuxAsync(connection);
            Interlocked.Decrement(ref allocated);
            return;
        }

        connectionPool.Add(connection);
    }

    internal async Task ReturnFaultedAsync(IConnectionMultiplexer connection)
    {
        Interlocked.Decrement(ref taken);

        try
        {
            await DisposeMuxAsync(connection);
        }
        finally
        {
            Interlocked.Decrement(ref allocated);
        }
    }

    private IConnectionMultiplexer EnsureNew()
    {
        var allocated = Interlocked.Increment(ref this.allocated);
        logger.LogDebug("Allocating new Redis connection. Allocated: {Allocated}", allocated);

        return ConnectionMultiplexer.Connect(cfg.GetConnectionString("cache")!);
    }

    private void PopulateInitial()
    {
        var ds     = Interlocked.Read(ref this.defaultSize);
        var target = (int)Math.Max(6, ds / 2);

        for (var i = 0; i < target; i++)
        {
            var mux = EnsureNew();
            connectionPool.Add(mux);
        }
    }

    private bool IsUsable(IConnectionMultiplexer mux)
    {
        if (!mux.IsConnected)
            return false;

        try
        {
            var endpoints = mux.GetEndPoints();
            if (endpoints.Length == 0)
                return false;

            return endpoints.Select(endpoint => mux.GetServer(endpoint)).Select(server => server.IsReplica || server.IsSlave)
               .Any(isReplica => !isReplica);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to validate Redis connection, treating as unusable.");
            return false;
        }
    }

    private async Task DisposeMuxAsync(IConnectionMultiplexer mux)
    {
        try
        {
            await mux.CloseAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error during Redis connection CloseAsync.");
        }

        try
        {
            await mux.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error during Redis connection DisposeAsync.");
        }
    }

    private async Task Cleanup()
    {
        var allocated = Interlocked.Read(ref this.allocated);
        var taken     = Interlocked.Read(ref this.taken);
        var maxSize   = Interlocked.Read(ref defaultSize);
        var excess    = allocated - taken;

        if (excess <= maxSize)
            return;

        var rmRequired = excess - maxSize;
        logger.LogInformation("Cleaning up {Count} excess redis connections", rmRequired);

        var removed = 0;

        while (rmRequired > 0 && connectionPool.TryTake(out var mux))
        {
            try
            {
                await DisposeMuxAsync(mux);
                Interlocked.Decrement(ref this.allocated);
                rmRequired--;
                removed++;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to clean up Redis connection.");
            }
        }

        logger.LogInformation("Removed {Removed} redis connections during cleanup", removed);
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting RedisConnectionPool with default size {Size}", defaultSize);

        PopulateInitial();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var allocated = Interlocked.Read(ref this.allocated);
                var taken     = Interlocked.Read(ref this.taken);

                logger.LogDebug(
                    "Cleaning up redis pool call, allocated: {allocated}, taken: {taken}",
                    allocated, taken);

                await Cleanup();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Redis cleanup service stopping...");
        }
    }

    private void TryIncreaseMaxSize()
    {
        var current = Interlocked.Read(ref defaultSize);

        if (current >= MaxAllowedSize)
        {
            logger.LogCritical(
                "Redis pool reached max allowed size {MaxAllowedSize}. Consider scaling infrastructure or adding replicas!",
                MaxAllowedSize);
            return;
        }

        var now                = DateTime.UtcNow;
        var timeSinceLastScale = now - lastScaleUp;
        if (timeSinceLastScale < TimeSpan.FromMinutes(1))
            return;

        var increment = current < 20
            ? 2u
            : current < 50
                ? 5u
                : 10u;

        var proposed = Math.Min(MaxAllowedSize, current + increment);

        if (proposed <= current)
            return;

        logger.LogInformation("Auto-scaling Redis pool size from {Old} to {New}", current, proposed);
        Interlocked.Exchange(ref defaultSize, proposed);
        lastScaleUp = now;
    }

    public async override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping RedisConnectionPool, disposing all connections...");

        timer.Dispose();

        while (connectionPool.TryTake(out var mux))
        {
            await DisposeMuxAsync(mux);
        }

        await base.StopAsync(cancellationToken);
    }
}