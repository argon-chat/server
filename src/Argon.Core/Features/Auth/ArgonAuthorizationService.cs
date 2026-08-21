namespace Argon.Features.Auth;

using Argon.Api.Features.CoreLogic.Otp;
using Argon.Core.Features.CoreLogic.Passkeys;
using Services;
using System.Diagnostics.Metrics;
using Fido2NetLib;
using Fido2NetLib.Objects;

public class ArgonAuthorizationService(
    IGrainFactory grainFactory,
    ILogger<ArgonAuthorizationService> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOtpService otpService,
    IFido2 fido2,
    IPendingPasskeyStore pendingPasskeyStore,
    IArgonCacheDatabase cacheDatabase
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
            ArgonAuthMode.PasskeyOnly or ArgonAuthMode.PasskeyWithOtp
                                           => AuthorizationError.BAD_CREDENTIALS, // passkey users must use passkey login flow
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
            ArgonAuthMode.PasskeyOnly or ArgonAuthMode.PasskeyWithOtp
                                           => AuthorizationError.BAD_CREDENTIALS, // passkey users must use passkey login flow
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
                userIp!
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
                AllowedSendOptionalEmails = input.argreeOptionalEmails,
                AgreeTosVersion           = input.tosVersion,
                AgreePrivacyVersion       = input.privacyVersion
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

    public async Task<Either<SuccessAuthorize, FailedRegistration>> ExternalRegister(NewUserCredentialsInput input)
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
                AllowedSendOptionalEmails = input.argreeOptionalEmails,
                AgreeTosVersion           = input.tosVersion,
                AgreePrivacyVersion       = input.privacyVersion
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

        return await GenerateJwt(user);
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

    public async Task<BeginPasskeyLoginResult> BeginPasskeyLogin(string? email, CancellationToken ct)
    {
        try
        {
            var allowedCredentials = new List<PublicKeyCredentialDescriptor>();

            if (!string.IsNullOrEmpty(email))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var normalizedEmail = email.ToLowerInvariant();
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
                if (user is null)
                    return new BeginPasskeyLoginResult(null, PasskeyLoginError.USER_NOT_FOUND);

                var passkeys = await db.Passkeys
                    .Where(p => p.UserId == user.Id && p.IsCompleted && !p.IsDeleted && p.CredentialId != null)
                    .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId!))
                    .ToListAsync(ct);

                if (passkeys.Count == 0)
                    return new BeginPasskeyLoginResult(null, PasskeyLoginError.NO_PASSKEYS);

                allowedCredentials = passkeys;
            }

            var options = fido2.GetAssertionOptions(
                new GetAssertionOptionsParams
                {
                    AllowedCredentials = allowedCredentials,
                    UserVerification = UserVerificationRequirement.Required
                });

            var optionsJson = options.ToJson();

            // Store challenge keyed by a nonce (not user-scoped since user may be unknown)
            var challengeNonce = Guid.NewGuid().ToString("N");
            await cacheDatabase.StringSetAsync(
                $"passkey:login:{challengeNonce}",
                optionsJson,
                TimeSpan.FromMinutes(5), ct);

            // Embed the nonce in the response so the client can send it back
            var responseJson = System.Text.Json.JsonSerializer.Serialize(new { nonce = challengeNonce, options = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(optionsJson) });

            return new BeginPasskeyLoginResult(responseJson, PasskeyLoginError.NONE);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin passkey login");
            return new BeginPasskeyLoginResult(null, PasskeyLoginError.INTERNAL_ERROR);
        }
    }

    public async Task<PasskeyLoginResult> CompletePasskeyLogin(string assertionResponseJson, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assertionResponseJson))
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.INVALID_ASSERTION);

            var payload = System.Text.Json.JsonSerializer.Deserialize<PasskeyCompletePayload>(assertionResponseJson);
            if (payload is null || string.IsNullOrEmpty(payload.Nonce) || string.IsNullOrEmpty(payload.AssertionResponseJson))
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.INVALID_ASSERTION);

            // Retrieve stored assertion options
            var optionsJson = await cacheDatabase.StringGetAsync($"passkey:login:{payload.Nonce}", ct);
            if (string.IsNullOrEmpty(optionsJson))
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.CHALLENGE_EXPIRED);

            var options = AssertionOptions.FromJson(optionsJson);

            var assertionResponse = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(payload.AssertionResponseJson);
            if (assertionResponse is null)
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.INVALID_ASSERTION);

            // Extract userHandle to find the user
            if (assertionResponse.Response.UserHandle is null || assertionResponse.Response.UserHandle.Length == 0)
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.INVALID_ASSERTION);

            var userId = new Guid(assertionResponse.Response.UserHandle);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Find the passkey by credential ID and user
            var credentialIdBytes = assertionResponse.RawId;
            var passkey = await db.Passkeys.FirstOrDefaultAsync(
                p => p.CredentialId != null && p.CredentialId == credentialIdBytes
                     && p.UserId == userId && p.IsCompleted && !p.IsDeleted, ct);

            if (passkey is null || passkey.PublicKey is null)
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.INVALID_ASSERTION);

            var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = options,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = async (args, cancellationToken) =>
                {
                    var stored = await db.Passkeys.AnyAsync(
                        p => p.CredentialId != null && p.CredentialId == args.CredentialId
                             && p.UserId == userId && !p.IsDeleted,
                        cancellationToken);
                    return stored;
                }
            }, ct);

            // Update sign count for clone detection
            passkey.SignCount = result.SignCount;
            passkey.LastUsedAt = DateTimeOffset.UtcNow;
            passkey.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Delete challenge from cache
            await cacheDatabase.KeyDeleteAsync($"passkey:login:{payload.Nonce}");

            // Check if user requires OTP after passkey
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.USER_NOT_FOUND);

            if (user.PreferredAuthMode == ArgonAuthMode.PasskeyWithOtp)
            {
                // Store a nonce marking passkey-verified, send OTP
                var passkeyNonce = Guid.NewGuid().ToString("N");
                await cacheDatabase.StringSetAsync(
                    $"passkey:otp:{passkeyNonce}",
                    userId.ToString(),
                    TimeSpan.FromMinutes(10), ct);

                var method = user.PreferredOtpMethod;
                await otpService.SendAsync(
                    new SendOtpRequest(user.Email, user.Id, OtpPurpose.SignIn, null, method),
                    "passkey-login");

                logger.LogInformation("Passkey verified for user {UserId}, OTP sent via {Method}", userId, method);
                return new PasskeyLoginResult(false, null, userId, true, passkeyNonce, PasskeyLoginError.NONE);
            }

            // PasskeyOnly — generate JWT directly
            logger.LogInformation("Passkey login successful for user {UserId}", userId);
            var jwt = await managerService.GenerateJwt(userId, ["argon.app"]);
            return new PasskeyLoginResult(true, jwt.token, userId, false, null, PasskeyLoginError.NONE);
        }
        catch (Fido2VerificationException ex)
        {
            logger.LogWarning(ex, "Passkey login verification failed");
            return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.VERIFICATION_FAILED);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to complete passkey login");
            return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.INTERNAL_ERROR);
        }
    }

    public async Task<PasskeyLoginResult> ConfirmPasskeyOtp(string passkeyNonce, string otpCode, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(passkeyNonce) || string.IsNullOrEmpty(otpCode))
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.BAD_OTP);

            var userIdStr = await cacheDatabase.StringGetAsync($"passkey:otp:{passkeyNonce}", ct);
            if (string.IsNullOrEmpty(userIdStr))
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.NONCE_EXPIRED);

            var userId = Guid.Parse(userIdStr);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.USER_NOT_FOUND);

            var method = user.PreferredOtpMethod;
            var verified = await otpService.VerifyAsync(
                new VerifyOtpRequest(user.Email, user.Id, OtpPurpose.SignIn, otpCode, null, method));

            if (!verified)
            {
                logger.LogWarning("Invalid OTP for passkey login, user {UserId}", userId);
                return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.BAD_OTP);
            }

            // Delete the OTP nonce
            await cacheDatabase.KeyDeleteAsync($"passkey:otp:{passkeyNonce}");

            logger.LogInformation("Passkey + OTP login successful for user {UserId}", userId);
            var jwt = await managerService.GenerateJwt(userId, ["argon.app"]);
            return new PasskeyLoginResult(true, jwt.token, userId, false, null, PasskeyLoginError.NONE);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to confirm passkey OTP");
            return new PasskeyLoginResult(false, null, null, false, null, PasskeyLoginError.INTERNAL_ERROR);
        }
    }

    private async Task<SuccessAuthorize> GenerateJwt(UserEntity user, string machineId)
        => await managerService.GenerateJwt(user.Id, machineId, ["argon.app"]);

    private async Task<SuccessAuthorize> GenerateJwt(UserEntity user)
        => await managerService.GenerateJwt(user.Id, ["argon.app"]);
}

public record PasskeyCompletePayload
{
    [System.Text.Json.Serialization.JsonPropertyName("nonce")]
    public string? Nonce { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("assertionResponseJson")]
    public string? AssertionResponseJson { get; init; }
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