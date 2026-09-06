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

    /// <summary>
    /// The single-instance stand-in for the Lua fold: read every member's key and write the winner
    /// under the same gate.
    /// </summary>
    /// <remarks>
    /// The property the contract asks for is that no other fold can interleave between the reads and
    /// the write, and a process-wide gate gives exactly that here — single-instance mode is one
    /// process by definition. It shares <see cref="setAndGetGate"/> with the compare-and-set above
    /// rather than taking a gate of its own: both are rare, both are short, and one gate cannot
    /// deadlock against itself the way two acquired in different orders eventually would.
    /// </remarks>
    public async Task<string> FoldRankedSetAsync(
        string setKey,
        string memberKeyPrefix,
        string memberKeySuffix,
        string destinationKey,
        IReadOnlyList<string> ranking,
        string unknownAs,
        TimeSpan expiration,
        CancellationToken ct = default)
    {
        if (ranking.Count == 0)
            throw new ArgumentException("a ranked fold has nothing to answer with unless it is given a ranking", nameof(ranking));

        var ranks = new Dictionary<string, int>(ranking.Count, StringComparer.Ordinal);

        for (var i = 0; i < ranking.Count; i++)
            ranks[ranking[i]] = i;

        // Below the floor when the caller named a value it did not rank, so an unrecognised value
        // loses rather than winning by accident — the same reading the script's `or 0` gives it.
        var unknown = ranks.TryGetValue(unknownAs, out var named) ? named : -1;

        await setAndGetGate.WaitAsync(ct);
        try
        {
            var best = ranking[0];
            var at   = 0;

            foreach (var member in await SetMembersAsync(setKey, ct))
            {
                var value = await cache.GetStringAsync($"{memberKeyPrefix}{member}{memberKeySuffix}", ct);
                if (string.IsNullOrEmpty(value))
                    continue;

                var rank = ranks.TryGetValue(value, out var found) ? found : unknown;

                if (rank > at)
                {
                    at   = rank;
                    best = value;
                }

                if (at >= ranking.Count - 1)
                    break;
            }

            await StringSetAsync(destinationKey, best, expiration, ct);
            return best;
        }
        finally
        {
            setAndGetGate.Release();
        }
    }

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