namespace Argon.Features.EphemeralState;

using Services;

public sealed class RedisRateLimiterService(IArgonCacheDatabase cache) : IRateLimiterService
{
    private static string RateLimitKey(string key) => $"rl:{key}";

    public async Task<RateLimitResult> TryConsumeAsync(string key, int maxCount, TimeSpan window, CancellationToken ct)
    {
        var redisKey = RateLimitKey(key);
        var count = await cache.StringIncrementAsync(redisKey, ct);

        if (count == 1)
            await cache.KeyExpireAsync(redisKey, window, ct);

        var resetsAt = DateTimeOffset.UtcNow.Add(window);
        var remaining = Math.Max(0, maxCount - (int)count);

        return new RateLimitResult(
            Allowed: count <= maxCount,
            Remaining: remaining,
            Limit: maxCount,
            ResetsAt: resetsAt
        );
    }

    public async Task<RateLimitResult> GetRemainingAsync(string key, int maxCount, TimeSpan window, CancellationToken ct)
    {
        var redisKey = RateLimitKey(key);
        var countStr = await cache.StringGetAsync(redisKey, ct);
        var count = string.IsNullOrEmpty(countStr) ? 0 : int.Parse(countStr);
        var remaining = Math.Max(0, maxCount - count);

        return new RateLimitResult(
            Allowed: count < maxCount,
            Remaining: remaining,
            Limit: maxCount,
            ResetsAt: DateTimeOffset.UtcNow.Add(window)
        );
    }

    public Task ResetAsync(string key, CancellationToken ct)
        => cache.KeyDeleteAsync(RateLimitKey(key), ct);
}
