namespace Argon.Grains;

using Api.Features.CoreLogic.Otp;
using Argon.Features.Auth;
using Argon.Features.PhoneProviders;
using Metrics;
using Metrics.Gauges;
using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class AuthorizationGrain(
    IGrainFactory grainFactory,
    ILogger<AuthorizationGrain> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> context,
    IArgonCacheDatabase cache,
    IMetricsCollector metrics,
    IOptions<ArgonAuthOptions> authOptions,
    IPhoneProvider phoneProvider) : Grain, IAuthorizationGrain
{
    private readonly CountPerTagGauge authSuccess    = new(metrics, new("auth_success"));
    private readonly CountPerTagGauge authFailure    = new(metrics, new("auth_failed"));
    private readonly CountPerTagGauge registerStatus = new(metrics, new("auth_register"));
    private readonly CountPerTagGauge resetCounter   = new(metrics, new("auth_reset"));

    public async Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input)
        => authOptions.Value.Scenario switch
        {
            AuthorizationScenario.Email_Pwd_Otp => await AuthorizeEmailPwdOtp(input),
            AuthorizationScenario.Email_Otp     => await AuthorizeEmailOtp(input),
            AuthorizationScenario.Phone_Otp     => await AuthorizePhoneOtp(input),
            _                                   => AuthorizationError.BAD_CONFIGURATION
        };

    private async Task<Either<string, AuthorizationError>> AuthorizeEmailPwdOtp(UserCredentialsInput input)
    {
        await using var sw = metrics.StartTimer(new MeasurementId("auth_latency"));
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.email);
        if (user is null)
        {
            logger.LogWarning("Not found user '{email}'", input.email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        var verified = passwordHashingService.VerifyPassword(input.password, user);
        if (!verified)
        {
            logger.LogWarning("User '{email}' entered bad password", input.email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        if (string.IsNullOrEmpty(input.otpCode))
        {
            var otp = passwordHashingService.GenerateOtp(user.Id);
            await cache.StringSetAsync($"otp_auth:{user.Id}", otp.Hashed, TimeSpan.FromMinutes(3));

            await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
               .SendOtpCodeAsync(user.Email, otp.Code, TimeSpan.FromMinutes(15));

            logger.LogInformation("User '{email}' requested otp code", input.email);
            return AuthorizationError.REQUIRED_OTP;
        }

        var hashed = await cache.StringGetAsync($"otp_auth:{user.Id}");
        if (string.IsNullOrEmpty(hashed))
            return AuthorizationError.BAD_OTP;

        var userOtp = new OtpCode(input.otpCode);
        if (!SecureEquals(userOtp.Hashed, hashed))
        {
            logger.LogError("User '{email}' entered invalid otp", input.email);
            return AuthorizationError.BAD_OTP;
        }

        await cache.KeyDeleteAsync($"otp_auth:{user.Id}");
        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();

        return await GenerateJwt(user, this.GetUserMachineId());
    }

    private async Task<Either<string, AuthorizationError>> AuthorizeEmailOtp(UserCredentialsInput input)
    {
        await using var ctx  = await context.CreateDbContextAsync();
        var             user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.email);
        if (user is null)
        {
            logger.LogWarning("Not found user '{email}'", input.email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        if (string.IsNullOrEmpty(input.otpCode))
        {
            var otp = passwordHashingService.GenerateOtp(user.Id);
            await cache.StringSetAsync($"otp_auth:{user.Id}", otp.Hashed, TimeSpan.FromMinutes(3));

            await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
               .SendOtpCodeAsync(user.Email, otp.Code, TimeSpan.FromMinutes(15));

            logger.LogInformation("User '{email}' requested otp code", input.email);
            return AuthorizationError.REQUIRED_OTP;
        }

        var hashed = await cache.StringGetAsync($"otp_auth:{user.Id}");
        if (string.IsNullOrEmpty(hashed))
            return AuthorizationError.BAD_OTP;

        var userOtp = new OtpCode(input.otpCode);
        if (!SecureEquals(userOtp.Hashed, hashed))
        {
            logger.LogError("User '{email}' entered invalid otp", input.email);
            return AuthorizationError.BAD_OTP;
        }

        await cache.KeyDeleteAsync($"otp_auth:{user.Id}");
        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();

        return await GenerateJwt(user, this.GetUserMachineId());
    }

    private async Task<Either<string, AuthorizationError>> AuthorizePhoneOtp(UserCredentialsInput input)
    {
        if (string.IsNullOrEmpty(input.phone))
            return AuthorizationError.BAD_CREDENTIALS;

        await using var ctx = await context.CreateDbContextAsync();
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.PhoneNumber == input.phone);
        if (user is null)
        {
            logger.LogWarning("Not found user with phone '{phone}'", input.phone);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        if (string.IsNullOrEmpty(input.otpCode))
        {
            var requestId = await phoneProvider.SendCode(input.phone);
            if (string.IsNullOrEmpty(requestId))
            {
                logger.LogError("Phone provider did not return requestId for {phone}", input.phone);
                return AuthorizationError.BAD_OTP;
            }

            await cache.StringSetAsync($"otp_phone:{user.Id}", requestId, TimeSpan.FromMinutes(5));

            logger.LogInformation("Sent login OTP to phone {phone} with requestId {requestId}", input.phone, requestId);
            return AuthorizationError.REQUIRED_OTP;
        }

        var requestIdCached = await cache.StringGetAsync($"otp_phone:{user.Id}");
        if (string.IsNullOrEmpty(requestIdCached))
        {
            logger.LogWarning("OTP requestId not found/expired for user '{phone}'", input.phone);
            return AuthorizationError.BAD_OTP;
        }

        var verified = await phoneProvider.VerifyCode(input.phone, requestIdCached, input.otpCode);
        if (!verified)
        {
            logger.LogWarning("User '{phone}' entered invalid otp code", input.phone);
            return AuthorizationError.BAD_OTP;
        }

        await cache.KeyDeleteAsync($"otp_phone:{user.Id}");

        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();

        return await GenerateJwt(user, this.GetUserMachineId());
    }


    private static bool SecureEquals(string? a, string? b)
    {
        if (a is null || b is null)
            return false;

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        if (aBytes.Length != bBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    public async Task<Either<string, FailedRegistration>> Register(NewUserCredentialsInput input)
    {
        await using var sw  = metrics.StartTimer(new MeasurementId("register_latency"));
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.email);
        if (user is not null)
        {
            await registerStatus.CountAsync("reason", "email_taken");
            logger.LogWarning("Email already registered '{email}'", input.email);
            return RegistrationErrorConstants.EmailAlreadyRegistered();
        }

        var normalizedUserName = input.username.ToLowerInvariant();

        user = await ctx.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUserName);
        if (user is not null)
        {
            logger.LogWarning("Username already registered '{username}'", input.username);
            await registerStatus.CountAsync("reason", "username_taken");
            return RegistrationErrorConstants.UsernameAlreadyTaken();
        }

        var reserved = await ctx.Reservation.FirstOrDefaultAsync(x => x.NormalizedUserName == normalizedUserName);

        if (reserved is not null)
        {
            logger.LogWarning("Username reserved '{username}'", input.username);
            await registerStatus.CountAsync("reason", reserved.IsBanned ? "username_banned" : "username_reserved");
            if (reserved.IsBanned)
                return RegistrationErrorConstants.UsernameAlreadyTaken();
            return RegistrationErrorConstants.UsernameReserved();
        }

        // TODO check sso email (mx records and etc)

        // TODO check region banned

        // TODO check banned emails
        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            try
            {
                var userId = Guid.NewGuid();
                user = new UserEntity()
                {
                    AvatarFileId       = null,
                    CreatedAt          = DateTime.UtcNow,
                    Email              = input.email,
                    Id                 = userId,
                    Username           = input.username,
                    NormalizedUsername = normalizedUserName,
                    PasswordDigest     = passwordHashingService.HashPassword(input.password),
                    DisplayName        = input.displayName,
                };
                await ctx.Users.AddAsync(user);

                var agreements = new UserAgreements()
                {
                    AgreeTOS                  = input.argreeTos,
                    AllowedSendOptionalEmails = input.argreeOptionalEmails,
                    UserId                    = userId
                };
                await ctx.UserAgreements.AddAsync(agreements);

                await ctx.UserProfiles.AddAsync(new UserProfileEntity
                {
                    UserId = userId,
                    Id     = Guid.NewGuid(),
                    Badges = [],
                });

                await ctx.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
        if (user is null)
            return RegistrationErrorConstants.InternalError();

        await registerStatus.CountAsync("result", "ok");

        return await GenerateJwt(user, this.GetUserMachineId());
    }

    public async Task<bool> BeginResetPass(string email)
    {
        await using var sw = metrics.StartTimer(new MeasurementId("begin_pass_reset_latency"));
        await resetCounter.CountAsync("stage", "begin");
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            logger.LogWarning("Email not registered '{email}' cannot be reset pass", email);
            return true;
        }

        var otp = passwordHashingService.GenerateOtp(user.Id);
        logger.LogError("User '{email}' generated otp code ({optHash})", email, otp.Hashed);
        await cache.StringSetAsync($"otp_reset:{user.Id}", otp.Hashed, TimeSpan.FromMinutes(3));
        await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
           .SendResetCodeAsync(email, otp.Code, TimeSpan.FromHours(1));
        return true;
    }

    public async Task<Either<string, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword)
    {
        await using var sw = metrics.StartTimer(new MeasurementId("pass_reset_latency"));
        await resetCounter.CountAsync("stage", "apply");
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            logger.LogWarning("Email not registered '{email}' cannot be reset pass", email);
            return AuthorizationError.BAD_OTP;
        }

        var hashed = await cache.StringGetAsync($"otp_reset:{user.Id}");

        if (string.IsNullOrEmpty(hashed))
            return AuthorizationError.BAD_OTP;

        var otp = new OtpCode(otpCode);

        if (!otp.Hashed.Equals(hashed))
        {
            logger.LogError("User '{email}' entered invalid otp code ({optHash}) != sysHash: ({hashed})", email,
                otp.Hashed, hashed);
            return AuthorizationError.BAD_OTP;
        }

        await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
           .SendNotificationResetPasswordAsync(email);

        user.PasswordDigest = passwordHashingService.HashPassword(newPassword);
        ctx.Users.Update(user);
        await ctx.SaveChangesAsync();
        return await GenerateJwt(user, this.GetUserMachineId());
    }

    private async Task<string> GenerateJwt(UserEntity User, string machineId) => await managerService.GenerateJwt(User.Id, machineId);
}