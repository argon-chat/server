namespace Argon.Features.WebSession;

using Argon.Features.Auth;
using Argon.Features.Jwt;
using Argon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Turning an Aegis sign-in into an Argon session the browser holds in a cookie.
/// </summary>
/// <remarks>
/// <para>Two endpoints and nothing else. The OAuth flow itself is untouched: the web client goes
/// through the widget, the consent screen and the token endpoint exactly as any other application
/// does, and what changes is only what it does with the token afterwards — it hands it here once, in
/// exchange for a session, instead of carrying it on every call.</para>
///
/// <para>This is the only place where an Aegis token opens the Argon API, and it is gated on the
/// audience allowlist in <see cref="WebSessionOptions.TrustedAudiences"/>. Applications outside that
/// list keep the tokens they already get and reach exactly what they already reach — the Ion
/// interceptor has never accepted an Aegis token and still does not.</para>
/// </remarks>
public static class WebSessionEndpoints
{
    public const string ExchangePath = "/auth/web/session";
    public const string LogoutPath   = "/auth/web/logout";

    /// <summary>The scopes a web session is issued with — the same set an installed client gets.</summary>
    private static readonly string[] SessionScopes = ["argon.app"];

    public static WebApplicationBuilder AddWebSession(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<AegisTokenValidator>();
        return builder;
    }

    public static WebApplication MapWebSession(this WebApplication app)
    {
        app.MapPost(ExchangePath, ExchangeAsync).AllowAnonymous();
        app.MapPost(LogoutPath, LogoutAsync).AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Trades a token the identity server signed for a session bound to this browser.
    /// </summary>
    /// <remarks>
    /// The access token goes back in the body and nowhere else: it belongs in memory and in the
    /// <c>Authorization</c> header, and putting it in a cookie would make every authenticated call
    /// forgeable from another site. Only the refresh token — the long-lived half, and the one that
    /// has no business being reachable from script — becomes a cookie.
    /// </remarks>
    private static async Task<IResult> ExchangeAsync(
        HttpContext                 http,
        AegisTokenValidator         validator,
        UserManagerService          users,
        IOptions<WebSessionOptions> options,
        CancellationToken           ct)
    {
        if (BearerToken(http) is not { } token)
            return Results.Unauthorized();

        if (await validator.ValidateAsync(token, ct) is not { } identity)
            return Results.Unauthorized();

        var settings = options.Value;
        var appId    = settings.TrustedAudiences[identity.Audience];

        // One id for both halves: it is the scid the device cookie carries and the sid signed into
        // the refresh token, so the tombstone written at sign-out ends the session on both paths.
        var sessionId = ArgonId.New();
        var machineId = ArgonSecureCookie.Issue(http, settings, appId, sessionId);

        var issued = await users.GenerateJwt(identity.UserId, machineId, SessionScopes, sessionId);

        WebSessionCookie.Write(http, settings, issued.refreshToken!);

        return Results.Ok(new WebSessionResponse(issued.token));
    }

    /// <summary>
    /// Ends the session this browser holds.
    /// </summary>
    /// <remarks>
    /// <para>The tombstone is written here rather than through <c>ISecurityGrain.RevokeSessionAsync</c>
    /// because that call is for ending some <i>other</i> device — it refuses the caller's own session
    /// outright, and looks the target up among the user's live presence rows, which a session that is
    /// signing out has no reason to still have.</para>
    ///
    /// <para>The device cookie is deliberately left alone. It is an identity, not a credential:
    /// signing out is not a claim to be a different machine, and dropping it would lose the thread
    /// between a returning user and the device history already recorded against them.</para>
    ///
    /// <para>Always answers the same way. A cookie that cannot be read is cleared just the same, and
    /// telling the caller which of the two happened would only describe the state of a credential to
    /// whoever presented it.</para>
    /// </remarks>
    private static async Task<IResult> LogoutAsync(
        HttpContext                 http,
        ClassicJwtFlow              flow,
        IArgonCacheDatabase         cache,
        IOptions<WebSessionOptions> options,
        ILoggerFactory              loggers,
        CancellationToken           ct)
    {
        var settings = options.Value;

        if (WebSessionCookie.Read(http, settings) is { } refreshToken)
        {
            try
            {
                var (userId, _) = flow.ValidateRefreshTokenSession(
                    refreshToken, http.GetMachineId(), out var sessionId, out _, out _);

                if (sessionId is { } id)
                {
                    var key = SessionRevocation.RevokedKey(userId);

                    await cache.SetAddAsync(key, id.ToString(), ct);
                    await cache.KeyExpireAsync(key, SessionRevocation.Window, ct);
                }
            }
            catch (Exception e)
            {
                // An expired or tampered cookie is nothing to report: the caller asked to be signed
                // out and is about to be, and the token it presented is one nothing would have
                // honoured anyway.
                loggers.CreateLogger(typeof(WebSessionEndpoints))
                       .LogDebug(e, "Could not read the session cookie while signing out");
            }
        }

        WebSessionCookie.Clear(http, settings);

        return Results.NoContent();
    }

    private static string? BearerToken(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("Authorization", out var header))
            return null;

        var value = header.ToString();

        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim() is { Length: > 0 } token ? token : null
            : null;
    }
}

/// <param name="AccessToken">
/// Short-lived, and the client is expected to keep it in memory only — everything that outlives the
/// tab is in the cookie.
/// </param>
public sealed record WebSessionResponse(string AccessToken);
