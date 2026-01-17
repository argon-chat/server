namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Features.CoreLogic.Passkeys;
using Features.Integrations.Phones;
using Api.Features.CoreLogic.Otp;
using ion.runtime;
using Orleans.Concurrency;
using OtpNet;
using Services;

[StatelessWorker]
public class SecurityGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPasswordHashingService passwordHashingService,
    ITotpKeyStore totpKeyStore,
    IPendingPasskeyStore pendingPasskeyStore,
    IPhoneProvider phoneProvider,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
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
                    p.LastUsedAt?.UtcDateTime))
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
                return new FailedBeginPasskey(PasskeyError.INVALID_PUBLIC_KEY);

            var (passkeyId, challenge) = await pendingPasskeyStore.CreatePendingAsync(UserId, name, ct);
            return new SuccessBeginPasskey(passkeyId, challenge);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin add passkey for user {UserId}", UserId);
            return new FailedBeginPasskey(PasskeyError.INTERNAL_ERROR);
        }
    }

    public async Task<ICompletePasskeyResult> CompleteAddPasskeyAsync(Guid passkeyId, string publicKey, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKey))
                return new FailedCompletePasskey(PasskeyError.INVALID_PUBLIC_KEY);

            // Retrieve pending passkey from cache
            var pending = await pendingPasskeyStore.GetPendingAsync(UserId, passkeyId, ct);
            if (pending is null)
                return new FailedCompletePasskey(PasskeyError.NOT_FOUND);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Check if passkey already exists in DB (shouldn't happen if flow is correct)
            var existingPasskey = await db.Passkeys.FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == UserId, ct);
            if (existingPasskey is not null)
            {
                await pendingPasskeyStore.DeletePendingAsync(UserId, passkeyId, ct);
                return new FailedCompletePasskey(PasskeyError.INTERNAL_ERROR);
            }

            // Create new passkey in database
            var passkey = new UserPasskeyEntity
            {
                Id = passkeyId,
                UserId = UserId,
                Name = pending.Name,
                PublicKey = publicKey,
                Challenge = null,
                IsCompleted = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await db.Passkeys.AddAsync(passkey, ct);
            await db.SaveChangesAsync(ct);

            // Clean up cache
            await pendingPasskeyStore.DeletePendingAsync(UserId, passkeyId, ct);

            _ = NotifySecurityDetailsChangedAsync(ct);

            var result = new Passkey(passkey.Id, passkey.Name, passkey.CreatedAt.UtcDateTime, passkey.LastUsedAt?.UtcDateTime);
            return new SuccessCompletePasskey(result);
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

            if (months.Value < 1 || months.Value > 36)
                return new FailedSetAutoDelete(AutoDeleteError.INVALID_PERIOD);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

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
                .Where(p => p.UserId == UserId && p.IsCompleted && !p.IsDeleted)
                .ToListAsync(ct);

            if (passkeys.Count == 0)
                return new FailedBeginValidatePasskey(PasskeyError.NOT_FOUND);

            var challenge = Convert.ToBase64String(OtpSecurity.GenerateSalt(32));

            var allowedCredentials = passkeys
                .Select(p => new PasskeyCredentialDescriptor(p.Id.ToString(), "public-key"))
                .ToList();

            return new SuccessBeginValidatePasskey(challenge, new IonArray<PasskeyCredentialDescriptor>(allowedCredentials));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin validate passkey for user {UserId}", UserId);
            return new FailedBeginValidatePasskey(PasskeyError.INTERNAL_ERROR);
        }
    }

    public async Task<ICompletePasskeyResult> CompleteValidatePasskeyAsync(string credentialId, string signature, string authenticatorData, string clientDataJSON, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(credentialId) || 
                string.IsNullOrWhiteSpace(signature) || 
                string.IsNullOrWhiteSpace(authenticatorData) || 
                string.IsNullOrWhiteSpace(clientDataJSON))
            {
                return new FailedCompletePasskey(PasskeyError.INVALID_PUBLIC_KEY);
            }

            if (!Guid.TryParse(credentialId, out var passkeyId))
                return new FailedCompletePasskey(PasskeyError.NOT_FOUND);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var passkey = await db.Passkeys.FirstOrDefaultAsync(
                p => p.Id == passkeyId && p.UserId == UserId && p.IsCompleted && !p.IsDeleted, ct);

            if (passkey is null)
                return new FailedCompletePasskey(PasskeyError.NOT_FOUND);

            passkey.LastUsedAt = DateTimeOffset.UtcNow;
            passkey.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);

            var result = new Passkey(passkey.Id, passkey.Name, passkey.CreatedAt.UtcDateTime, passkey.LastUsedAt?.UtcDateTime);
            return new SuccessCompletePasskey(result);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to complete validate passkey for user {UserId}", UserId);
            return new FailedCompletePasskey(PasskeyError.INTERNAL_ERROR);
        }
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
