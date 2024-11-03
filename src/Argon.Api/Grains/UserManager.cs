namespace Argon.Api.Grains;

using Entities;
using Helpers;
using Interfaces;

public class UserManager(
    // [PersistentState("userServers", "OrleansStorage")]
    // IPersistentState<UserToServerRelations> userServerStore,
    IGrainFactory grainFactory,
    ILogger<UserManager> logger,
    ApplicationDbContext context
) : Grain, IUserManager
{
    public Task CreateUser(UserCredentialsInput input)
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
        return context.SaveChangesAsync();
    }

    public Task UpdateUser(UserCredentialsInput input)
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.Email = input.Email;
        user.Username = input.Username ?? user.Username;
        user.PhoneNumber = input.PhoneNumber ?? user.PhoneNumber;
        user.PasswordDigest =
            UserHelper.HashPassword(UserHelper.VerifyPassword(input.Password, input.PasswordConfirmation)) ??
            user.PasswordDigest;
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user);
        return context.SaveChangesAsync();
    }

    public Task DeleteUser()
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.DeletedAt = DateTime.UtcNow;
        return context.SaveChangesAsync();
    }

    public Task GetUser()
    {
        return Task.CompletedTask;
    }
}