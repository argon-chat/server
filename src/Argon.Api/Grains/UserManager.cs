namespace Argon.Api.Grains;

using AutoMapper;
using Contracts;
using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;

public class UserManager(IPasswordHashingService passwordHashingService, ApplicationDbContext context, IMapper mapper) : Grain, IUserManager
{
    public async Task<UserDto> CreateUser(UserCredentialsInput input)
    {
        var user = new User
        {
            Email          = input.Email,
            Username       = input.Username,
            PhoneNumber    = input.PhoneNumber,
            PasswordDigest = passwordHashingService.HashPassword(input.Password)
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return mapper.Map<UserDto>(user);
    }

    public async Task<UserDto> UpdateUser(UserCredentialsInput input)
    {
        var user = await Get();
        user.Email          = input.Email;
        user.Username       = input.Username ?? user.Username;
        user.PhoneNumber    = input.PhoneNumber ?? user.PhoneNumber;
        user.PasswordDigest = passwordHashingService.HashPassword(input.Password) ?? user.PasswordDigest;
        context.Users.Update(user);
        await context.SaveChangesAsync();
        return mapper.Map<UserDto>(user);
    }

    public Task DeleteUser()
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.DeletedAt = DateTime.UtcNow;
        context.Users.Update(user);
        return context.SaveChangesAsync();
    }

    public async Task<UserDto> GetUser() => mapper.Map<UserDto>(await Get());

    public async Task<List<ServerDto>> GetMyServers()
    {
        var user = await context.Users.Include(user => user.UsersToServerRelations)
           .ThenInclude(usersToServerRelation => usersToServerRelation.Server).FirstAsync(u => u.Id == this.GetPrimaryKey());
        return user.UsersToServerRelations.Select(x => x.Server).ToList().Select(mapper.Map<ServerDto>).ToList();
    }

    private async Task<User> Get() => await context.Users.Include(x => x.UsersToServerRelations).ThenInclude(x => x.Server)
       .ThenInclude(x => x.Channels).FirstAsync(user => user.Id == this.GetPrimaryKey());
}