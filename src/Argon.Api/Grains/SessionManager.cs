namespace Argon.Api.Grains;

using Entities;
using Helpers;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;

public class SessionManager(
    IGrainFactory grainFactory,
    ILogger<UserManager> logger,
    
    UserManagerService managerService,
    ApplicationDbContext context
) : Grain, ISessionManager
{
    public async Task<JwtToken> Authorize(UserCredentialsInput input)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == input.Email);

        if (user is null)
            throw new Exception("User not found with given credentials"); // TODO: implement application errors

        if (input.GenerateOtp)
        {
            user = await UserHelper.GenerateOtp(user);
            await context.SaveChangesAsync();
            return new JwtToken("");
        }

        await UserHelper.ValidatePassword(input.Password, user);
        user = await UserHelper.GenerateOtp(user); // regenerate OTP after successful login
        await context.SaveChangesAsync();
        return await GenerateJwt(user);
    }

    private async Task<JwtToken> GenerateJwt(User User) =>
        new(await managerService.GenerateJwt(User.Email, User.Id));

    public Task GetUser()
    {
        throw new NotImplementedException();
    }

    public Task Logout()
    {
        throw new NotImplementedException();
    }
}