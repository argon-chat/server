namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;

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
