namespace Argon.Grains;

using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class UserGrain(IPasswordHashingService passwordHashingService, ApplicationDbContext context) : Grain, IUserGrain
{
    public async Task<User> UpdateUser(UserEditInput input)
    {
        var user = await context.Users.FirstAsync(x => x.Id == this.GetPrimaryKey());
        user.Username     = input.Username ?? user.Username;
        user.Username     = input.DisplayName ?? user.DisplayName;
        user.AvatarFileId = input.AvatarId ?? user.AvatarFileId;
        context.Users.Update(user);
        await context.SaveChangesAsync();
        return user;
    }

    public async Task<User> GetMe()
        => await context.Users
           .Include(x => x.ServerMembers)
           .ThenInclude(x => x.Server)
           .ThenInclude(x => x.Channels)
           .FirstAsync(user => user.Id == this.GetPrimaryKey());

    public async Task<List<Server>> GetMyServers()
        => await context.Users
           .Include(user => user.ServerMembers)
           .ThenInclude(usersToServerRelation => usersToServerRelation.Server)
           .ThenInclude(x => x.Users)
           .ThenInclude(x => x.User)
           .Include(user => user.ServerMembers)
           .ThenInclude(usersToServerRelation => usersToServerRelation.Server)
           .ThenInclude(x => x.Channels)
           .Where(x => x.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.Server)
           .AsSplitQuery()
           .ToListAsync();

    public async Task<List<Guid>> GetMyServersIds()
        => await context.Users
           .Include(user => user.ServerMembers)
           .Where(u => u.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.ServerId)
           .ToListAsync();
}