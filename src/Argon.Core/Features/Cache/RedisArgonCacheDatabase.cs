namespace Argon.Services;

public class RedisArgonCacheDatabase(IRedisPoolConnections pool) : IArgonCacheDatabase
{
    public async Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();

        await scope.GetDatabase().StringSetAsync(key, value, expiration).WaitAsync(ct);
    }

    public async Task UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();
        await scope.GetDatabase().KeyExpireAsync(key, expiration).WaitAsync(ct);
    }

    public async Task StringSetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();
        await scope.GetDatabase().StringSetAsync(key, value).WaitAsync(ct);
    }

    public async Task<string?> StringGetAsync(string key, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();

        return await scope.GetDatabase().StringGetAsync(key).WaitAsync(ct);
    }

    public async Task KeyDeleteAsync(string key, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();

        await scope.GetDatabase().KeyDeleteAsync(key).WaitAsync(ct);
    }

    public async Task<bool> KeyExistsAsync(string key, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();

        return await scope.GetDatabase().KeyExistsAsync(key).WaitAsync(ct);
    }

    public async Task<long> StringIncrementAsync(string key, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();

        return await scope.GetDatabase().StringIncrementAsync(key);
    }

    public async Task<string> KeyExpireAsync(string key, TimeSpan window, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();

        return await scope.GetDatabase().StringGetSetExpiryAsync(key, window);
    }

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern, CancellationToken ct = default)
    {
        await using var scope = pool.Rent();
        foreach (var key in scope.GetServer().Keys(pattern: pattern, pageSize: 1))
            yield return key;
    }
}