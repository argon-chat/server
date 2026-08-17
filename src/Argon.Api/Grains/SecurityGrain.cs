namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Features.CoreLogic.Passkeys;
using Argon.Features.Auth;
using Argon.Features.Logic;
using Features.Integrations.Phones;
using Api.Features.CoreLogic.Otp;
using ion.runtime;
using Orleans.Concurrency;
using OtpNet;
using Services;
using Fido2NetLib;
using Fido2NetLib.Objects;
using System.Buffers.Text;

[StatelessWorker]
public class SecurityGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPasswordHashingService passwordHashingService,
    ITotpKeyStore totpKeyStore,
    IPendingPasskeyStore pendingPasskeyStore,
    IPhoneProvider phoneProvider,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    IUserPresenceService presence,
    IArgonCacheDatabase cache,
    IFido2 fido2,
    ILogger<SecurityGrain> logger) : Grain, ISecurityGrain
{
    private const int MaxPasskeys = 10;
    private static readonly TimeSpan VerificationCodeTtl = TimeSpan.FromMinutes(15);
    private const int MaxVerificationAttempts = 5;
    private const int MinPasswordLength = 8;
    private const int DefaultAutoDeleteMonths = 12;

    private Guid UserId => this.GetPrimaryKey();

    public async Task<IRequestEmailChangeResult> RequestEmailChangeAsync(string newEmail, string password, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new FailedRequestEmailChange(EmailChangeError.INTERNAL_ERROR);

            if (!passwordHashingService.VerifyPassword(password, user))
                return new FailedRequestEmailChange(EmailChangeError.INVALID_PASSWORD);

            if (!IsValidEmail(newEmail))
                return new FailedRequestEmailChange(EmailChangeError.INVALID_EMAIL);

            var normalizedNewEmail = newEmail.ToLowerInvariant();

            var existingUser = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedNewEmail, ct);
            if (existingUser is not null)
                return new FailedRequestEmailChange(EmailChangeError.EMAIL_ALREADY_USED);

            var existingPendingCount = await db.PendingEmailChanges
                .CountAsync(p => p.UserId == UserId && p.ExpiresAt > DateTimeOffset.UtcNow, ct);
            if (existingPendingCount >= 3)
                return new FailedRequestEmailChange(EmailChangeError.RATE_LIMITED);

            var code = OtpSecurity.GenerateNumericCode(6);
            var salt = OtpSecurity.GenerateSalt(16);
            var hash = OtpSecurity.ComputeHmac(salt, code);

            var pending = new PendingEmailChangeEntity
            {
                Id = Guid.CreateVersion7(),
                UserId = UserId,
                NewEmail = newEmail,
                CodeHash = Convert.ToBase64String(hash),
                CodeSalt = Convert.ToBase64String(salt),
                ExpiresAt = DateTimeOffset.UtcNow.Add(VerificationCodeTtl),
                AttemptsLeft = MaxVerificationAttempts,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await db.PendingEmailChanges.AddAsync(pending, ct);
            await db.SaveChangesAsync(ct);

            var emailGrain = GrainFactory.GetGrain<IEmailManager>(Guid.NewGuid());
            await emailGrain.SendOtpCodeAsync(newEmail, code, VerificationCodeTtl);

            return new SuccessRequestEmailChange();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to request email change for user {UserId}", UserId);
            return new FailedRequestEmailChange(EmailChangeError.INTERNAL_ERROR);
        }
    }

    public async Task<IConfirmEmailChangeResult> ConfirmEmailChangeAsync(string verificationCode, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var pending = await db.PendingEmailChanges
                .Where(p => p.UserId == UserId && p.ExpiresAt > DateTimeOffset.UtcNow && p.AttemptsLeft > 0)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (pending is null)
                return new FailedConfirmEmailChange(EmailChangeError.VERIFICATION_CODE_EXPIRED);

            var salt = Convert.FromBase64String(pending.CodeSalt);
            var expectedHash = Convert.FromBase64String(pending.CodeHash);
            var actualHash = OtpSecurity.ComputeHmac(salt, verificationCode);

            if (!OtpSecurity.ConstantTimeEquals(actualHash, expectedHash))
            {
                pending.AttemptsLeft--;
                pending.UpdatedAt = DateTimeOffset.UtcNow;

                if (pending.AttemptsLeft <= 0)
                    db.PendingEmailChanges.Remove(pending);

                await db.SaveChangesAsync(ct);
                return new FailedConfirmEmailChange(EmailChangeError.INVALID_VERIFICATION_CODE);
            }

            var normalizedNewEmail = pending.NewEmail.ToLowerInvariant();
            var existingUser = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedNewEmail, ct);
            if (existingUser is not null)
            {
                db.PendingEmailChanges.Remove(pending);
                await db.SaveChangesAsync(ct);
                return new FailedConfirmEmailChange(EmailChangeError.EMAIL_ALREADY_USED);
            }

            var user = await db.Users.FirstAsync(u => u.Id == UserId, ct);
            user.Email = pending.NewEmail;

            db.PendingEmailChanges.Remove(pending);
            await db.SaveChangesAsync(ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            return new SuccessConfirmEmailChange();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to confirm email change for user {UserId}", UserId);
            return new FailedConfirmEmailChange(EmailChangeError.INTERNAL_ERROR);
        }
    }

    public async Task<IRequestPhoneChangeResult> RequestPhoneChangeAsync(string newPhone, string password, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new FailedRequestPhoneChange(PhoneChangeError.INTERNAL_ERROR);

            if (!passwordHashingService.VerifyPassword(password, user))
                return new FailedRequestPhoneChange(PhoneChangeError.INVALID_PASSWORD);

            if (!IsValidPhoneNumber(newPhone))
                return new FailedRequestPhoneChange(PhoneChangeError.INVALID_PHONE);

            var normalizedPhone = NormalizePhoneNumber(newPhone);

            var existingUser = await db.Users.FirstOrDefaultAsync(u => u.PhoneNumber == normalizedPhone, ct);
            if (existingUser is not null)
                return new FailedRequestPhoneChange(PhoneChangeError.PHONE_ALREADY_USED);

            var existingPendingCount = await db.PendingPhoneChanges
                .CountAsync(p => p.UserId == UserId && p.ExpiresAt > DateTimeOffset.UtcNow, ct);
            if (existingPendingCount >= 3)
                return new FailedRequestPhoneChange(PhoneChangeError.RATE_LIMITED);

            // Send code via phone provider
            var userIp = this.GetUserIp() ?? "unknown";
            await phoneProvider.SendCode(normalizedPhone, userIp, "Argon", "1.0");

            var pending = new PendingPhoneChangeEntity
            {
                Id = Guid.CreateVersion7(),
                UserId = UserId,
                NewPhone = normalizedPhone,
                CodeHash = string.Empty, // Code is managed by phone provider
                CodeSalt = string.Empty,
                ExpiresAt = DateTimeOffset.UtcNow.Add(VerificationCodeTtl),
                AttemptsLeft = MaxVerificationAttempts,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await db.PendingPhoneChanges.AddAsync(pending, ct);
            await db.SaveChangesAsync(ct);

            return new SuccessRequestPhoneChange();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to request phone change for user {UserId}", UserId);
            return new FailedRequestPhoneChange(PhoneChangeError.INTERNAL_ERROR);
        }
    }

    public async Task<IConfirmPhoneChangeResult> ConfirmPhoneChangeAsync(string verificationCode, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var pending = await db.PendingPhoneChanges
                .Where(p => p.UserId == UserId && p.ExpiresAt > DateTimeOffset.UtcNow && p.AttemptsLeft > 0)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (pending is null)
                return new FailedConfirmPhoneChange(PhoneChangeError.VERIFICATION_CODE_EXPIRED);

            // Verify code via phone provider
            var result = await phoneProvider.VerifyCode(pending.NewPhone, pending.Id.ToString(), verificationCode);

            if (result.verifyResult != VerifyStatus.Verified)
            {
                pending.AttemptsLeft--;
                pending.UpdatedAt = DateTimeOffset.UtcNow;

                if (pending.AttemptsLeft <= 0 || result.verifyResult == VerifyStatus.TooManyAttempts)
                    db.PendingPhoneChanges.Remove(pending);

                await db.SaveChangesAsync(ct);
                return new FailedConfirmPhoneChange(PhoneChangeError.INVALID_VERIFICATION_CODE);
            }

            var existingUser = await db.Users.FirstOrDefaultAsync(u => u.PhoneNumber == pending.NewPhone, ct);
            if (existingUser is not null)
            {
                db.PendingPhoneChanges.Remove(pending);
                await db.SaveChangesAsync(ct);
                return new FailedConfirmPhoneChange(PhoneChangeError.PHONE_ALREADY_USED);
            }

            var user = await db.Users.FirstAsync(u => u.Id == UserId, ct);
            user.PhoneNumber = pending.NewPhone;

            db.PendingPhoneChanges.Remove(pending);
            await db.SaveChangesAsync(ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            return new SuccessConfirmPhoneChange();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to confirm phone change for user {UserId}", UserId);
            return new FailedConfirmPhoneChange(PhoneChangeError.INTERNAL_ERROR);
        }
    }

    public async Task<IRemovePhoneResult> RemovePhoneAsync(string password, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new FailedRemovePhone(PhoneChangeError.INTERNAL_ERROR);

            if (!passwordHashingService.VerifyPassword(password, user))
                return new FailedRemovePhone(PhoneChangeError.INVALID_PASSWORD);

            user.PhoneNumber = null;
            await db.SaveChangesAsync(ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            return new SuccessRemovePhone();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to remove phone for user {UserId}", UserId);
            return new FailedRemovePhone(PhoneChangeError.INTERNAL_ERROR);
        }
    }

    public async Task<IChangePasswordResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new FailedChangePassword(PasswordChangeError.INTERNAL_ERROR);

            if (!passwordHashingService.VerifyPassword(currentPassword, user))
                return new FailedChangePassword(PasswordChangeError.INVALID_CURRENT_PASSWORD);

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinPasswordLength)
                return new FailedChangePassword(PasswordChangeError.PASSWORD_TOO_SHORT);

            if (currentPassword == newPassword)
                return new FailedChangePassword(PasswordChangeError.PASSWORD_SAME_AS_CURRENT);

            user.PasswordDigest = passwordHashingService.HashPassword(newPassword);
            await db.SaveChangesAsync(ct);

            // Every refresh token issued before this moment is now dead.
            //
            // Changing a password is the one action that means "whoever else is holding my
            // credentials, stop": per-session tombstones cannot express it, because they only reach
            // sessions still visible to discovery and only tokens minted since the sid claim
            // existed. A floor reaches every token by date, including the caller's own — which is
            // correct here and is why this does not live in RevokeAllSessions.
            await cache.StringSetAsync(
                SessionRevocation.FloorKey(UserId),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                SessionRevocation.Window,
                ct);

            var emailGrain = GrainFactory.GetGrain<IEmailManager>(Guid.NewGuid());
            await emailGrain.SendNotificationResetPasswordAsync(user.Email);

            return new SuccessChangePassword();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to change password for user {UserId}", UserId);
            return new FailedChangePassword(PasswordChangeError.INTERNAL_ERROR);
        }
    }

    public async Task<IEnableOTPResult> EnableOTPAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new FailedEnableOTP(OTPError.INTERNAL_ERROR);

            if (!string.IsNullOrEmpty(user.TotpSecret))
                return new FailedEnableOTP(OTPError.ALREADY_ENABLED);

            // Generate secret and store in cache (not in DB yet)
            var secret = await totpKeyStore.CreatePendingSecret(UserId, ct);
            var base32Secret = Base32Encoding.ToString(secret);

            var issuer = "ArgonChat";
            var qrCodeUrl = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.Email)}?secret={base32Secret}&issuer={issuer}";

            return new SuccessEnableOTP(base32Secret, qrCodeUrl);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to enable OTP for user {UserId}", UserId);
            return new FailedEnableOTP(OTPError.INTERNAL_ERROR);
        }
    }

    public async Task<IVerifyOTPResult> VerifyAndEnableOTPAsync(string code, CancellationToken ct = default)
    {
        try
        {
            // Get pending secret from cache
            var secret = await totpKeyStore.GetPendingSecret(UserId, ct);
            if (secret is null)
                return new FailedVerifyOTP(OTPError.NOT_ENABLED);

            var totp = new Totp(secret);
            if (!totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay))
                return new FailedVerifyOTP(OTPError.INVALID_CODE);

            // Save secret to database only after successful verification
            await totpKeyStore.SaveSecret(UserId, secret, ct);
            
            // Remove pending secret from cache
            await totpKeyStore.DeletePendingSecret(UserId, ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            return new SuccessVerifyOTP();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to verify OTP for user {UserId}", UserId);
            return new FailedVerifyOTP(OTPError.INTERNAL_ERROR);
        }
    }

    public async Task<IDisableOTPResult> DisableOTPAsync(string code, CancellationToken ct = default)
    {
        try
        {
            var secret = await totpKeyStore.GetSecret(UserId, ct);
            if (secret is null)
                return new FailedDisableOTP(OTPError.NOT_ENABLED);

            var totp = new Totp(secret);
            if (!totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay))
                return new FailedDisableOTP(OTPError.INVALID_CODE);

            await totpKeyStore.DeleteSecret(UserId, ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            return new SuccessDisableOTP();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to disable OTP for user {UserId}", UserId);
            return new FailedDisableOTP(OTPError.INTERNAL_ERROR);
        }
    }

    public async Task<List<Passkey>> GetPasskeysAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var entities = await db.Passkeys
                .Where(p => p.UserId == UserId && p.IsCompleted && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct);

            return entities
                .Select(p => new Passkey(
                    p.Id, 
                    p.Name, 
                    p.CreatedAt.UtcDateTime, 
                    p.LastUsedAt?.UtcDateTime,
                    p.AaGuid,
                    p.AaGuid.HasValue ? AuthenticatorNames.Lookup(p.AaGuid.Value) : null))
                .ToList();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to get passkeys for user {UserId}", UserId);
            return [];
        }
    }

    public async Task<IBeginPasskeyResult> BeginAddPasskeyAsync(string name, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var existingCount = await db.Passkeys.CountAsync(p => p.UserId == UserId && !p.IsDeleted, ct);
            if (existingCount >= MaxPasskeys)
                return new FailedBeginPasskey(PasskeyError.LIMIT_REACHED);

            if (string.IsNullOrWhiteSpace(name))
                return new FailedBeginPasskey(PasskeyError.INVALID_CREDENTIAL);

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new FailedBeginPasskey(PasskeyError.INTERNAL_ERROR);

            // Get existing credential IDs to exclude (prevent re-registration)
            var existingCredentials = await db.Passkeys
                .Where(p => p.UserId == UserId && p.IsCompleted && !p.IsDeleted && p.CredentialId != null)
                .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId!))
                .ToListAsync(ct);

            // Generate Fido2 credential creation options
            var fido2User = new Fido2User
            {
                Id = UserId.ToByteArray(),
                Name = user.Username ?? user.Email,
                DisplayName = user.DisplayName ?? user.Username ?? user.Email
            };

            var options = fido2.RequestNewCredential(
                new RequestNewCredentialParams
                {
                    User = fido2User,
                    ExcludeCredentials = existingCredentials,
                    AuthenticatorSelection = new AuthenticatorSelection
                    {
                        UserVerification = UserVerificationRequirement.Preferred,
                        ResidentKey = ResidentKeyRequirement.Preferred
                    },
                    AttestationPreference = AttestationConveyancePreference.Direct
                });

            var optionsJson = options.ToJson();

            await pendingPasskeyStore.StoreRegistrationStateAsync(UserId, name, optionsJson, ct);

            return new SuccessBeginPasskey(optionsJson);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin add passkey for user {UserId}", UserId);
            return new FailedBeginPasskey(PasskeyError.INTERNAL_ERROR);
        }
    }

    public async Task<ICompletePasskeyResult> CompleteAddPasskeyAsync(string registrationResponse, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(registrationResponse))
                return new FailedCompletePasskey(PasskeyError.INVALID_CREDENTIAL);

            var attestationResponse = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(registrationResponse);
            if (attestationResponse is null)
                return new FailedCompletePasskey(PasskeyError.INVALID_CREDENTIAL);

            // Retrieve stored registration state from cache
            var registration = await pendingPasskeyStore.GetRegistrationStateAsync(UserId, ct);
            if (registration is null)
                return new FailedCompletePasskey(PasskeyError.CHALLENGE_EXPIRED);

            var options = CredentialCreateOptions.FromJson(registration.OptionsJson);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = attestationResponse,
                    OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = async (args, cancellationToken) =>
                    {
                        var exists = await db.Passkeys.AnyAsync(
                            p => p.CredentialId != null && p.CredentialId == args.CredentialId && !p.IsDeleted,
                            cancellationToken);
                        return !exists;
                    }
                }, ct);

            var passkey = new UserPasskeyEntity
            {
                Id = Guid.CreateVersion7(),
                UserId = UserId,
                Name = registration.Name,
                CredentialId = credential.Id,
                PublicKey = credential.PublicKey,
                SignCount = credential.SignCount,
                AaGuid = credential.AaGuid,
                IsCompleted = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await db.Passkeys.AddAsync(passkey, ct);
            await db.SaveChangesAsync(ct);

            await pendingPasskeyStore.DeleteRegistrationStateAsync(UserId, ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            var result = new Passkey(passkey.Id, passkey.Name, passkey.CreatedAt.UtcDateTime, passkey.LastUsedAt?.UtcDateTime,
                passkey.AaGuid, passkey.AaGuid.HasValue ? AuthenticatorNames.Lookup(passkey.AaGuid.Value) : null);
            return new SuccessCompletePasskey(result);
        }
        catch (Fido2VerificationException ex)
        {
            logger.LogWarning(ex, "Passkey registration verification failed for user {UserId}", UserId);
            return new FailedCompletePasskey(PasskeyError.VERIFICATION_FAILED);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to complete add passkey for user {UserId}", UserId);
            return new FailedCompletePasskey(PasskeyError.INTERNAL_ERROR);
        }
    }

    public async Task<IRemovePasskeyResult> RemovePasskeyAsync(Guid passkeyId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var passkey = await db.Passkeys.FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == UserId && !p.IsDeleted, ct);
            if (passkey is null)
                return new FailedRemovePasskey(PasskeyError.NOT_FOUND);

            passkey.IsDeleted = true;
            passkey.DeletedAt = DateTimeOffset.UtcNow;
            passkey.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            return new SuccessRemovePasskey();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to remove passkey for user {UserId}", UserId);
            return new FailedRemovePasskey(PasskeyError.INTERNAL_ERROR);
        }
    }

    public async Task<ISetAutoDeleteResult> SetAutoDeletePeriodAsync(int? months, CancellationToken ct = default)
    {
        try
        {
            // null is not allowed - auto-delete cannot be disabled
            if (!months.HasValue)
                return new FailedSetAutoDelete(AutoDeleteError.INVALID_PERIOD);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);

            // Premium users can set up to 72 months, regular users up to 36
            var maxMonths = user?.HasActiveUltima == true ? 72 : 36;

            if (months.Value < 1 || months.Value > maxMonths)
                return new FailedSetAutoDelete(AutoDeleteError.INVALID_PERIOD);

            var setting = await db.AutoDeleteSettings.FirstOrDefaultAsync(s => s.UserId == UserId, ct);

            if (setting is null)
            {
                setting = new UserAutoDeleteSettingEntity
                {
                    Id = Guid.CreateVersion7(),
                    UserId = UserId,
                    Months = months.Value,
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await db.AutoDeleteSettings.AddAsync(setting, ct);
            }
            else
            {
                setting.Months = months.Value;
                setting.Enabled = true;
                setting.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            return new SuccessSetAutoDelete();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to set auto-delete period for user {UserId}", UserId);
            return new FailedSetAutoDelete(AutoDeleteError.INTERNAL_ERROR);
        }
    }

    public async Task<AutoDeletePeriod> GetAutoDeletePeriodAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var setting = await db.AutoDeleteSettings.FirstOrDefaultAsync(s => s.UserId == UserId, ct);

            // Default to 12 months if not set
            return setting is null
                ? new AutoDeletePeriod(DefaultAutoDeleteMonths, true)
                : new AutoDeletePeriod(setting.Months, setting.Enabled);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to get auto-delete period for user {UserId}", UserId);
            return new AutoDeletePeriod(DefaultAutoDeleteMonths, true);
        }
    }

    public async Task<SecurityDetails> GetSecurityDetailsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new SecurityDetails(false, IonArray<Passkey>.Empty, null, null, new AutoDeletePeriod(DefaultAutoDeleteMonths, true));

            var otpEnabled = !string.IsNullOrEmpty(user.TotpSecret) || 
                             await totpKeyStore.GetSecret(UserId, ct) is not null;

            var passkeys = await GetPasskeysAsync(ct);

            var autoDeletePeriod = await GetAutoDeletePeriodAsync(ct);

            return new SecurityDetails(
                otpEnabled: otpEnabled,
                passkeys: new IonArray<Passkey>(passkeys),
                email: user.Email,
                phone: user.PhoneNumber,
                autoDeletePeriod: autoDeletePeriod);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to get security details for user {UserId}", UserId);
            return new SecurityDetails(false, IonArray<Passkey>.Empty, null, null, new AutoDeletePeriod(DefaultAutoDeleteMonths, true));
        }
    }

    public async Task<IBeginPasskeyValidateResult> BeginValidatePasskeyAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var passkeys = await db.Passkeys
                .Where(p => p.UserId == UserId && p.IsCompleted && !p.IsDeleted && p.CredentialId != null)
                .ToListAsync(ct);

            if (passkeys.Count == 0)
                return new FailedBeginValidatePasskey(PasskeyError.NOT_FOUND);

            var allowedCredentials = passkeys
                .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId!))
                .ToList();

            var options = fido2.GetAssertionOptions(
                new GetAssertionOptionsParams
                {
                    AllowedCredentials = allowedCredentials,
                    UserVerification = UserVerificationRequirement.Preferred
                });

            var optionsJson = options.ToJson();

            await pendingPasskeyStore.StoreValidationOptionsAsync(UserId, optionsJson, ct);

            return new SuccessBeginValidatePasskey(optionsJson);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin validate passkey for user {UserId}", UserId);
            return new FailedBeginValidatePasskey(PasskeyError.INTERNAL_ERROR);
        }
    }

    public async Task<ICompletePasskeyResult> CompleteValidatePasskeyAsync(string authenticationResponse, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(authenticationResponse))
                return new FailedCompletePasskey(PasskeyError.INVALID_CREDENTIAL);

            var assertionResponse = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(authenticationResponse);
            if (assertionResponse is null)
                return new FailedCompletePasskey(PasskeyError.INVALID_CREDENTIAL);

            var optionsJson = await pendingPasskeyStore.GetValidationOptionsAsync(UserId, ct);
            if (optionsJson is null)
                return new FailedCompletePasskey(PasskeyError.CHALLENGE_EXPIRED);

            var options = AssertionOptions.FromJson(optionsJson);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Find the passkey by credential ID
            var credentialIdBytes = Base64Url.DecodeFromChars(assertionResponse.Id);
            var passkey = await db.Passkeys.FirstOrDefaultAsync(
                p => p.CredentialId != null && p.CredentialId == credentialIdBytes 
                     && p.UserId == UserId && p.IsCompleted && !p.IsDeleted, ct);

            if (passkey is null || passkey.PublicKey is null)
                return new FailedCompletePasskey(PasskeyError.NOT_FOUND);

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
                                 && p.UserId == UserId && !p.IsDeleted,
                            cancellationToken);
                        return stored;
                    }
                }, ct);

            // Update sign count for clone detection
            passkey.SignCount = result.SignCount;
            passkey.LastUsedAt = DateTimeOffset.UtcNow;
            passkey.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            await pendingPasskeyStore.DeleteValidationOptionsAsync(UserId, ct);

            var passkeyResult = new Passkey(passkey.Id, passkey.Name, passkey.CreatedAt.UtcDateTime, passkey.LastUsedAt?.UtcDateTime,
                passkey.AaGuid, passkey.AaGuid.HasValue ? AuthenticatorNames.Lookup(passkey.AaGuid.Value) : null);
            return new SuccessCompletePasskey(passkeyResult);
        }
        catch (Fido2VerificationException ex)
        {
            logger.LogWarning(ex, "Passkey authentication verification failed for user {UserId}", UserId);
            return new FailedCompletePasskey(PasskeyError.VERIFICATION_FAILED);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to complete validate passkey for user {UserId}", UserId);
            return new FailedCompletePasskey(PasskeyError.INTERNAL_ERROR);
        }
    }

    public async Task<List<SessionInfo>> GetSessionsAsync(Guid currentSessionId, CancellationToken ct = default)
    {
        try
        {
            var sessions = await sessionDiscovery.GetUserSessionsAsync(UserId, ct);
            var result   = new List<SessionInfo>(sessions.Count);

            foreach (var session in sessions)
            {
                // A sid that will not parse is not a session this screen can offer to end — RevokeSession
                // takes a guid — so listing it would put a row on the screen whose button cannot work.
                if (!Guid.TryParse(session.SessionId, out var sessionId))
                    continue;

                result.Add(new SessionInfo(
                    sessionId,
                    session.ClientName ?? "",
                    session.ClientRegion ?? "",
                    session.LastSeenAt ?? DateTime.UtcNow,
                    sessionId == currentSessionId));
            }

            // Current first, then most recently seen: the row the user is least likely to want is the
            // one they are least likely to hit by accident, and the rest sort into recognisability
            // order — a session from ten minutes ago is far easier to place than one from Tuesday.
            return result
               .OrderByDescending(x => x.isCurrent)
               .ThenByDescending(x => x.lastSeenAt)
               .ToList();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to list sessions for user {UserId}", UserId);
            return [];
        }
    }

    public async Task<IRevokeSessionResult> RevokeSessionAsync(Guid sessionId, Guid currentSessionId, CancellationToken ct = default)
    {
        if (sessionId == currentSessionId)
            return new FailedRevokeSession(SessionError.CANNOT_REVOKE_CURRENT);

        try
        {
            var sessions = await sessionDiscovery.GetUserSessionsAsync(UserId, ct);

            // Scoped to this user's own live sessions, so a guessed or copied sid from another account
            // reads as NOT_FOUND rather than becoming a way to sign strangers out.
            if (!sessions.Any(x => Guid.TryParse(x.SessionId, out var id) && id == sessionId))
                return new FailedRevokeSession(SessionError.NOT_FOUND);

            await EndSessionAsync(sessionId, ct);
            await NotifySecurityDetailsChangedAsync(ct);

            return new SuccessRevokeSession();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to revoke session {SessionId} for user {UserId}", sessionId, UserId);
            return new FailedRevokeSession(SessionError.INTERNAL_ERROR);
        }
    }

    public async Task<IRevokeSessionResult> RevokeAllSessionsAsync(Guid currentSessionId, CancellationToken ct = default)
    {
        try
        {
            var sessions = await sessionDiscovery.GetUserSessionsAsync(UserId, ct);
            var revoked  = 0;

            foreach (var session in sessions)
            {
                if (!Guid.TryParse(session.SessionId, out var sessionId) || sessionId == currentSessionId)
                    continue;

                await EndSessionAsync(sessionId, ct);
                revoked++;
            }

            // No revocation floor here, on purpose. A floor is a timestamp and cannot make an
            // exception for the caller's own refresh token, so writing one would sign them out of
            // the screen they are standing on — the exact thing the spare below exists to avoid.
            // The floor belongs to ChangePassword, where being signed out yourself is the point.
            //
            // The cost is that this reaches only sessions discovery can still see, and only refresh
            // tokens minted since the sid claim existed.
            //
            // Deliberately spares the caller. The button that reaches here sits on the devices screen
            // next to the phone's own row, and a user auditing their sessions is trying to remove the
            // ones they do not recognise — signing themselves out as a side effect would cost them the
            // screen they are working on. Signing out this device is what the sign-out button is for.
            logger.LogInformation("Revoked {Count} session(s) for user {UserId}", revoked, UserId);

            if (revoked > 0)
                await NotifySecurityDetailsChangedAsync(ct);

            return new SuccessRevokeSession();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to revoke all sessions for user {UserId}", UserId);
            return new FailedRevokeSession(SessionError.INTERNAL_ERROR);
        }
    }

    /// <summary>
    /// Ends one session three times over, because none of the three is sufficient alone.
    /// </summary>
    /// <remarks>
    /// The tombstone is what actually shuts the credentials out (see <see cref="SessionRevocation"/>);
    /// <c>GoOfflineAsync</c> is what makes it immediate, since a connected client would otherwise keep
    /// receiving events off a transport that was authenticated before the tombstone existed; and
    /// removing the presence key is what stops the row reappearing on the screen a moment later.
    /// </remarks>
    private async Task EndSessionAsync(Guid sessionId, CancellationToken ct)
    {
        // One set per user, not one key per revoked session: a key per session would be retained for
        // the refresh token's whole lifetime and never reused, so the store would grow by one entry
        // for every device anyone has ever signed out and drop none of them.
        var revokedKey = SessionRevocation.RevokedKey(UserId);

        await cache.SetAddAsync(revokedKey, sessionId.ToString(), ct);
        await cache.KeyExpireAsync(revokedKey, SessionRevocation.Window, ct);

        try
        {
            await GrainFactory.GetGrain<IUserSessionGrain>($"{UserId}:{sessionId}").GoOfflineAsync();
        }
        catch (Exception e)
        {
            // A session whose grain cannot be reached is still revoked — the tombstone is already
            // written, and presence will lapse on its own TTL within two minutes.
            logger.LogWarning(e, "Could not take session {SessionId} offline for user {UserId}", sessionId, UserId);
        }

        await presence.RemoveSessionAsync(UserId, sessionId.ToString(), ct);
        await presence.RemoveSessionStatusAsync(UserId, sessionId.ToString(), ct);
    }

    private async Task NotifySecurityDetailsChangedAsync(CancellationToken ct = default)
    {
        try
        {
            var details = await GetSecurityDetailsAsync(ct);
            var sessions = await sessionDiscovery.GetUserSessionsAsync(UserId, ct);

            if (sessions.Count == 0) return;

            await notifier.NotifySessionsAsync(sessions, new UserSecurityDetailsUpdated(UserId, details), ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to notify security details changed for user {UserId}", UserId);
        }
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return false;

        var digits = phone.Count(char.IsDigit);
        return digits is >= 7 and <= 15;
    }

    private static string NormalizePhoneNumber(string phone)
        => new(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
}
