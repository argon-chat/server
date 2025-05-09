namespace Argon.Grains;

using Features.Logic;
using Orleans;
using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class UserGrain(
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> context,
    IUserPresenceService presenceService,
    ILogger<IUserGrain> logger) : Grain, IUserGrain
{
    public async Task<User> UpdateUser(UserEditInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstAsync(x => x.Id == this.GetPrimaryKey());
        user.Username     = input.Username ?? user.Username;
        user.DisplayName  = input.DisplayName ?? user.DisplayName;
        user.AvatarFileId = input.AvatarId ?? user.AvatarFileId;
        ctx.Users.Update(user);
        await ctx.SaveChangesAsync();

        var userServers = await GetMyServersIds();

        await Task.WhenAll(userServers
           .Select(id => GrainFactory
               .GetGrain<IServerGrain>(id)
               .DoUserUpdatedAsync(this.GetPrimaryKey())
               .AsTask())
           .ToArray());

        return user;
    }

    public async Task<User> GetMe()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .Include(x => x.ServerMembers)
           .ThenInclude(x => x.Server)
           .ThenInclude(x => x.Channels)
           .FirstAsync(user => user.Id == this.GetPrimaryKey());
    }

    public async Task<List<Server>> GetMyServers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .Include(user => user.ServerMembers)
           .ThenInclude(usersToServerRelation => usersToServerRelation.Server)
           .ThenInclude(x => x.Users)
           .Include(user => user.ServerMembers)
           .ThenInclude(usersToServerRelation => usersToServerRelation.Server)
           .ThenInclude(x => x.Channels)
           .Where(x => x.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.Server)
           .AsSplitQuery()
           .ToListAsync();
    }

    public async Task<List<Guid>> GetMyServersIds()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .Include(user => user.ServerMembers)
           .Where(u => u.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.ServerId)
           .ToListAsync();
    }

    public async ValueTask BroadcastPresenceAsync(UserActivityPresence presence)
    {
        await presenceService.BroadcastActivityPresence(presence, this.GetPrimaryKey(), Guid.Empty);
        var servers = await GetMyServersIds();
        foreach (var server in servers)
            await GrainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserPresence(this.GetPrimaryKey(), presence);
    }

    public async ValueTask RemoveBroadcastPresenceAsync()
    {
        logger.LogInformation("Called remove broadcast presence for {userId}", this.GetPrimaryKey());
        await presenceService.RemoveActivityPresence(this.GetPrimaryKey());

        var servers = await GetMyServersIds();
        foreach (var server in servers)
            await GrainFactory
               .GetGrain<IServerGrain>(server)
               .RemoveUserPresence(this.GetPrimaryKey());
    }

    public async ValueTask CreateSocialBound(SocialKind kind, string userData, string socialId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        await ctx.SocialIntegrations.AddAsync(new UserSocialIntegration()
        {
            Kind     = kind,
            SocialId = socialId,
            UserData = userData,
            Id       = Guid.NewGuid(),
            UserId   = this.GetPrimaryKey()
        });
        await ctx.SaveChangesAsync();
    }
}