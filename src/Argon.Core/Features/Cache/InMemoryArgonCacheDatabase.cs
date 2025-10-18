namespace Argon.Services;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Distributed;

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

    public Task<long> StringIncrementAsync(string key, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<string> KeyExpireAsync(string key, TimeSpan window, CancellationToken ct = default)
        => throw new NotImplementedException();

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