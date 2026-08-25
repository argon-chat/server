namespace Argon.Api.Features.Aegis;

using System.Security.Claims;
using Argon.Features.Aegis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

/// <summary>
/// The browser session the sign-in widget keeps, and the list of accounts signed in on it.
/// </summary>
/// <remarks>
/// One place rather than four, because the list is the part that is easy to get wrong: signing in a
/// second account has to <i>add</i> to what the cookie already carries, and the version of this that
/// rebuilt the claims from scratch each time would quietly sign the first account out. Switching
/// accounts is the one operation that does not add — see <see cref="SwitchToAsync"/>, which is also
/// where the previous account's operator step-up is thrown away.
/// </remarks>
public sealed class AegisSession(
    IHttpContextAccessor accessor,
    IOperatorVerificationStore operatorVerifications,
    IOptions<AegisSessionOptions> options)
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    private HttpContext Http
        => accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context to sign in on");

    public bool IsAuthenticated
        => Http.User.Identity?.IsAuthenticated ?? false;

    /// <summary>The account this session is currently acting as, or <c>null</c> if signed out.</summary>
    public Guid? CurrentUserId
        => Guid.TryParse(Http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    public Guid RequireUserId
        => CurrentUserId ?? throw new InvalidOperationException("No user id on an authenticated session");

    /// <summary>Every account signed in on this browser, current one included.</summary>
    public List<Guid> SignedInAccounts
    {
        get
        {
            var stored = Http.User.FindFirst(AegisClaims.LoggedUsers)?.Value;

            if (string.IsNullOrEmpty(stored))
                return [];

            var ids = JsonConvert.DeserializeObject<List<string>>(stored) ?? [];

            return [.. ids.Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty).Where(id => id != Guid.Empty)];
        }
    }

    /// <summary>Whether the user has already been through the account picker on this session.</summary>
    public bool AccountAlreadySelected
        => Http.User.FindFirst(AegisClaims.AccountSelected)?.Value == "true";

    /// <summary>
    /// Signs <paramref name="userId"/> in, keeping every account already signed in on this browser.
    /// </summary>
    public Task SignInAsync(Guid userId)
    {
        var accounts = SignedInAccounts;

        if (!accounts.Contains(userId))
            accounts.Add(userId);

        return WriteAsync(userId, accounts, selected: false);
    }

    /// <summary>
    /// Switches to an account already signed in on this browser.
    /// </summary>
    /// <remarks>
    /// The previous account's operator verification is dropped rather than carried across: a
    /// hardware key was touched to prove <i>that</i> account is an operator, and letting the proof
    /// survive an account switch is how one gets promoted into another.
    /// </remarks>
    public async Task SwitchToAsync(Guid userId, CancellationToken ct = default)
    {
        if (CurrentUserId is { } previous)
            await operatorVerifications.ConsumeAsync(previous, ct);

        await WriteAsync(userId, SignedInAccounts, selected: true);
    }

    public Task SignOutAsync()
        => Http.SignOutAsync(Scheme);

    private Task WriteAsync(Guid userId, List<Guid> accounts, bool selected)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(OpenIddictConstants.Claims.Subject, userId.ToString()),
            new(AegisClaims.LoggedUsers, JsonConvert.SerializeObject(accounts.Select(id => id.ToString())))
        };

        if (selected)
            claims.Add(new Claim(AegisClaims.AccountSelected, "true"));

        return Http.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)),
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc   = DateTimeOffset.UtcNow + options.Value.RememberFor
            });
    }
}
