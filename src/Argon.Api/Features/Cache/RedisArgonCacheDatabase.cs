namespace Argon.Services;

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