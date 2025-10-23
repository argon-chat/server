namespace Argon.Grains;

using Api.Features.CoreLogic.Otp;
using k8s.KubeConfigModels;
using Metrics;
using Metrics.Gauges;
using Orleans.Concurrency;
using Services;
using Temporalio.Api.Update.V1;

[StatelessWorker]
public class AuthorizationGrain(
    IGrainFactory grainFactory,
    ILogger<AuthorizationGrain> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IMetricsCollector metrics,
    IOtpService otpService
) : Grain, IAuthorizationGrain
{
    private readonly CountPerTagGauge authSuccess = new(metrics, new("auth_success"));
    private readonly CountPerTagGauge authFailure = new(metrics, new("auth_failed"));
    private readonly CountPerTagGauge registerStatus = new(metrics, new("auth_register"));
    private readonly CountPerTagGauge resetCounter = new(metrics, new("auth_reset"));

    public async Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == input.email);

        if (user is null)
        {
            logger.LogWarning("User '{email}' not found", input.email);
            await authFailure.CountAsync("reason", "user_not_found");
            return AuthorizationError.BAD_CREDENTIALS;
        }

        return user.PreferredAuthMode switch
        {
            ArgonAuthMode.EmailPassword => await AuthorizePassword(user, input),
            ArgonAuthMode.EmailOtp => await AuthorizeWithOtp(user, input, requirePassword: false),
            ArgonAuthMode.EmailPasswordOtp => await AuthorizeWithOtp(user, input, requirePassword: true),
            _ => AuthorizationError.NONE
        };
    }

    private async Task<Either<string, AuthorizationError>> AuthorizePassword(UserEntity user, UserCredentialsInput input)
    {
        if (string.IsNullOrEmpty(input.password))
            return AuthorizationError.BAD_CREDENTIALS;

        var verified = passwordHashingService.VerifyPassword(input.password, user);
        if (!verified)
        {
            logger.LogWarning("User '{email}' entered invalid password", user.Email);
            await authFailure.CountAsync("reason", "bad_password");
            return AuthorizationError.BAD_CREDENTIALS;
        }

        await authSuccess.CountAsync("mode", "pwd");
        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
        return await GenerateJwt(user, this.GetUserMachineId());
    }

    private async Task<Either<string, AuthorizationError>> AuthorizeWithOtp(UserEntity user, UserCredentialsInput input, bool requirePassword)
    {
        if (requirePassword)
        {
            if (string.IsNullOrEmpty(input.password) || !passwordHashingService.VerifyPassword(input.password, user))
            {
                logger.LogWarning("Invalid password for '{email}'", user.Email);
                await authFailure.CountAsync("reason", "bad_password");
                return AuthorizationError.BAD_CREDENTIALS;
            }
        }

        var userIp = this.GetUserIp() ?? "unknown";
        var machineId = this.GetUserMachineId();
        var method = user.PreferredOtpMethod;

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
            await authFailure.CountAsync("reason", "bad_otp");
            return AuthorizationError.BAD_OTP;
        }

        await authSuccess.CountAsync("mode", $"{(requirePassword ? "pwd+" : "")}otp:{method}");
        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
        return await GenerateJwt(user, machineId);
    }

    public async Task<Either<string, FailedRegistration>> Register(NewUserCredentialsInput input)
    {
        await using var sw = metrics.StartTimer(new MeasurementId("register_latency"));
        await using var ctx = await dbFactory.CreateDbContextAsync();

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
        await strategy.ExecuteAsync(async () => {
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            try
            {
                var userId = Guid.NewGuid();
                user = new UserEntity()
                {
                    AvatarFileId = null,
                    CreatedAt = DateTime.UtcNow,
                    Email = input.email,
                    Id = userId,
                    Username = input.username,
                    NormalizedUsername = normalizedUserName,
                    PasswordDigest = passwordHashingService.HashPassword(input.password),
                    DisplayName = input.displayName,
                    DateOfBirth = input.birthDate
                };
                await ctx.Users.AddAsync(user);

                var agreements = new UserAgreements()
                {
                    AgreeTOS = input.argreeTos,
                    AllowedSendOptionalEmails = input.argreeOptionalEmails,
                    UserId = userId
                };
                await ctx.UserAgreements.AddAsync(agreements);

                await ctx.UserProfiles.AddAsync(new UserProfileEntity
                {
                    UserId = userId,
                    Id = Guid.NewGuid(),
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
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            logger.LogWarning("Email not registered '{email}' cannot reset password", email);
            return true;
        }

        var userIp = this.GetUserIp() ?? "unknown";
        var machineId = this.GetUserMachineId();

        await otpService.SendAsync(
            new SendOtpRequest(user.Email, user.Id, OtpPurpose.ResetPassword, machineId, user.PreferredOtpMethod),
            userIp
        );

        logger.LogInformation("User '{email}' requested password reset via {method}", email, user.PreferredOtpMethod);
        return true;
    }

    public async Task<Either<string, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword)
    {
        await using var sw = metrics.StartTimer(new MeasurementId("pass_reset_latency"));
        await resetCounter.CountAsync("stage", "apply");
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

    private async Task<string> GenerateJwt(UserEntity user, string machineId)
        => await managerService.GenerateJwt(user.Id, machineId);
}
