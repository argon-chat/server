namespace Argon.Services;

using System.Runtime.CompilerServices;
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

    /// <summary>
    /// The single-instance stand-in for <c>SET … GET</c>: read and write under one gate.
    /// </summary>
    /// <remarks>
    /// <see cref="IDistributedCache"/> has no compare-and-set of any kind, so the atomicity the
    /// contract promises is bought with a process-wide gate instead. That is the same guarantee here
    /// as Redis gives with one command — single-instance mode is one process by definition — and the
    /// method is called once per presence broadcast, so serialising it costs nothing measurable.
    /// Without it, defect S19 (every racing broadcaster believing it made the change) would simply
    /// move into this implementation.
    /// </remarks>
    public async Task<string?> StringSetAndGetPreviousAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
    {
        await setAndGetGate.WaitAsync(ct);
        try
        {
            var previous = await cache.GetStringAsync(key, ct);
            await StringSetAsync(key, value, expiration, ct);
            return previous;
        }
        finally
        {
            setAndGetGate.Release();
        }
    }

    private static readonly SemaphoreSlim setAndGetGate = new(1, 1);

    public Task<IAsyncDisposable> SubscribeToExpired(Func<string, Task> onKeyExpired, CancellationToken ct = default)
        => throw new NotImplementedException();

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern, [EnumeratorCancellation] CancellationToken ct = default)
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

    // In-memory analogue of the Redis SET ops used by the presence index (single-instance mode).
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sets = new();

    public Task<bool> SetAddAsync(string key, string member, CancellationToken ct = default)
    {
        var set = _sets.GetOrAdd(key, _ => new());
        return Task.FromResult(set.TryAdd(member, 0));
    }

    public Task<bool> SetRemoveAsync(string key, string member, CancellationToken ct = default)
    {
        var removed = _sets.TryGetValue(key, out var set) && set.TryRemove(member, out _);
        return Task.FromResult(removed);
    }

    public Task<string[]> SetMembersAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_sets.TryGetValue(key, out var set) ? set.Keys.ToArray() : System.Array.Empty<string>());
}