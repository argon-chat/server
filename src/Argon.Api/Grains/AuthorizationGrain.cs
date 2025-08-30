namespace Argon.Grains;

using Features.Otp;
using Metrics;
using Metrics.Gauges;
using Org.BouncyCastle.Ocsp;
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
    IMetricsCollector metrics) : Grain, IAuthorizationGrain
{
    private readonly CountPerTagGauge authSuccess    = new(metrics, new("auth_success"));
    private readonly CountPerTagGauge authFailure    = new(metrics, new("auth_failed"));
    private readonly CountPerTagGauge registerStatus = new(metrics, new("auth_register"));
    private readonly CountPerTagGauge resetCounter   = new(metrics, new("auth_reset"));


    public async Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input)
    {
        await using var sw  = metrics.StartTimer(new MeasurementId("auth_latency"));
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.email);
        if (user is null)
        {
            logger.LogWarning("Not found user '{email}'", input.email);
            await authFailure.CountAsync("reason", "user_not_found");
            return AuthorizationError.BAD_CREDENTIALS;
        }

        var verified = passwordHashingService.VerifyPassword(input.password, user);

        if (!verified)
        {
            logger.LogWarning("User '{email}' entered bad password, not matched", input.email);
            await authFailure.CountAsync("reason", "bad_password");
            return AuthorizationError.BAD_CREDENTIALS;
        }

        if (string.IsNullOrEmpty(input.otpCode))
        {
            var otp = passwordHashingService.GenerateOtp(user.Id);
            //user.OtpHash = otp.Hashed;
            ctx.Users.Update(user);
            await ctx.SaveChangesAsync();
            // TODO check latest send otp time (evade ddos)
            await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
               .SendOtpCodeAsync(user.Email, otp.Code, TimeSpan.FromMinutes(15));
            logger.LogInformation("User '{email}' invoked a generate otp code", input.email);
            return AuthorizationError.REQUIRED_OTP;
        }

        var userOtp = new OtpCode(input.otpCode);

        //if (!(user.o?.Equals(userOtp.Hashed) ?? false))
        //{
        //    logger.LogError("User '{email}' entered invalid otp code {otp} {optHash}", input.Email, userOtp.Code, userOtp.Hashed);
        //    await authFailure.CountAsync("reason", "bad_otp");
        //    return AuthorizationError.BAD_OTP;
        //}

        await authSuccess.CountAsync("method", "password+otp");


        //user.OtpHash = null;
        //ctx.Users.Update(user);


        //await ctx.SaveChangesAsync();

        

        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();

        return await GenerateJwt(user, this.GetUserMachineId());
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
        logger.LogError("User '{email}' generated otp code {otp} ({optHash})", email, otp.Code, otp.Hashed);
        await cache.StringSetAsync($"otp_reset:{user.Id}", otp.Hashed, TimeSpan.FromMinutes(3));
        await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
           .SendResetCodeAsync(email, otp.Code, TimeSpan.FromHours(1));
        return true;
    }

    //public async Task<Either<string, AuthorizationError>> ResetPass(UserResetPassInput resetData)
    //{
    //    await using var sw = metrics.StartTimer(new MeasurementId("pass_reset_latency"));
    //    await resetCounter.CountAsync("stage", "apply");
    //    await using var ctx = await context.CreateDbContextAsync();

    //    var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == resetData.Email);
    //    if (user is null)
    //    {
    //        logger.LogWarning("Email not registered '{email}' cannot be reset pass", resetData.Email);
    //        return AuthorizationError.BAD_OTP;
    //    }

    //    var hashed = await cache.StringGetAsync($"otp_reset:{user.Id}");

    //    if (string.IsNullOrEmpty(hashed))
    //        return AuthorizationError.BAD_OTP;

    //    var otp = new OtpCode(resetData.otpCode);

    //    if (!otp.Hashed.Equals(hashed))
    //    {
    //        logger.LogError("User '{email}' entered invalid otp code {otp} enterHash: ({optHash}) != sysHash: ({hashed})", resetData.Email, otp.Code,
    //            otp.Hashed, hashed);
    //        return AuthorizationError.BAD_OTP;
    //    }

    //    await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
    //       .SendNotificationResetPasswordAsync(resetData.Email);

    //    user.PasswordDigest = passwordHashingService.HashPassword(resetData.newPassword);
    //    ctx.Users.Update(user);
    //    await ctx.SaveChangesAsync();
    //    return await GenerateJwt(user, this.GetUserMachineId());
    //}

    private async Task<string> GenerateJwt(UserEntity User, string machineId) => await managerService.GenerateJwt(User.Id, machineId);
}