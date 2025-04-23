namespace Argon.Services;

using System.Text.RegularExpressions;
using Features.Env;
using Grpc.Core;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

public interface IArgonCacheDatabase
{
    Task          StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default);
    Task          StringSetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> StringGetAsync(string key, CancellationToken ct = default);
    Task          KeyDeleteAsync(string key, CancellationToken ct = default);
    Task<bool>    KeyExistsAsync(string key, CancellationToken ct = default);

    IAsyncEnumerable<string> ScanKeysAsync(string pattern);
}

public class RedisArgonCacheDatabase(IConnectionMultiplexer multiplexer, IServer server) : IArgonCacheDatabase
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

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern)
    {
        foreach (var key in server.Keys(pattern: pattern))
            yield return key;
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

    public Task StringSetAsync(string key, string value, CancellationToken ct = default)
    {
        _keys.TryAdd(key, 0);
        return cache.SetStringAsync(key, value, token: ct);
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

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern)
    {
        var regex = PatternToRegex(pattern);

        foreach (var key in _keys.Keys)
        {
            if (regex.IsMatch(key))
                yield return key;
            await Task.Yield(); 
        }
    }

    private static Regex PatternToRegex(string pattern)
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
            builder.Services.AddSingleton<IArgonCacheDatabase, RedisArgonCacheDatabase>();
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("cache")!));
            builder.Services.AddSingleton(sp => {
                var mux = sp.GetRequiredService<IConnectionMultiplexer>();
                var endpoint = mux.GetEndPoints().First();
                return mux.GetServer(endpoint);
            });
        }

        return builder.Services;
    }
}