namespace Argon.Api.Grains;

using Contracts;
using Contracts.Models;
using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;

public class UserGrain(IPasswordHashingService passwordHashingService, ApplicationDbContext context) : Grain, IUserGrain
{
    public async Task<User> UpdateUser(UserEditInput input)
    {
        var user = await Get();
        user.Username     = input.Username ?? user.Username;
        user.Username     = input.DisplayName ?? user.DisplayName;
        user.AvatarFileId = input.AvatarId ?? user.AvatarFileId;
        context.Users.Update(user);
        await context.SaveChangesAsync();
        return user;
    }

    public Task DeleteUser()
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.DeletedAt = DateTime.UtcNow;
        context.Users.Update(user);
        return context.SaveChangesAsync();
    }

    public async Task<User> GetUser()
    {
        var user = await Get();
        return user;
    }

    public async Task<List<Server>> GetMyServers()
    {
        var user = await context.Users
           .Include(user => user.ServerMembers).ThenInclude(usersToServerRelation => usersToServerRelation.Server)
           .FirstAsync(u => u.Id == this.GetPrimaryKey());
        var r = user.ServerMembers
           .Select(x => x.Server)
           .ToList();
        return r;
    }

    public async Task<List<Guid>> GetMyServersIds()
    {
        var user = await context.Users
           .Include(user => user.ServerMembers)
           .FirstAsync(u => u.Id == this.GetPrimaryKey());
        return user.ServerMembers.Select(x => x.ServerId).ToList();
    }

    private async Task<User> Get() => await context.Users.Include(x => x.ServerMembers).ThenInclude(x => x.Server)
       .ThenInclude(x => x.Channels).FirstAsync(user => user.Id == this.GetPrimaryKey());
}