namespace Argon.Features.EphemeralState;

/// <summary>
/// Distributed rate limiter with sliding window semantics.
/// Implementations: Redis (default), Aerospike (high-scale).
/// </summary>
public interface IRateLimiterService
{
    /// <summary>
    /// Attempts to consume one token from the rate limit bucket.
    /// Returns the result indicating whether the request is allowed.
    /// </summary>
    Task<RateLimitResult> TryConsumeAsync(
        string key, int maxCount, TimeSpan window, CancellationToken ct = default);

    /// <summary>
    /// Checks remaining capacity without consuming a token.
    /// </summary>
    Task<RateLimitResult> GetRemainingAsync(
        string key, int maxCount, TimeSpan window, CancellationToken ct = default);

    /// <summary>
    /// Resets the rate limit counter for a key.
    /// </summary>
    Task ResetAsync(string key, CancellationToken ct = default);
}

public readonly record struct RateLimitResult(
    bool Allowed,
    int Remaining,
    int Limit,
    DateTimeOffset ResetsAt
);
