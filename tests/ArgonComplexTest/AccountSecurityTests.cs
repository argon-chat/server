namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Auth;
using Argon.Features.Jwt;
using Argon.Features.Logic;
using Argon.Grains.Interfaces;
using Argon.Services;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The refusals and failure modes of the security screen, and the sign-in flows behind the identity
/// grain, that <c>SecurityTests</c> and <c>SessionTests</c> leave to the happy path.
/// </summary>
/// <remarks>
/// <para>The session tests here put presence entries in place through <see cref="IUserPresenceService"/>
/// and call <see cref="ISecurityGrain"/> with an explicit current-session id. A real connection can
/// only ever produce a well-formed sid for itself, and the cases worth covering are exactly the
/// others: a sid that will not parse, the development placeholder, a store that answers with an
/// error. Where a store failure is needed it is a real one — a key of the wrong type, which Redis
/// refuses with <c>WRONGTYPE</c> — and the key is removed again before the test ends.</para>
/// </remarks>
[TestFixture]
public class AccountSecurityTests : TestBase
{
    private ISecurityGrain Security(Guid userId) => GetGrainFactory().GetGrain<ISecurityGrain>(userId);

    private IArgonCacheDatabase Cache => FactoryAsp.Services.GetRequiredService<IArgonCacheDatabase>();

    private IUserPresenceService Presence => FactoryAsp.Services.GetRequiredService<IUserPresenceService>();

    private Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct);

    private static string NewEmail() => $"chg_{Guid.NewGuid():N}@test.local";

    private static string NewPhone() => $"+7{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}";

    // ── email change ────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task An_email_change_is_refused_for_something_that_is_not_an_address_or_is_taken(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var other   = await CreateSessionAsync(ct);
        var pass    = account.Credentials.password;

        var notAnAddress = await account.Security.RequestEmailChange("not-an-address", pass, ct);
        var blank        = await account.Security.RequestEmailChange("   ", pass, ct);
        var displayForm  = await account.Security.RequestEmailChange($"Someone <{NewEmail()}>", pass, ct);
        var taken        = await account.Security.RequestEmailChange(other.Credentials.email.ToUpperInvariant(), pass, ct);
        var tooLong      = await account.Security.RequestEmailChange(
            $"{new string('a', 64)}@{string.Join('.', Enumerable.Repeat(new string('b', 60), 4))}.local", pass, ct);

        Assert.Multiple(() =>
        {
            Assert.That((notAnAddress as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.INVALID_EMAIL));
            Assert.That((blank as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.INVALID_EMAIL));
            Assert.That((displayForm as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.INVALID_EMAIL),
                "a display-name form was accepted as a bare address");
            Assert.That((taken as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.EMAIL_ALREADY_USED),
                "another account's address, in another case, was offered as free");
            Assert.That((tooLong as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.INVALID_EMAIL),
                "an address longer than the 254 characters an address may have was accepted and sent a code, "
              + "though Users.NormalizedEmail (varchar(255)) cannot hold it and the confirmation can only fail");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Three_pending_email_changes_are_the_most_an_account_may_hold(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);

        var results = new List<IRequestEmailChangeResult>();

        for (var i = 0; i < 4; i++)
            results.Add(await account.Security.RequestEmailChange(NewEmail(), account.Credentials.password, ct));

        Assert.Multiple(() =>
        {
            Assert.That(results.Take(3), Is.All.InstanceOf<SuccessRequestEmailChange>());
            Assert.That((results[3] as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.RATE_LIMITED));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Five_wrong_codes_spend_the_pending_email_change(CancellationToken ct = default)
    {
        var account  = await CreateSessionAsync(ct);
        var newEmail = NewEmail();

        var nothingPending = await account.Security.ConfirmEmailChange("123456", ct);

        await account.Security.RequestEmailChange(newEmail, account.Credentials.password, ct);
        var code  = await GetEmailCodeAsync(newEmail, ct: ct);
        var wrong = code == "000000" ? "111111" : "000000";

        var attempts = new List<IConfirmEmailChangeResult>();

        for (var i = 0; i < 5; i++)
            attempts.Add(await account.Security.ConfirmEmailChange(wrong, ct));

        var tooLate = await account.Security.ConfirmEmailChange(code!, ct);
        var details = await account.Security.GetSecurityDetails(ct);

        Assert.Multiple(() =>
        {
            Assert.That((nothingPending as FailedConfirmEmailChange)?.error, Is.EqualTo(EmailChangeError.VERIFICATION_CODE_EXPIRED));
            Assert.That(attempts.Select(a => (a as FailedConfirmEmailChange)?.error),
                Is.All.EqualTo(EmailChangeError.INVALID_VERIFICATION_CODE));
            Assert.That((tooLate as FailedConfirmEmailChange)?.error, Is.EqualTo(EmailChangeError.VERIFICATION_CODE_EXPIRED),
                "the right code still worked after five wrong guesses, so the code can be brute-forced");
            Assert.That(details.email, Is.EqualTo(account.Credentials.email));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_address_taken_while_the_change_was_pending_is_not_granted(CancellationToken ct = default)
    {
        var account  = await CreateSessionAsync(ct);
        var newEmail = NewEmail();

        await account.Security.RequestEmailChange(newEmail, account.Credentials.password, ct);
        var code = await GetEmailCodeAsync(newEmail, ct: ct);

        // Someone else signs up with the address before the code is typed in.
        var creds = GenerateCredentials();
        var registered = await GetIdentityService().Registration(new NewUserCredentialsInput(
            newEmail, creds.username, creds.password, creds.displayName, true, creds.birthDate, true, null, "1.0", "1.0"), ct);

        Assert.That(registered, Is.InstanceOf<SuccessRegistration>(), "the address was not free to register");

        var confirmed = await account.Security.ConfirmEmailChange(code!, ct);
        var retried   = await account.Security.ConfirmEmailChange(code!, ct);
        var details   = await account.Security.GetSecurityDetails(ct);

        Assert.Multiple(() =>
        {
            Assert.That((confirmed as FailedConfirmEmailChange)?.error, Is.EqualTo(EmailChangeError.EMAIL_ALREADY_USED));
            Assert.That((retried as FailedConfirmEmailChange)?.error, Is.EqualTo(EmailChangeError.VERIFICATION_CODE_EXPIRED),
                "a change that can no longer be granted was left pending");
            Assert.That(details.email, Is.EqualTo(account.Credentials.email));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_confirmed_email_change_is_the_address_the_account_signs_in_with(CancellationToken ct = default)
    {
        var account  = await CreateSessionAsync(ct);
        var newEmail = NewEmail();

        await account.Security.RequestEmailChange(newEmail, account.Credentials.password, ct);
        var confirmed = await account.Security.ConfirmEmailChange((await GetEmailCodeAsync(newEmail, ct: ct))!, ct);

        var details   = await account.Security.GetSecurityDetails(ct);
        var directory = GetGrainFactory().GetGrain<IIdentityDirectoryGrain>(Guid.Empty);
        var byNew     = await directory.GetUserIdByEmailAsync(newEmail, ct);
        var byOld     = await directory.GetUserIdByEmailAsync(account.Credentials.email, ct);
        var signIn    = await GetIdentityService().Authorize(
            new UserCredentialsInput(newEmail, null, null, account.Credentials.password, null, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(confirmed, Is.InstanceOf<SuccessConfirmEmailChange>());
            Assert.That(details.email, Is.EqualTo(newEmail));
            Assert.That(byNew, Is.EqualTo(account.UserId));
            Assert.That(byOld, Is.Null, "the old address still leads to the account");
            Assert.That(signIn, Is.InstanceOf<SuccessAuthorize>(),
                $"the new address does not sign in: {(signIn as FailedAuthorize)?.error}");
        });
    }

    // ── phone change ────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_phone_change_is_refused_for_something_that_is_not_a_number_or_is_taken(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var other   = await CreateSessionAsync(ct);
        var pass    = account.Credentials.password;
        var taken   = NewPhone();

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == other.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.PhoneNumber, taken), ct);

        var tooShort  = await account.Security.RequestPhoneChange("+7 123 45", pass, ct);
        var tooLong   = await account.Security.RequestPhoneChange("+1234567890123456", pass, ct);
        var blank     = await account.Security.RequestPhoneChange("  ", pass, ct);
        var formatted = await account.Security.RequestPhoneChange($"{taken[..2]} ({taken[2..5]}) {taken[5..8]}-{taken[8..]}", pass, ct);
        var badPass   = await account.Security.RequestPhoneChange(NewPhone(), "not-the-password", ct);

        Assert.Multiple(() =>
        {
            Assert.That((tooShort as FailedRequestPhoneChange)?.error, Is.EqualTo(PhoneChangeError.INVALID_PHONE));
            Assert.That((tooLong as FailedRequestPhoneChange)?.error, Is.EqualTo(PhoneChangeError.INVALID_PHONE));
            Assert.That((blank as FailedRequestPhoneChange)?.error, Is.EqualTo(PhoneChangeError.INVALID_PHONE));
            Assert.That((formatted as FailedRequestPhoneChange)?.error, Is.EqualTo(PhoneChangeError.PHONE_ALREADY_USED),
                "another account's number, written with spaces and dashes, was offered as free");
            Assert.That((badPass as FailedRequestPhoneChange)?.error, Is.EqualTo(PhoneChangeError.INVALID_PASSWORD));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Three_pending_phone_changes_are_the_most_an_account_may_hold(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);

        var results = new List<IRequestPhoneChangeResult>();

        for (var i = 0; i < 4; i++)
            results.Add(await account.Security.RequestPhoneChange(NewPhone(), account.Credentials.password, ct));

        Assert.Multiple(() =>
        {
            Assert.That(results.Take(3), Is.All.InstanceOf<SuccessRequestPhoneChange>());
            Assert.That((results[3] as FailedRequestPhoneChange)?.error, Is.EqualTo(PhoneChangeError.RATE_LIMITED));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Five_wrong_codes_spend_the_pending_phone_change(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var phone   = NewPhone();

        var nothingPending = await account.Security.ConfirmPhoneChange("123456", ct);

        await account.Security.RequestPhoneChange(phone, account.Credentials.password, ct);
        var code  = await GetPhoneCodeAsync(phone, ct: ct);
        var wrong = code == "000000" ? "111111" : "000000";

        var attempts = new List<IConfirmPhoneChangeResult>();

        for (var i = 0; i < 5; i++)
            attempts.Add(await account.Security.ConfirmPhoneChange(wrong, ct));

        var tooLate = await account.Security.ConfirmPhoneChange(code!, ct);
        var details = await account.Security.GetSecurityDetails(ct);

        Assert.Multiple(() =>
        {
            Assert.That((nothingPending as FailedConfirmPhoneChange)?.error, Is.EqualTo(PhoneChangeError.VERIFICATION_CODE_EXPIRED));
            Assert.That(attempts.Select(a => (a as FailedConfirmPhoneChange)?.error),
                Is.All.EqualTo(PhoneChangeError.INVALID_VERIFICATION_CODE));
            Assert.That((tooLate as FailedConfirmPhoneChange)?.error, Is.EqualTo(PhoneChangeError.VERIFICATION_CODE_EXPIRED),
                "the right code still worked after five wrong guesses");
            Assert.That(details.phone, Is.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_number_taken_while_the_change_was_pending_is_not_granted(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var other   = await CreateSessionAsync(ct);
        var phone   = NewPhone();

        await account.Security.RequestPhoneChange(phone, account.Credentials.password, ct);
        var code = await GetPhoneCodeAsync(phone, ct: ct);

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == other.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.PhoneNumber, phone), ct);

        var confirmed = await account.Security.ConfirmPhoneChange(code!, ct);
        var details   = await account.Security.GetSecurityDetails(ct);

        Assert.Multiple(() =>
        {
            Assert.That((confirmed as FailedConfirmPhoneChange)?.error, Is.EqualTo(PhoneChangeError.PHONE_ALREADY_USED));
            Assert.That(details.phone, Is.Null, "two accounts now hold the same number");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_confirmed_number_shows_on_the_security_screen_until_it_is_removed(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var phone   = NewPhone();

        await account.Security.RequestPhoneChange(phone, account.Credentials.password, ct);
        var confirmed = await account.Security.ConfirmPhoneChange((await GetPhoneCodeAsync(phone, ct: ct))!, ct);
        var withPhone = await account.Security.GetSecurityDetails(ct);

        var wrongPassword = await account.Security.RemovePhone("not-the-password", ct);
        var removed       = await account.Security.RemovePhone(account.Credentials.password, ct);
        var without       = await account.Security.GetSecurityDetails(ct);

        Assert.Multiple(() =>
        {
            Assert.That(confirmed, Is.InstanceOf<SuccessConfirmPhoneChange>());
            Assert.That(withPhone.phone, Is.EqualTo(phone));
            Assert.That((wrongPassword as FailedRemovePhone)?.error, Is.EqualTo(PhoneChangeError.INVALID_PASSWORD));
            Assert.That(removed, Is.InstanceOf<SuccessRemovePhone>());
            Assert.That(without.phone, Is.Null);
        });
    }

    // ── an account that is gone ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// An erased account's grain changes nothing and describes nothing.
    /// </summary>
    /// <remarks>
    /// Erasure soft-deletes the row, and the query filter then hides it from every read the grain
    /// makes. The Ion gate refuses such an account before it gets this far; this is the grain's own
    /// answer for anything that reaches it anyway.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_erased_accounts_security_grain_refuses_every_change(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var pass    = account.Credentials.password;

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == account.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.IsDeleted, true).SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow), ct);

        var grain = Security(account.UserId);

        var email    = await grain.RequestEmailChangeAsync(NewEmail(), pass, ct);
        var phone    = await grain.RequestPhoneChangeAsync(NewPhone(), pass, ct);
        var remove   = await grain.RemovePhoneAsync(pass, ct);
        var password = await grain.ChangePasswordAsync(pass, $"Nw!{Guid.NewGuid():N}"[..20], ct);
        var otp      = await grain.EnableOTPAsync(ct);
        var passkey  = await grain.BeginAddPasskeyAsync("Key", ct);
        var details  = await grain.GetSecurityDetailsAsync(ct);

        await using var check = await NewDbAsync(ct);
        var pendingEmails = await check.PendingEmailChanges.CountAsync(p => p.UserId == account.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((email as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.INTERNAL_ERROR));
            Assert.That((phone as FailedRequestPhoneChange)?.error, Is.EqualTo(PhoneChangeError.INTERNAL_ERROR));
            Assert.That((remove as FailedRemovePhone)?.error, Is.EqualTo(PhoneChangeError.INTERNAL_ERROR));
            Assert.That((password as FailedChangePassword)?.error, Is.EqualTo(PasswordChangeError.INTERNAL_ERROR));
            Assert.That((otp as FailedEnableOTP)?.error, Is.EqualTo(OTPError.INTERNAL_ERROR));
            Assert.That((passkey as FailedBeginPasskey)?.error, Is.EqualTo(PasskeyError.INTERNAL_ERROR));
            Assert.That(details.email, Is.Null, "an erased account's address is still handed out");
            Assert.That(details.otpEnabled, Is.False);
            Assert.That(pendingEmails, Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_premium_account_may_keep_its_data_for_longer(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);

        var regularLong = await account.Security.SetAutoDeletePeriod(48, ct);

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == account.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.HasActiveUltima, true), ct);

        var premiumLong = await account.Security.SetAutoDeletePeriod(48, ct);
        var tooLong     = await account.Security.SetAutoDeletePeriod(73, ct);
        var period      = await account.Security.GetAutoDeletePeriod(ct);

        Assert.Multiple(() =>
        {
            Assert.That((regularLong as FailedSetAutoDelete)?.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
            Assert.That(premiumLong, Is.InstanceOf<SuccessSetAutoDelete>());
            Assert.That((tooLong as FailedSetAutoDelete)?.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
            Assert.That(period.months, Is.EqualTo(48));
        });
    }

    // ── the devices screen ──────────────────────────────────────────────────────────────────────

    private async Task<Guid> OnlineAsync(Guid userId, UserSessionMeta? meta = null, CancellationToken ct = default)
    {
        var sid = Guid.NewGuid();

        await Presence.SetSessionOnlineAsync(userId, sid.ToString(), ct);

        if (meta is not null)
            await Presence.TouchSessionMetaAsync(userId, sid.ToString(), meta, ct);

        return sid;
    }

    private async Task<bool> IsTombstonedAsync(Guid userId, Guid sessionId)
        => (await Cache.SetMembersAsync(SessionRevocation.RevokedKey(userId))).Contains(sessionId.ToString());

    [Test, CancelAfter(120_000)]
    public async Task The_devices_screen_lists_the_current_session_first_and_skips_one_it_cannot_end(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var now     = DateTime.UtcNow;

        var current = await OnlineAsync(account.UserId, ct: ct);
        var older   = await OnlineAsync(account.UserId,
            new UserSessionMeta("Firefox", "DE", now.AddHours(-3), now.AddHours(-3), AppName: "Firefox"), ct);
        var recent  = await OnlineAsync(account.UserId,
            new UserSessionMeta("Argon/1.0", "NL", now.AddMinutes(-5), now.AddMinutes(-5), AppName: "Argon Desktop",
                Platform: ClientPlatform.WINDOWS, OsName: "Windows 11", DeviceName: "DESKTOP-1", City: "Amsterdam"), ct);

        await Presence.SetSessionOnlineAsync(account.UserId, "not-a-session-id", ct);

        var sessions = await Security(account.UserId).GetSessionsAsync(current, ct);
        var described = sessions.Single(s => s.sessionId == recent);

        Assert.Multiple(() =>
        {
            Assert.That(sessions.Select(s => s.sessionId), Is.EqualTo(new[] { current, recent, older }),
                "the current session first, then the most recently seen");
            Assert.That(sessions.Select(s => s.isCurrent), Is.EqualTo(new[] { true, false, false }));
            Assert.That(described.appName, Is.EqualTo("Argon Desktop"));
            Assert.That(described.region, Is.EqualTo("NL"));
            Assert.That(described.platform, Is.EqualTo(ClientPlatform.WINDOWS));
            Assert.That(described.deviceName, Is.EqualTo("DESKTOP-1"));
            Assert.That(described.city, Is.EqualTo("Amsterdam"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_current_session_is_not_ended_from_the_list(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var current = await OnlineAsync(account.UserId, ct: ct);

        var result = await Security(account.UserId).RevokeSessionAsync(current, current, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((result as FailedRevokeSession)?.error, Is.EqualTo(SessionError.CANNOT_REVOKE_CURRENT));
            Assert.That(await IsTombstonedAsync(account.UserId, current), Is.False);
        });
    }

    /// <summary>
    /// The development placeholder sid names no device, so ending it is refused rather than written.
    /// </summary>
    /// <remarks>
    /// A tombstone on <see cref="Guid.AllBitsSet"/> would sign out every past and future session that
    /// arrived without a sid of its own, for ten years. The refusal has to reach the caller as a
    /// failure — reporting it as a sign-out would tell the user a device was ended when nothing was.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_development_placeholder_session_is_never_tombstoned(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var current = await OnlineAsync(account.UserId, ct: ct);

        await Presence.SetSessionOnlineAsync(account.UserId, Guid.AllBitsSet.ToString(), ct);

        var result = await Security(account.UserId).RevokeSessionAsync(Guid.AllBitsSet, current, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((result as FailedRevokeSession)?.error, Is.EqualTo(SessionError.INTERNAL_ERROR),
                "a sign-out that wrote nothing was reported as done");
            Assert.That(await IsTombstonedAsync(account.UserId, Guid.AllBitsSet), Is.False,
                "the placeholder was tombstoned, locking out every session that ever lacked a sid");
        });
    }

    /// <summary>
    /// Signing out everywhere ends every device it can, and says so when one of them could not be.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Signing_out_everywhere_reports_the_device_it_could_not_end(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var current = await OnlineAsync(account.UserId, ct: ct);
        var laptop  = await OnlineAsync(account.UserId, ct: ct);

        await Presence.SetSessionOnlineAsync(account.UserId, Guid.AllBitsSet.ToString(), ct);
        await Presence.SetSessionOnlineAsync(account.UserId, "not-a-session-id", ct);

        var result    = await Security(account.UserId).RevokeAllSessionsAsync(current, ct);
        var remaining = (await Presence.GetActiveSessionIdsAsync(account.UserId, ct)).ToHashSet();

        Assert.Multiple(async () =>
        {
            Assert.That((result as FailedRevokeSession)?.error, Is.EqualTo(SessionError.INTERNAL_ERROR),
                "one device is still signed in, and the answer said every one was ended");
            Assert.That(await IsTombstonedAsync(account.UserId, laptop), Is.True,
                "the failure on one device spared the ones after it");
            Assert.That(await IsTombstonedAsync(account.UserId, current), Is.False, "the caller signed themselves out");
            Assert.That(remaining, Does.Not.Contain(laptop.ToString()), "the ended device is still on the screen");
            Assert.That(remaining, Does.Contain(current.ToString()));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_sign_out_whose_tombstone_cannot_be_written_is_reported_as_failed(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var current = await OnlineAsync(account.UserId, ct: ct);
        var laptop  = await OnlineAsync(account.UserId, ct: ct);
        var key     = SessionRevocation.RevokedKey(account.UserId);

        // A plain string where the tombstone set belongs: every SADD against it is refused.
        await Cache.StringSetAsync(key, "corrupt", TimeSpan.FromMinutes(5), ct);

        try
        {
            var one = await Security(account.UserId).RevokeSessionAsync(laptop, current, ct);

            // The failed sign-out above still took the laptop's presence down, so sign-out-everywhere
            // needs a device of its own to fail on.
            await OnlineAsync(account.UserId, ct: ct);

            var all = await Security(account.UserId).RevokeAllSessionsAsync(current, ct);

            Assert.Multiple(() =>
            {
                Assert.That((one as FailedRevokeSession)?.error, Is.EqualTo(SessionError.INTERNAL_ERROR),
                    "a device whose credential was never shut out was reported as signed out");
                Assert.That((all as FailedRevokeSession)?.error, Is.EqualTo(SessionError.INTERNAL_ERROR));
            });
        }
        finally
        {
            await Cache.KeyDeleteAsync(key, ct);
        }
    }

    /// <summary>
    /// The credential mapping is extra reach, not a precondition: a sign-out still ends the session
    /// the button was pressed on when the mapping cannot be read.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task An_unreadable_credential_mapping_does_not_stop_the_sign_out(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var current = await OnlineAsync(account.UserId, ct: ct);
        var laptop  = await OnlineAsync(account.UserId, ct: ct);
        var mapping = SessionRevocation.CredentialsKey(account.UserId, laptop);

        await Cache.StringSetAsync(mapping, "corrupt", TimeSpan.FromMinutes(5), ct);

        try
        {
            var result = await Security(account.UserId).RevokeSessionAsync(laptop, current, ct);

            Assert.Multiple(async () =>
            {
                Assert.That(result, Is.InstanceOf<SuccessRevokeSession>(),
                    $"one unreadable key cost the whole sign-out: {(result as FailedRevokeSession)?.error}");
                Assert.That(await IsTombstonedAsync(account.UserId, laptop), Is.True);
            });
        }
        finally
        {
            await Cache.KeyDeleteAsync(mapping, ct);
        }
    }

    /// <summary>
    /// When the list of live sessions cannot be read, nothing is claimed about it — the screen is
    /// empty, both sign-out buttons fail, and unrelated security changes still go through.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task An_unreadable_session_index_fails_closed(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var index   = $"presence:user:{account.UserId}:sessions";

        await Cache.StringSetAsync(index, "corrupt", TimeSpan.FromMinutes(5), ct);

        try
        {
            var grain = Security(account.UserId);

            // First, so the notification it sends in the background meets the broken index too.
            var setting  = await grain.SetAutoDeletePeriodAsync(24, ct);
            var sessions = await grain.GetSessionsAsync(Guid.NewGuid(), ct);
            var one      = await grain.RevokeSessionAsync(Guid.NewGuid(), Guid.NewGuid(), ct);
            var all      = await grain.RevokeAllSessionsAsync(Guid.NewGuid(), ct);
            var period   = await grain.GetAutoDeletePeriodAsync(ct);

            Assert.Multiple(() =>
            {
                Assert.That(sessions, Is.Empty);
                Assert.That((one as FailedRevokeSession)?.error, Is.EqualTo(SessionError.INTERNAL_ERROR));
                Assert.That((all as FailedRevokeSession)?.error, Is.EqualTo(SessionError.INTERNAL_ERROR),
                    "sign-out-everywhere reported success without knowing which sessions exist");
                Assert.That(setting, Is.InstanceOf<SuccessSetAutoDelete>(),
                    "a failed notification to the user's other devices undid the change it was announcing");
                Assert.That(period.months, Is.EqualTo(24));
            });
        }
        finally
        {
            await Cache.KeyDeleteAsync(index, ct);
        }
    }

    // ── signing up and resetting a password ─────────────────────────────────────────────────────

    private IAuthorizationGrain Authorization => GetGrainFactory().GetGrain<IAuthorizationGrain>(Guid.NewGuid());

    [Test, CancelAfter(120_000)]
    public async Task An_account_created_at_the_identity_server_can_be_signed_into(CancellationToken ct = default)
    {
        var creds = GenerateCredentials();
        var input = new NewUserCredentialsInput(creds.email, creds.username, creds.password, creds.displayName,
            true, creds.birthDate, false, null, "1.0", "1.0");

        var created   = await Authorization.ExternalRegister(input);
        var duplicate = await Authorization.ExternalRegister(input);
        var sameName  = await Authorization.ExternalRegister(input with { email = NewEmail() });
        var signIn    = await GetIdentityService().Authorize(
            new UserCredentialsInput(creds.email, null, null, creds.password, null, null), ct);
        var userId    = await GetGrainFactory().GetGrain<IIdentityDirectoryGrain>(Guid.Empty).GetUserIdByEmailAsync(creds.email, ct);

        Assert.Multiple(() =>
        {
            Assert.That(created.IsSuccess, Is.True, $"registration failed: {(created.IsSuccess ? null : created.Error.message)}");
            Assert.That(created.Value.token, Is.Not.Empty);
            Assert.That(duplicate.IsSuccess, Is.False);
            Assert.That(duplicate.Error.error, Is.EqualTo(RegistrationError.EMAIL_ALREADY_REGISTERED));
            Assert.That(sameName.IsSuccess, Is.False);
            Assert.That(sameName.Error.error, Is.EqualTo(RegistrationError.USERNAME_ALREADY_TAKEN));
            Assert.That(signIn, Is.InstanceOf<SuccessAuthorize>());
            Assert.That(userId, Is.Not.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_password_reset_with_the_emailed_code_replaces_the_password(CancellationToken ct = default)
    {
        var account     = await CreateSessionAsync(ct);
        var email       = account.Credentials.email;
        var newPassword = $"Rs!{Guid.NewGuid():N}"[..20];

        Assert.That(await GetIdentityService().BeginResetPassword(email, ct), Is.True);

        var code  = await GetEmailCodeAsync(email, ct: ct);
        var wrong = await GetIdentityService().ResetPassword(email, code == "000000" ? "111111" : "000000", newPassword, ct);
        var reset = await GetIdentityService().ResetPassword(email, code!, newPassword, ct);

        var withOld = await GetIdentityService().Authorize(
            new UserCredentialsInput(email, null, null, account.Credentials.password, null, null), ct);
        var withNew = await GetIdentityService().Authorize(
            new UserCredentialsInput(email, null, null, newPassword, null, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Not.Null, "no reset code was sent");
            Assert.That((wrong as FailedAuthorize)?.error, Is.EqualTo(AuthorizationError.BAD_OTP));
            Assert.That(reset, Is.InstanceOf<SuccessAuthorize>(), $"the right code was refused: {(reset as FailedAuthorize)?.error}");
            Assert.That(withOld, Is.InstanceOf<FailedAuthorize>(), "the old password still signs in after a reset");
            Assert.That(withNew, Is.InstanceOf<SuccessAuthorize>());
        });
    }

    /// <summary>
    /// A password reset should end the sessions that were signed in before it, as a password change does.
    /// </summary>
    /// <remarks>
    /// <para>Open. <c>SecurityGrain.ChangePasswordAsync</c> writes a revocation floor
    /// (<c>SessionRevocation.FloorKey</c>) because "changing a password is the one action that means
    /// whoever else is holding my credentials, stop". <c>ArgonAuthorizationService.ResetPass</c>,
    /// reached through <c>AuthorizationGrain.ResetPass</c>, replaces the password digest and writes
    /// nothing, so a refresh token taken before the reset keeps minting access tokens for its ten-year
    /// lifetime. A reset is the flow a person uses precisely when they no longer control the account.</para>
    ///
    /// <para>Not fixed here because the fix is not a one-liner: <c>SessionRevocation.IsBelowFloor</c>
    /// compares with <c>&lt;=</c> in whole seconds, and <c>ResetPass</c> mints the caller a fresh token
    /// in the same call — a floor written at "now" would revoke the very token the reset hands back.
    /// Whether the reset should sign the resetting device in at all, or write the floor a second
    /// earlier, is the decision this is waiting on.</para>
    /// </remarks>
    [Test, CancelAfter(120_000), Category("KnownPresenceBug")]
    public async Task A_password_reset_ends_the_sessions_signed_in_before_it(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var account = await CreateSessionAsync(ct);
        var before  = scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>()
           .GenerateRefreshToken(account.UserId, MachineId, ["user"], Guid.CreateVersion7());

        // Whole seconds on both sides of the comparison: step past the one the token was minted in.
        await Task.Delay(TimeSpan.FromSeconds(1.2), ct);

        await GetIdentityService().BeginResetPassword(account.Credentials.email, ct);
        var code  = await GetEmailCodeAsync(account.Credentials.email, ct: ct);
        var reset = await GetIdentityService().ResetPassword(account.Credentials.email, code!, $"Rs!{Guid.NewGuid():N}"[..20], ct);

        Assert.That(reset, Is.InstanceOf<SuccessAuthorize>());

        var refreshed = await GetIdentityService().GetMyAuthorization("", before, ct);

        Assert.That(refreshed, Is.InstanceOf<BadAuthStatus>(),
            "a refresh token issued before the password reset still mints access tokens");
    }
}
