namespace Argon.Features.WebSession;

using System.Text.Json;
using System.Text.Json.Serialization;
using Argon.Features.Jwt;
using Argon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// What the server remembers about a session bound to a device key.
/// </summary>
/// <remarks>
/// <para><b>The durable half moved off the cookie and onto this.</b> A bound session's cookie is cut
/// to minutes, so it is no longer what carries the session across time — the browser is expected to
/// lose it and ask for another. What survives a closed laptop is this record plus the key in the
/// machine's TPM, and a refresh mints a fresh credential from the two. Keeping only the old cookie
/// and re-stamping its expiry would have made a bound session <i>less</i> durable than an unbound
/// one: an hour away from the keyboard and there would be nothing left to re-stamp.</para>
/// </remarks>
/// <summary>
/// What the script-driven endpoints take: the two values DBSC puts in <c>Sec-</c> headers.
/// </summary>
/// <param name="Proof">The signed JWS, or null on the leg that is asking for a challenge.</param>
/// <param name="SessionId">Which binding to refresh. Ignored by registration.</param>
public sealed record DeviceProofRequest(string? Proof, string? SessionId);

public sealed record DeviceBoundSession(
    [property: JsonPropertyName("uid")] Guid UserId,
    [property: JsonPropertyName("sid")] Guid ArgonSessionId,
    [property: JsonPropertyName("mid")] string MachineId,
    [property: JsonPropertyName("scp")] string[] Scopes,
    [property: JsonPropertyName("jwk")] string PublicKeyJwk,
    [property: JsonPropertyName("thb")] string Thumbprint);

/// <summary>
/// Device Bound Session Credentials: the browser proves, per refresh, that it still holds a key it
/// cannot export.
/// </summary>
/// <remarks>
/// <para><b>What this buys and what it does not.</b> It stops a stolen cookie being usable somewhere
/// else: the value is good for minutes and only the device that registered can obtain another. It
/// does nothing about the access token, which is a bearer in a header — that is what the short
/// <see cref="WebSessionOptions.AccessTokenLifetime"/> is for, and the two are meant to be read
/// together.</para>
///
/// <para><b>Chromium only, and silently so.</b> A browser that does not implement this ignores the
/// registration header and sends nothing at all, which is indistinguishable on the wire from one
/// that tried and failed. So the absence of a binding is never reported as "your browser cannot" —
/// only as "this session is not bound". See <see cref="StateAsync"/>.</para>
///
/// <para><b>It cannot work cross-site.</b> The protocol protects a cookie, and a front-end served
/// from another site has no cookie to protect. A development build on <c>localhost</c> talking to a
/// deployed API will never register, and that is correct rather than broken.</para>
/// </remarks>
public static class DeviceBoundSessionEndpoints
{
    public const string RegisterPath = "/auth/web/dbsc/register";
    public const string RefreshPath  = "/auth/web/dbsc/refresh";
    public const string StatePath    = "/auth/web/session/state";

    /// <summary>Offered on the response that opens a session, and ignored by everything but Chromium.</summary>
    public const string RegistrationHeader = "Secure-Session-Registration";

    /// <summary>Carries the browser's signed proof, on both registration and refresh.</summary>
    public const string ResponseHeader = "Secure-Session-Response";

    /// <summary>Names the session being refreshed. <c>Sec-</c> prefixed, so script cannot forge it.</summary>
    public const string SessionIdHeader = "Sec-Secure-Session-Id";

    /// <summary>Our challenge, sent when a refresh arrives without a usable one.</summary>
    public const string ChallengeHeader = "Secure-Session-Challenge";

    /// <summary>Where a browser without DBSC drives registration itself. See the remarks on
    /// <see cref="DeviceRegisterAsync"/> for why the protocol's own path cannot be reused.</summary>
    public const string DeviceRegisterPath = "/auth/web/device/register";

    /// <inheritdoc cref="DeviceRegisterPath"/>
    public const string DeviceRefreshPath = "/auth/web/device/refresh";

    public static WebApplication MapDeviceBoundSessions(this WebApplication app)
    {
        app.MapPost(RegisterPath, RegisterAsync).AllowAnonymous();
        app.MapPost(RefreshPath, RefreshAsync).AllowAnonymous();
        app.MapGet(StatePath, StateAsync).AllowAnonymous();

        app.MapPost(DeviceRegisterPath, DeviceRegisterAsync).AllowAnonymous();
        app.MapPost(DeviceRefreshPath, DeviceRefreshAsync).AllowAnonymous();

        return app;
    }

    // ── offering the binding ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Invites the browser to bind this session, by putting a challenge on the response.
    /// </summary>
    /// <remarks>
    /// Called from the session exchange. Costs one header and one short-lived cache entry; a browser
    /// that cannot do this simply never calls back, and the session carries on exactly as it did
    /// before any of this existed.
    /// </remarks>
    public static async Task OfferAsync(HttpContext http, WebSessionOptions settings,
        IArgonCacheDatabase cache, Guid argonSessionId, CancellationToken ct)
    {
        if (!settings.DeviceBinding.Enabled)
            return;

        var challenge = NewChallenge();

        await cache.StringSetAsync(ChallengeKey(challenge), argonSessionId.ToString(),
            settings.DeviceBinding.ChallengeLifetime, ct);

        // Structured fields: the algorithm list is an inner list, the rest are parameters. Chromium
        // parses this strictly and reports nothing when it does not like it — a stray space inside
        // the quotes is the same observable as a browser that has never heard of DBSC.
        http.Response.Headers[RegistrationHeader] =
            $"(ES256);path=\"{RegisterPath}\";challenge=\"{challenge}\"";
    }

    // ── the two ways in ──────────────────────────────────────────────────────────────────────────

    /// <summary>Chromium's entry point: it sets the headers the protocol names, on its own.</summary>
    private static Task<IResult> RegisterAsync(HttpContext http, ClassicJwtFlow flow,
        UserManagerService users, IArgonCacheDatabase cache, IOptions<WebSessionOptions> options,
        ILoggerFactory loggers, CancellationToken ct)
        => RegisterCoreAsync(http, flow, users, cache, options, loggers, Header(http, ResponseHeader), ct);

    /// <inheritdoc cref="RegisterAsync"/>
    private static Task<IResult> RefreshAsync(HttpContext http, UserManagerService users,
        IArgonCacheDatabase cache, IOptions<WebSessionOptions> options, ILoggerFactory loggers,
        CancellationToken ct)
        => Header(http, SessionIdHeader) is { } named
            ? RefreshCoreAsync(http, users, cache, options, loggers, Unquote(named),
                Header(http, ResponseHeader), asJson: false, ct)
            : Task.FromResult(Results.BadRequest());

    /// <summary>
    /// The same exchange, driven by script, for the browsers that will not drive it themselves.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a second pair of endpoints and not the same ones.</b> DBSC names the session in
    /// <c>Sec-Secure-Session-Id</c> and carries the proof in <c>Secure-Session-Response</c>.
    /// <c>Sec-</c> is a forbidden header prefix — <c>fetch</c> drops such headers without telling
    /// anyone — so script physically cannot speak the protocol as written. These carry the same two
    /// values in a body and hand them to the same verification, so the two paths cannot drift: what
    /// differs is the envelope and nothing else.</para>
    ///
    /// <para><b>What the fallback is worth.</b> The key is generated non-extractable, so
    /// <c>exportKey</c> throws on it and the private half never becomes bytes a script can read or
    /// send anywhere. A cookie copied off this machine cannot be renewed on another one.</para>
    ///
    /// <para><b>And what it is not.</b> It does not stop someone already running code on the page —
    /// they can sign with the key in place — and it does not stop malware reading the browser
    /// profile off disk, because a software key lives in that profile. Resisting that is what a TPM
    /// is for and is exactly the gap DBSC exists to close; this closes the remote half of it.</para>
    /// </remarks>
    private static async Task<IResult> DeviceRegisterAsync(HttpContext http, ClassicJwtFlow flow,
        UserManagerService users, IArgonCacheDatabase cache, IOptions<WebSessionOptions> options,
        ILoggerFactory loggers, DeviceProofRequest? body, CancellationToken ct)
    {
        var settings = options.Value;

        if (!settings.DeviceBinding.Enabled)
            return Results.NotFound();

        // No proof yet: this is the first leg, and it asks for something to sign.
        if (body?.Proof is not { Length: > 0 } proof)
            return await OfferChallengeAsync(http, flow, cache, settings, ct);

        return await RegisterCoreAsync(http, flow, users, cache, options, loggers, proof, ct);
    }

    /// <inheritdoc cref="DeviceRegisterAsync"/>
    private static async Task<IResult> DeviceRefreshAsync(HttpContext http, UserManagerService users,
        IArgonCacheDatabase cache, IOptions<WebSessionOptions> options, ILoggerFactory loggers,
        DeviceProofRequest? body, CancellationToken ct)
    {
        if (body?.SessionId is not { Length: > 0 } session)
            return Results.BadRequest();

        return await RefreshCoreAsync(http, users, cache, options, loggers, session, body.Proof,
            asJson: true, ct);
    }

    /// <summary>
    /// Hands out a challenge bound to the session this browser already holds.
    /// </summary>
    /// <remarks>
    /// Chromium gets its first challenge on the session exchange, in a header it reads for itself.
    /// Script cannot read that header cross-origin, so the driver asks for one — and the session it
    /// is issued against is read from the credential, never from anything the caller said.
    /// </remarks>
    private static async Task<IResult> OfferChallengeAsync(HttpContext http, ClassicJwtFlow flow,
        IArgonCacheDatabase cache, WebSessionOptions settings, CancellationToken ct)
    {
        if (WebSessionCookie.Read(http, settings) is not { } refreshToken)
            return Results.Unauthorized();

        Guid? boundSession;

        try
        {
            flow.ValidateRefreshTokenSession(refreshToken, http.GetMachineId(), out boundSession, out _, out _);
        }
        catch (Exception)
        {
            return Results.Unauthorized();
        }

        if (boundSession is not { } sessionId)
            return Results.Unauthorized();

        var challenge = NewChallenge();

        await cache.StringSetAsync(ChallengeKey(challenge), sessionId.ToString(),
            settings.DeviceBinding.ChallengeLifetime, ct);

        return Results.Json(new { challenge });
    }

    // ── registration ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts a freshly made device key and binds this browser's session to it.
    /// </summary>
    /// <remarks>
    /// <para>The session being bound is the one this request already carries — the cookie is sent,
    /// because DBSC only ever runs same-site. So the user is not re-authenticated here: the proof
    /// says "the machine holding this session also holds this key", which is exactly the claim the
    /// binding records.</para>
    ///
    /// <para>The challenge is deleted before anything is issued. A proof is worth one registration,
    /// and a replayed one must find nothing to match against rather than a second session.</para>
    /// </remarks>
    private static async Task<IResult> RegisterCoreAsync(
        HttpContext                 http,
        ClassicJwtFlow              flow,
        UserManagerService          users,
        IArgonCacheDatabase         cache,
        IOptions<WebSessionOptions> options,
        ILoggerFactory              loggers,
        string?                     proof,
        CancellationToken           ct)
    {
        var settings = options.Value;
        var log      = loggers.CreateLogger(typeof(DeviceBoundSessionEndpoints));

        if (!settings.DeviceBinding.Enabled)
            return Results.NotFound();

        if (proof is null)
            return Results.BadRequest();

        if (ChallengeOf(proof) is not { } challenge)
            return Results.BadRequest();

        var key   = ChallengeKey(challenge);
        var owner = await cache.StringGetAsync(key, ct);

        await cache.KeyDeleteAsync(key, ct);

        if (owner is null || !Guid.TryParse(owner, out var offeredTo))
        {
            log.LogWarning("A device binding presented a challenge this server did not issue, or issued too long ago");
            return Results.BadRequest();
        }

        if (DeviceBoundProof.VerifyRegistration(proof, challenge) is not { } identity)
        {
            log.LogWarning("A device binding proof for session {SessionId} did not verify", offeredTo);
            return Results.BadRequest();
        }

        // The session this browser already holds, read from the credential rather than from anything
        // the caller said about itself. ReadForDeviceBinding rather than Read: this request has no
        // fetch metadata because no page made it — see that method for why the check is both
        // inapplicable and unnecessary here.
        if (WebSessionCookie.ReadForDeviceBinding(http, settings) is not { } refreshToken)
        {
            // Said out loud because this used to be the one silent 401 on the path: a binding that
            // fails here leaves nothing in the log and a browser retrying for ever.
            log.LogWarning("A device binding for session {SessionId} arrived without a session cookie", offeredTo);
            return Results.Unauthorized();
        }

        Guid   userId;
        Guid?  boundSession;
        string machineId;
        IReadOnlyList<string> scopes;

        try
        {
            machineId = http.GetMachineId();
            (userId, scopes) = flow.ValidateRefreshTokenSession(refreshToken, machineId, out boundSession, out _, out _);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "A device binding arrived with a session credential that does not hold");
            return Results.Unauthorized();
        }

        if (boundSession != offeredTo)
        {
            // The challenge was issued to one session and presented by another. Nothing good produces
            // this, and binding the key to the wrong session would move a device onto it.
            log.LogWarning("A device binding challenge issued for {Offered} was presented by {Presented}",
                offeredTo, boundSession);
            return Results.BadRequest();
        }

        var record = new DeviceBoundSession(userId, boundSession.Value, machineId, [.. scopes],
            identity.PublicKeyJwk, identity.Thumbprint);

        var dbscSessionId = ArgonId.New().ToString();

        await StoreAsync(cache, settings, dbscSessionId, record, ct);

        log.LogInformation("Bound web session {SessionId} to device key {Thumbprint}",
            record.ArgonSessionId, record.Thumbprint);

        return await IssueAsync(http, users, cache, settings, dbscSessionId, record, ct);
    }

    // ── refresh ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-issues the short-lived cookie, for a browser that can still sign for the bound key.
    /// </summary>
    /// <remarks>
    /// Two legs by design. The first arrives with no usable proof and is answered with a challenge;
    /// the second carries the signature. A challenge is spent on use, so a captured proof buys one
    /// refresh that has already happened.
    /// </remarks>
    private static async Task<IResult> RefreshCoreAsync(
        HttpContext                 http,
        UserManagerService          users,
        IArgonCacheDatabase         cache,
        IOptions<WebSessionOptions> options,
        ILoggerFactory              loggers,
        string                      session,
        string?                     proof,
        bool                        asJson,
        CancellationToken           ct)
    {
        var settings = options.Value;
        var log      = loggers.CreateLogger(typeof(DeviceBoundSessionEndpoints));

        if (!settings.DeviceBinding.Enabled)
            return Results.NotFound();

        if (await LoadAsync(cache, session, ct) is not { } record)
        {
            // Nothing to refresh: the binding expired or was ended. Answering "continue: false" is
            // how the protocol says a session is over, and it stops the browser asking again.
            log.LogDebug("A refresh named a device binding this server does not hold");
            return Results.Json(new { @continue = false }, statusCode: StatusCodes.Status200OK);
        }

        var pendingAt = PendingChallengeKey(session);

        if (proof is null)
            return await ChallengeAsync(http, cache, settings, session, pendingAt, asJson, ct);

        var expected = await cache.StringGetAsync(pendingAt, ct);

        if (expected is null)
            return await ChallengeAsync(http, cache, settings, session, pendingAt, asJson, ct);

        await cache.KeyDeleteAsync(pendingAt, ct);

        if (!DeviceBoundProof.VerifyRefresh(proof, record.PublicKeyJwk, expected))
        {
            log.LogWarning("A refresh for device binding {Thumbprint} did not verify", record.Thumbprint);
            return await ChallengeAsync(http, cache, settings, session, pendingAt, asJson, ct);
        }

        // Seen recently, so keep it: a binding is as long-lived as the session it carries, and its
        // clock restarts every time the device proves itself.
        await StoreAsync(cache, settings, session, record, ct);

        return await IssueAsync(http, users, cache, settings, session, record, ct);
    }

    /// <param name="asJson">
    /// Whether to answer in the body rather than in the <c>Secure-Session-Challenge</c> header.
    /// <para>The header is the protocol, and Chromium reads it. Script cannot: the response is
    /// cross-origin and nothing exposes that header to it, so the driver in the browsers without
    /// DBSC is handed the same challenge the only way it can receive one.</para>
    /// </param>
    private static async Task<IResult> ChallengeAsync(HttpContext http, IArgonCacheDatabase cache,
        WebSessionOptions settings, string session, string pendingKey, bool asJson, CancellationToken ct)
    {
        var challenge = NewChallenge();

        await cache.StringSetAsync(pendingKey, challenge, settings.DeviceBinding.ChallengeLifetime, ct);

        if (asJson)
            return Results.Json(new { challenge, sessionId = session },
                statusCode: StatusCodes.Status401Unauthorized);

        http.Response.Headers[ChallengeHeader] = $"\"{challenge}\";id=\"{session}\"";

        return Results.Unauthorized();
    }

    // ── issuing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mints a credential for a proven device and writes it into the short-lived cookie.
    /// </summary>
    /// <remarks>
    /// A fresh refresh token rather than the one that arrived, on both legs. The old cookie may be
    /// gone entirely — that is the ordinary case after a machine has been asleep — so there is
    /// nothing to re-stamp; and minting from the stored record is what lets this endpoint be the
    /// thing that keeps a bound session alive.
    /// </remarks>
    private static async Task<IResult> IssueAsync(HttpContext http, UserManagerService users,
        IArgonCacheDatabase cache, WebSessionOptions settings, string dbscSessionId,
        DeviceBoundSession record, CancellationToken ct)
    {
        var issued = await users.GenerateJwt(record.UserId, record.MachineId, record.Scopes,
            record.ArgonSessionId, accessLifetime: settings.AccessTokenLifetime);

        WebSessionCookie.Write(http, settings, issued.refreshToken!, settings.DeviceBinding.BoundCookieLifetime);
        WebAccessCookie.Write(http, settings, issued.token);

        // So the state endpoint can answer without the browser telling it anything it chose itself.
        await cache.StringSetAsync(BoundKey(record.ArgonSessionId), dbscSessionId, settings.Lifetime, ct);

        var origin = $"{http.Request.Scheme}://{http.Request.Host}";

        return Results.Json(new DeviceBoundSessionConfig(
            dbscSessionId,
            RefreshPath,
            new DeviceBoundScope(origin, IncludeSite: false),
            [
                new DeviceBoundCredential("cookie", settings.CookieName, "Path=/; Secure; HttpOnly"),
                // The credential that actually authorises calls. Binding the refresh cookie alone
                // would leave the one spent on every request unprotected — which is what the bearer
                // header was, and the reason this cookie exists.
                new DeviceBoundCredential("cookie", settings.AccessCookieName, "Path=/; Secure; HttpOnly"),
            ]));
    }

    // ── what the page is allowed to know ─────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this session is bound to a device.
    /// </summary>
    /// <remarks>
    /// <para>The only check there is. DBSC defines no JavaScript API and puts nothing on an ordinary
    /// request once a session is bound, so a page cannot find this out for itself — the server knows
    /// because registration either arrived or did not.</para>
    ///
    /// <para>And the negative means less than it looks: no DBSC, no usable key, a policy, blocked
    /// third-party cookies and a cross-site front-end all produce the same silence. Hence
    /// <c>bound: false</c> and nothing about why.</para>
    /// </remarks>
    private static async Task<IResult> StateAsync(
        HttpContext                 http,
        IArgonCacheDatabase         cache,
        IOptions<WebSessionOptions> options,
        CancellationToken           ct)
    {
        var settings = options.Value;

        if (!http.TryGetSessionId(out var sessionId) || sessionId == Guid.Empty || sessionId == Guid.AllBitsSet)
            return Results.Json(new { bound = false, offered = settings.DeviceBinding.Enabled });

        var bound = await cache.StringGetAsync(BoundKey(sessionId), ct) is not null;

        return Results.Json(new { bound, offered = settings.DeviceBinding.Enabled });
    }

    /// <summary>
    /// Ends the binding on a session that is over.
    /// </summary>
    /// <remarks>
    /// <para>Deleting the record is also what ends it for the browser: the next refresh finds
    /// nothing and is answered with <c>continue: false</c>, which is the protocol's way of saying a
    /// session is finished and what stops the browser asking again.</para>
    ///
    /// <para>Left behind, the binding outlives the session it named — the browser keeps showing a
    /// device session that can never succeed, and the next sign-in registers a second one beside
    /// it. Two for one browser, one of them dead.</para>
    /// </remarks>
    public static async Task EndAsync(IArgonCacheDatabase cache, Guid argonSessionId, CancellationToken ct)
    {
        var boundAt = BoundKey(argonSessionId);

        // The record is stored under the binding's own id, which only this pointer knows.
        if (await cache.StringGetAsync(boundAt, ct) is { } dbscSessionId)
            await cache.KeyDeleteAsync(SessionKey(dbscSessionId), ct);

        await cache.KeyDeleteAsync(boundAt, ct);
    }

    // ── storage ──────────────────────────────────────────────────────────────────────────────────

    private static Task StoreAsync(IArgonCacheDatabase cache, WebSessionOptions settings,
        string dbscSessionId, DeviceBoundSession record, CancellationToken ct)
        => cache.StringSetAsync(SessionKey(dbscSessionId), JsonSerializer.Serialize(record), settings.Lifetime, ct);

    private static async Task<DeviceBoundSession?> LoadAsync(IArgonCacheDatabase cache, string dbscSessionId,
        CancellationToken ct)
    {
        if (await cache.StringGetAsync(SessionKey(dbscSessionId), ct) is not { } stored)
            return null;

        try
        {
            return JsonSerializer.Deserialize<DeviceBoundSession>(stored);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ChallengeKey(string challenge) => $"dbsc:challenge:{challenge}";
    private static string PendingChallengeKey(string session) => $"dbsc:pending:{session}";
    private static string SessionKey(string session) => $"dbsc:session:{session}";
    private static string BoundKey(Guid argonSessionId) => $"dbsc:bound:{argonSessionId}";

    // ── wire helpers ─────────────────────────────────────────────────────────────────────────────

    private static string NewChallenge()
        => DeviceBoundProof.Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string? Header(HttpContext http, string name)
        => http.Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Unquote(value.ToString())
            : null;

    /// <summary>Structured-field strings arrive quoted; everything downstream wants the value.</summary>
    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>The challenge a proof answers, read from its own payload without trusting it.</summary>
    private static string? ChallengeOf(string jwt)
    {
        var parts = jwt.Split('.');

        if (parts.Length != 3)
            return null;

        try
        {
            return JsonDocument.Parse(DeviceBoundProof.FromBase64Url(parts[1]))
                               .RootElement.TryGetProperty("jti", out var jti)
                ? jti.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
