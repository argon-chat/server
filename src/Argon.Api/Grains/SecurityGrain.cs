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
    ISessionRevocationBroadcaster revocations,
    SessionRegistryStore sessionRegistry,
    IFido2 fido2,
    IOptions<ClientAppsOptions> clientApps,
    ILogger<SecurityGrain> logger) : Grain, ISecurityGrain
{
    private const int MaxPasskeys = 10;
    private const int MaxPasskeyNameLength = 128;
    private const int MaxEmailLength = 254;
    private static readonly TimeSpan VerificationCodeTtl = TimeSpan.FromMinutes(15);
    private const int MaxVerificationAttempts = 5;
    private const int MinPasswordLength = 8;
    private const int DefaultAutoDeleteMonths = 12;

    private Guid UserId => this.GetPrimaryKey();

    // The address is only looked at once the flow is verified, so a session alone cannot probe which
    // addresses are taken.
    public async Task<IRequestEmailChangeResult> RequestEmailChangeAsync(Guid flowId, string newEmail, Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var verification = GrainFactory.GetGrain<IVerificationGrain>(UserId);

            if (!await verification.IsVerifiedAsync(flowId, SensitiveAction.CHANGE_EMAIL, sessionId, ct))
                return new FailedRequestEmailChange(EmailChangeError.VERIFICATION_REQUIRED);

            if (!IsValidEmail(newEmail))
                return new FailedRequestEmailChange(EmailChangeError.INVALID_EMAIL);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var normalizedNewEmail = newEmail.ToLowerInvariant();

            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedNewEmail, ct))
                return new FailedRequestEmailChange(EmailChangeError.EMAIL_ALREADY_USED);

            var sent = await verification.SendTargetCodeAsync(flowId, SensitiveAction.CHANGE_EMAIL, sessionId, newEmail, ct);

            return sent.Outcome switch
            {
                TargetCodeOutcome.Sent        => new SuccessRequestEmailChange(sent.ResendAt!.Value.UtcDateTime),
                TargetCodeOutcome.NotVerified => new FailedRequestEmailChange(EmailChangeError.VERIFICATION_REQUIRED),
                _                             => new FailedRequestEmailChange(EmailChangeError.RATE_LIMITED)
            };
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to request email change for user {UserId}", UserId);
            return new FailedRequestEmailChange(EmailChangeError.INTERNAL_ERROR);
        }
    }

    public async Task<IConfirmEmailChangeResult> ConfirmEmailChangeAsync(Guid flowId, string verificationCode, Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var verification = GrainFactory.GetGrain<IVerificationGrain>(UserId);
            var check        = await verification.CheckTargetCodeAsync(flowId, SensitiveAction.CHANGE_EMAIL, sessionId, verificationCode, ct);

            switch (check.Outcome)
            {
                case TargetCheckOutcome.NotVerified or TargetCheckOutcome.Exhausted:
                    return new FailedConfirmEmailChange(EmailChangeError.VERIFICATION_REQUIRED);
                case TargetCheckOutcome.NoCode:
                    return new FailedConfirmEmailChange(EmailChangeError.VERIFICATION_CODE_EXPIRED);
                case TargetCheckOutcome.Invalid:
                    return new FailedConfirmEmailChange(EmailChangeError.INVALID_VERIFICATION_CODE);
            }

            var newEmail           = check.Target!;
            var normalizedNewEmail = newEmail.ToLowerInvariant();

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Taken while the code was on its way. The flow stays verified, so another address can be tried.
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedNewEmail, ct))
                return new FailedConfirmEmailChange(EmailChangeError.EMAIL_ALREADY_USED);

            var user     = await db.Users.FirstAsync(u => u.Id == UserId, ct);
            var oldEmail = user.Email;

            user.Email = newEmail;
            await db.SaveChangesAsync(ct);

            await verification.CompleteAsync(flowId, ct);

            await GrainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
               .SendEmailChangedAsync(oldEmail, newEmail, DateTimeOffset.UtcNow);

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
                Id = ArgonId.New(),
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
            await SessionRevocation.RaiseFloorAsync(cache, UserId, ct);

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
                .AsNoTracking()
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

            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxPasskeyNameLength)
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
                Id = ArgonId.New(),
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

    /// <summary>
    /// Sets — or, with no period at all, switches off — the account's inactivity deletion.
    /// </summary>
    /// <remarks>
    /// <para><b><c>null</c> means off, and used to mean <c>INVALID_PERIOD</c>.</b> Four places in the
    /// product describe an off state: the Ion signature takes a nullable, <c>AutoDeletePeriod</c> carries an
    /// <c>enabled</c> flag beside the months, <c>UserAutoDeleteSettingEntity.Months</c> says in as many
    /// words that null means disabled, and the desktop client's privacy screen offers a "Disabled" item and
    /// sends exactly this. Only the write path disagreed, so the item was dead: it raised an error toast and
    /// snapped back. Defect CON-2, pinned by
    /// <c>AccountConsoleTests.SetAutoDeletePeriod_WithNoPeriod_TurnsAutoDeleteOff</c>.</para>
    ///
    /// <para><b>Granting the off switch is only half a change, and the dangerous half on its own.</b> The
    /// inactivity scan used to read the setting as
    /// <c>Where(s =&gt; s.UserId == u.Id &amp;&amp; s.Enabled).Select(s =&gt; s.Months)</c>, which answers
    /// the same absent value for "switched off" and "never chose", and then fell back to the twelve-month
    /// platform default — so an account that turned auto-delete off would have been proposed for deletion
    /// <em>sooner</em> than one that left it at thirty-six months. <c>AutoDeleteSchedulerGrain</c> reads
    /// <c>Enabled</c> alongside <c>Months</c> and skips a disabled row outright; the two land together and
    /// must stay together.</para>
    ///
    /// <para><c>Months</c> is cleared rather than kept, so the row says the same thing the entity's own
    /// comment says and <c>GetAutoDeletePeriodAsync</c> answers <c>(null, false)</c> — which is what the
    /// client renders as "Disabled". Turning it back on is a period like any other.</para>
    /// </remarks>
    public async Task<ISetAutoDeleteResult> SetAutoDeletePeriodAsync(int? months, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);

            // Premium users can set up to 72 months, regular users up to 36
            var maxMonths = user?.HasActiveUltima == true ? 72 : 36;

            if (months is { } chosen && (chosen < 1 || chosen > maxMonths))
                return new FailedSetAutoDelete(AutoDeleteError.INVALID_PERIOD);

            var enabled = months.HasValue;
            var setting = await db.AutoDeleteSettings.FirstOrDefaultAsync(s => s.UserId == UserId, ct);

            if (setting is null)
            {
                setting = new UserAutoDeleteSettingEntity
                {
                    Id = ArgonId.New(),
                    UserId = UserId,
                    Months = months,
                    Enabled = enabled,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await db.AutoDeleteSettings.AddAsync(setting, ct);
            }
            else
            {
                setting.Months = months;
                setting.Enabled = enabled;
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

    /// <summary>
    /// What the account chose, or the platform default it has never moved off.
    /// </summary>
    /// <remarks>
    /// Three answers rather than two, since <see cref="SetAutoDeletePeriodAsync"/> learned to switch the
    /// feature off: no row means the shipped twelve months, a row with <c>Enabled</c> means the period the
    /// account chose, and a row without it means <c>(null, false)</c> — off, which the clients render as
    /// "Disabled" and the inactivity scan reads as "never propose this account".
    /// </remarks>
    public async Task<AutoDeletePeriod> GetAutoDeletePeriodAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var setting = await db.AutoDeleteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == UserId, ct);

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

            var optionsJson = await PasskeyAssertion.BeginAsync(db, fido2, UserId, ct);
            if (optionsJson is null)
                return new FailedBeginValidatePasskey(PasskeyError.NOT_FOUND);

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

            var optionsJson = await pendingPasskeyStore.GetValidationOptionsAsync(UserId, ct);
            if (optionsJson is null)
                return new FailedCompletePasskey(PasskeyError.CHALLENGE_EXPIRED);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var passkey = await PasskeyAssertion.CompleteAsync(db, fido2, UserId, optionsJson, authenticationResponse, ct);
            if (passkey is null)
                return new FailedCompletePasskey(PasskeyError.NOT_FOUND);

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

    public Task<List<SessionInfo>> GetSessionsAsync(Guid currentSessionId, CancellationToken ct = default)
        => ListSessionsAsync(currentSessionId, null, ct);

    public Task<IRevokeSessionResult> RevokeSessionAsync(Guid sessionId, Guid currentSessionId, CancellationToken ct = default)
        => RevokeOneSessionAsync(sessionId, currentSessionId, null, ct);

    public Task<IRevokeSessionResult> RevokeAllSessionsAsync(Guid currentSessionId, CancellationToken ct = default)
        => RevokeOtherSessionsAsync(currentSessionId, null, ct);

    // Registry rows first — every signed-in device, connected or not — then any presence session the
    // registry does not cover (a bot, a token from before the registry existed), which is live by
    // definition. sessionId stays the presence sid where there is one, so a row can be revoked by
    // either of its ids.
    public async Task<List<SessionInfo>> ListSessionsAsync(Guid currentSessionId, Guid? currentCredentialSessionId, CancellationToken ct = default)
    {
        try
        {
            var (rows, live) = await LoadDevicesAsync(ct);
            var caller       = Caller.Of(rows, currentSessionId, currentCredentialSessionId);
            var now          = DateTime.UtcNow;
            var result       = new List<SessionInfo>(rows.Count + live.Count);
            var covered      = new HashSet<Guid>();

            foreach (var row in OnePerDevice(rows, caller))
            {
                UserSessionDescriptor? session = null;

                var online = row.PresenceSessionId is { } presence && live.TryGetValue(presence, out session);

                if (online)
                    covered.Add(row.PresenceSessionId!.Value);

                var lastSeen = online ? session!.LastSeenAt ?? now : row.LastSeenAt.UtcDateTime;

                result.Add(new SessionInfo(
                    row.PresenceSessionId ?? row.CredentialSessionId,
                    row.ClientName,
                    row.Region,
                    lastSeen,
                    caller.Is(row),
                    row.AppId,
                    !string.IsNullOrEmpty(row.AppName) ? row.AppName : clientApps.Value.Find(row.AppId)?.Name ?? "",
                    row.AppVersion,
                    row.Platform,
                    row.OsName,
                    row.DeviceName,
                    row.Ip,
                    row.City,
                    row.CreatedAt.UtcDateTime,
                    online));
            }

            foreach (var (sid, session) in live)
            {
                if (covered.Contains(sid))
                    continue;

                var lastSeen = session.LastSeenAt ?? now;

                result.Add(new SessionInfo(
                    sid,
                    session.ClientName ?? "",
                    session.ClientRegion ?? "",
                    lastSeen,
                    sid == currentSessionId,
                    session.AppId ?? "",
                    !string.IsNullOrEmpty(session.AppName) ? session.AppName : clientApps.Value.Find(session.AppId)?.Name ?? "",
                    session.AppVersion ?? "",
                    session.Platform,
                    session.OsName ?? "",
                    session.DeviceName ?? "",
                    session.Ip ?? "",
                    session.City ?? "",
                    session.StartedAt ?? lastSeen,
                    true));
            }

            return result
               .OrderByDescending(x => x.isCurrent)
               .ThenByDescending(x => x.online)
               .ThenByDescending(x => x.lastSeenAt)
               .ToList();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to list sessions for user {UserId}", UserId);
            return [];
        }
    }

    public async Task<IRevokeSessionResult> RevokeOneSessionAsync(Guid sessionId, Guid currentSessionId, Guid? currentCredentialSessionId, CancellationToken ct = default)
    {
        if (sessionId == currentSessionId || sessionId == currentCredentialSessionId)
            return new FailedRevokeSession(SessionError.CANNOT_REVOKE_CURRENT);

        try
        {
            var (rows, live) = await LoadDevicesAsync(ct);
            var caller       = Caller.Of(rows, currentSessionId, currentCredentialSessionId);

            var targets = rows
               .Where(r => r.CredentialSessionId == sessionId || r.PresenceSessionId == sessionId)
               .ToList();

            if (targets.Any(r => caller.Is(r) || r.PresenceSessionId == currentSessionId))
                return new FailedRevokeSession(SessionError.CANNOT_REVOKE_CURRENT);

            var legacy = live.ContainsKey(sessionId) && targets.All(r => r.PresenceSessionId != sessionId);

            if (targets.Count == 0 && !legacy)
                return new FailedRevokeSession(SessionError.NOT_FOUND);

            var ended = true;

            foreach (var target in targets)
                ended &= await EndDeviceAsync(target, ct);

            if (legacy)
                ended &= await EndSessionAsync(sessionId, ct);

            if (!ended)
                return new FailedRevokeSession(SessionError.INTERNAL_ERROR);

            await NotifySecurityDetailsChangedAsync(ct);

            return new SuccessRevokeSession();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to revoke session {SessionId} for user {UserId}", sessionId, UserId);
            return new FailedRevokeSession(SessionError.INTERNAL_ERROR);
        }
    }

    // Ends every device except the caller's. Every one is attempted whatever the ones before it did,
    // and the result is binary because the contract has no shape for "n of m": a partial outcome is
    // INTERNAL_ERROR, so the user knows pressing the button again is worth doing. No revocation
    // floor: a floor cannot spare the caller's own token.
    public async Task<IRevokeSessionResult> RevokeOtherSessionsAsync(Guid currentSessionId, Guid? currentCredentialSessionId, CancellationToken ct = default)
    {
        try
        {
            var (rows, live) = await LoadDevicesAsync(ct);
            var caller       = Caller.Of(rows, currentSessionId, currentCredentialSessionId);
            var revoked      = 0;
            var failed       = 0;
            var covered      = new HashSet<Guid>();

            foreach (var row in rows)
            {
                if (row.PresenceSessionId is { } presence)
                    covered.Add(presence);

                // Rows sharing the caller's presence sid are the caller's own launch (an earlier
                // sign-in of it); ending their presence sid would end the caller.
                if (caller.Is(row) || row.PresenceSessionId == currentSessionId)
                    continue;

                try
                {
                    if (await EndDeviceAsync(row, ct))
                        revoked++;
                    else
                        failed++;
                }
                catch (Exception e)
                {
                    failed++;
                    logger.LogError(e, "Failed to revoke credential {CredentialSessionId} for user {UserId} during sign-out-everywhere",
                        row.CredentialSessionId, UserId);
                }
            }

            foreach (var sessionId in live.Keys)
            {
                if (sessionId == currentSessionId || covered.Contains(sessionId))
                    continue;

                try
                {
                    if (await EndSessionAsync(sessionId, ct))
                        revoked++;
                    else
                        failed++;
                }
                catch (Exception e)
                {
                    failed++;
                    logger.LogError(e, "Failed to revoke session {SessionId} for user {UserId} during sign-out-everywhere",
                        sessionId, UserId);
                }
            }

            logger.LogInformation("Revoked {Count} session(s) for user {UserId}", revoked, UserId);

            if (revoked > 0)
                await NotifySecurityDetailsChangedAsync(ct);

            if (failed == 0)
                return new SuccessRevokeSession();

            logger.LogError(
                "Signed {Revoked} session(s) out for user {UserId} but {Failed} could not be tombstoned and are still signed in",
                revoked, UserId, failed);

            return new FailedRevokeSession(SessionError.INTERNAL_ERROR);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to revoke all sessions for user {UserId}", UserId);
            return new FailedRevokeSession(SessionError.INTERNAL_ERROR);
        }
    }

    // Presence is read first and unguarded: an unreadable index has to fail the whole answer, not
    // just the online marks, or a revoke would report NOT_FOUND for a device that is signed in.
    private async Task<(IReadOnlyList<SessionRegistryRecord> Rows, Dictionary<Guid, UserSessionDescriptor> Live)> LoadDevicesAsync(CancellationToken ct)
    {
        var live = new Dictionary<Guid, UserSessionDescriptor>();

        foreach (var session in await sessionDiscovery.GetUserSessionsAsync(UserId, ct))
        {
            if (Guid.TryParse(session.SessionId, out var sid))
                live[sid] = session;
        }

        return (await sessionRegistry.ListAsync(UserId, ct), live);
    }

    // The caller's own row is the one carrying the token's sid. A token from before the registry, or
    // one whose row has been superseded, has none, and then the presence sid names it.
    private readonly record struct Caller(Guid PresenceSessionId, Guid? CredentialSessionId)
    {
        public static Caller Of(IReadOnlyList<SessionRegistryRecord> rows, Guid presenceSessionId, Guid? credentialSessionId)
            => new(presenceSessionId, rows.Any(r => r.CredentialSessionId == credentialSessionId) ? credentialSessionId : null);

        public bool Is(SessionRegistryRecord row)
            => CredentialSessionId is { } credential
                ? row.CredentialSessionId == credential
                : row.PresenceSessionId == PresenceSessionId;
    }

    // Two rows on one presence sid are one launch that signed in twice; the screen shows the newer,
    // or the caller's own if it is one of them. Revocation still reaches both through the sid.
    private static IEnumerable<SessionRegistryRecord> OnePerDevice(IReadOnlyList<SessionRegistryRecord> rows, Caller caller)
    {
        var byPresence = new Dictionary<Guid, SessionRegistryRecord>();

        foreach (var row in rows)
        {
            if (row.PresenceSessionId is not { } presence)
            {
                yield return row;
                continue;
            }

            if (!byPresence.TryGetValue(presence, out var kept)
                || caller.Is(row)
                || (!caller.Is(kept) && row.CreatedAt > kept.CreatedAt))
                byPresence[presence] = row;
        }

        foreach (var row in byPresence.Values)
            yield return row;
    }

    // Ends a registry row: the credential's tombstone first, because that is what outlives everything
    // else; then its presence sid the usual way, which takes the connection down; then the row.
    private async Task<bool> EndDeviceAsync(SessionRegistryRecord row, CancellationToken ct)
    {
        var revokedKey = SessionRevocation.RevokedKey(UserId);

        try
        {
            await cache.SetAddAsync(revokedKey, row.CredentialSessionId.ToString(), ct);
            await cache.UpdateStringExpirationAsync(revokedKey, SessionRevocation.Window, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not tombstone credential {CredentialSessionId} for user {UserId}", row.CredentialSessionId, UserId);
            return false;
        }

        var ended = true;

        if (row.PresenceSessionId is { } presence && presence != Guid.AllBitsSet)
            ended = await EndSessionAsync(presence, ct);

        try
        {
            await revocations.PublishAsync(UserId, row.PresenceSessionId, [row.CredentialSessionId], ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not broadcast the revocation of credential {CredentialSessionId} for user {UserId}",
                row.CredentialSessionId, UserId);
        }

        try
        {
            await sessionRegistry.RemoveAsync(UserId, [row.CredentialSessionId], ct);
        }
        catch (Exception e)
        {
            // The tombstone already keeps it off the list; the row goes with the next sweep.
            logger.LogWarning(e, "Could not remove the registry row of credential {CredentialSessionId} for user {UserId}",
                row.CredentialSessionId, UserId);
        }

        return ended;
    }

    /// <summary>
    /// Ends one session three times over, because none of the three is sufficient alone.
    /// </summary>
    /// <remarks>
    /// <para>The tombstone is what actually shuts the credentials out (see <see cref="SessionRevocation"/>);
    /// <c>GoOfflineAsync</c> is what makes it immediate, since a connected client would otherwise keep
    /// receiving events off a transport that was authenticated before the tombstone existed; and
    /// removing the presence key is what stops the row reappearing on the screen a moment later.</para>
    ///
    /// <para>The tombstone is written under <em>both</em> of the session's ids, because the screen and
    /// the refresh path do not mean the same thing by "session id" — see
    /// <see cref="SessionRevocation.CredentialsKey"/> for why they cannot, and
    /// <c>PresenceRevocationTests.Revoking_a_device_stops_its_refresh_token_from_minting</c> for what
    /// it cost while only one of them was written (S7).</para>
    ///
    /// <para><b>Every step is guarded and the method never throws</b>, so one failing device cannot
    /// spare the ones after it in <see cref="RevokeAllSessionsAsync"/>. What it answers instead is
    /// whether the <em>tombstone</em> was written, because that is the step the other three cannot
    /// stand in for: presence lapses on a two-minute TTL and a grain call can be retried, but a
    /// credential with no tombstone keeps minting for ten years. A caller that reports success on a
    /// false here is telling the user a device was signed out when it was not.</para>
    /// </remarks>
    /// <returns>Whether the revocation tombstone is committed.</returns>
    private async Task<bool> EndSessionAsync(Guid sessionId, CancellationToken ct)
    {
        // Guid.AllBitsSet is not a device. It is what a Development host hands a caller that presents
        // no session id of its own (HttpContextExtensions.GetSessionId), so tombstoning it would not
        // end one session — it would end every session of this account that has ever arrived without
        // one, and every future one too, since the tombstone is kept for the refresh token's ten-year
        // lifetime and nothing removes it. A local sign-out is not a reason to lock an account out
        // permanently, so this refuses rather than writes, and says so: the answer is false, which
        // the callers report as a sign-out that did not happen. (S22 — the placeholder is now the last
        // resort in GetSessionId, so a client that sends Sec-Ref never reaches this at all.)
        if (sessionId == Guid.AllBitsSet)
        {
            logger.LogWarning(
                "Refused to tombstone the development placeholder session id for user {UserId}: it names no device",
                UserId);

            return false;
        }

        // One set per user, not one key per revoked session: a key per session would be retained for
        // the refresh token's whole lifetime and never reused, so the store would grow by one entry
        // for every device anyone has ever signed out and drop none of them.
        var revokedKey = SessionRevocation.RevokedKey(UserId);
        var tombstoned = false;

        try
        {
            await cache.SetAddAsync(revokedKey, sessionId.ToString(), ct);
            tombstoned = true;
        }
        catch (Exception e)
        {
            // Reported rather than thrown, and the rest of the sign-out still runs: taking this
            // device's presence down is worth doing even when the credential could not be shut out,
            // and the answer above is what stops the caller calling that a success.
            logger.LogError(e, "Could not tombstone session {SessionId} for user {UserId}", sessionId, UserId);
        }

        // The credential this device is holding, if the sign-in or a refresh recorded it. Guarded on
        // its own: this is the one step of the four that is genuinely per-session, so a mapping that
        // cannot be read must not take the rest of the sign-out — or the sessions after it in
        // RevokeAllSessions — down with it.
        var credentialSessionIds = new List<Guid>();

        try
        {
            // Not counted towards `tombstoned`: the answer is about the row the user pressed the
            // button on, and a credential shut out while its presence sid was not is a device the
            // hub and the interceptor would still admit.
            foreach (var credentialSessionId in await SessionRevocation.CredentialSessionsAsync(cache, UserId, sessionId, ct))
            {
                await cache.SetAddAsync(revokedKey, credentialSessionId, ct);

                if (Guid.TryParse(credentialSessionId, out var parsed))
                    credentialSessionIds.Add(parsed);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the credential sessions of {SessionId} for user {UserId}", sessionId, UserId);
        }

        // The tombstone is committed, so the sockets this session is holding can go. They are not
        // ours to close — a hub connection can only be aborted on the node holding it, and this grain
        // runs on a silo that holds none — so the ids go out on the bus and every node that maps the
        // hub closes what it has. See AppHub's remarks for why this exists at all: without it a
        // signed-out device that simply stops talking keeps receiving every broadcast until it says
        // something, which for a client that has been deliberately silenced is never.
        //
        // Deliberately never throws, and deliberately not counted towards the answer: the tombstone
        // is the truth and this is the courtesy. RevokeAllSessions comes through here once per
        // session, so it needs nothing of its own.
        await revocations.PublishAsync(UserId, sessionId, credentialSessionIds, ct);

        try
        {
            // EXPIRE, not GETEX. IArgonCacheDatabase.KeyExpireAsync is StringGetSetExpiry underneath,
            // so asking it to put a TTL on the set above answers WRONGTYPE and throws — which is how
            // "sign this device out" came to fail with INTERNAL_ERROR in the only case that matters
            // (S5, pinned by PresenceRevocationTests.Signing_another_device_out_succeeds).
            // UpdateStringExpirationAsync is the type-agnostic one despite its name.
            await cache.UpdateStringExpirationAsync(revokedKey, SessionRevocation.Window, ct);
        }
        catch (Exception e)
        {
            // Retention, not revocation: the tombstone is already committed above, and a key that
            // outlives its window is a bookkeeping problem. Letting it throw is what turned one
            // mistyped call into a sign-out that reported failure and left the device online — no step
            // that only tidies up may stand between the tombstone and the three below it. (The
            // single-instance cache does not implement this at all, which is the same failure by a
            // different route.)
            logger.LogWarning(e, "Could not set the retention on the revocation tombstone for user {UserId}", UserId);
        }

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

        try
        {
            await presence.RemoveSessionAsync(UserId, sessionId.ToString(), ct);
            await presence.RemoveSessionStatusAsync(UserId, sessionId.ToString(), ct);
        }
        catch (Exception e)
        {
            // The last of the four, and the least load-bearing: both keys carry a two-minute TTL and
            // nothing refreshes them for a session that is now tombstoned, so the row leaves the
            // screen on its own. Letting it throw was what took the sessions after this one in
            // RevokeAllSessions down with it (S8).
            logger.LogWarning(e, "Could not clear the presence of session {SessionId} for user {UserId}", sessionId, UserId);
        }

        return tombstoned;
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
        // Longer than RFC 5321 allows, and than Users.NormalizedEmail (varchar(255)) can hold.
        if (string.IsNullOrWhiteSpace(email) || email.Length > MaxEmailLength)
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
