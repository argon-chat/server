namespace Argon.Services;

using System.Text.RegularExpressions;
using Features.Env;
using Grpc.Core;
using MessagePipe;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;
using StackExchange.Redis;

public interface IArgonCacheDatabase
{
    Task          StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default);
    Task          UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default);
    Task          StringSetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> StringGetAsync(string key, CancellationToken ct = default);
    Task          KeyDeleteAsync(string key, CancellationToken ct = default);
    Task<bool>    KeyExistsAsync(string key, CancellationToken ct = default);


    IAsyncEnumerable<string> ScanKeysAsync(string pattern, CancellationToken ct = default);
}

public class RedisArgonCacheDatabase(IRedisPoolConnections pool) : IArgonCacheDatabase
{
    public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
    {
        using var scope = pool.Rent();

        return scope.GetDatabase().StringSetAsync(key, value, expiration).WaitAsync(ct);
    }

    public Task UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default)
    {
        using var scope = pool.Rent();
        return scope.GetDatabase().KeyExpireAsync(key, expiration).WaitAsync(ct);
    }

    public Task StringSetAsync(string key, string value, CancellationToken ct = default)
    {
        using var scope = pool.Rent();
        return scope.GetDatabase().StringSetAsync(key, value).WaitAsync(ct);
    }

    public async Task<string?> StringGetAsync(string key, CancellationToken ct = default)
    {
        using var scope = pool.Rent();

        return await scope.GetDatabase().StringGetAsync(key).WaitAsync(ct);
    }

    public Task KeyDeleteAsync(string key, CancellationToken ct = default)
    {
        using var scope = pool.Rent();

        return scope.GetDatabase().KeyDeleteAsync(key).WaitAsync(ct);
    }

    public Task<bool> KeyExistsAsync(string key, CancellationToken ct = default)
    {
        using var scope = pool.Rent();

        return scope.GetDatabase().KeyExistsAsync(key).WaitAsync(ct);
    }

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern, CancellationToken ct = default)
    {
        using var scope = pool.Rent();
        foreach (var key in scope.GetServer().Keys(pattern: pattern, pageSize: 1))
            yield return key;
    }
}

public class CacheSubscriber(RedisChannel channelKey, ISubscriber subscriber, ConnectionScope scope) : IDisposable, IAsyncDisposable
{
    public void Dispose()
    {
        subscriber.Unsubscribe(channelKey);
        ((IDisposable)scope).Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await subscriber.UnsubscribeAsync(channelKey);
        ((IDisposable)scope).Dispose();
    }
}

public sealed class InMemoryArgonCacheDatabase(IDistributedCache cache) : IArgonCacheDatabase
{
    private static readonly ConcurrentDictionary<string, byte> _keys = new();

    public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
    {
        _keys.TryAdd(key, 0);
        return cache.SetAsync(key, Encoding.UTF8.GetBytes(value), new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.Now + expiration
        }, ct);
    }

    public Task UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task StringSetAsync(string key, string value, CancellationToken ct = default)
    {
        _keys.TryAdd(key, 0);
        return cache.SetStringAsync(key, value, ct);
    }

    public Task<string?> StringGetAsync(string key, CancellationToken ct = default)
        => cache.GetStringAsync(key, ct);

    public Task KeyDeleteAsync(string key, CancellationToken ct = default)
    {
        cache.Remove(key);
        _keys.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async Task<bool> KeyExistsAsync(string key, CancellationToken ct = default)
    {
        var r = await cache.GetStringAsync(key, ct);
        return !string.IsNullOrEmpty(r);
    }

    public Task<IAsyncDisposable> SubscribeToExpired(Func<string, Task> onKeyExpired, CancellationToken ct = default)
        => throw new NotImplementedException();

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern, CancellationToken ct = default)
    {
        var regex = PatternToRegex(pattern);

        foreach (var key in _keys.Keys)
        {
            if (regex.IsMatch(key))
                yield return key;
            await Task.Yield();
        }
    }

    private static Regex PatternToRegex(string pattern, CancellationToken ct = default)
    {
        // Redis wildcard to regex: "*" => ".*", "?" => ".", "[abc]" => "[abc]"
        var escaped = Regex.Escape(pattern)
           .Replace(@"\*", ".*")
           .Replace(@"\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.Compiled);
    }
}

public static class ArgonCacheDatabaseFeature
{
    public static IServiceCollection AddArgonCacheDatabase(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsSingleInstance())
        {
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton<IArgonCacheDatabase, InMemoryArgonCacheDatabase>();
        }
        else
        {
            builder.Services.Configure<RedisConnectionPoolOptions>(builder.Configuration.GetSection("redis"));
            builder.Services.AddSingleton<IRedisPoolConnections, RedisConnectionPool>();
            builder.Services.AddHostedService(q => q.GetRequiredService<IRedisPoolConnections>());
            builder.Services.AddHostedService<RedisEventHandler>();
            builder.Services.AddScoped<IArgonCacheDatabase, RedisArgonCacheDatabase>();
        }

        return builder.Services;
    }
}

public readonly record struct OnRedisKeyExpired(string key);
public class RedisEventHandler(IRedisPoolConnections pool, IAsyncPublisher<OnRedisKeyExpired> publisher) : IHostedService, IDisposable, IAsyncDisposable
{
    private CacheSubscriber? _cacheSubscriber;

#pragma warning disable CA1816 // idiotic hint GC.SuppressFinalize, but it's not necessary here
    public async ValueTask DisposeAsync()
    {
        if (_cacheSubscriber != null) await _cacheSubscriber.DisposeAsync();
    }

    public void Dispose()
        => _cacheSubscriber?.Dispose();
#pragma warning restore CA1816

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scope = pool.Rent();

        var k = new RedisChannel("__keyevent@0__:expired", RedisChannel.PatternMode.Auto);
        var s = scope.GetMultiplexer().GetSubscriber();
        _cacheSubscriber = new CacheSubscriber(k, s, scope);
        var w = await s.SubscribeAsync(k);
        w.OnMessage(async message => await publisher.PublishAsync(new OnRedisKeyExpired(message.Message.ToString()), cancellationToken));
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public readonly struct ConnectionScope(IConnectionMultiplexer multiplexer, RedisConnectionPool pool) : IDisposable
{
    void IDisposable.Dispose()
        => pool.Return(multiplexer);

    public IServer GetServer()
        => multiplexer.GetServer(multiplexer.GetEndPoints().First());

    public IConnectionMultiplexer GetMultiplexer() 
        => multiplexer;

    public IDatabase GetDatabase(int dbId = -1)
        => multiplexer.GetDatabase(dbId);
}

public record RedisConnectionPoolOptions
{
    public uint MaxSize { get; set; } = 16;
}

public interface IRedisPoolConnections : IHostedService
{
    ConnectionScope Rent();
}

public class RedisConnectionPool(IConfiguration cfg, ILogger<RedisConnectionPool> logger, IOptions<RedisConnectionPoolOptions> options) : BackgroundService, IRedisPoolConnections
{
    private readonly ConcurrentBag<IConnectionMultiplexer> ConnectionPool = new();

    private readonly PeriodicTimer timer = new(TimeSpan.FromMinutes(2));

    private ulong Taken;
    private ulong Allocated;


    public ConnectionScope Rent()
    {
        Interlocked.Increment(ref Taken);
        return ConnectionPool.TryTake(out var multiplexer) ? new ConnectionScope(multiplexer, this) : new ConnectionScope(EnsureNew(), this);
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
        foreach (var _ in Enumerable.Range(0, (int)Math.Max(6, (int)options.Value.MaxSize / 2u)).Select(_ => EnsureNew())) { }
    }

    private async Task Cleanup()
    {
        var (allocated, taken) = (Interlocked.Read(ref Allocated), Interlocked.Read(ref Taken));
        var maxSize = options.Value.MaxSize;
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
}