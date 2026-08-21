namespace Argon.Services.Ion;

using Core.Services.Validators;
using Features.Auth;
using Features.Jwt;

public class IdentityInteraction(
    ILogger<IIdentityInteraction> logger,
    ClassicJwtFlow flow,
    IArgonCacheDatabase cache,
    DeviceProofVerifier deviceProofs,
    DeviceIdentityService devices,
    IDbContextFactory<ApplicationDbContext> context,
    IHttpContextAccessor http,
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
    /// Resolves the machine this request came from, from the proof native code put in the cookie.
    /// </summary>
    /// <remarks>
    /// <para>No RPC and no challenge round trip: the <c>ArgonSecure</c> cookie exists so native code
    /// can carry device identity, and giving hardware its own ion service would put a TPM into a
    /// contract that has no business knowing what one is.</para>
    ///
    /// <para>Read here rather than in the interceptor because verifying costs a signature check and
    /// a replay lookup, and because signing costs the client a TPM operation slow enough to notice.
    /// Refresh is the one moment where both are worth paying: from here the resolved device rides
    /// the access token as <c>did</c>, and every later request is judged on that.</para>
    /// </remarks>
    private async Task<Guid?> ResolveDeviceAsync(Guid userId, CancellationToken ct)
    {
        var raw = http.HttpContext?.GetDeviceProof();

        if (DeviceProofVerifier.Parse(raw) is not { } proof)
            return null;

        if (!DeviceProofVerifier.IsAcceptablePublicKey(proof.PublicKey))
            return null;

        if (!await deviceProofs.VerifyAsync(proof, this.GetMachineId(), ct))
            return null;

        return await devices.ResolveByKeyAsync(userId, proof, ct);
    }

    public async Task<IMyAuthStatus> GetMyAuthorization(string token, string? refreshToken, CancellationToken ct = default)
    {
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

            // A token bound to a hardware key is only good where that key is. Checked before
            // revocation because it is the cheaper of the two and refuses the same requests.
            // Resolved for every caller that offers a proof, not only bound ones: this is also how
            // a machine is first recorded, since there is no enrolment call to make.
            var provenDevice = await ResolveDeviceAsync(userId, ct);

            // A token bound to a hardware key is only good where that key is. A missing or failed
            // proof leaves provenDevice null, and the two not matching is the refusal.
            if (deviceThumbprint is not null && provenDevice is null)
                return new BadAuthStatus(BadAuthKind.SESSION_EXPIRED);

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
    public Task<ICreateLoginRequestResult> CreateLoginRequest(CancellationToken ct = default)
        => qrLogin.CreateAsync(ct);

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