namespace Argon.Api.Grains;

using Entities;
using Helpers;
using Interfaces;
using Microsoft.EntityFrameworkCore;

public class UserManager(
    // [PersistentState("userServers", "OrleansStorage")]
    // IPersistentState<UserToServerRelations> userServerStore,
    IGrainFactory grainFactory,
    ILogger<UserManager> logger,
    ApplicationDbContext context
) : Grain, IUserManager
{
    public async Task<UserDto> CreateUser(UserCredentialsInput input)
    {
        var user = new User
        {
            Email = input.Email,
            Username = input.Username,
            PhoneNumber = input.PhoneNumber,
            PasswordDigest =
                UserHelper.HashPassword(UserHelper.VerifyPassword(input.Password, input.PasswordConfirmation)),
            OTP = ""
        };
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    public async Task<UserDto> UpdateUser(UserCredentialsInput input)
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.Email = input.Email;
        user.Username = input.Username ?? user.Username;
        user.PhoneNumber = input.PhoneNumber ?? user.PhoneNumber;
        user.PasswordDigest =
            UserHelper.HashPassword(UserHelper.VerifyPassword(input.Password, input.PasswordConfirmation)) ??
            user.PasswordDigest;
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user);
        await context.SaveChangesAsync();
        return user;
    }

    public Task DeleteUser()
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.DeletedAt = DateTime.UtcNow;
        return context.SaveChangesAsync();
    }

    public async Task<UserDto> GetUser()
    {
        return await context.Users.FirstAsync(u => u.Id == this.GetPrimaryKey());
    }
}