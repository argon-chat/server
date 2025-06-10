namespace Argon.Services;

using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

public class RedisDistributedCacheOptions
{
    public int DbId { get; set; } = 99;
}

public class RedisDistributedCache(IRedisPoolConnections redis, IOptions<RedisDistributedCacheOptions> options) : IDistributedCache, IDisposable
{
    private const string AbsoluteExpirationKey = "absexp";
    private const string SlidingExpirationKey  = "sldexp";
    private const string DataKey               = "data";
    private static readonly RedisValue[] _hashMembersAbsoluteExpirationSlidingExpirationData =
    [
        (RedisValue) "absexp",
        (RedisValue) "sldexp",
        (RedisValue) "data"
    ];
    private static readonly RedisValue[] _hashMembersAbsoluteExpirationSlidingExpiration =
    [
        (RedisValue) "absexp",
        (RedisValue) "sldexp"
    ];
    private const long NotPresent = -1;


    public byte[]? Get(string key)
        => GetAndRefresh(key, true);

    private byte[]? GetAndRefresh(string key, bool getData)
    {
        using var conn    = redis.Rent();
        var       cache   = conn.GetDatabase(options.Value.DbId);
        var       results = cache.HashGet((RedisKey)key, GetHashFields(getData));
        if (results.Length >= 2)
        {
            MapMetadata(results, out var absoluteExpiration, out var slidingExpiration);
            if (slidingExpiration.HasValue)
                this.Refresh(cache, key, absoluteExpiration, slidingExpiration.GetValueOrDefault());
        }
        return results is [_, _, { IsNull: false } _, ..] ? (byte[]?)results[2] : null;
    }

    private async Task<byte[]?> GetAndRefreshAsync(string key, bool getData, CancellationToken token = default(CancellationToken))
    {
        using var conn    = redis.Rent();
        var       cache   = conn.GetDatabase(options.Value.DbId);
        var       results = await cache.HashGetAsync((RedisKey)key, GetHashFields(getData)).ConfigureAwait(false);
        if (results.Length >= 2)
        {
            MapMetadata(results, out var absoluteExpiration, out var slidingExpiration);
            if (slidingExpiration.HasValue)
                await RefreshAsync(cache, key, absoluteExpiration, slidingExpiration.GetValueOrDefault(), token).ConfigureAwait(false);
        }
        var andRefreshAsync = results.Length < 3 || results[2].IsNull ? (byte[]?)null : (byte[]?)results[2];
        return andRefreshAsync;
    }

    private void Refresh(IDatabase cache, string key, DateTimeOffset? absExpr, TimeSpan sldExpr)
    {
        TimeSpan? expiry;
        if (absExpr.HasValue)
        {
            var timeSpan = absExpr.Value - DateTimeOffset.Now;
            expiry = timeSpan <= sldExpr ? timeSpan : sldExpr;
        }
        else
            expiry = sldExpr;
        cache.KeyExpire((RedisKey)key, expiry);
    }

    private async Task RefreshAsync(
        IDatabase cache,
        string key,
        DateTimeOffset? absExpr,
        TimeSpan sldExpr,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TimeSpan? expiry;
        if (absExpr.HasValue)
        {
            var timeSpan = absExpr.Value - DateTimeOffset.Now;
            expiry = timeSpan <= sldExpr ? timeSpan : sldExpr;
        }
        else
            expiry = sldExpr;
        await cache.KeyExpireAsync((RedisKey)key, expiry).ConfigureAwait(false);
    }

    private static void MapMetadata(
        RedisValue[] results,
        out DateTimeOffset? absoluteExpiration,
        out TimeSpan? slidingExpiration)
    {
        absoluteExpiration = null;
        slidingExpiration  = null;
        var result1 = (long?)results[0];
        if (result1.HasValue && result1.Value != -1L)
            absoluteExpiration = new DateTimeOffset(result1.Value, TimeSpan.Zero);
        var result2 = (long?)results[1];
        if (result2 is null or -1L)
            return;
        slidingExpiration = new TimeSpan(result2.Value);
    }

    private static RedisValue[] GetHashFields(bool getData)
        => !getData ? _hashMembersAbsoluteExpirationSlidingExpiration : _hashMembersAbsoluteExpirationSlidingExpirationData;

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return await GetAndRefreshAsync(key, true, token).ConfigureAwait(false);
    }

    public void Refresh(string key)
        => this.GetAndRefresh(key, false);

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await this.GetAndRefreshAsync(key, false, token).ConfigureAwait(false);
    }

    public void Remove(string key)
    {
        using var conn  = redis.Rent();
        var       cache = conn.GetDatabase(options.Value.DbId);
        cache.KeyDelete((RedisKey)key);
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        using var conn  = redis.Rent();
        var       cache = conn.GetDatabase(options.Value.DbId);
        await cache.KeyDeleteAsync((RedisKey)key).ConfigureAwait(false);
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions opt)
        => SetImpl(key, new(value), opt);

    private void SetImpl(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions opt)
    {
        using var conn  = redis.Rent();
        var       cache = conn.GetDatabase(options.Value.DbId);

        var creationTime = DateTimeOffset.UtcNow;

        var absoluteExpiration = GetAbsoluteExpiration(creationTime, opt);
        var prefixedKey        = key;
        var ttl                = GetExpirationInSeconds(creationTime, absoluteExpiration, opt);
        var fields             = GetHashFields(Linearize(value, out var lease), absoluteExpiration, opt.SlidingExpiration);

        if (ttl is null)
        {
            cache.HashSet(prefixedKey, fields);
        }
        else
        {
            // use the batch API to pipeline the two commands and wait synchronously;
            // SE.Redis reuses the async API shape for this scenario
            var batch = cache.CreateBatch();
            var setFields = batch.HashSetAsync(prefixedKey, fields);
            var setTtl = batch.KeyExpireAsync(prefixedKey, TimeSpan.FromSeconds(ttl.GetValueOrDefault()));
            batch.Execute(); // synchronous wait-for-all; the two tasks should be either complete or *literally about to* (race conditions)
            cache.WaitAll(setFields, setTtl); // note this applies usual SE.Redis timeouts etc
        }
        Recycle(lease); // we're happy to only recycle on success
    }
    private static HashEntry[] GetHashFields(RedisValue value, DateTimeOffset? absoluteExpiration, TimeSpan? slidingExpiration)
        => [
            new(AbsoluteExpirationKey, absoluteExpiration?.Ticks ?? NotPresent),
            new(SlidingExpirationKey, slidingExpiration?.Ticks ?? NotPresent),
            new(DataKey, value)
        ];


    private static void Recycle(byte[]? lease)
    {
        if (lease is not null)
        {
            ArrayPool<byte>.Shared.Return(lease);
        }
    }

    private static ReadOnlyMemory<byte> Linearize(in ReadOnlySequence<byte> value, out byte[]? lease)
    {
        // RedisValue only supports single-segment chunks; this will almost never be an issue, but
        // on those rare occasions: use a leased array to harmonize things
        if (value.IsSingleSegment)
        {
            lease = null;
            return value.First;
        }
        var length = checked((int)value.Length);
        lease = ArrayPool<byte>.Shared.Rent(length);
        value.CopyTo(lease);
        return new(lease, 0, length);
    }
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = new())
        => throw new NotImplementedException();

    private static long? GetExpirationInSeconds(DateTimeOffset creationTime, DateTimeOffset? absoluteExpiration, DistributedCacheEntryOptions options)
    {
        if (absoluteExpiration.HasValue && options.SlidingExpiration.HasValue)
            return (long)Math.Min(
                (absoluteExpiration.Value - creationTime).TotalSeconds,
                options.SlidingExpiration.Value.TotalSeconds);
        if (absoluteExpiration.HasValue)
            return (long)(absoluteExpiration.Value - creationTime).TotalSeconds;
        if (options.SlidingExpiration.HasValue)
            return (long)options.SlidingExpiration.Value.TotalSeconds;
        return null;
    }

    private static DateTimeOffset? GetAbsoluteExpiration(DateTimeOffset creationTime, DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration.HasValue && options.AbsoluteExpiration <= creationTime)
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
                options.AbsoluteExpiration.Value,
                "The absolute expiration value must be in the future.");
        if (options.AbsoluteExpirationRelativeToNow.HasValue)
            return creationTime + options.AbsoluteExpirationRelativeToNow;
        return options.AbsoluteExpiration;
    }

    public void Dispose()
    {
        
    }
}