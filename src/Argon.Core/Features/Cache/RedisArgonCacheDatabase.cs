namespace Argon.Services;

using System.Runtime.CompilerServices;
using StackExchange.Redis;

public class RedisArgonCacheDatabase(IRedisPoolConnections pool, ILogger<IArgonCacheDatabase> logger) : IArgonCacheDatabase
{
    private async Task<T> ExecWithRetry<T>(
    Func<IDatabase, Task<T>> action,
    [CallerMemberName] string caller = "")
    {
        var sw = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            attempt++;

            await using var scope = pool.Rent();
            var db = scope.GetDatabase();

            try
            {
                logger.LogDebug("Redis Exec ({Caller}) attempt {Attempt} started", caller, attempt);
                var result = await action(db);
                logger.LogDebug("Redis Exec ({Caller}) attempt {Attempt} succeeded", caller, attempt);
                return result;
            }
            catch (Exception ex)
            {
                var retryable = RedisErrorClassifier.IsReplicaWriteError(ex, logger);

                logger.LogError(
                    ex,
                    "Redis Exec ({Caller}) FAILED on attempt {Attempt}. Retryable={Retryable}. Elapsed={Elapsed}ms",
                    caller,
                    attempt,
                    retryable,
                    sw.ElapsedMilliseconds
                );

                if (!retryable)
                {
                    logger.LogCritical(
                        "Redis Exec ({Caller}) FAILED and NOT retryable. Throwing.",
                        caller);
                    throw;
                }

                scope.MarkFaulted();

                if (sw.ElapsedMilliseconds < 500)
                {
                    logger.LogWarning(
                        "Redis Exec ({Caller}) RETRYING after READONLY/LOADING etc. Delay=5ms",
                        caller);

                    await Task.Delay(5);
                    continue;
                }

                logger.LogCritical(
                    "Redis Exec ({Caller}) hit retry timeout. Throwing final failure.",
                    caller);

                throw;
            }
        }
    }

    private async Task ExecWithRetry(Func<IDatabase, Task> action,
        [CallerMemberName] string caller = "")
        => await ExecWithRetry(async db => { await action(db); return true; }, caller);


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