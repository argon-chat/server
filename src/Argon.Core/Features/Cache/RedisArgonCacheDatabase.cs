namespace Argon.Services;

using System.Runtime.CompilerServices;
using System.Diagnostics;
using StackExchange.Redis;

public class RedisArgonCacheDatabase(
    [FromKeyedServices(RedisProfiles.Cache)] IRedisPoolConnections pool,
    ILogger<IArgonCacheDatabase> logger) : IArgonCacheDatabase
{
    private async Task<T> ExecWithRetry<T>(
        Func<IDatabase, Task<T>> action,
        [CallerMemberName] string caller = "")
    {
        var sw      = Stopwatch.StartNew();
        var attempt = 0;
        var success = false;

        try
        {
            while (true)
            {
                attempt++;

                await using var scope = pool.Rent();
                var             db    = scope.GetDatabase();

                try
                {
                    logger.LogDebug("Redis Exec ({Caller}) attempt {Attempt} started", caller, attempt);
                    var result = await action(db);
                    logger.LogDebug("Redis Exec ({Caller}) attempt {Attempt} succeeded", caller, attempt);
                    success = true;
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

                    if (attempt > 1)
                    {
                        CacheInstruments.OperationRetries.Add(1, new KeyValuePair<string, object?>("operation", caller));
                    }

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
        finally
        {
            sw.Stop();
            var tags = new[]
            {
                new KeyValuePair<string, object?>("operation", caller),
                new KeyValuePair<string, object?>("result", success ? "success" : "failure")
            };

            CacheInstruments.Operations.Add(1, tags);
            CacheInstruments.OperationDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
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
        => (await ExecWithRetry(db => db.StringGetSetExpiryAsync(key, window)))!;

    /// <summary>
    /// <c>SET key value EX … GET</c> — one command, so the previous value is the one this write
    /// replaced and no concurrent caller can see it too.
    /// </summary>
    /// <remarks>
    /// The server has spoken <c>GETEX</c> since <see cref="KeyExpireAsync"/> was written, so the
    /// 6.2-era <c>SET … GET</c> this uses needs nothing newer than what is already deployed.
    /// </remarks>
    public async Task<string?> StringSetAndGetPreviousAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
        => await ExecWithRetry(db => db.StringSetAndGetAsync(key, value, expiration));

    /// <summary>
    /// The ranked fold, as a Lua script — the only way Redis offers to read several keys and write one
    /// without anything getting between the two.
    /// </summary>
    /// <remarks>
    /// <para>KEYS are the set and the destination; ARGV is the member key's prefix and suffix, the
    /// value an unrecognised one is ranked beside, the destination's lifetime in milliseconds, and
    /// then the ranking itself, weakest first. The winner is kept verbatim rather than replaced by the
    /// ranking entry it matched, which is what lets a status this build has never heard of survive the
    /// fold that chose it.</para>
    ///
    /// <para><b>It reads keys it does not declare</b>, which is deliberate and is the reason this is a
    /// script rather than a transaction: the keys to read are not known until the set has been read.
    /// That is legal on a standalone server and rejected by a cluster — and a cluster could not run
    /// this fold under any spelling, because the presence keys carry no hash tag and so do not share a
    /// slot. Argon's cache profiles select a logical database, which a cluster does not have either,
    /// so "standalone" is already the deployment shape rather than an assumption added here.</para>
    ///
    /// <para><c>SMEMBERS</c> returns in an arbitrary order, so the script is not deterministic by
    /// command; every server since 5.0 replicates a script's effects rather than the script, so this
    /// replicates correctly with nothing declared.</para>
    /// </remarks>
    private const string FoldRankedSetScript =
        """
        local rank = {}
        for i = 5, #ARGV do rank[ARGV[i]] = i - 4 end

        local top     = #ARGV - 4
        local unknown = rank[ARGV[3]] or 0
        local best    = ARGV[5]
        local at      = 1

        for _, member in ipairs(redis.call('SMEMBERS', KEYS[1])) do
            local value = redis.call('GET', ARGV[1] .. member .. ARGV[2])
            if value then
                local r = rank[value] or unknown
                if r > at then
                    at   = r
                    best = value
                end
                if at >= top then break end
            end
        end

        redis.call('SET', KEYS[2], best, 'PX', tonumber(ARGV[4]))
        return best
        """;

    /// <inheritdoc cref="IArgonCacheDatabase.FoldRankedSetAsync"/>
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

        var argv = new RedisValue[4 + ranking.Count];

        argv[0] = memberKeyPrefix;
        argv[1] = memberKeySuffix;
        argv[2] = unknownAs;
        argv[3] = (long)expiration.TotalMilliseconds;

        for (var i = 0; i < ranking.Count; i++)
            argv[4 + i] = ranking[i];

        var keys = new RedisKey[] { setKey, destinationKey };

        var folded = await ExecWithRetry(db => db.ScriptEvaluateAsync(FoldRankedSetScript, keys, argv));

        // The script always writes and always returns what it wrote; a null here would mean the script
        // did not run, and answering the caller's own floor would hide that behind a plausible value.
        return folded.ToString() ?? throw new InvalidOperationException(
            $"the ranked fold over '{setKey}' answered nothing");
    }

    public async IAsyncEnumerable<string> ScanKeysAsync(string pattern, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var scope  = pool.Rent();
        var             server = scope.GetServer();
        foreach (var key in server.Keys(pattern: pattern, pageSize: 1000))
            yield return key!;
    }

    public Task<bool> SetAddAsync(string key, string member, CancellationToken ct = default)
        => ExecWithRetry(db => db.SetAddAsync(key, member));

    public Task<bool> SetRemoveAsync(string key, string member, CancellationToken ct = default)
        => ExecWithRetry(db => db.SetRemoveAsync(key, member));

    public async Task<string[]> SetMembersAsync(string key, CancellationToken ct = default)
        => (await ExecWithRetry(db => db.SetMembersAsync(key))).Select(v => v.ToString()).ToArray();
}