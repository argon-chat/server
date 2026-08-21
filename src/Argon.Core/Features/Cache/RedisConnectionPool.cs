namespace Argon.Services;

using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class RedisConnectionPool : BackgroundService, IRedisPoolConnections
{
    private readonly string                       profileName;
    private readonly ConfigurationOptions         configuration;
    private readonly ILogger<RedisConnectionPool> logger;

    private readonly ConcurrentBag<IConnectionMultiplexer> connectionPool = new();
    private readonly PeriodicTimer                         timer          = new(TimeSpan.FromMinutes(2));

    private const uint MaxAllowedSize = 2000;

    private       long     taken;
    private       long     allocated;
    private       long     defaultSize;
    private       long     maxSizeOveruseCounter;
    private const long     MaxOveruseThreshold = 5;
    private       DateTime lastScaleUp         = DateTime.UtcNow;

    // Live pools keyed by profile, so metrics can be reported per profile instead of clobbering
    // shared static counters when several pools (cache / hybrid-cache / orleans-storage) coexist.
    private static readonly ConcurrentDictionary<string, RedisConnectionPool> Pools = new();

    public RedisConnectionPool(string profileName, RedisProfileOptions profile, uint maxSize, ILogger<RedisConnectionPool> logger)
    {
        this.profileName  = profileName;
        this.logger       = logger;
        this.defaultSize  = maxSize;
        this.configuration                 = ConfigurationOptions.Parse(profile.ConnectionString!);
        this.configuration.DefaultDatabase = profile.Database;

        Pools[profileName] = this;
    }

    public ConnectionScope Rent()
    {
        Interlocked.Increment(ref taken);

        CacheInstruments.ConnectionsRented.Add(1, ProfileTag);

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
            CacheInstruments.ConnectionsDeallocated.Add(1, ProfileTag);

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
            CacheInstruments.ConnectionsDeallocated.Add(1, ProfileTag);
            CacheInstruments.ConnectionsReturnedFaulted.Add(1, ProfileTag);
            return;
        }

        connectionPool.Add(connection);
        CacheInstruments.ConnectionsReturned.Add(1, ProfileTag);
    }

    internal async Task ReturnFaultedAsync(IConnectionMultiplexer connection)
    {
        Interlocked.Decrement(ref taken);
        CacheInstruments.ConnectionsReturnedFaulted.Add(1, ProfileTag);

        try
        {
            await DisposeMuxAsync(connection);
        }
        finally
        {
            Interlocked.Decrement(ref allocated);
            CacheInstruments.ConnectionsDeallocated.Add(1, ProfileTag);
        }
    }

    private IConnectionMultiplexer EnsureNew()
    {
        var newAllocated = Interlocked.Increment(ref this.allocated);
        logger.LogDebug("[{Profile}] Allocating new Redis connection. Allocated: {Allocated}", profileName, newAllocated);

        CacheInstruments.ConnectionsAllocated.Add(1, ProfileTag);

        return ConnectionMultiplexer.Connect(configuration);
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

            #pragma warning disable CS0618 // Type or member is obsolete
            return endpoints.Select(endpoint => mux.GetServer(endpoint)).Select(server => server.IsReplica || server.IsSlave)
            #pragma warning restore CS0618 // Type or member is obsolete
               .Any(isReplica => !isReplica);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[{Profile}] Failed to validate Redis connection, treating as unusable.", profileName);
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
            logger.LogDebug(ex, "[{Profile}] Error during Redis connection CloseAsync.", profileName);
        }

        try
        {
            await mux.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[{Profile}] Error during Redis connection DisposeAsync.", profileName);
        }
    }

    private async Task Cleanup()
    {
        var allocated = Interlocked.Read(ref this.allocated);
        var taken     = Interlocked.Read(ref this.taken);
        var maxSize   = Interlocked.Read(ref defaultSize);
        var excess    = allocated - taken;

        CacheInstruments.PoolCleanups.Add(1, ProfileTag);

        if (excess <= maxSize)
            return;

        var rmRequired = excess - maxSize;
        logger.LogInformation("[{Profile}] Cleaning up {Count} excess redis connections", profileName, rmRequired);

        var removed = 0;

        while (rmRequired > 0 && connectionPool.TryTake(out var mux))
        {
            try
            {
                await DisposeMuxAsync(mux);
                Interlocked.Decrement(ref this.allocated);
                CacheInstruments.ConnectionsDeallocated.Add(1, ProfileTag);
                rmRequired--;
                removed++;
            }
            catch (Exception e)
            {
                logger.LogError(e, "[{Profile}] Failed to clean up Redis connection.", profileName);
            }
        }

        if (removed > 0)
        {
            CacheInstruments.PoolConnectionsRemoved.Add(removed, ProfileTag);
            logger.LogInformation("[{Profile}] Removed {Removed} redis connections during cleanup", profileName, removed);
        }
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[{Profile}] Starting RedisConnectionPool with default size {Size}", profileName, defaultSize);

        PopulateInitial();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var allocated = Interlocked.Read(ref this.allocated);
                var taken     = Interlocked.Read(ref this.taken);

                logger.LogDebug(
                    "[{Profile}] Cleaning up redis pool call, allocated: {allocated}, taken: {taken}",
                    profileName, allocated, taken);

                await Cleanup();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Profile}] Redis cleanup service stopping...", profileName);
        }
    }

    private void TryIncreaseMaxSize()
    {
        var current = Interlocked.Read(ref defaultSize);

        if (current >= MaxAllowedSize)
        {
            logger.LogCritical(
                "[{Profile}] Redis pool reached max allowed size {MaxAllowedSize}. Consider scaling infrastructure or adding replicas!",
                profileName, MaxAllowedSize);
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

        logger.LogInformation("[{Profile}] Auto-scaling Redis pool size from {Old} to {New}", profileName, current, proposed);
        Interlocked.Exchange(ref defaultSize, proposed);
        lastScaleUp = now;

        CacheInstruments.PoolScaleUps.Add(1, ProfileTag);
    }

    public async override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[{Profile}] Stopping RedisConnectionPool, disposing all connections...", profileName);

        timer.Dispose();

        while (connectionPool.TryTake(out var mux))
        {
            await DisposeMuxAsync(mux);
            CacheInstruments.ConnectionsDeallocated.Add(1, ProfileTag);
        }

        Pools.TryRemove(new KeyValuePair<string, RedisConnectionPool>(profileName, this));

        await base.StopAsync(cancellationToken);
    }

    private KeyValuePair<string, object?> ProfileTag => new("profile", profileName);

    // Per-pool gauge observations, each tagged with its profile so OpenTelemetry reports them separately.
    internal static IEnumerable<Measurement<long>> ObserveTaken()
        => Pools.Values.Select(p => new Measurement<long>(Interlocked.Read(ref p.taken), p.ProfileTag));

    internal static IEnumerable<Measurement<long>> ObserveAllocated()
        => Pools.Values.Select(p => new Measurement<long>(Interlocked.Read(ref p.allocated), p.ProfileTag));

    internal static IEnumerable<Measurement<long>> ObserveMaxSize()
        => Pools.Values.Select(p => new Measurement<long>(Interlocked.Read(ref p.defaultSize), p.ProfileTag));
}
