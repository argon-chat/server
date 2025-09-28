namespace Argon.Grains;

using Api.Features.CoreLogic.Otp;
using Argon.Features.Auth;
using Argon.Features.PhoneProviders;
using Metrics;
using Metrics.Gauges;
using Orleans.Concurrency;
using Services;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

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
    IPhoneProvider phoneProvider,
    ITemporalClient temporalClient) : Grain, IAuthorizationGrain
{
    private readonly CountPerTagGauge authSuccess    = new(metrics, new("auth_success"));
    private readonly CountPerTagGauge authFailure    = new(metrics, new("auth_failed"));
    private readonly CountPerTagGauge registerStatus = new(metrics, new("auth_register"));
    private readonly CountPerTagGauge resetCounter   = new(metrics, new("auth_reset"));

    public async Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input)
        => authOptions.Value.Scenario switch
        {
            AuthorizationScenario.Email_Pwd_Otp => await AuthorizeEmailPwdOtp(input, true),
            AuthorizationScenario.Email_Otp     => await AuthorizeEmailPwdOtp(input, false),
            AuthorizationScenario.Phone_Otp     => await AuthorizePhoneOtp(input),
            _                                   => AuthorizationError.NONE
        };


    private async Task<Either<string, AuthorizationError>> AuthorizeEmailPwdOtp(UserCredentialsInput input, bool validatePassword)
    {
        await using var ctx = await context.CreateDbContextAsync();
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.email);
        if (user is null)
        {
            logger.LogWarning("Not found user '{email}'", input.email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        if (validatePassword)
        {
            var verified = passwordHashingService.VerifyPassword(input.password, user);
            if (!verified)
            {
                logger.LogWarning("User '{email}' entered bad password", input.email);
                return AuthorizationError.BAD_CREDENTIALS;
            }
        }

        var workflowId = $"otp-auth:{user.Id}";
        var userIp     = this.GetUserIp();
        var machineId  = this.GetUserMachineId();

        if (string.IsNullOrEmpty(input.otpCode))
        {
            await temporalClient.StartWorkflowAsync(
                (OtpWorkflow wf) => wf.RunAsync(user.Email, OtpPurpose.SignIn, machineId, userIp ?? "unknown"),
                new WorkflowOptions(id: workflowId, taskQueue: "argon-task-queue")
                {
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting
                });

            logger.LogInformation("User '{email}' requested otp code (workflow {WorkflowId})", input.email, workflowId);
            return AuthorizationError.REQUIRED_OTP;
        }

        var handle = temporalClient.GetWorkflowHandle<OtpWorkflow>(workflowId);

        try
        {
            await handle.SignalAsync(wf => wf.SubmitCodeAsync(input.otpCode));

            var verified = await handle.QueryAsync(wf => wf.IsVerified);
            if (!verified)
            {
                logger.LogError("User '{email}' entered invalid otp", input.email);
                return AuthorizationError.BAD_OTP;
            }
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            logger.LogWarning("OTP workflow not found for '{email}'", input.email);
            return AuthorizationError.BAD_OTP;
        }
        catch (WorkflowFailedException ex)
        {
            logger.LogError(ex, "OTP workflow failed for '{email}'", input.email);
            return AuthorizationError.BAD_OTP;
        }

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
                    DateOfBirth        = input.birthDate
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

        var userIp        = this.GetUserIp() ?? throw new InvalidOperationException("UserIp is required");
        var userMachineId = this.GetUserMachineId();
        var workflowId    = $"otp-reset:{user.Id}";

        await temporalClient.StartWorkflowAsync(
            (OtpWorkflow wf) => wf.RunAsync(email, OtpPurpose.ResetPassword, userMachineId, userIp),
            new WorkflowOptions(id: workflowId, taskQueue: "argon-task-queue")
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting
            });

        logger.LogInformation("User '{email}' requested password reset (workflow {WorkflowId})", email, workflowId);
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

        var workflowId = $"otp-reset:{user.Id}";
        var handle     = temporalClient.GetWorkflowHandle<OtpWorkflow, bool>(workflowId);

        try
        {
            await handle.SignalAsync(wf => wf.SubmitCodeAsync(otpCode));

            var ok = await handle.GetResultAsync();
            if (!ok)
            {
                logger.LogError("User '{email}' entered invalid/expired reset code", email);
                return AuthorizationError.BAD_OTP;
            }
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            logger.LogWarning("No reset workflow found for '{email}'", email);
            return AuthorizationError.BAD_OTP;
        }
        catch (WorkflowFailedException ex)
        {
            logger.LogError(ex, "Reset workflow failed for '{email}'", email);
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