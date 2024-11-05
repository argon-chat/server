namespace Argon.Api.Grains;

using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;

public class SessionManager(
    IGrainFactory grainFactory,
    ILogger<UserManager> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
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
            user.OTP = passwordHashingService.GenerateOtp();
            logger.LogCritical(user.OTP); // TODO: replace with emailing the user the OTP
            context.Users.Update(user);
            await context.SaveChangesAsync();
            return new JwtToken("");
        }

        var verified = passwordHashingService.VerifyPassword(input.Password, user);
        if (!verified)
            throw new Exception("Invalid credentials"); // TODO: implement application errors
        user.OTP = passwordHashingService.GenerateOtp();
        context.Users.Update(user);
        await context.SaveChangesAsync();
        return await GenerateJwt(user);
    }

    public async Task<UserDto> GetUser()
    {
        return await grainFactory.GetGrain<IUserManager>(this.GetPrimaryKey()).GetUser();
    }

    public Task Logout()
    {
        throw new NotImplementedException();
    }

    private async Task<JwtToken> GenerateJwt(User User)
    {
        return new JwtToken(await managerService.GenerateJwt(User.Email, User.Id));
    }
}