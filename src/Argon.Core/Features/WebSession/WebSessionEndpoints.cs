namespace Argon.Features.WebSession;

using Argon.Core.Features.Transport;
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
/// audience allowlist in <see cref="WebSessionOptions.TrustedApplications"/>. Applications outside that
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
        app.MapDeviceBoundSessions();

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
        IArgonCacheDatabase         cache,
        IOptions<WebSessionOptions> options,
        CancellationToken           ct)
    {
        if (BearerToken(http) is not { } token)
            return Results.Unauthorized();

        if (await validator.ValidateAsync(token, ct) is not { } identity)
            return Results.Unauthorized();

        var settings = options.Value;
        var appId    = identity.ApplicationId;

        // One id for both halves: it is the scid the device cookie carries and the sid signed into
        // the refresh token, so the tombstone written at sign-out ends the session on both paths.
        var sessionId = ArgonId.New();
        var machineId = ArgonSecureCookie.Issue(http, settings, appId, sessionId);

        // Minutes, not the deployment's default days: see WebSessionOptions.AccessTokenLifetime for
        // why a browser's token is the cheap half and the cookie is the durable one.
        var issued = await users.GenerateJwt(identity.UserId, machineId, SessionScopes, sessionId,
            accessLifetime: settings.AccessTokenLifetime);

        WebSessionCookie.Write(http, settings, issued.refreshToken!);

        // The credential every subsequent call authorises with, out of reach of script. The body
        // still carries it as well: a browser running an older bundle authorises with the bearer,
        // and native clients have no cookie jar at all.
        WebAccessCookie.Write(http, settings, issued.token);

        // Asks the browser to bind this session to a device key. Chromium answers on its own; every
        // other browser ignores the header, and the session it just got is unaffected either way.
        await DeviceBoundSessionEndpoints.OfferAsync(http, settings, cache, sessionId, ct);

        return Results.Ok(new WebSessionResponse(issued.token, sessionId));
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
        HttpContext                   http,
        ClassicJwtFlow                flow,
        IArgonCacheDatabase           cache,
        ISessionRevocationBroadcaster revocations,
        IOptions<WebSessionOptions>   options,
        ILoggerFactory                loggers,
        CancellationToken             ct)
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

                    // The device binding ends with the session it was made for. See EndAsync: a
                    // binding left behind is one the browser keeps and can never use again.
                    await DeviceBoundSessionEndpoints.EndAsync(cache, id, ct);

                    // Both halves of the identity, as everywhere else a session is ended. The cookie's
                    // sid is the server-minted credential one, which stops the refresh; the presence
                    // sid is what the hub gate and the interceptor look up, and without it the tab
                    // that just signed out kept its realtime feed until the socket happened to drop.
                    // See SessionRevocation's remarks for why one is never enough.
                    // Empty is "nothing was carried" and AllBitsSet is what a development host hands
                    // every caller that sent no session header — neither names a device, and
                    // tombstoning either would sign out everyone who shares the placeholder.
                    Guid? presence = http.TryGetSessionId(out var presenceSessionId)
                                  && presenceSessionId != Guid.Empty && presenceSessionId != Guid.AllBitsSet
                        ? presenceSessionId
                        : null;

                    if (presence is { } row)
                        await cache.SetAddAsync(key, row.ToString(), ct);

                    // And the tab's own socket, which the tombstone alone does not touch. The hub
                    // authenticated it once, from a ticket minted before any of this existed, and a
                    // tab that signs out and is left open makes no further calls — so nothing on the
                    // realtime path would ever ask. The nodes holding it close it on this signal; see
                    // AppHub's remarks for the two layers underneath. Never throws.
                    await revocations.PublishAsync(userId, presence, [id], ct);

                    // EXPIRE, not GETEX — KeyExpireAsync is StringGetSetExpiry underneath and answers
                    // WRONGTYPE against the set just written. Here the throw was swallowed by the catch
                    // below, so web logout worked but left the tombstone with no TTL at all; the same
                    // pair in SecurityGrain.EndSessionAsync failed loudly instead (S5).
                    await cache.UpdateStringExpirationAsync(key, SessionRevocation.Window, ct);
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
        WebAccessCookie.Clear(http, settings);

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
/// <param name="SessionId">
/// The <c>scid</c> this session is filed under, handed back so the page can present it.
/// <para><b>Because a cross-site tab cannot read the cookie that carries it.</b> The device cookie is
/// written on the API's host with <c>SameSite=Lax</c>, so a front-end served from another site never
/// sends it back — and <c>GetSessionId</c> then finds nothing, which every Ion call fails on. The
/// page echoes this in <c>X-Sec-Ref</c> instead, the channel an installed client has always had.</para>
/// <para>Safe to hand over, and no more than was already true: the session id is a <i>label</i>. Every
/// client writes its own into the cookie, nothing is authorised on it, and revocation keys on the
/// <c>sid</c> claim inside the signed token — which the caller cannot choose. See
/// <c>HttpContextExtensions.GetSessionId</c> for the whole argument.</para>
/// </param>
public sealed record WebSessionResponse(string AccessToken, Guid SessionId);
