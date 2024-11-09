namespace Argon.Api.Grains;

using Entities;
using global::Grains.Interface;
using Microsoft.EntityFrameworkCore;
using Models;
using Models.DTO;
using Orleans;
using Services;

public class UserManager(IPasswordHashingService passwordHashingService, ApplicationDbContext context) : Grain, IUserManager
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
        return user;
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
        return user;
    }

    public Task DeleteUser()
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.DeletedAt = DateTime.UtcNow;
        context.Users.Update(user);
        return context.SaveChangesAsync();
    }

    public async Task<UserDto> GetUser() => await Get();

    private async Task<User> Get() => await context.Users.Include(x => x.UsersToServerRelations).ThenInclude(x => x.Server)
       .ThenInclude(x => x.Channels).FirstAsync(user => user.Id == this.GetPrimaryKey());
}