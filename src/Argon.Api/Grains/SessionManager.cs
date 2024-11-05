namespace Argon.Api.Grains;

using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;

public class SessionManager(
    IGrainFactory           grainFactory,
    ILogger<UserManager>    logger,
    UserManagerService      managerService,
    IPasswordHashingService passwordHashingService,
    ApplicationDbContext    context
) : Grain, ISessionManager
{
    public async Task<JwtToken> Authorize(UserCredentialsInput input)
    {
        var user = await context.Users.FirstOrDefaultAsync(predicate: u => u.Email == input.Email);

        if (user is null)
            throw new Exception(message: "User not found with given credentials"); // TODO: implement application errors

        if (input.GenerateOtp)
        {
            user.OTP = passwordHashingService.GenerateOtp();
            logger.LogCritical(message: user.OTP); // TODO: replace with emailing the user the OTP
            context.Users.Update(entity: user);
            await context.SaveChangesAsync();
            return new JwtToken(token: "");
        }

        var verified = passwordHashingService.VerifyPassword(inputPassword: input.Password, user: user);
        if (!verified)
            throw new Exception(message: "Invalid credentials"); // TODO: implement application errors
        user.OTP = passwordHashingService.GenerateOtp();
        context.Users.Update(entity: user);
        await context.SaveChangesAsync();
        return await GenerateJwt(User: user);
    }

    public async Task<UserDto> GetUser()
        => await grainFactory.GetGrain<IUserManager>(primaryKey: this.GetPrimaryKey()).GetUser();

    public Task Logout()
        => throw new NotImplementedException();

    private async Task<JwtToken> GenerateJwt(User User)
        => new(token: await managerService.GenerateJwt(email: User.Email, id: User.Id));
}