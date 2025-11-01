namespace Argon.Grains;

using Api.Features.CoreLogic.Otp;
using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class AuthorizationGrain(
    IGrainFactory grainFactory,
    ILogger<AuthorizationGrain> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOtpService otpService
) : Grain, IAuthorizationGrain
{
    public async Task<Either<SuccessAuthorize, AuthorizationError>> Authorize(UserCredentialsInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == input.email);

        if (user is null)
        {
            logger.LogWarning("User '{email}' not found", input.email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        return user.PreferredAuthMode switch
        {
            ArgonAuthMode.EmailPassword    => await AuthorizePassword(user, input),
            ArgonAuthMode.EmailOtp         => await AuthorizeWithOtp(user, input, requirePassword: false),
            ArgonAuthMode.EmailPasswordOtp => await AuthorizeWithOtp(user, input, requirePassword: true),
            _                              => AuthorizationError.NONE
        };
    }

    private async Task<Either<SuccessAuthorize, AuthorizationError>> AuthorizePassword(UserEntity user, UserCredentialsInput input)
    {
        if (string.IsNullOrEmpty(input.password))
            return AuthorizationError.BAD_CREDENTIALS;

        var verified = passwordHashingService.VerifyPassword(input.password, user);
        if (!verified)
        {
            logger.LogWarning("User '{email}' entered invalid password", user.Email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
        return await GenerateJwt(user, this.GetUserMachineId());
    }

    private async Task<Either<SuccessAuthorize, AuthorizationError>> AuthorizeWithOtp(UserEntity user, UserCredentialsInput input, bool requirePassword)
    {
        if (requirePassword)
        {
            if (string.IsNullOrEmpty(input.password) || !passwordHashingService.VerifyPassword(input.password, user))
            {
                logger.LogWarning("Invalid password for '{email}'", user.Email);
                return AuthorizationError.BAD_CREDENTIALS;
            }
        }

        var userIp    = this.GetUserIp() ?? "unknown";
        var machineId = this.GetUserMachineId();
        var method    = user.PreferredOtpMethod;

        if (string.IsNullOrEmpty(input.otpCode))
        {
            await otpService.SendAsync(
                new SendOtpRequest(user.Email, user.Id, OtpPurpose.SignIn, machineId, method),
                userIp
            );

            logger.LogInformation("OTP sent via {method} to {email}", method, user.Email);
            return AuthorizationError.REQUIRED_OTP;
        }

        var verified = await otpService.VerifyAsync(
            new VerifyOtpRequest(user.Email, user.Id, OtpPurpose.SignIn, input.otpCode, machineId, method)
        );

        if (!verified)
        {
            logger.LogWarning("Invalid OTP for '{email}' via {method}", user.Email, method);
            return AuthorizationError.BAD_OTP;
        }

        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
        return await GenerateJwt(user, machineId);
    }

    public async Task<Either<SuccessAuthorize, FailedRegistration>> Register(NewUserCredentialsInput input)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.email);
        if (user is not null)
        {
            logger.LogWarning("Email already registered '{email}'", input.email);
            return RegistrationErrorConstants.EmailAlreadyRegistered();
        }

        var normalizedUserName = input.username.ToLowerInvariant();

        user = await ctx.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUserName);
        if (user is not null)
        {
            logger.LogWarning("Username already registered '{username}'", input.username);
            return RegistrationErrorConstants.UsernameAlreadyTaken();
        }

        var reserved = await ctx.Reservation.FirstOrDefaultAsync(x => x.NormalizedUserName == normalizedUserName);

        if (reserved is not null)
        {
            logger.LogWarning("Username reserved '{username}'", input.username);
            if (reserved.IsBanned)
                return RegistrationErrorConstants.UsernameAlreadyTaken();
            return RegistrationErrorConstants.UsernameReserved();
        }

        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var userId = Guid.NewGuid();
            user = new UserEntity()
            {
                AvatarFileId              = null,
                CreatedAt                 = DateTime.UtcNow,
                Email                     = input.email,
                Id                        = userId,
                Username                  = input.username,
                PasswordDigest            = passwordHashingService.HashPassword(input.password),
                DisplayName               = input.displayName,
                DateOfBirth               = input.birthDate,
                AgreeTOS                  = input.argreeTos,
                AllowedSendOptionalEmails = input.argreeOptionalEmails
            };
            await ctx.Users.AddAsync(user);

            await ctx.UserProfiles.AddAsync(new UserProfileEntity
            {
                UserId = userId,
                Id     = Guid.NewGuid(),
                Badges = [],
            });

            await ctx.SaveChangesAsync();
        });
        if (user is null)
            return RegistrationErrorConstants.InternalError();

        return await GenerateJwt(user, this.GetUserMachineId());
    }

    public async Task<bool> BeginResetPass(string email)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            logger.LogWarning("Email not registered '{email}' cannot reset password", email);
            return true;
        }

        var userIp    = this.GetUserIp() ?? "unknown";
        var machineId = this.GetUserMachineId();

        await otpService.SendAsync(
            new SendOtpRequest(user.Email, user.Id, OtpPurpose.ResetPassword, machineId, user.PreferredOtpMethod),
            userIp
        );

        logger.LogInformation("User '{email}' requested password reset via {method}", email, user.PreferredOtpMethod);
        return true;
    }

    public async Task<Either<SuccessAuthorize, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            logger.LogWarning("Email not registered '{email}' cannot reset password", email);
            return AuthorizationError.BAD_OTP;
        }

        var machineId = this.GetUserMachineId();

        var verified = await otpService.VerifyAsync(
            new VerifyOtpRequest(user.Email, user.Id, OtpPurpose.ResetPassword, otpCode, machineId, user.PreferredOtpMethod)
        );

        if (!verified)
        {
            logger.LogWarning("Invalid OTP during password reset for '{email}'", email);
            return AuthorizationError.BAD_OTP;
        }

        user.PasswordDigest = passwordHashingService.HashPassword(newPassword);
        await db.SaveChangesAsync();

        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
        return await GenerateJwt(user, machineId);
    }

    public async Task<string> GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct)
    {
        await using var db              = await dbFactory.CreateDbContextAsync(ct);
        var             normalizedEmail = data.email?.ToLowerInvariant();
        var             user            = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        if (user is null)
            return "";

        return user.PreferredAuthMode.ToString();
    }

    private async Task<SuccessAuthorize> GenerateJwt(UserEntity user, string machineId)
        => await managerService.GenerateJwt(user.Id, machineId, ["argon.app"]);
}