namespace Argon.Features.BotApi;

using Argon.Entities;
using Argon.Grains.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// Resolves userId → <see cref="BotUserV1"/> with L1 (in-process) + L2 (Redis) caching
/// via <see cref="HybridCache"/>. Avoids per-event grain calls for user data needed by bot event payloads.
/// </summary>
public sealed class BotUserCache(
    IGrainFactory                          grainFactory,
    HybridCache                            cache,
    ILogger<BotUserCache>                  logger)
{
    private static readonly HybridCacheEntryOptions Options = new()
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(2),
        Expiration           = TimeSpan.FromMinutes(5)
    };

    private static string Key(Guid userId) => $"bot:user:{userId:N}";

    public async ValueTask<BotUserV1> GetOrResolveAsync(Guid userId)
    {
        return await cache.GetOrCreateAsync(
            Key(userId),
            (grainFactory, userId),
            static async (state, ct) =>
            {
                var (factory, uid) = state;
                var user = await factory.GetGrain<IUserGrain>(uid).GetAsArgonUser();

                return new BotUserV1(
                    user.userId,
                    user.username,
                    user.displayName,
                    user.avatarFileId,
                    user.flags);
            },
            Options);
    }

    /// <summary>
    /// Converts an already-loaded <see cref="ArgonUser"/> and populates the cache.
    /// </summary>
    public BotUserV1 FromArgonUser(ArgonUser user)
    {
        var botUser = new BotUserV1(user.userId, user.username, user.displayName, user.avatarFileId, user.flags);
        _ = cache.SetAsync(Key(user.userId), botUser, Options);
        return botUser;
    }

    public void Invalidate(Guid userId)
    {
        _ = cache.RemoveAsync(Key(userId));
    }
}