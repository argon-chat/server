namespace Argon.Services.Ion;

using Core.Services.Validators;
using Features.Auth;
using Features.Jwt;
using Features.WebSession;

public class IdentityInteraction(
    ILogger<IIdentityInteraction> logger,
    ClassicJwtFlow flow,
    IArgonCacheDatabase cache,
    DeviceProofVerifier deviceProofs,
    DeviceIdentityService devices,
    IDbContextFactory<ApplicationDbContext> context,
    IHttpContextAccessor http,
    IOptions<WebSessionOptions> webSession,
    IQrLoginService qrLogin) : IIdentityInteraction
{
    public async Task<IAuthorizeResult> Authorize(UserCredentialsInput data, CancellationToken ct = default)
    {
        // Per-email login throttle (complements the per-IP throttle in ArgonTransactionInterceptor).
        // Surfaced as a generic BAD_CREDENTIALS so we neither leak account existence nor add a new
        // error code, and generous enough that a real user fixing a typo is never locked out.
        if (!string.IsNullOrWhiteSpace(data.email) &&
            !await CheckEmailRateLimitAsync("login", data.email!, max: 15, TimeSpan.FromMinutes(5), ct))
            return new FailedAuthorize(AuthorizationError.BAD_CREDENTIALS);

        await BindProvenDeviceAsync(ct);

        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).Authorize(data);

        if (result.IsSuccess)
            return result.Value;
        return new FailedAuthorize(result.Error);
    }

    public async Task<IRegistrationResult> Registration(NewUserCredentialsInput data, CancellationToken ct = default)
    {
        var validationStatus = await new NewUserCredentialsInputValidator(this.GetUserCountry()).ValidateAsync(data, ct);

        if (!validationStatus.IsValid)
        {
            var err = validationStatus.Errors.First();
            return new FailedRegistration(RegistrationError.VALIDATION_FAILED, err.PropertyName, err.ErrorMessage);
        }

        if (!await CheckEmailRateLimitAsync("register", data.email, max: 5, TimeSpan.FromMinutes(15), ct))
            return new FailedRegistration(RegistrationError.VALIDATION_FAILED, "email", "Too many attempts, please try again later");

        await BindProvenDeviceAsync(ct);

        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).Register(data);

        if (result.IsSuccess)
            return new SuccessRegistration(result.Value.token, result.Value.refreshToken);
        return new FailedRegistration(result.Error.error, result.Error.field, result.Error.message);
    }

    public async Task<bool> BeginResetPassword(string email, CancellationToken ct = default)
    {
        // Quietly drop excess reset requests per-email. Returning true preserves the existing
        // anti-enumeration contract (BeginResetPass already returns true for unknown emails).
        if (!string.IsNullOrWhiteSpace(email) &&
            !await CheckEmailRateLimitAsync("reset", email, max: 5, TimeSpan.FromMinutes(15), ct))
            return true;

        return await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).BeginResetPass(email);
    }

    public async Task<IAuthorizeResult> ResetPassword(string email, string otpCode, string newPassword, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(email) &&
            !await CheckEmailRateLimitAsync("reset-verify", email, max: 15, TimeSpan.FromMinutes(10), ct))
            return new FailedAuthorize(AuthorizationError.BAD_OTP);

        await BindProvenDeviceAsync(ct);

        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).ResetPass(email, otpCode, newPassword);

        if (result.IsSuccess)
            return result.Value;
        return new FailedAuthorize(result.Error);
    }

    public Task<string> GetAuthorizationScenario(CancellationToken ct = default)
        => Task.FromResult("Email_Otp");

    public Task<string> GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct = default)
        => this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).GetAuthorizationScenarioFor(data, ct);

    private async Task<string?> IsBadClient()
    {
        try
        {
            _ = this.GetMachineId();
        }
        catch (Exception)
        {
            return "Invalid machine ID";
        }

        try
        {
            _ = this.GetSessionId();
        }
        catch (Exception)
        {
            return "Invalid session ID";
        }

        return null;
    }

    /// <summary>
    /// Whether this refresh token has been shut out, by its own session or by a sign-out-everywhere.
    /// </summary>
    /// <remarks>
    /// A token minted before the <c>sid</c> claim existed has no session of its own to check, so
    /// only the floor applies to it. That is deliberate rather than lenient: the alternative is
    /// rejecting every such token outright, which signs out everyone who has not logged in since.
    /// </remarks>
    private async Task<bool> IsRefreshRevokedAsync(
        Guid userId, Guid? sessionId, DateTimeOffset? issuedAt, CancellationToken ct)
    {
        try
        {
            if (sessionId is { } sid)
            {
                var revoked = await cache.SetMembersAsync(SessionRevocation.RevokedKey(userId), ct);

                if (revoked.Contains(sid.ToString()))
                    return true;

                // Also the shape this replaced. A revocation written before the set existed would
                // otherwise be invisible, and the session its owner ended would start working again
                // the moment this deploys.
                if (await cache.KeyExistsAsync(SessionRevocation.LegacyRevokedKey(userId, sid), ct))
                    return true;
            }

            var floor = await cache.StringGetAsync(SessionRevocation.FloorKey(userId), ct);

            if (string.IsNullOrEmpty(floor) || !long.TryParse(floor, out var seconds))
                return false;

            // No iat means the token predates the claim and cannot be placed in time. Treating it as
            // older than any floor is the safe reading: a floor is only ever written by someone
            // asking to be signed out everywhere.
            return issuedAt is not { } when || when <= DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception e)
        {
            // The store being unreachable must not become a way to keep using a revoked token.
            logger.LogError(e, "Could not check refresh revocation for {UserId}", userId);
            return true;
        }
    }

    /// <summary>
    /// The device proof this request carried, if it verifies. Verifying consumes it.
    /// </summary>
    /// <remarks>
    /// <para>No RPC and no challenge round trip: the proof rides beside the request — in the
    /// <c>Sec-Proof</c> header when the desktop signed one for this call, in the <c>ArgonSecure</c>
    /// cookie when native code wrote one at launch — and giving hardware its own ion service would
    /// put a TPM into a contract that has no business knowing what one is.</para>
    ///
    /// <para>Read here rather than in the interceptor because verifying costs a signature check and
    /// a replay lookup, and because signing costs the client a TPM operation slow enough to notice.
    /// The calls that mint or refresh a session are the moments where both are worth paying.</para>
    /// </remarks>
    private async Task<DeviceProof?> VerifiedProofAsync(CancellationToken ct)
    {
        var raw = http.HttpContext?.GetDeviceProof();

        if (DeviceProofVerifier.Parse(raw) is not { } proof)
            return null;

        if (!DeviceProofVerifier.IsAcceptablePublicKey(proof.PublicKey))
            return null;

        // The proof is signed over the machine id, so without one there is nothing to check it against.
        if (ArgonRequestContext.Current.MachineId is not { Length: > 0 } machineId)
            return null;

        return await deviceProofs.VerifyAsync(proof, machineId, ct) ? proof : null;
    }

    /// <summary>
    /// Marks this request as coming from a machine whose hardware key just proved itself, so the
    /// refresh token minted for it is bound to that key.
    /// </summary>
    /// <remarks>
    /// <para>This is the answer to "copy the client's data folder to another machine and sign in":
    /// the folder holds the refresh token and the cookie, and both are bearer values. A token bound
    /// to a key that never leaves the TPM cannot be refreshed anywhere that TPM is not, however
    /// faithfully the folder was copied.</para>
    ///
    /// <para>Best effort, deliberately. A machine without a usable TPM, a browser, an older build —
    /// none of them can prove anything and all of them must still be able to sign in; they get the
    /// session they always got, bound to the machine id alone. Refusing the proof-less would be
    /// refusing every Mac and every Linux desktop today.</para>
    /// </remarks>
    private async Task BindProvenDeviceAsync(CancellationToken ct)
    {
        try
        {
            var proof = await VerifiedProofAsync(ct);

            this.SetUserDeviceThumbprint(proof is null ? null : DeviceProofVerifier.Thumbprint(proof.PublicKey));
        }
        catch (Exception e)
        {
            // A proof that cannot be read is a proof that was not offered. Signing in must not fail
            // over the binding on the token it is about to receive.
            logger.LogWarning(e, "Could not verify a device proof at sign-in");
            this.SetUserDeviceThumbprint(null);
        }
    }

    /// <summary>The thumbprint of a verified proof on this request, or null. For callers that carry it elsewhere than the request context.</summary>
    private async Task<string?> ProvenThumbprintAsync(CancellationToken ct)
    {
        try
        {
            return await VerifiedProofAsync(ct) is { } proof ? DeviceProofVerifier.Thumbprint(proof.PublicKey) : null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not verify a device proof");
            return null;
        }
    }

    public async Task<IMyAuthStatus> GetMyAuthorization(string token, string? refreshToken, CancellationToken ct = default)
    {
        // A browser has no refresh token to pass: for the web client it is in a cookie the page
        // cannot read, which is the point of it being there. The argument still wins where it is
        // given, so every installed client keeps the path it has always taken and this branch is
        // only reached by a caller that has nothing to offer otherwise.
        if (string.IsNullOrEmpty(refreshToken) && http.HttpContext is { } context)
            refreshToken = WebSessionCookie.Read(context, webSession.Value);

        if (string.IsNullOrEmpty(refreshToken))
            return new BadAuthStatus(BadAuthKind.REQUIRED_RELOGIN);

        var badClientReason = await IsBadClient();

        if (!string.IsNullOrEmpty(badClientReason))
            return new CertificateErrorAuthStatus(badClientReason);
        
        try
        {
            var machineId = this.GetMachineId();

            var (userId, scopes) = flow.ValidateRefreshTokenSession(
                refreshToken, machineId, out var tokenSessionId, out var issuedAt, out var deviceThumbprint);

            // Resolved for every caller that offers a proof, not only bound ones: this is also how a
            // machine is first recorded, since there is no enrolment call to make. Checked before
            // revocation because it is the cheaper of the two and refuses the same requests.
            var proof        = await VerifiedProofAsync(ct);
            var provenDevice = proof is null ? null : await devices.ResolveByKeyAsync(userId, proof, ct);

            // A token bound to a hardware key is only good where that key is: the proof has to be
            // there, has to verify, and has to come from the key the token names — a valid proof
            // from some other machine's TPM is a proof of the wrong thing, and used to pass here.
            // A barred machine also lands here, since ResolveByKeyAsync reports it as no device.
            if (deviceThumbprint is not null)
            {
                if (proof is null || provenDevice is null)
                    return new BadAuthStatus(BadAuthKind.SESSION_EXPIRED);

                var presented = DeviceProofVerifier.Thumbprint(proof.PublicKey);

                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(deviceThumbprint),
                        Encoding.UTF8.GetBytes(presented)))
                {
                    logger.LogWarning("Refresh for {UserId} presented a proof from a different key than the token is bound to", userId);
                    return new BadAuthStatus(BadAuthKind.SESSION_EXPIRED);
                }
            }

            // Checked here, against claims out of the signed token, rather than relying on the
            // interceptor's check: that one keys on the session id from the ArgonSecure cookie, and
            // a caller who simply omits the cookie skips it. This is the path that mints new access
            // tokens from a ten-year credential, so it is the one that has to hold.
            if (await IsRefreshRevokedAsync(userId, tokenSessionId, issuedAt, ct))
                return new BadAuthStatus(BadAuthKind.SESSION_EXPIRED);

            var limitation = await this.GetGrain<IUserGrain>(userId).GetLimitationForUser();

            if (limitation.lockdownReason is not null)
                return limitation;

            // The proven device travels on the access token, so every subsequent request knows which
            // machine is asking without asking the database again — which is what makes a hardware
            // ban enforceable per request rather than only at refresh.
            var newIssued = flow.GenerateAccessToken(userId, machineId, scopes,
                provenDevice is { } device ? [new System.Security.Claims.Claim("did", device.ToString())] : null);

            return new GoodAuthStatus(newIssued);
        }
        catch (InvalidOperationException e)
        {
            logger.LogWarning(e, "failed validate machineId");
            return new BadAuthStatus(BadAuthKind.REQUIRED_RELOGIN);
        }
        catch (TokenTypeNotAllowed e)
        {
            logger.LogWarning(e, "trying authorize by invalid scope token");
            return new BadAuthStatus(BadAuthKind.BAD_TOKEN);
        }
        catch (MachineIdNotMatchedException e)
        {
            logger.LogWarning(e, "trying authorize with not matched machineId");
            return new BadAuthStatus(BadAuthKind.SESSION_EXPIRED);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed call GetMyAuthorization");
            return new BadAuthStatus(BadAuthKind.REQUIRED_RELOGIN);
        }
    }

    // The two anonymous halves of the QR sign-in. Both are @AllowAnonymous but NOT
    // @DoNotRequireSessionContext: the desktop already sends its ArgonSecure cookie on Authorize, and
    // the machine id inside it is what binds a pending request to the browser that opened it. The
    // per-IP throttle in ArgonTransactionInterceptor does not cover these methods (its table is
    // keyed by method name), so QrLoginService carries its own.
    // The desktop's proof, when it has one, binds the session the phone will approve to this
    // machine's key — the same binding a password sign-in gets, arrived at through a different door.
    public async Task<ICreateLoginRequestResult> CreateLoginRequest(CancellationToken ct = default)
        => await qrLogin.CreateAsync(await ProvenThumbprintAsync(ct), ct);

    public Task<ILoginPollResult> PollLoginRequest(string token, CancellationToken ct = default)
        => qrLogin.PollAsync(token, ct);

    // The three authenticated halves — the phone is already signed in, and its user is the one the
    // desktop will be signed in as.
    public Task<ILoginRequestPreviewResult> PreviewLoginRequest(string token, CancellationToken ct = default)
        => qrLogin.PreviewAsync(token, this.GetUserId(), ct);

    public Task<IApproveLoginRequestResult> ApproveLoginRequest(string token, CancellationToken ct = default)
        => qrLogin.ApproveAsync(token, this.GetUserId(), ct);

    public Task<IRejectLoginRequestResult> RejectLoginRequest(string token, CancellationToken ct = default)
        => qrLogin.RejectAsync(token, this.GetUserId(), ct);

    // Sliding-window per-email limiter mirroring EmailOtpStrategy.CheckRateLimitAsync (INCR, set
    // EXPIRE on first hit) over the shared Dragonfly cache. Returns true if allowed. Fails OPEN on
    // any cache error (incl. the InMemory single-instance cache, which doesn't implement INCR) so a
    // cache incident never blocks legitimate auth.
    private async Task<bool> CheckEmailRateLimitAsync(string scope, string email, int max, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var key   = $"rl:auth:email:{scope}:{email.ToLowerInvariant()}";
            var count = await cache.StringIncrementAsync(key, ct);
            if (count == 1)
                await cache.KeyExpireAsync(key, window, ct);
            return count <= max;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Per-email rate-limit cache call failed; allowing (fail-open)");
            return true;
        }
    }
}