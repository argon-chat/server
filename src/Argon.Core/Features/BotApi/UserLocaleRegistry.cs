namespace Argon.Features.BotApi;

using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// Holds each online user's *current* locale (BCP-47), refreshed on every authenticated request
/// from the <c>x-argon-locale</c> header. Ephemeral (L1 in-process + L2 Redis) — never persisted to
/// the database — so a language change is reflected to bots immediately (some bots ship easter eggs
/// tied to live language switches). Keyed by userId, so a user's locale can be resolved even for
/// events published outside their own HTTP request (e.g. a LiveKit voice-join webhook).
/// </summary>
public sealed class UserLocaleRegistry(HybridCache cache)
{
    private static readonly HybridCacheEntryOptions WriteOptions = new()
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(2),
        Expiration           = TimeSpan.FromMinutes(30),
    };

    // Read-only fetch: never store the factory's null result on a miss.
    private static readonly HybridCacheEntryOptions ReadOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    private static string Key(Guid userId) => $"user:locale:{userId:N}";

    public ValueTask Set(Guid userId, string? bcp47)
        => string.IsNullOrEmpty(bcp47)
            ? ValueTask.CompletedTask
            : cache.SetAsync(Key(userId), bcp47, WriteOptions);

    public async ValueTask<string?> Get(Guid userId)
        => await cache.GetOrCreateAsync<string?>(
            Key(userId),
            static _ => ValueTask.FromResult<string?>(null),
            ReadOptions);
}
