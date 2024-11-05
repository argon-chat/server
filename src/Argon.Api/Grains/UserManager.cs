namespace Argon.Api.Grains;

using Entities;
using Helpers;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;

public class UserManager(
    IPasswordHashingService passwordHashingService,
    ApplicationDbContext    context
) : Grain, IUserManager
{
    public async Task<UserDto> CreateUser(UserCredentialsInput input)
    {
        var user = new User
        {
            Email          = input.Email,
            Username       = input.Username,
            PhoneNumber    = input.PhoneNumber,
            PasswordDigest = passwordHashingService.HashPassword(password: input.Password, passwordConfirmation: input.PasswordConfirmation),
            OTP            = passwordHashingService.GenerateOtp()
        };
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user: user);
        context.Users.Add(entity: user);
        await context.SaveChangesAsync();
        return user;
    }

    public async Task<UserDto> UpdateUser(UserCredentialsInput input)
    {
        var user = await Get();
        user.Email       = input.Email;
        user.Username    = input.Username ?? user.Username;
        user.PhoneNumber = input.PhoneNumber ?? user.PhoneNumber;
        user.PasswordDigest = passwordHashingService.HashPassword(password: input.Password, passwordConfirmation: input.PasswordConfirmation) ??
                              user.PasswordDigest;
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user: user);
        context.Users.Update(entity: user);
        await context.SaveChangesAsync();
        return user;
    }

    public Task DeleteUser()
    {
        var user = context.Users.First(predicate: u => u.Id == this.GetPrimaryKey());
        user.DeletedAt = DateTime.UtcNow;
        context.Users.Update(entity: user);
        return context.SaveChangesAsync();
    }

    public async Task<UserDto> GetUser()
        => await Get();

    private async Task<User> Get()
    {
        return await context.Users
                            .Include(navigationPropertyPath: x => x.UsersToServerRelations)
                            .ThenInclude(navigationPropertyPath: x => x.Server)
                            .ThenInclude(navigationPropertyPath: x => x.Channels)
                            .FirstAsync(predicate: user => user.Id == this.GetPrimaryKey());
    }
}