namespace Argon.Api.Grains;

using Contracts;
using Contracts.Models;
using Entities;
using Features.Otp;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Services;

public class AuthorizationGrain(
    IGrainFactory grainFactory,
    ILogger<AuthorizationGrain> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    ApplicationDbContext context) : Grain, IAuthorizationGrain
{
    public async Task<Either<JwtToken, AuthorizationError>> Authorize(UserCredentialsInput input, UserConnectionInfo connectionInfo)
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

        var machineSessions = grainFactory.GetGrain<IUserMachineSessions>(user.Id);
        var machineId       = await machineSessions.CreateMachineKey(connectionInfo);

        user.OtpHash = null;
        context.Users.Update(user);
        await context.SaveChangesAsync();
        return await GenerateJwt(user, machineId);
    }

    public async Task<Maybe<RegistrationError>> Register(NewUserCredentialsInput input, UserConnectionInfo connectionInfo)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == input.Email);
        if (user is not null)
        {
            logger.LogError("Email already registered '{email}'", input.Email);
            return RegistrationError.EMAIL_ALREADY_REGISTERED;
        }

        user = await context.Users.FirstOrDefaultAsync(u => u.Username == input.Username);
        if (user is not null)
        {
            logger.LogError("Username already registered '{username}'", input.Username);
            return RegistrationError.USERNAME_ALREADY_TAKEN;
        }

        // TODO check reserved username

        // TODO check sso email (mx records and etc)

        // TODO check region banned

        // TODO check banned emails
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            try
            {
                var userId = Guid.NewGuid();
                user = new User()
                {
                    AvatarFileId   = null,
                    CreatedAt      = DateTime.UtcNow,
                    Email          = input.Email,
                    Id             = userId,
                    Username       = input.Username,
                    PasswordDigest = passwordHashingService.HashPassword(input.Password),
                    PhoneNumber    = input.PhoneNumber,
                    DisplayName    = input.DisplayName,
                };
                await context.Users.AddAsync(user);

                var agreements = new UserAgreements()
                {
                    AgreeTOS                  = input.AgreeTos,
                    AllowedSendOptionalEmails = input.AgreeOptionalEmails,
                    UserId                    = userId
                };
                await context.UserAgreements.AddAsync(agreements);

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
        return Maybe<RegistrationError>.None();
    }

    private async Task<JwtToken> GenerateJwt(User User, Guid machineId) => new(await managerService.GenerateJwt(User.Id, machineId));
}