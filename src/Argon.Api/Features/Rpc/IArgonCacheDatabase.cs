namespace Argon.Services;

using Features.Env;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

public interface IArgonCacheDatabase
{
    Task          StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default);
    Task          StringSetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> StringGetAsync(string key, CancellationToken ct = default);
    Task          KeyDeleteAsync(string key, CancellationToken ct = default);
    Task<bool>    KeyExistsAsync(string key, CancellationToken ct = default);
}

public class RedisArgonCacheDatabase(IConnectionMultiplexer multiplexer) : IArgonCacheDatabase
{
    public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
        => multiplexer.GetDatabase().StringSetAsync(key, value, expiration).WaitAsync(ct);

    public Task StringSetAsync(string key, string value, CancellationToken ct = default)
        => multiplexer.GetDatabase().StringSetAsync(key, value).WaitAsync(ct);

    public async Task<string?> StringGetAsync(string key, CancellationToken ct = default)
        => await multiplexer.GetDatabase().StringGetAsync(key).WaitAsync(ct);

    public Task KeyDeleteAsync(string key, CancellationToken ct = default)
        => multiplexer.GetDatabase().KeyDeleteAsync(key).WaitAsync(ct);

    public Task<bool> KeyExistsAsync(string key, CancellationToken ct = default)
        => multiplexer.GetDatabase().KeyExistsAsync(key).WaitAsync(ct);
}

public sealed class InMemoryArgonCacheDatabase(IDistributedCache cache) : IArgonCacheDatabase
{
    public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
        => cache.SetAsync(key, Encoding.UTF8.GetBytes(value), new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.Now + expiration
        }, ct);

    public Task StringSetAsync(string key, string value, CancellationToken ct = default)
        => cache.SetStringAsync(key, value, token: ct);

    public Task<string?> StringGetAsync(string key, CancellationToken ct = default)
        => cache.GetStringAsync(key, ct);

    public Task KeyDeleteAsync(string key, CancellationToken ct = default)
    {
        cache.Remove(key);
        return Task.CompletedTask;
    }

    public async Task<bool> KeyExistsAsync(string key, CancellationToken ct = default)
    {
        var r = await cache.GetStringAsync(key, ct);
        return !string.IsNullOrEmpty(r);
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
            builder.Services.AddSingleton<IArgonCacheDatabase, RedisArgonCacheDatabase>();
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("cache")!));
        }

        return builder.Services;
    }
}