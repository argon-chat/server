namespace Argon.Api.Features.Aegis;

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Argon.Features.Aegis;
using Argon.Features.Auth;
using Argon.Features.Jwt;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

/// <summary>
/// The sign-in widget's back end: everything between "a user arrived with an authorization request"
/// and "OpenIddict may mint a code for them".
/// </summary>
/// <remarks>
/// Two different things live here and it is worth keeping them apart. <see cref="Authorize"/> is the
/// OAuth authorization endpoint — the standard one, reached by a redirect, answered with a code. The
/// rest are the widget's own calls, which check a password or a passkey, keep a browser session, and
/// report back what the user still has to do: a one-time code, a hardware key, a consent screen.
/// None of them issue a token; they only establish that the session is entitled to one.
/// </remarks>
[ApiController, Route("api/auth")]
public class AuthController(
    IClusterClient cluster,
    IAegisDirectory directory,
    IOperatorVerificationStore operatorVerifications,
    AegisSession session,
    ClassicJwtFlow jwtFlow,
    ILogger<AuthController> logger) : ControllerBase
{
    private IAuthorizationGrain Authorization => cluster.GetGrain<IAuthorizationGrain>(Guid.NewGuid());

    /// <summary>
    /// The OAuth authorization endpoint. Turns an established browser session into a code.
    /// </summary>
    /// <remarks>
    /// Everything that decides <i>whether</i> the user may be here has already happened, on the
    /// widget's own calls below. What is left is assembling the identity the code will carry —
    /// including, for staff, the operator claims their hardware-key step-up earned.
    /// </remarks>
    [HttpPost("~/")]
    public async Task<IActionResult> Authorize()
    {
        try
        {
            var request = HttpContext.GetOpenIddictServerRequest()
                       ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            if (!session.IsAuthenticated)
            {
                logger.LogWarning("[OAuth] Authorization attempted without a session, clientId={ClientId}", request.ClientId);
                return Unauthorized();
            }

            var userId = session.RequireUserId;
            var user   = await directory.GetUserAsync(userId, HttpContext.RequestAborted);

            if (user is null)
            {
                logger.LogWarning("[OAuth] User {UserId} not found", userId);
                return Unauthorized();
            }

            var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, userId.ToString()));
            identity.AddClaim(new Claim(OpenIddictConstants.Claims.ClientId, request.ClientId ?? ""));
            identity.AddClaim(new Claim(AegisClaims.DisplayName, user.Username)
               .SetDestinations(OpenIddictConstants.Destinations.AccessToken));
            identity.AddClaim(new Claim(AegisClaims.AvatarFileId, user.AvatarFileId ?? "")
               .SetDestinations(OpenIddictConstants.Destinations.AccessToken));

            var verification = await operatorVerifications.ReadAsync(userId, HttpContext.RequestAborted);
            var grantedScopes = verification is null
                ? null
                : await AddOperatorClaimsAsync(identity, verification, request.ClientId);

            var scopes = request.GetScopes();

            if (grantedScopes is not null)
            {
                scopes = [.. scopes.Where(grantedScopes.Contains)];
                logger.LogInformation("[OAuth] Filtered operator scopes to: {Scopes}", string.Join(", ", scopes));
            }

            identity.SetScopes(scopes);
            identity.SetResources([]);

            if (!string.IsNullOrEmpty(request.RedirectUri))
                identity.SetAudiences(new Uri(request.RedirectUri).GetLeftPart(UriPartial.Authority));

            // Spent, not merely expiring: the next internal application asks for the key again.
            if (verification is not null)
                await operatorVerifications.ConsumeAsync(userId, HttpContext.RequestAborted);

            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }
        catch (Exception e)
        {
            logger.LogError(e, "[OAuth] Exception in Authorize");
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Puts the operator identity on the token, and returns the scopes that operator is allowed to
    /// use for this application, or <c>null</c> if their scopes are not narrowed.
    /// </summary>
    private async Task<HashSet<string>?> AddOperatorClaimsAsync(
        ClaimsIdentity identity, OperatorVerificationState verification, string? clientId)
    {
        const string bothTokens = $"{OpenIddictConstants.Destinations.AccessToken} {OpenIddictConstants.Destinations.IdentityToken}";
        var          ct         = HttpContext.RequestAborted;

        void Add(string type, string? value, string destinations = bothTokens)
            => identity.AddClaim(new Claim(type, value ?? "").SetDestinations(destinations.Split(' ')));

        Add(AegisClaims.OperatorId, verification.OperatorId.ToString());
        Add(AegisClaims.OperatorEmail, verification.OperatorEmail);
        Add(AegisClaims.OperatorCertThumbprint, verification.CertThumbprint, OpenIddictConstants.Destinations.AccessToken);
        Add(AegisClaims.TokenType, "operator");

        if (!string.IsNullOrEmpty(verification.DisplayName))
            Add(OpenIddictConstants.Claims.Name, verification.DisplayName);

        Add(OpenIddictConstants.Claims.Email, verification.OperatorEmail);
        Add(OpenIddictConstants.Claims.EmailVerified, "true");

        // RFC 8176: how they authenticated, not merely that they did.
        Add(AegisClaims.AuthenticationMethod, AegisClaims.HardwareKey, OpenIddictConstants.Destinations.IdentityToken);

        var roles = new List<string>();

        if (verification.IsSystemOperator)
            roles.Add(AegisClaims.SystemOperatorRole);

        HashSet<string>? allowedScopes = null;

        if (!string.IsNullOrEmpty(clientId) &&
            await directory.GetAppLoginCheckAsync(clientId, ct) is { } app &&
            await directory.HasAnyOperatorAppAccessAsync(verification.OperatorId, ct) &&
            await directory.GetOperatorAppAccessAsync(verification.OperatorId, app.AppId, ct) is { } access)
        {
            foreach (var claim in access.Claims)
            {
                Add(AegisClaims.OperatorAppClaim, claim, OpenIddictConstants.Destinations.AccessToken);
                roles.Add(claim);
            }

            // An empty list is "not narrowed", not "nothing allowed".
            if (access.AllowedScopes.Count > 0)
                allowedScopes = [.. access.AllowedScopes];
        }

        foreach (var role in roles)
            Add(AegisClaims.Roles, role);

        logger.LogInformation("[OAuth] Added operator claims: operatorId={OperatorId}, roles={Roles}",
            verification.OperatorId, string.Join(", ", roles));

        return allowedScopes;
    }

    /// <summary>
    /// Checks a password (and a one-time code, if the account uses them) and opens a browser session.
    /// </summary>
    [HttpPost("oauth/authorize")]
    [EnableRateLimiting(AegisRateLimitOptions.AuthPolicy)]
    public async Task<IActionResult> AuthorizeOAuth([FromBody] OAuthAuthorizeRequest request)
    {
        try
        {
            var ct = HttpContext.RequestAborted;

            var result = await Authorization.ExternalAuthorize(new UserCredentialsInput(
                request.Email, request.Phone, request.Username,
                request.Password, request.OtpCode, request.CaptchaToken));

            if (!result.IsSuccess)
            {
                logger.LogWarning("[OAuth] Authorization failed: {Error}", result.Error);

                return Ok(new OAuthAuthorizeResponse
                {
                    Error       = result.Error.ToString(),
                    RequiresOtp = result.Error == AuthorizationError.REQUIRED_OTP
                });
            }

            var (userId, _, _) = jwtFlow.ValidateAccessToken(result.Value.token, "argon.app");

            var app = await directory.GetAppInfoAsync(request.ClientId, ScopesOf(request.Scope), ct);

            if (app is null)
            {
                logger.LogWarning("[OAuth] App not found: {ClientId}", request.ClientId);
                return BadRequest(new { error = "invalid_client" });
            }

            await session.SignInAsync(userId);

            var allowed = await directory.CanSignInAsync(request.ClientId, userId, ct);

            if (!allowed.IsAllowed)
                return BadRequest(new { error = "access_denied", error_description = allowed.Reason });

            if (app.IsInternalApp)
            {
                if (await CheckOperatorAccessAsync(userId, app.AppId, ct) is { } denial)
                    return BadRequest(new { error = "access_denied", error_description = denial });

                if (!await operatorVerifications.IsVerifiedAsync(userId, ct))
                {
                    logger.LogInformation("[OAuth] Internal app, operator step-up required for user {UserId}", userId);
                    return Ok(new OAuthAuthorizeResponse { Success = true, RequiresOperatorAuth = true });
                }
            }

            return Ok(new OAuthAuthorizeResponse
            {
                Success         = true,
                RequiresConsent = true,
                ConsentInfo     = ConsentInfo.Of(app)
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "[OAuth] Exception in AuthorizeOAuth");
            return StatusCode(500, new { error = "server_error" });
        }
    }

    [HttpPost("oauth/complete")]
    public IActionResult CompleteOAuth()
        => session.IsAuthenticated
            ? Ok(new OAuthCompleteResponse { Success = true })
            : BadRequest(new { error = "invalid_grant" });

    /// <summary>
    /// Which credential the widget should ask this account for — a password, a one-time code, a
    /// passkey — before it asks for anything.
    /// </summary>
    [HttpPost("scenario")]
    [EnableRateLimiting(AegisRateLimitOptions.AuthPolicy)]
    public async Task<IActionResult> GetAuthScenario([FromBody] GetScenarioRequest request)
        => Ok(new
        {
            scenario = await Authorization.GetAuthorizationScenarioFor(
                new UserLoginInput(request.Email, request.Phone, request.Username), HttpContext.RequestAborted)
        });

    [HttpPost("passkey/begin")]
    [EnableRateLimiting(AegisRateLimitOptions.AuthPolicy)]
    public async Task<IActionResult> BeginPasskeyLogin([FromBody] BeginPasskeyRequest request)
    {
        try
        {
            var result = await Authorization.BeginPasskeyLogin(request.Email, HttpContext.RequestAborted);

            if (result.Error != PasskeyLoginError.NONE)
            {
                logger.LogWarning("[Passkey] Begin failed: {Error}", result.Error);
                return Ok(new PasskeyBeginResponse { Error = result.Error.ToString() });
            }

            return Ok(new PasskeyBeginResponse { Success = true, OptionsJson = result.OptionsJson });
        }
        catch (Exception e)
        {
            logger.LogError(e, "[Passkey] Exception in BeginPasskeyLogin");
            return StatusCode(500, new { error = "server_error" });
        }
    }

    [HttpPost("passkey/complete")]
    [EnableRateLimiting(AegisRateLimitOptions.AuthPolicy)]
    public async Task<IActionResult> CompletePasskeyLogin([FromBody] CompletePasskeyRequest request)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                nonce                 = request.Nonce,
                assertionResponseJson = request.AssertionResponseJson
            });

            var result = await Authorization.CompletePasskeyLogin(payload, HttpContext.RequestAborted);

            return await RespondToPasskeyAsync(result, request.ClientId, request.Scope);
        }
        catch (Exception e)
        {
            logger.LogError(e, "[Passkey] Exception in CompletePasskeyLogin");
            return StatusCode(500, new { error = "server_error" });
        }
    }

    [HttpPost("passkey/confirm-otp")]
    [EnableRateLimiting(AegisRateLimitOptions.AuthPolicy)]
    public async Task<IActionResult> ConfirmPasskeyOtp([FromBody] ConfirmPasskeyOtpRequest request)
    {
        try
        {
            var result = await Authorization.ConfirmPasskeyOtp(
                request.PasskeyNonce, request.OtpCode, HttpContext.RequestAborted);

            return await RespondToPasskeyAsync(result, request.ClientId, request.Scope);
        }
        catch (Exception e)
        {
            logger.LogError(e, "[Passkey] Exception in ConfirmPasskeyOtp");
            return StatusCode(500, new { error = "server_error" });
        }
    }

    /// <summary>
    /// Turns a passkey step's outcome into the widget's next screen: an error, a one-time code, or a
    /// signed-in session with a consent screen behind it.
    /// </summary>
    private async Task<IActionResult> RespondToPasskeyAsync(PasskeyLoginResult result, string? clientId, string? scope)
    {
        if (result.RequiresOtp)
        {
            logger.LogInformation("[Passkey] Passkey verified, OTP required for user {UserId}", result.UserId);

            return Ok(new PasskeyCompleteResponse
            {
                Success      = true,
                RequiresOtp  = true,
                PasskeyNonce = result.PasskeyNonce
            });
        }

        if (result.Error != PasskeyLoginError.NONE)
        {
            logger.LogWarning("[Passkey] Failed: {Error}", result.Error);
            return Ok(new PasskeyCompleteResponse { Error = result.Error.ToString() });
        }

        if (result.UserId is not { } userId)
        {
            logger.LogError("[Passkey] Reported success without a user id");
            return StatusCode(500, new { error = "server_error" });
        }

        await session.SignInAsync(userId);

        var app = string.IsNullOrEmpty(clientId)
            ? null
            : await directory.GetAppInfoAsync(clientId, ScopesOf(scope), HttpContext.RequestAborted);

        logger.LogInformation("[Passkey] Login successful for user {UserId}", userId);

        return Ok(new PasskeyCompleteResponse
        {
            Success         = true,
            RequiresConsent = app is not null,
            ConsentInfo     = app is null ? null : ConsentInfo.Of(app)
        });
    }

    /// <summary>
    /// Switches to another account already signed in on this browser.
    /// </summary>
    [HttpPost("accounts/select")]
    public async Task<IActionResult> SelectAccount([FromBody] SelectAccountRequest request)
    {
        try
        {
            if (!session.IsAuthenticated)
                return BadRequest(new { error = "not_authenticated" });

            var current = session.CurrentUserId;

            if (current == request.UserId)
                return Ok(new { success = true });

            var accounts = session.SignedInAccounts;

            if (accounts.Count == 0)
                return BadRequest(new { error = "no_logged_users" });

            // Only accounts already signed in on this browser: the claim is the whole authority for
            // switching without a password, so anything not in it must be refused.
            if (!accounts.Contains(request.UserId))
                return BadRequest(new { error = "user_not_in_list" });

            if (await directory.GetUserAsync(request.UserId, HttpContext.RequestAborted) is null)
                return BadRequest(new { error = "user_not_found" });

            logger.LogInformation("[OAuth] Switching from account {FromUserId} to {ToUserId}", current, request.UserId);

            await session.SwitchToAsync(request.UserId, HttpContext.RequestAborted);

            return Ok(new { success = true });
        }
        catch (Exception e)
        {
            logger.LogError(e, "[OAuth] Exception in SelectAccount");
            return StatusCode(500, new { error = "server_error" });
        }
    }

    /// <summary>
    /// What the widget should show a returning visitor: nothing, an account picker, a step-up, or a
    /// consent screen.
    /// </summary>
    [HttpGet("session/check")]
    public async Task<IActionResult> CheckSession(
        [FromQuery] string? clientId, [FromQuery] string? prompt, [FromQuery] string? scope)
    {
        try
        {
            var ct = HttpContext.RequestAborted;

            if (!session.IsAuthenticated)
                return Ok(new SessionCheckResponse { HasSession = false });

            var userId = session.RequireUserId;

            // prompt=login means the application wants the credential re-entered whatever we think.
            if (string.Equals(prompt, "login", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("[OAuth] prompt=login, forcing re-authentication");
                await session.SignOutAsync();
                return Ok(new SessionCheckResponse { HasSession = false, RequiresLogin = true });
            }

            var accounts     = session.SignedInAccounts;
            var forcePicker  = string.Equals(prompt, "select_account", StringComparison.OrdinalIgnoreCase);

            // More than one account means the user has to say which; already having said so is what
            // stops that from asking again on every check.
            if ((forcePicker && accounts.Count > 0) || (accounts.Count > 1 && !session.AccountAlreadySelected))
            {
                logger.LogInformation("[OAuth] Showing account selection (count={Count}, forced={Forced})",
                    accounts.Count, forcePicker);

                var known = new List<AccountInfo>();

                foreach (var id in accounts)
                {
                    if (await directory.GetUserAsync(id, ct) is { } info)
                        known.Add(new AccountInfo
                        {
                            UserId       = id,
                            Username     = info.Username,
                            AvatarFileId = info.AvatarFileId,
                            IsCurrent    = id == userId
                        });
                }

                return Ok(new SessionCheckResponse
                {
                    HasSession               = true,
                    RequiresAccountSelection = true,
                    Accounts                 = known
                });
            }

            if (string.IsNullOrEmpty(clientId))
                return Ok(new SessionCheckResponse { HasSession = true, RequiresConsent = false });

            var app = await directory.GetAppInfoAsync(clientId, ScopesOf(scope), ct);

            if (app is null)
            {
                logger.LogWarning("[OAuth] App not found: {ClientId}", clientId);
                return Ok(new SessionCheckResponse { HasSession = false });
            }

            var allowed = await directory.CanSignInAsync(clientId, userId, ct);

            if (!allowed.IsAllowed)
            {
                logger.LogWarning("[OAuth] Access denied: {Reason}", allowed.Reason);

                return Ok(new SessionCheckResponse
                {
                    HasSession   = true,
                    AccessDenied = true,
                    DenialReason = allowed.Reason
                });
            }

            if (app.IsInternalApp)
            {
                if (await CheckOperatorAccessAsync(userId, app.AppId, ct) is { } denial)
                    return Ok(new SessionCheckResponse
                    {
                        HasSession   = true,
                        AccessDenied = true,
                        DenialReason = denial
                    });

                if (!await operatorVerifications.IsVerifiedAsync(userId, ct))
                {
                    logger.LogInformation("[OAuth] Session check: operator step-up required for user {UserId}", userId);
                    return Ok(new SessionCheckResponse { HasSession = true, RequiresOperatorAuth = true });
                }
            }

            return Ok(new SessionCheckResponse
            {
                HasSession      = true,
                RequiresConsent = true,
                ConsentInfo     = ConsentInfo.Of(app)
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "[OAuth] Exception in CheckSession");
            return Ok(new SessionCheckResponse { HasSession = false });
        }
    }

    /// <summary>
    /// Whether this user is an operator entitled to reach an internal application. Returns the
    /// refusal to send back, or <c>null</c> when there is nothing to refuse.
    /// </summary>
    /// <remarks>
    /// Being an operator is not the same as being verified: this is the standing entitlement, and
    /// the hardware-key step-up is checked separately by whoever calls this. Both are needed and
    /// they fail differently — one is "you may not", the other is "not yet".
    /// </remarks>
    private async Task<string?> CheckOperatorAccessAsync(Guid userId, Guid appId, CancellationToken ct)
    {
        var op = await directory.GetOperatorAsync(userId, ct);

        if (op is null)
        {
            logger.LogWarning("[OAuth] Internal app requires an operator, but user {UserId} has no operator record", userId);
            return "This application requires operator access. No operator record found for your account.";
        }

        if (!op.IsActive)
        {
            logger.LogWarning("[OAuth] Operator {OperatorId} is inactive for user {UserId}", op.OperatorId, userId);
            return "Your operator account is inactive.";
        }

        // No grants at all means the permissive model: every internal app. One grant anywhere means
        // the explicit model, and this app has to be among them.
        if (await directory.HasAnyOperatorAppAccessAsync(op.OperatorId, ct) &&
            await directory.GetOperatorAppAccessAsync(op.OperatorId, appId, ct) is null)
        {
            logger.LogWarning("[OAuth] Operator {OperatorId} has no access to app {AppId}", op.OperatorId, appId);
            return "Your operator account does not have access to this application.";
        }

        return null;
    }

    private static List<string> ScopesOf(string? scope)
        => scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];
}
