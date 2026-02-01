namespace Argon.Features.Auth;

using Argon.Api.Features.CoreLogic.Otp;
using Services;
using System.Diagnostics.Metrics;

public class ArgonAuthorizationService(
    IGrainFactory grainFactory,
    ILogger<ArgonAuthorizationService> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOtpService otpService
) : IArgonAuthorizationService
{
    public async Task<Either<SuccessAuthorize, AuthorizationError>> Authorize(UserCredentialsInput input, string userIp, string machineId)
    {
        var             sw = Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == input.email);

        if (user is null)
        {
            sw.Stop();

            AuthorizationGrainInstrument.AuthorizationAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "bad_credentials"),
                new KeyValuePair<string, object?>("auth_mode", "unknown"));

            AuthorizationGrainInstrument.AuthorizationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "failed"),
                new KeyValuePair<string, object?>("auth_mode", "unknown"));

            return AuthorizationError.BAD_CREDENTIALS;
        }

        var authModeTag = user.PreferredAuthMode.ToString().ToLowerInvariant();

        var result = user.PreferredAuthMode switch
        {
            ArgonAuthMode.EmailPassword    => await AuthorizePassword(user, input, true, machineId),
            ArgonAuthMode.EmailOtp         => await AuthorizeWithOtp(user, input, requirePassword: false, true, userIp, machineId),
            ArgonAuthMode.EmailPasswordOtp => await AuthorizeWithOtp(user, input, requirePassword: true, true, userIp, machineId),
            _                              => AuthorizationError.NONE
        };

        sw.Stop();

        var resultTag = result.IsSuccess
            ? "success"
            : result.Error switch
            {
                AuthorizationError.BAD_CREDENTIALS => "bad_credentials",
                AuthorizationError.BAD_OTP         => "bad_otp",
                AuthorizationError.REQUIRED_OTP    => "required_otp",
                _                                  => "error"
            };

        AuthorizationGrainInstrument.AuthorizationAttempts.Add(1,
            new KeyValuePair<string, object?>("result", resultTag),
            new KeyValuePair<string, object?>("auth_mode", authModeTag));

        AuthorizationGrainInstrument.AuthorizationDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("result", result.IsSuccess ? "success" : "failed"),
            new KeyValuePair<string, object?>("auth_mode", authModeTag));

        return result;
    }

    public async Task<Either<SuccessAuthorize, AuthorizationError>> ExternalAuthorize(UserCredentialsInput input)
    {
        var             sw = Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == input.email);

        if (user is null)
        {
            sw.Stop();

            AuthorizationGrainInstrument.ExternalAuthorizationAttempts.Add(1,
                new KeyValuePair<string, object?>("result", "failed"),
                new KeyValuePair<string, object?>("auth_mode", "unknown"));

            return AuthorizationError.BAD_CREDENTIALS;
        }

        var authModeTag = user.PreferredAuthMode.ToString().ToLowerInvariant();

        var result = user.PreferredAuthMode switch
        {
            ArgonAuthMode.EmailPassword    => await AuthorizePassword(user, input, false),
            ArgonAuthMode.EmailOtp         => await AuthorizeWithOtp(user, input, requirePassword: false, requiredMachineId: false),
            ArgonAuthMode.EmailPasswordOtp => await AuthorizeWithOtp(user, input, requirePassword: true, requiredMachineId: false),
            _                              => AuthorizationError.NONE
        };

        sw.Stop();

        AuthorizationGrainInstrument.ExternalAuthorizationAttempts.Add(1,
            new KeyValuePair<string, object?>("result", result.IsSuccess ? "success" : "failed"),
            new KeyValuePair<string, object?>("auth_mode", authModeTag));

        return result;
    }

    private async Task<Either<SuccessAuthorize, AuthorizationError>> AuthorizePassword(UserEntity user, UserCredentialsInput input,
        bool requiredMachineId = true, string? machineId = null)
    {
        if (string.IsNullOrEmpty(input.password))
            return AuthorizationError.BAD_CREDENTIALS;

        var verified = passwordHashingService.VerifyPassword(input.password, user);
        if (!verified)
        {
            logger.LogWarning("User '{email}' entered invalid password", user.Email);
            return AuthorizationError.BAD_CREDENTIALS;
        }


        if (requiredMachineId)
        {
            await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
            return await GenerateJwt(user, machineId ?? throw new InvalidOperationException());
        }

        return await GenerateJwt(user);
    }

    private async Task<Either<SuccessAuthorize, AuthorizationError>> AuthorizeWithOtp(UserEntity user, UserCredentialsInput input,
        bool requirePassword, bool requiredMachineId = true, string? userIp = null, string? machineId = null)
    {
        if (requirePassword)
        {
            if (string.IsNullOrEmpty(input.password) || !passwordHashingService.VerifyPassword(input.password, user))
            {
                logger.LogWarning("Invalid password for '{email}'", user.Email);
                return AuthorizationError.BAD_CREDENTIALS;
            }
        }
        var method    = user.PreferredOtpMethod;

        if (string.IsNullOrEmpty(input.otpCode))
        {
            await otpService.SendAsync(
                new SendOtpRequest(user.Email, user.Id, OtpPurpose.SignIn, machineId, method),
                userIp
            );

            AuthorizationGrainInstrument.AuthorizationOtpSent.Add(1,
                new KeyValuePair<string, object?>("purpose", "sign_in"),
                new KeyValuePair<string, object?>("method", method.ToString().ToLowerInvariant()));

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

        if (requiredMachineId)
        {
            await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
            return await GenerateJwt(user, machineId ?? throw new InvalidOperationException());
        }

        return await GenerateJwt(user);
    }

    public async Task<Either<SuccessAuthorize, FailedRegistration>> Register(NewUserCredentialsInput input, string machineId)
    {
        var             sw  = Stopwatch.StartNew();
        await using var ctx = await dbFactory.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.email);
        if (user is not null)
        {
            sw.Stop();

            AuthorizationGrainInstrument.UserRegistrations.Add(1,
                new KeyValuePair<string, object?>("result", "email_taken"));

            AuthorizationGrainInstrument.UserRegistrationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "failed"));

            logger.LogWarning("Email already registered '{email}'", input.email);
            return RegistrationErrorConstants.EmailAlreadyRegistered();
        }

        var normalizedUserName = input.username.ToLowerInvariant();

        user = await ctx.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUserName);
        if (user is not null)
        {
            sw.Stop();

            AuthorizationGrainInstrument.UserRegistrations.Add(1,
                new KeyValuePair<string, object?>("result", "username_taken"));

            AuthorizationGrainInstrument.UserRegistrationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "failed"));

            logger.LogWarning("Username already registered '{username}'", input.username);
            return RegistrationErrorConstants.UsernameAlreadyTaken();
        }

        var reserved = await ctx.Reservation.FirstOrDefaultAsync(x => x.NormalizedUserName == normalizedUserName);

        if (reserved is not null)
        {
            sw.Stop();

            var resultTag = reserved.IsBanned ? "username_taken" : "username_reserved";

            AuthorizationGrainInstrument.UserRegistrations.Add(1,
                new KeyValuePair<string, object?>("result", resultTag));

            AuthorizationGrainInstrument.UserRegistrationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "failed"));

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

        sw.Stop();

        if (user is null)
        {
            AuthorizationGrainInstrument.UserRegistrations.Add(1,
                new KeyValuePair<string, object?>("result", "error"));

            AuthorizationGrainInstrument.UserRegistrationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "failed"));

            return RegistrationErrorConstants.InternalError();
        }

        AuthorizationGrainInstrument.UserRegistrations.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        AuthorizationGrainInstrument.UserRegistrationDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("result", "success"));

        return await GenerateJwt(user, machineId);
    }

    public async Task<bool> BeginResetPass(string email, string userIp, string machineId)
    {
        var             sw = Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            sw.Stop();

            AuthorizationGrainInstrument.PasswordResets.Add(1,
                new KeyValuePair<string, object?>("stage", "request"),
                new KeyValuePair<string, object?>("result", "failed"));

            AuthorizationGrainInstrument.PasswordResetDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", "request"));

            logger.LogWarning("Email not registered '{email}' cannot reset password", email);
            return true;
        }

        await otpService.SendAsync(
            new SendOtpRequest(user.Email, user.Id, OtpPurpose.ResetPassword, machineId, user.PreferredOtpMethod),
            userIp
        );

        sw.Stop();

        AuthorizationGrainInstrument.PasswordResets.Add(1,
            new KeyValuePair<string, object?>("stage", "request"),
            new KeyValuePair<string, object?>("result", "success"));

        AuthorizationGrainInstrument.PasswordResetDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", "request"));

        AuthorizationGrainInstrument.AuthorizationOtpSent.Add(1,
            new KeyValuePair<string, object?>("purpose", "reset_password"),
            new KeyValuePair<string, object?>("method", user.PreferredOtpMethod.ToString().ToLowerInvariant()));

        logger.LogInformation("User '{email}' requested password reset via {method}", email, user.PreferredOtpMethod);
        return true;
    }

    public async Task<Either<SuccessAuthorize, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword, string userMachineId)
    {
        var             sw = Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            sw.Stop();

            AuthorizationGrainInstrument.PasswordResets.Add(1,
                new KeyValuePair<string, object?>("stage", "verify"),
                new KeyValuePair<string, object?>("result", "failed"));

            AuthorizationGrainInstrument.PasswordResetDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", "verify"));

            logger.LogWarning("Email not registered '{email}' cannot reset password", email);
            return AuthorizationError.BAD_OTP;
        }

        var verified = await otpService.VerifyAsync(
            new VerifyOtpRequest(user.Email, user.Id, OtpPurpose.ResetPassword, otpCode, userMachineId, user.PreferredOtpMethod)
        );

        if (!verified)
        {
            sw.Stop();

            AuthorizationGrainInstrument.PasswordResets.Add(1,
                new KeyValuePair<string, object?>("stage", "verify"),
                new KeyValuePair<string, object?>("result", "failed"));

            AuthorizationGrainInstrument.PasswordResetDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", "verify"));

            logger.LogWarning("Invalid OTP during password reset for '{email}'", email);
            return AuthorizationError.BAD_OTP;
        }

        user.PasswordDigest = passwordHashingService.HashPassword(newPassword);
        await db.SaveChangesAsync();

        sw.Stop();

        AuthorizationGrainInstrument.PasswordResets.Add(1,
            new KeyValuePair<string, object?>("stage", "verify"),
            new KeyValuePair<string, object?>("result", "success"));

        AuthorizationGrainInstrument.PasswordResetDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", "verify"));

        await grainFactory.GetGrain<IUserGrain>(user.Id).UpdateUserDeviceHistory();
        return await GenerateJwt(user, userMachineId);
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

    private async Task<SuccessAuthorize> GenerateJwt(UserEntity user)
        => await managerService.GenerateJwt(user.Id, ["argon.app"]);
}

public static class AuthorizationGrainInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly Counter<long> AuthorizationAttempts = Meter.CreateCounter<long>(
        InstrumentNames.AuthorizationAttempts,
        description: "Total number of user authorization attempts");

    public static readonly Histogram<double> AuthorizationDuration = Meter.CreateHistogram<double>(
        InstrumentNames.AuthorizationDuration,
        unit: "ms",
        description: "Duration of authorization operations");

    public static readonly Counter<long> UserRegistrations = Meter.CreateCounter<long>(
        InstrumentNames.UserRegistrations,
        description: "Total number of user registrations");

    public static readonly Histogram<double> UserRegistrationDuration = Meter.CreateHistogram<double>(
        InstrumentNames.UserRegistrationDuration,
        unit: "ms",
        description: "Duration of registration operations");

    public static readonly Counter<long> PasswordResets = Meter.CreateCounter<long>(
        InstrumentNames.PasswordResets,
        description: "Total number of password reset requests");

    public static readonly Histogram<double> PasswordResetDuration = Meter.CreateHistogram<double>(
        InstrumentNames.PasswordResetDuration,
        unit: "ms",
        description: "Duration of password reset operations");

    public static readonly Counter<long> ExternalAuthorizationAttempts = Meter.CreateCounter<long>(
        InstrumentNames.ExternalAuthorizationAttempts,
        description: "Total number of external authorization attempts");

    public static readonly Counter<long> AuthorizationOtpSent = Meter.CreateCounter<long>(
        InstrumentNames.AuthorizationOtpSent,
        description: "Total number of OTP sends during authorization flow");
}