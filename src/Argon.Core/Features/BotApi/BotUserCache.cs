namespace Argon.Features.BotApi;

using Argon.Entities;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// Resolves userId → <see cref="BotUserV1"/> with L1 (in-process) + L2 (Redis) caching
/// via <see cref="HybridCache"/>. Avoids per-event DB queries for user data needed by bot event payloads.
/// </summary>
public sealed class BotUserCache(
    IDbContextFactory<ApplicationDbContext> dbFactory,
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
            (dbFactory, userId),
            static async (state, ct) =>
            {
                var (factory, uid) = state;
                await using var ctx = await factory.CreateDbContextAsync(ct);
                var dbUser = await ctx.Users
                   .AsNoTracking()
                   .Where(u => u.Id == uid)
                   .Select(u => new
                   {
                       u.Id,
                       u.Username,
                       DisplayName = u.DisplayName ?? u.Username,
                       u.AvatarFileId,
                       u.BotEntityId,
                       u.LockdownReason,
                       u.IsDeleted,
                       IsVerifiedBot = u.BotEntity != null && u.BotEntity.IsVerified
                   })
                   .FirstOrDefaultAsync(ct);

                if (dbUser is null)
                    return new BotUserV1(uid, "unknown", "Unknown User", null, UserFlag.NONE);

                var flags = UserFlag.NONE;
                if (dbUser.BotEntityId is not null) flags |= UserFlag.BOT;
                if (dbUser.LockdownReason != LockdownReason.NONE) flags |= UserFlag.BANNED;
                if (dbUser.IsDeleted) flags |= UserFlag.DELETED;
                if (dbUser.Id == UserEntity.SystemUser) flags |= UserFlag.SYSTEM;
                if (dbUser.IsVerifiedBot) flags |= UserFlag.VERIFIED;

                return new BotUserV1(dbUser.Id, dbUser.Username, dbUser.DisplayName, dbUser.AvatarFileId, flags);
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