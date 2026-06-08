namespace Argon.Services.Ion;

using Core.Services.Validators;
using Features.Jwt;

public class IdentityInteraction(ILogger<IIdentityInteraction> logger, ClassicJwtFlow flow, IArgonCacheDatabase cache) : IIdentityInteraction
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

            var (userId, _, scopes) = flow.ValidateRefreshToken(refreshToken, machineId);

            var limitation = await this.GetGrain<IUserGrain>(userId).GetLimitationForUser();

            if (limitation.lockdownReason is not null)
                return limitation;

            var newIssued = flow.GenerateAccessToken(userId, machineId, scopes);

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