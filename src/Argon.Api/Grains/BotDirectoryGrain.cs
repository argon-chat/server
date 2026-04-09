namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;
using System.Security.Cryptography;

[StatelessWorker]
public class BotDirectoryGrain(
    IDbContextFactory<ApplicationDbContext> context
) : Grain, IBotDirectoryGrain
{
    public async Task<BotSearchInfo?> FindByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var normalized = username.Trim().ToLowerInvariant();

        await using var db = await context.CreateDbContextAsync();

        return await db.BotEntities
           .AsNoTracking()
           .Where(b => b.IsPublic && !b.IsRestricted)
           .Join(db.Users.AsNoTracking(),
                b => b.BotAsUserId,
                u => u.Id,
                (b, u) => new { Bot = b, User = u })
           .Where(x => x.User.NormalizedUsername == normalized)
           .Select(x => new BotSearchInfo(
                x.Bot.AppId,
                x.Bot.Name,
                x.User.Username,
                x.Bot.Description,
                x.User.AvatarFileId,
                x.Bot.IsVerified,
                x.Bot.RequiredScopes))
           .FirstOrDefaultAsync();
    }

    public async Task<BotAuthInfo?> ResolveByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        // Token format: HEX_REVERSED_GUID:BASE64URL_SECRET
        var colonIdx = token.IndexOf(':');
        if (colonIdx < 0)
            return null;

        var hexPart = token[..colonIdx];
        if (hexPart.Length != 32)
            return null;

        // Parse hex → bytes → reverse → Guid (AppId)
        Span<byte> botBytes = stackalloc byte[16];
        try
        {
            var decoded = Convert.FromHexString(hexPart);
            if (decoded.Length != 16) return null;
            decoded.CopyTo(botBytes);
        }
        catch { return null; }
        botBytes.Reverse();
        var appId = new Guid(botBytes);

        await using var db = await context.CreateDbContextAsync();

        var bot = await db.BotEntities
           .AsNoTracking()
           .Where(b => b.AppId == appId)
           .Select(b => new
            {
                b.BotToken,
                b.AppId,
                b.TeamId,
                b.BotAsUserId,
                b.Name,
                b.IsRestricted,
                b.IsVerified,
                b.MaxSpaces
            })
           .FirstOrDefaultAsync();

        if (bot is null)
            return null;

        // Constant-time comparison of full token
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token),
                Encoding.UTF8.GetBytes(bot.BotToken)))
            return null;

        return new BotAuthInfo(
            bot.AppId,
            bot.TeamId,
            bot.BotAsUserId,
            bot.Name,
            bot.IsRestricted,
            bot.IsVerified,
            bot.MaxSpaces);
    }

    public async Task<BotDetailInfo?> GetBotDetails(Guid botAppId)
    {
        await using var db = await context.CreateDbContextAsync();

        return await db.BotEntities
           .AsNoTracking()
           .Where(b => b.AppId == botAppId && b.IsPublic && !b.IsRestricted)
           .Join(db.Users.AsNoTracking(),
                b => b.BotAsUserId,
                u => u.Id,
                (b, u) => new { Bot = b, User = u })
           .Join(db.TeamEntities.AsNoTracking(),
                x => x.Bot.TeamId,
                t => t.TeamId,
                (x, t) => new { x.Bot, x.User, Team = t })
           .Select(x => new BotDetailInfo(
                x.Bot.AppId,
                x.Bot.Name,
                x.User.Username,
                x.Bot.Description,
                x.User.AvatarFileId,
                x.Bot.IsVerified,
                x.Bot.IsPublic,
                x.Bot.RequiredScopes,
                x.Bot.MaxSpaces,
                x.Team.Name))
           .FirstOrDefaultAsync();
    }
}
