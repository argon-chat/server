namespace Argon.Grains;

using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class UserGrain(IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> context) : Grain, IUserGrain
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
}