namespace Argon.Services;

using System.Runtime.CompilerServices;
using StackExchange.Redis;

public class RedisArgonCacheDatabase(IRedisPoolConnections pool) : IArgonCacheDatabase
{
    private async Task<T> ExecWithRetry<T>(Func<IDatabase, Task<T>> action)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            await using var scope = pool.Rent();
            var             db    = scope.GetDatabase();

            try
            {
                return await action(db);
            }
            catch (Exception ex) when (RedisErrorClassifier.IsReplicaWriteError(ex) && attempt < 2)
            {
                scope.MarkFaulted();
            }
        }
    }

    private async Task ExecWithRetry(Func<IDatabase, Task> action)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            await using var scope = pool.Rent();
            var             db    = scope.GetDatabase();

            try
            {
                await action(db);
                return;
            }
            catch (Exception ex) when (RedisErrorClassifier.IsReplicaWriteError(ex) && attempt < 2)
            {
                scope.MarkFaulted();
                continue;
            }
        }
    }
    public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
        => ExecWithRetry(db => db.StringSetAsync(key, value, expiration));

    public Task UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default)
        => ExecWithRetry(db => db.KeyExpireAsync(key, expiration));

    public Task StringSetAsync(string key, string value, CancellationToken ct = default)
        => ExecWithRetry(db => db.StringSetAsync(key, value));

    public async Task<string?> StringGetAsync(string key, CancellationToken ct = default)
        => await ExecWithRetry(db => db.StringGetAsync(key));

    public Task KeyDeleteAsync(string key, CancellationToken ct = default)
        => ExecWithRetry(db => db.KeyDeleteAsync(key));

    public Task<bool> KeyExistsAsync(string key, CancellationToken ct = default)
        => ExecWithRetry(db => db.KeyExistsAsync(key));

    public Task<long> StringIncrementAsync(string key, CancellationToken ct = default)
        => ExecWithRetry(db => db.StringIncrementAsync(key));

    public async Task<string> KeyExpireAsync(string key, TimeSpan window, CancellationToken ct = default)
        => await ExecWithRetry(db => db.StringGetSetExpiryAsync(key, window));

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var scope  = pool.Rent();
        var             server = scope.GetServer();
        foreach (var key in server.Keys(pattern: pattern, pageSize: 1))
            yield return key;
    }
}