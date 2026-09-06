namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Auth;
using Argon.Services;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime.client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;

[TestFixture]
public class SecurityTests : TestBase
{
    #region Email Change Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task RequestEmailChange_WithValidPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, FakedTestCreds.password, ct);

        if (result is FailedRequestEmailChange failed)
            Assert.Fail($"Request failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessRequestEmailChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task RequestEmailChange_WithInvalidPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, "wrongpassword123", ct);

        Assert.That(result, Is.InstanceOf<FailedRequestEmailChange>());
        var failed = result as FailedRequestEmailChange;
        Assert.That(failed!.error, Is.EqualTo(EmailChangeError.INVALID_PASSWORD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task ConfirmEmailChange_WithValidCode_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        // Request email change
        var requestResult = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, FakedTestCreds.password, ct);
        
        if (requestResult is FailedRequestEmailChange requestFailed)
            Assert.Fail($"Request failed with error: {requestFailed.error}");
        
        Assert.That(requestResult, Is.InstanceOf<SuccessRequestEmailChange>());

        // Get verification code from test store
        var code = await GetEmailCodeAsync(newEmail, ct: ct);
        Assert.That(code, Is.Not.Null, "Verification code should be available in test store");

        // Confirm email change
        var confirmResult = await GetSecurityService(scope.ServiceProvider)
            .ConfirmEmailChange(code!, ct);

        Assert.That(confirmResult, Is.InstanceOf<SuccessConfirmEmailChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task ConfirmEmailChange_WithInvalidCode_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        // Request email change
        var requestResult = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, FakedTestCreds.password, ct);
        
        if (requestResult is FailedRequestEmailChange requestFailed)
            Assert.Fail($"Request failed with error: {requestFailed.error}");

        // Try to confirm with wrong code
        var confirmResult = await GetSecurityService(scope.ServiceProvider)
            .ConfirmEmailChange("000000", ct);

        Assert.That(confirmResult, Is.InstanceOf<FailedConfirmEmailChange>());
        var failed = confirmResult as FailedConfirmEmailChange;
        Assert.That(failed!.error, Is.EqualTo(EmailChangeError.INVALID_VERIFICATION_CODE));
    }

    #endregion

    #region Password Change Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task ChangePassword_WithValidCurrentPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newPassword = "NewSecurePassword123!";

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword(FakedTestCreds.password, newPassword, ct);

        if (result is FailedChangePassword failed)
            Assert.Fail($"Change password failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessChangePassword>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task ChangePassword_WithInvalidCurrentPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword("wrongpassword123", "NewPassword123!", ct);

        Assert.That(result, Is.InstanceOf<FailedChangePassword>());
        var failed = result as FailedChangePassword;
        Assert.That(failed!.error, Is.EqualTo(PasswordChangeError.INVALID_CURRENT_PASSWORD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task ChangePassword_WithSamePassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword(FakedTestCreds.password, FakedTestCreds.password, ct);

        Assert.That(result, Is.InstanceOf<FailedChangePassword>());
        var failed = result as FailedChangePassword;
        Assert.That(failed!.error, Is.EqualTo(PasswordChangeError.PASSWORD_SAME_AS_CURRENT));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(13)]
    public async Task ChangePassword_WithShortPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword(FakedTestCreds.password, "short", ct);

        Assert.That(result, Is.InstanceOf<FailedChangePassword>());
        var failed = result as FailedChangePassword;
        Assert.That(failed!.error, Is.EqualTo(PasswordChangeError.PASSWORD_TOO_SHORT));
    }

    #endregion

    #region OTP (TOTP) Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(20)]
    public async Task EnableOTP_WhenNotEnabled_ReturnsSecretAndQrCode(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).EnableOTP(ct);

        Assert.That(result, Is.InstanceOf<SuccessEnableOTP>());
        var success = result as SuccessEnableOTP;
        Assert.That(success!.secret, Is.Not.Null.And.Not.Empty);
        Assert.That(success.qrCodeUrl, Does.Contain("otpauth://totp/"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(21)]
    public async Task VerifyAndEnableOTP_WithInvalidCode_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // First enable OTP to get secret
        await GetSecurityService(scope.ServiceProvider).EnableOTP(ct);

        // Try to verify with invalid code
        var result = await GetSecurityService(scope.ServiceProvider)
            .VerifyAndEnableOTP("000000", ct);

        Assert.That(result, Is.InstanceOf<FailedVerifyOTP>());
        var failed = result as FailedVerifyOTP;
        Assert.That(failed!.error, Is.EqualTo(OTPError.INVALID_CODE));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(22)]
    public async Task DisableOTP_WhenNotEnabled_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).DisableOTP("123456", ct);

        Assert.That(result, Is.InstanceOf<FailedDisableOTP>());
        var failed = result as FailedDisableOTP;
        Assert.That(failed!.error, Is.EqualTo(OTPError.NOT_ENABLED));
    }

    #endregion

    #region Passkey Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(30)]
    public async Task GetPasskeys_WhenEmpty_ReturnsEmptyList(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).GetPasskeys(ct);

        Assert.That(result.Values, Is.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(31)]
    public async Task BeginAddPasskey_ReturnsChallenge(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .BeginAddPasskey("My Passkey", ct);

        Assert.That(result, Is.InstanceOf<SuccessBeginPasskey>());
        var success = result as SuccessBeginPasskey;
        Assert.That(success!.optionsJson, Is.Not.Null.And.Not.Empty);

        // optionsJson holds the WebAuthn credential-creation options and must carry a challenge
        using var options = System.Text.Json.JsonDocument.Parse(success.optionsJson);
        Assert.That(options.RootElement.TryGetProperty("challenge", out var challenge), Is.True,
            "WebAuthn options must contain a challenge");
        Assert.That(challenge.GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(32)]
    public async Task CompleteAddPasskey_WithInvalidResponse_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Begin add passkey to establish a pending registration state
        var beginResult = await GetSecurityService(scope.ServiceProvider)
            .BeginAddPasskey("Test Passkey", ct);
        Assert.That(beginResult, Is.InstanceOf<SuccessBeginPasskey>());

        // A valid attestation can only be produced by a real authenticator;
        // completing with a malformed registration response must fail gracefully.
        var completeResult = await GetSecurityService(scope.ServiceProvider)
            .CompleteAddPasskey("{}", ct);

        Assert.That(completeResult, Is.InstanceOf<FailedCompletePasskey>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(33)]
    public async Task RemovePasskey_WhenExists_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // WebAuthn completion needs a real authenticator, so seed a completed passkey
        // directly for the authenticated user, then remove it through the API.
        var passkeyId = await CreatePasskeyForUserAsync(me.userId, "To Remove", ct);

        var removeResult = await GetSecurityService(scope.ServiceProvider)
            .RemovePasskey(passkeyId, ct);

        Assert.That(removeResult, Is.InstanceOf<SuccessRemovePasskey>());
    }

    private async Task<Guid> CreatePasskeyForUserAsync(Guid userId, string name, CancellationToken ct)
    {
        var factory = FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var passkey = new UserPasskeyEntity
        {
            Id           = Guid.CreateVersion7(),
            UserId       = userId,
            Name         = name,
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey    = new byte[32],
            SignCount    = 0,
            IsCompleted  = true,
            CreatedAt    = DateTimeOffset.UtcNow,
            UpdatedAt    = DateTimeOffset.UtcNow
        };

        db.Passkeys.Add(passkey);
        await db.SaveChangesAsync(ct);

        return passkey.Id;
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(34)]
    public async Task RemovePasskey_WhenNotExists_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RemovePasskey(Guid.NewGuid(), ct);

        Assert.That(result, Is.InstanceOf<FailedRemovePasskey>());
        var failed = result as FailedRemovePasskey;
        Assert.That(failed!.error, Is.EqualTo(PasskeyError.NOT_FOUND));
    }

    #endregion

    #region Auto Delete Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(40)]
    public async Task GetAutoDeletePeriod_WhenNotSet_ReturnsDefault12Months(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).GetAutoDeletePeriod(ct);

        Assert.That(result.enabled, Is.True);
        Assert.That(result.months, Is.EqualTo(12));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(41)]
    public async Task SetAutoDeletePeriod_WithValidMonths_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(24, ct);

        Assert.That(result, Is.InstanceOf<SuccessSetAutoDelete>());

        // Verify it was set
        var period = await GetSecurityService(scope.ServiceProvider).GetAutoDeletePeriod(ct);
        Assert.That(period.enabled, Is.True);
        Assert.That(period.months, Is.EqualTo(24));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(42)]
    public async Task SetAutoDeletePeriod_WithNull_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Trying to disable auto-delete should fail
        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(null, ct);

        Assert.That(result, Is.InstanceOf<FailedSetAutoDelete>());
        var failed = result as FailedSetAutoDelete;
        Assert.That(failed!.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(43)]
    public async Task SetAutoDeletePeriod_WithInvalidMonths_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Try invalid period (> 36 months)
        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(100, ct);

        Assert.That(result, Is.InstanceOf<FailedSetAutoDelete>());
        var failed = result as FailedSetAutoDelete;
        Assert.That(failed!.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(44)]
    public async Task SetAutoDeletePeriod_WithZeroMonths_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Try invalid period (0 months)
        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(0, ct);

        Assert.That(result, Is.InstanceOf<FailedSetAutoDelete>());
        var failed = result as FailedSetAutoDelete;
        Assert.That(failed!.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
    }

    #endregion

    #region Phone Change Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(50)]
    public async Task RequestPhoneChange_WithValidPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newPhone = "+79001234567";

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestPhoneChange(newPhone, FakedTestCreds.password, ct);

        if (result is FailedRequestPhoneChange failed)
            Assert.Fail($"Request failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessRequestPhoneChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(51)]
    public async Task RequestPhoneChange_WithInvalidPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestPhoneChange("+79001234567", "wrongpassword", ct);

        Assert.That(result, Is.InstanceOf<FailedRequestPhoneChange>());
        var failed = result as FailedRequestPhoneChange;
        Assert.That(failed!.error, Is.EqualTo(PhoneChangeError.INVALID_PASSWORD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(52)]
    public async Task ConfirmPhoneChange_WithValidCode_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newPhone = "+79009876543";
        var normalizedPhone = "+79009876543"; // Same after normalization

        // Request phone change
        var requestResult = await GetSecurityService(scope.ServiceProvider)
            .RequestPhoneChange(newPhone, FakedTestCreds.password, ct);
        
        if (requestResult is FailedRequestPhoneChange requestFailed)
            Assert.Fail($"Request failed with error: {requestFailed.error}");
        
        Assert.That(requestResult, Is.InstanceOf<SuccessRequestPhoneChange>());

        // Get verification code from test store (NullPhoneChannel stores it with normalized phone)
        var code = await GetPhoneCodeAsync(normalizedPhone, ct: ct);
        Assert.That(code, Is.Not.Null, "Phone verification code should be available in test store");

        // Confirm phone change
        var confirmResult = await GetSecurityService(scope.ServiceProvider)
            .ConfirmPhoneChange(code!, ct);

        Assert.That(confirmResult, Is.InstanceOf<SuccessConfirmPhoneChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(53)]
    public async Task RemovePhone_WithValidPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RemovePhone(FakedTestCreds.password, ct);

        if (result is FailedRemovePhone failed)
            Assert.Fail($"Remove phone failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessRemovePhone>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(54)]
    public async Task RemovePhone_WithInvalidPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RemovePhone("wrongpassword", ct);

        Assert.That(result, Is.InstanceOf<FailedRemovePhone>());
        var failed = result as FailedRemovePhone;
        Assert.That(failed!.error, Is.EqualTo(PhoneChangeError.INVALID_PASSWORD));
    }

    #endregion

    #region Revocation identity and channel visibility

    /// <summary>
    /// A device of an existing account, signed in on a client of its own — with the two things the
    /// account's own <see cref="TestUserSession"/> does not expose: the refresh token the sign-in
    /// minted, and the machine id its tokens are bound to.
    /// </summary>
    /// <remarks>
    /// Both are needed to act out a stolen-credential scenario honestly. The refresh token is what a
    /// revocation is ultimately meant to stop; the machine id is what <c>ClassicJwtFlow</c> hashes
    /// into every token, so a client that presented a different one would be refused for a reason
    /// the test is not about.
    /// </remarks>
    private sealed record SignedInDevice(TestUserSession Session, string RefreshToken, Guid MachineId);

    private async Task<SignedInDevice> SignInOnAnotherDeviceAsync(TestUserSession account, CancellationToken ct)
    {
        var machineId   = Guid.CreateVersion7();
        var interceptor = new DefaultHeaderInterceptor(machineId);
        var client      = IonClient.Create(HttpClient, WebSocketFactory);

        client.WithInterceptor(interceptor);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Authorize(
            new UserCredentialsInput(account.Credentials.email, null, null, account.Credentials.password, null, null),
            ct);

        if (result is not SuccessAuthorize authorized)
        {
            Assert.Fail($"Could not sign the account in on a second device: {(result as FailedAuthorize)?.error}");
            return null!;
        }

        interceptor.SetToken(authorized.token);

        var session = new TestUserSession(client, FactoryAsp.Services, account.Credentials, authorized.token,
            interceptor.SessionId);

        session.UserId = (await session.Users.GetMe(ct)).userId;

        Assert.That(session.UserId, Is.EqualTo(account.UserId),
            "the second client signed in as a different user, so it is not a second device of this account");

        return new SignedInDevice(session, authorized.refreshToken!, machineId);
    }

    /// <summary>
    /// The same credential, presented from a client that claims a brand new session id.
    /// </summary>
    /// <remarks>
    /// This is the whole attack in one helper: the <c>scid</c> is a value the client writes and
    /// regenerates whenever it likes, so "the device that was signed out" is not something the
    /// server can recognise by it. Same machine id — the token is bound to that — same access token,
    /// new sid.
    /// </remarks>
    private TestUserSession RotatedSessionId(TestUserSession original, Guid machineId, string accessToken)
    {
        var interceptor = new DefaultHeaderInterceptor(machineId);
        var client      = IonClient.Create(HttpClient, WebSocketFactory);

        client.WithInterceptor(interceptor);
        interceptor.SetToken(accessToken);

        var rotated = new TestUserSession(client, FactoryAsp.Services, original.Credentials, accessToken,
            interceptor.SessionId);

        rotated.UserId = original.UserId;

        Assert.That(rotated.SessionId, Is.Not.EqualTo(original.SessionId),
            "the rotated client kept the original sid, so nothing is being rotated");

        return rotated;
    }

    private Task<WebSocket> WebSocketFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = FactoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    private IArchetypeInteraction Archetypes(TestUserSession session)
        => session.Client.ForService<IArchetypeInteraction>(FactoryAsp.Services);

    private static List<string> ClaimValues(string jwt, string type)
        => new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims
           .Where(c => c.Type == type)
           .Select(c => c.Value)
           .ToList();

    private async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Revocation tests", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)?.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private async Task<Guid> CreateChannelAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Text, "Revocation tests", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    private async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Guest could not join the space: {(joined as FailedJoin)?.error}");
    }

    /// <summary>
    /// The hub ticket names the session the caller chose <em>and</em> the credential the server
    /// minted, and says when it was issued.
    /// </summary>
    /// <remarks>
    /// <para>The mint side of the identity model in <c>SessionRevocation</c>. Every gate on the
    /// realtime path reads its identity off this ticket, so what the ticket carries is the whole
    /// limit of what those gates can enforce: with only the <c>sid</c> on it — a value out of the
    /// caller's own <c>ArgonSecure</c> cookie — the strongest possible hub check is "is the id this
    /// client just made up on the tombstone list", which is no check at all.</para>
    ///
    /// <para><c>iat</c> is here for the other half: a ticket lives a day, an established socket is
    /// never re-authenticated, and a sign-out-everywhere writes a timestamp rather than an id. A
    /// ticket that cannot be placed in time cannot be measured against it.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task TheHubTicket_CarriesTheCredentialSessionIdAndItsIssueTime(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var device  = await SignInOnAnotherDeviceAsync(account, ct);

        // The refresh every running client does at startup, and the moment the server writes the
        // credential id onto the short-lived token the rest of the pipeline sees.
        var refreshed = await device.Session.Identity.GetMyAuthorization(device.Session.Token, device.RefreshToken, ct);

        Assert.That(refreshed, Is.InstanceOf<GoodAuthStatus>(),
            $"the device could not refresh: {(refreshed as BadAuthStatus)?.error}");

        var accessToken   = ((GoodAuthStatus)refreshed).token;
        var credentialIds = ClaimValues(accessToken, "sid");

        Assert.That(credentialIds, Is.Not.Empty,
            "the refreshed access token carries no credential session id, so nothing downstream can tell "
          + "which credential a request was authenticated by");

        var ticket = await device.Session.Bus.PickTicket(ct);

        Assert.Multiple(() =>
        {
            Assert.That(ClaimValues(ticket, "sid"), Does.Contain(device.Session.SessionId.ToString()),
                "the ticket does not name the presence session, so the hub cannot resolve the session grain");
            Assert.That(ClaimValues(ticket, "csid"), Is.SupersetOf(credentialIds),
                "the ticket does not carry the server-minted credential id, so the only identity the hub "
              + "can test is the one the caller chose for itself");
            Assert.That(ClaimValues(ticket, "iat"), Is.Not.Empty,
                "the ticket cannot be placed against a sign-out-everywhere floor");
        });
    }

    /// <summary>
    /// Signing a device out reaches it even when it comes back claiming a session id nobody has ever
    /// heard of.
    /// </summary>
    /// <remarks>
    /// <para>The presence sid is written by the client: it is the <c>scid</c> field of the
    /// <c>ArgonSecure</c> cookie for a browser and a value an installed client mints at every launch
    /// (<c>HttpContextExtensions.GetSessionId</c>). So a revocation that keys on it alone is escaped
    /// by the cheapest move available to an attacker holding a copied data folder: generate a new
    /// one. The tombstone was written for the row the user pressed the button on; the next request
    /// arrives under an id that was never on it, and every gate — the Ion interceptor, the hub, the
    /// session grain — waves it through, with a fresh presence row on the devices screen to match.</para>
    ///
    /// <para>What closes it is that the ended session is tombstoned under <em>both</em> of its ids —
    /// the row's, and the server-minted credential id recorded against it
    /// (<c>SessionRevocation.CredentialsKey</c>) — and that every gate now tests the credential id
    /// too, taking it from the signature on the token the request is authenticated by rather than
    /// from anything the caller can choose.</para>
    ///
    /// <para>Polled rather than asserted outright because the interceptor's revocation read is
    /// cached per user for fifteen seconds, exactly as it was before: the promise is that the
    /// sign-out takes effect, not that it takes effect on the very next packet.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task ARevokedDevice_CannotComeBackByRotatingItsSessionId(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var doomed = await SignInOnAnotherDeviceAsync(laptop, ct);

        var refreshed = await doomed.Session.Identity.GetMyAuthorization(doomed.Session.Token, doomed.RefreshToken, ct);

        Assert.That(refreshed, Is.InstanceOf<GoodAuthStatus>(),
            $"the second device could not refresh: {(refreshed as BadAuthStatus)?.error}");

        var accessToken = ((GoodAuthStatus)refreshed).token;

        // A row only appears on the devices screen once the device holds a presence key, and
        // RevokeSession refuses a sid it cannot see there.
        await using var onDoomed = await RealtimeClient.ConnectAsync(doomed.Session, ct);

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(doomed.Session.SessionId), TimeSpan.FromSeconds(30), ct: ct);

        Assert.That(listed, Does.Contain(doomed.Session.SessionId),
            "the second device never reached the devices screen, so there is no row to press the button on");

        var revoked = await laptop.Security.RevokeSession(doomed.Session.SessionId, ct);

        Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
            $"signing out a live device of the account failed with {(revoked as FailedRevokeSession)?.error}");

        var rotated = RotatedSessionId(doomed.Session, doomed.MachineId, accessToken);

        var refused = await Poll.UntilAsync(async () =>
        {
            try
            {
                await rotated.Users.GetMe(ct);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(1), ct);

        Assert.That(refused, Is.True,
            "a signed-out device kept its access to the whole Ion surface by generating a new session id");
    }

    /// <summary>
    /// A hub ticket minted before a sign-out-everywhere does not open a connection after it.
    /// </summary>
    /// <remarks>
    /// <para>A ticket is good for twenty-four hours and an established socket is never
    /// re-authenticated, so the floor a password change writes — the one handle that reaches a
    /// credential nobody registered under any id — has to be checked where a connection is accepted.
    /// Without it, an account whose password was changed after a compromise leaves the attacker a
    /// live feed of every space, channel and presence event for the rest of the day, while the same
    /// account's Ion calls are correctly refused.</para>
    ///
    /// <para>The connect gate reads the floor <em>uncached</em>, which is why this needs no polling
    /// and is the assertion worth making here: a fifteen-second-stale "not revoked" is enough to let
    /// a device back onto every one of its space groups, and a connect happens once.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task ATicketMintedBeforeASignOutEverywhere_IsRefusedByTheHub(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var session = await CreateSessionAsync(ct);

        await using (var before = await RealtimeClient.ConnectAsync(session, ct))
        {
            Assert.That(before.IsConnected, Is.True,
                "the session could not connect before the floor, so the refusal below would prove nothing");
        }

        // What ChangePassword writes. A minute ahead, so every ticket this test can mint is below it
        // without the test having to wait for anything.
        await scope.ServiceProvider.GetRequiredService<IArgonCacheDatabase>().StringSetAsync(
            SessionRevocation.FloorKey(session.UserId),
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString(),
            SessionRevocation.Window, ct);

        var admitted = await ConnectionSurvivesAsync(session, TimeSpan.FromSeconds(15), ct);

        Assert.That(admitted, Is.False,
            "a ticket issued before a sign-out-everywhere still opened a realtime connection");
    }

    /// <summary>
    /// A floor already in the past is not a lockout: a ticket minted after it connects normally.
    /// </summary>
    /// <remarks>
    /// <para>The counterpart of the refusal above, and the one that matters more in practice, because
    /// its failure mode is silent and permanent. A floor is written by every password change and kept
    /// for the refresh token's whole ten-year lifetime, so if the hub could not read a ticket's
    /// <c>iat</c> — a renamed claim, a claim the token library rewrote, a ticket minted by an older
    /// build — every ticket would read as "cannot be placed in time", which the gate deliberately
    /// treats as older than any floor. The user would change their password once and never hold a
    /// realtime connection again, while every Ion call kept working and every log stayed quiet.</para>
    ///
    /// <para>The mirror of <c>RefreshRevocationTests.AFloor_LeavesTokensIssuedAfterItWorking</c>, on
    /// the path that has no other way of being wrong safely.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task AFloorAlreadyPast_StillLetsAFreshTicketConnect(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var session = await CreateSessionAsync(ct);

        await scope.ServiceProvider.GetRequiredService<IArgonCacheDatabase>().StringSetAsync(
            SessionRevocation.FloorKey(session.UserId),
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(),
            SessionRevocation.Window, ct);

        var admitted = await ConnectionSurvivesAsync(session, TimeSpan.FromSeconds(5), ct);

        Assert.That(admitted, Is.True,
            "signing in again after a password change left the account unable to open a realtime connection at all");
    }

    /// <summary>
    /// An access token older than a password change stops being served — the ticket call included.
    /// </summary>
    /// <remarks>
    /// <para>The floor is the only handle a password change has: <c>SecurityGrain.ChangePasswordAsync</c>
    /// writes a watermark and no per-session tombstone at all, because per-session tombstones reach
    /// only the sessions discovery can still see and only credentials minted since the <c>sid</c>
    /// claim existed. Every other gate in the wave reads it — the hub against the ticket's <c>iat</c>,
    /// the session grain against <c>SessionStartTime</c>, the refresh path against the refresh token's
    /// <c>iat</c> — and the Ion interceptor did not.</para>
    ///
    /// <para>Which made the other three ornamental, because both of the values they compare are ones a
    /// still-valid access token can refresh at will. <c>IEventBus.PickTicket</c> is an ordinary Ion
    /// RPC: it passed the gate and minted a ticket stamped with a current <c>iat</c>, the hub read that
    /// as newer than the floor and admitted it, and the reconnect started a session with a current
    /// <c>SessionStartTime</c> that the grain read the same way. An access token is good for a week, so
    /// an attacker holding a copied data folder kept the whole product — messages, spaces, the live
    /// feed — for a week after the victim changed their password. What actually stopped was the
    /// refresh, and only that.</para>
    ///
    /// <para>The ticket is the call worth pinning because it is the one that re-mints an identity: any
    /// other refused RPC costs the attacker one call, while this one costs them the day-long ticket
    /// every realtime gate downstream reads its identity off.</para>
    ///
    /// <para>Polled rather than asserted outright, exactly as the tombstone above is: the interceptor
    /// reads the floor through the same fifteen-second per-user cache entry the revoked set uses, so
    /// the promise is that a sign-out-everywhere takes effect, not that it takes effect on the very
    /// next packet.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task AnAccessTokenOlderThanAPasswordChange_CannotMintAHubTicket(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        Assert.That(await session.Bus.PickTicket(ct), Is.Not.Empty,
            "the session could not mint a ticket before the password change, so the refusal below would prove nothing");

        var changed = await session.Security.ChangePassword(
            session.Credentials.password, $"Ch{Guid.NewGuid():N}A1!", ct);

        Assert.That(changed, Is.InstanceOf<SuccessChangePassword>(),
            $"the password change failed with {(changed as FailedChangePassword)?.error}, so no floor was written");

        var refused = await Poll.UntilAsync(async () =>
        {
            try
            {
                await session.Bus.PickTicket(ct);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(1), ct);

        Assert.That(refused, Is.True,
            "an access token minted before a sign-out-everywhere is still minting hub tickets, so the "
          + "floor the hub and the session grain check is one the caller can re-issue itself");
    }

    /// <summary>
    /// A floor already in the past is not a lockout: a token minted after it is served normally.
    /// </summary>
    /// <remarks>
    /// <para>The counterpart of the refusal above, and the one whose failure would be silent and
    /// permanent. A floor is written by every password change and kept for the refresh token's ten-year
    /// lifetime, and an access token carries no <c>iat</c> at all — <c>ClassicJwtFlow.GenerateAccessToken</c>
    /// gives <c>JwtSecurityToken</c> a <c>notBefore</c> and no issued-at, and the token library writes
    /// <c>iat</c> only when it is given one. A gate that read <c>iat</c> alone would therefore find
    /// every access token undatable, which <c>SessionRevocation.IsBelowFloor</c> deliberately reads as
    /// older than any floor: one password change and the account could never make an authenticated Ion
    /// call again, including with the token it signs in with afterwards.</para>
    ///
    /// <para>The floor is written before this user's first authenticated call, which is what makes the
    /// assertion mean something — the gate's per-user cache entry has nothing in it yet, so the read
    /// that admits this call is a real one.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task AFloorAlreadyPast_StillServesAFreshAccessToken(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        // Registration is anonymous, so nothing has read this user's floor yet and nothing is cached.
        var token  = await RegisterAndGetTokenAsync(ct);
        var userId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type == "sub").Value);

        await scope.ServiceProvider.GetRequiredService<IArgonCacheDatabase>().StringSetAsync(
            SessionRevocation.FloorKey(userId),
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(),
            SessionRevocation.Window, ct);

        SetAuthToken(token);

        Assert.That(async () => await GetUserService(scope.ServiceProvider).GetMe(ct), Throws.Nothing,
            "signing in again after a password change left the account unable to make an authenticated "
          + "call at all — the gate cannot place an access token in time and reads every one of them as "
          + "older than the floor");
    }

    /// <summary>
    /// Connects and answers whether the hub let the connection live.
    /// </summary>
    /// <remarks>
    /// A refusal is <c>Context.Abort()</c> inside <c>OnConnectedAsync</c>, which runs <em>after</em>
    /// the handshake has been answered — so <c>StartAsync</c> can perfectly well return before the
    /// socket is closed underneath it. "Refused" therefore means "did not throw and did not stay
    /// connected", and both halves have to be tolerated for the assertion to mean what it says.
    /// </remarks>
    private static async Task<bool> ConnectionSurvivesAsync(TestUserSession session, TimeSpan window, CancellationToken ct)
    {
        RealtimeClient client;

        try
        {
            client = await RealtimeClient.ConnectAsync(session, ct);
        }
        catch (Exception)
        {
            return false;
        }

        await using (client)
            return !await Poll.UntilAsync(() => Task.FromResult(!client.IsConnected), window, ct: ct);
    }

    /// <summary>
    /// A member of a space cannot subscribe to a channel the space says they may not view.
    /// </summary>
    /// <remarks>
    /// <para>Defect: <c>AppHub.SubscribeToChannel</c> gated on space membership alone, while the read
    /// path is narrower — <c>SpaceReadGrain.VisibleChannelsAsync</c> filters the channel list through
    /// <c>ArgonEntitlement.ViewChannel</c>, so a member without it never sees a restricted channel
    /// listed and cannot read its history. Meanwhile <c>ChannelGrain.FireChannel</c> publishes every
    /// message, edit, reaction and typing event to <c>channels/{id}</c>. So an ordinary member who
    /// learned the id of a moderators-only channel — a mention, an audit row, a screenshot; channel
    /// ids travel — could ask the hub for it and read it live, in real time, leaving no trace on any
    /// read path.</para>
    ///
    /// <para>Both halves are asserted. The channel the member <em>may</em> see has to keep working:
    /// this call is the one the desktop client really makes, on every channel the user opens, so a
    /// gate that refuses too much breaks the product outright.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task AMemberWithoutViewChannel_CannotSubscribeToThatChannel(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Restricted", ct);
        var open    = await CreateChannelAsync(owner, spaceId, $"open-{Guid.NewGuid():N}"[..12], ct);
        var locked  = await CreateChannelAsync(owner, spaceId, $"lockd-{Guid.NewGuid():N}"[..12], ct);

        await JoinAsync(owner, member, spaceId, ct);

        // "everyone" is the archetype every member holds and nothing else; denying ViewChannel on it
        // is how a space makes a channel private. The owner keeps the channel through the
        // Administrator flag on the owner archetype, which is what the roster filter does too.
        var archetypes = await Archetypes(owner).GetServerArchetypes(spaceId, ct);
        var everyone   = archetypes.Values.FirstOrDefault(a => a.isDefault);

        Assert.That(everyone, Is.Not.Null, "the space has no default archetype to deny anything on");

        await Archetypes(owner).UpsertArchetypeEntitlementForChannel(
            spaceId, locked, everyone!.id,
            deny: ArgonEntitlement.ViewChannel,
            allow: ArgonEntitlement.None,
            ct);

        var visible = await member.Servers.GetChannels(spaceId, ct);

        Assert.That(visible.Values.Select(c => c.channel.channelId), Does.Not.Contain(locked),
            "the read path still lists the restricted channel, so this test is not measuring a restricted channel");

        await using var client = await RealtimeClient.ConnectAsync(member, ct);

        Assert.That(async () => await client.SubscribeToChannel(open, ct), Throws.Nothing,
            "an ordinary member was refused a channel they can see — the gate refuses too much");

        Assert.That(async () => await client.SubscribeToChannel(locked, ct), Throws.Exception,
            "a member without ViewChannel was put on the channel group and is now reading its messages live");
    }

    #endregion
}
