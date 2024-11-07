namespace Argon.Api.Grains;

using Contracts;
using Entities;
using Features.Otp;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;


public class AuthorizationGrain(IGrainFactory grainFactory,
    ILogger<AuthorizationGrain> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    ApplicationDbContext context) : Grain, IAuthorizationGrain
{
    // TODO machineKey
    public async Task<Either<JwtToken, AuthorizationError>> Authorize(UserCredentialsInput input)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == input.Email);
        if (user is null)
        {
            logger.LogError("Not found user '{email}'", input.Email);
            return AuthorizationError.BAD_CREDENTIALS;
        }
        var verified = passwordHashingService.VerifyPassword(input.Password, user);

        if (!verified)
            return AuthorizationError.BAD_CREDENTIALS;

        if (string.IsNullOrEmpty(input.OtpCode))
        {
            var otp = passwordHashingService.GenerateOtp(user.Id);
            user.OtpHash = otp.Hashed;
            context.Users.Update(user);
            await context.SaveChangesAsync();
            // TODO check latest send otp time (evade ddos)
            await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
               .SendOtpCodeAsync(user.Email, otp.Code, TimeSpan.FromMinutes(15));
            return AuthorizationError.REQUIRED_OTP;
        }

        var userOtp = new OtpCode(input.OtpCode);

        if (!(user.OtpHash?.Equals(userOtp.Hashed) ?? false))
        {
            logger.LogError("User '{email}' entered invalid otp code {otp} {optHash}", input.Email, userOtp.Code, userOtp.Hashed);
            return AuthorizationError.BAD_OTP;
        }

        user.OtpHash = null;
        context.Users.Update(user);
        await context.SaveChangesAsync();
        return await GenerateJwt(user);
    }
    private async Task<JwtToken> GenerateJwt(User User) => new(await managerService.GenerateJwt(User.Email, User.Id));
}