namespace Argon.Features.WebSession;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// The device half of a browser session: the <c>ArgonSecure</c> cookie, written by the server.
/// </summary>
/// <remarks>
/// <para>The same cookie an installed client writes for itself, in the same format, because
/// everything downstream already reads it — <c>GetMachineId</c>, <c>GetSessionId</c> and
/// <c>GetAppId</c> in <c>HttpContextExtensions</c>, and through them the <c>mh</c> binding on every
/// token, session revocation, and device history. Inventing a second channel for browsers would mean
/// teaching all of that about it.</para>
///
/// <para><b>What is in it, and what is not.</b> A browser has no attested hardware: there is no
/// <c>dev</c> proof, so tokens minted for a web session carry no <c>cnf</c> and the access tokens
/// refreshed from them carry no <c>did</c> — per-request hardware bans do not reach web sessions,
/// and only the per-user and per-session tombstones do. There is no <c>hwv</c> either, and that is a
/// choice rather than an omission: the signals a browser can offer collide freely between different
/// people, and <c>DeviceIdentityService</c> already treats an empty vector as an unknown device
/// while a wrong one would attribute strangers to each other.</para>
/// </remarks>
public static class ArgonSecureCookie
{
    public const string CookieName = "ArgonSecure";

    /// <summary>
    /// Writes the cookie and returns the machine id the session must be bound to.
    /// </summary>
    /// <remarks>
    /// An existing machine id is kept rather than replaced, because tokens are bound to it: minting a
    /// new one on every sign-in would invalidate a session the same browser still holds in another
    /// tab. It only has to be stable, not meaningful — which is why a value written by an older
    /// issuer is reused as-is even where that issuer left it unescaped.
    /// </remarks>
    public static string Issue(HttpContext http, WebSessionOptions options, string appId, Guid sessionId)
    {
        var machineId = ReadMachineId(http) ?? NewMachineId();

        // Same shape the native clients write: a query string, read back with ParseQuery. hwid is
        // read by nothing and is here only so a cookie's origin is obvious in a support session.
        var value = string.Join('&',
            "hwid=web",
            $"scid={sessionId}",
            $"colt={Uri.EscapeDataString(machineId)}",
            $"ner={Uri.EscapeDataString(appId)}");

        http.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            Domain      = string.IsNullOrWhiteSpace(options.DeviceCookieDomain) ? null : options.DeviceCookieDomain,
            Path        = "/",
            HttpOnly    = true,
            Secure      = true,
            SameSite    = options.SameSite,
            Expires     = DateTimeOffset.UtcNow + options.DeviceLifetime,
            IsEssential = true
        });

        return machineId;
    }

    /// <summary>
    /// The machine identity this browser already presents, by whichever of the two channels it has.
    /// </summary>
    /// <remarks>
    /// <para><b>The header is not a fallback for old clients here; it is the only channel a tab
    /// served from another site has.</b> The cookie is written on the API's host with
    /// <c>SameSite=Lax</c>, so a page on a different site never sends it back — and the machine id
    /// is what <c>mh</c> on every access token is checked against. Read from the cookie alone, such
    /// a browser was issued a fresh identity on every exchange, bound its token to an id it could
    /// never present again, and had the very next call fail with <c>MachineId is not defined</c>.
    /// The sign-in appeared to work and nothing after it did.</para>
    ///
    /// <para>The same precedence <c>GetMachineId</c> reads with, so the id the session is bound to
    /// and the id the request pipeline resolves can never disagree. <c>X-Sec-Carry</c> is the
    /// spelling a browser can actually use: <c>Sec-</c> is a forbidden header prefix in fetch, so a
    /// page setting <c>Sec-Carry</c> has it dropped before the request leaves — which is why both
    /// names exist and why only one of them is reachable from script.</para>
    ///
    /// <para>Trusting a caller-supplied value costs nothing that was not already the case: an
    /// installed client writes its own <c>ArgonSecure</c> cookie, so this identity has always been
    /// the caller's to choose. It is a label for a device, not a claim to be one — the unforgeable
    /// half is the <c>sid</c> inside the token.</para>
    /// </remarks>
    private static string? ReadMachineId(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(CookieName, out var cookie) && !string.IsNullOrWhiteSpace(cookie)
         && QueryHelpers.ParseQuery(cookie).TryGetValue("colt", out var colt) && !string.IsNullOrWhiteSpace(colt))
            return colt.ToString();

        foreach (var header in (ReadOnlySpan<string>)["Sec-Carry", "X-Sec-Carry"])
            if (http.Request.Headers.TryGetValue(header, out var carried) && !string.IsNullOrWhiteSpace(carried))
                return carried.ToString();

        return null;
    }

    /// <summary>
    /// A fresh browser identity: 128 random bits, in the alphabet the cookie can carry unescaped.
    /// </summary>
    /// <remarks>
    /// Not a hardware identifier and it must not be read as one — it identifies a browser profile,
    /// and clearing cookies produces a new one. The name <c>colt</c> is the field an installed client
    /// fills from real hardware; what is shared is the slot, not the strength of the claim.
    /// </remarks>
    private static string NewMachineId()
        => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// The credential half: the refresh token, in a cookie the page cannot read.
/// </summary>
/// <remarks>
/// <para>Only the refresh token. The access token stays in the <c>Authorization</c> header and in the
/// page's memory, and that is the point of the split: a header cannot be attached to a cross-site
/// request without a preflight, so every authenticated call keeps its forgery resistance, while the
/// one long-lived credential moves out of reach of any script on the page.</para>
///
/// <para>Host-scoped by way of the <c>__Host-</c> prefix, so it belongs to the API host alone.</para>
/// </remarks>
public static class WebSessionCookie
{
    /// <param name="lifetime">
    /// Overrides <see cref="WebSessionOptions.Lifetime"/>, for a session bound to a device key.
    /// <para>Those get minutes rather than a month, and that is the point of binding: the cookie
    /// stops being what carries the session and becomes something the browser is expected to lose
    /// and replace, by proving it still holds the key. An unbound session keeps the long cookie,
    /// because for it the cookie is still the only thing that survives.</para>
    /// </param>
    public static void Write(HttpContext http, WebSessionOptions options, string refreshToken,
        TimeSpan? lifetime = null)
        => http.Response.Cookies.Append(options.CookieName, refreshToken, Attributes(options,
            DateTimeOffset.UtcNow + (lifetime ?? options.Lifetime)));

    /// <remarks>
    /// Deleted with the same attributes it was written with. A browser matches a deletion by name,
    /// path and domain, so a <c>Delete</c> that disagrees on any of them leaves the cookie in place
    /// and the user signed in.
    /// </remarks>
    public static void Clear(HttpContext http, WebSessionOptions options)
        => http.Response.Cookies.Delete(options.CookieName, Attributes(options, null));

    /// <summary>
    /// The refresh token this browser holds, if the request is one a browser could not have been
    /// tricked into making.
    /// </summary>
    /// <remarks>
    /// <para><c>Sec-Fetch-Site</c> rather than <c>Origin</c>: it is set by the browser, cannot be
    /// written from script, and says precisely what is being asked here — whether the request was
    /// started by our own site or by somebody else's. An <c>Origin</c> allowlist answers a narrower
    /// question and would still have to be kept in step with the CORS list.</para>
    ///
    /// <para>Belt and braces over <c>SameSite</c>, which already stops the cross-site case before the
    /// request is sent — except where a deployment has had to relax it to <c>None</c> to serve a
    /// front-end from somewhere else, which is exactly when this check is the only one left.</para>
    ///
    /// <para>A request with no <c>Sec-Fetch-Site</c> at all is not a browser, and a non-browser has
    /// no reason to be authenticating out of a cookie — it can send the token it holds.</para>
    /// </remarks>
    public static string? Read(HttpContext http, WebSessionOptions options)
    {
        if (!http.Request.Headers.TryGetValue("Sec-Fetch-Site", out var site))
            return null;

        var value = site.ToString();

        if (value is not ("same-origin" or "same-site" or "none"))
            return null;

        return http.Request.Cookies.TryGetValue(options.CookieName, out var token) && !string.IsNullOrWhiteSpace(token)
            ? token
            : null;
    }

    private static CookieOptions Attributes(WebSessionOptions options, DateTimeOffset? expires)
        => new()
        {
            // No Domain, and Path must be "/": both are conditions of the __Host- prefix, and a
            // browser silently drops a cookie that claims the prefix without meeting them.
            Path        = "/",
            HttpOnly    = true,
            Secure      = true,
            SameSite    = options.SameSite,
            Expires     = expires,
            IsEssential = true
        };
}

/// <summary>
/// The access token, for browsers, as a cookie rather than a bearer header.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Every Ion call authorises with <c>Authorization: Bearer</c>, which
/// on a browser means the credential for every call is a string sitting in JavaScript. Anything
/// that runs on the page can read it, and once copied it works from any machine until it expires.
/// Shortening its life caps the damage but does not change its nature.</para>
///
/// <para>A cookie is a different kind of thing: <c>HttpOnly</c> puts it out of reach of script,
/// <c>__Host-</c> confines it to this host, and <c>SameSite</c> keeps other sites from spending it.
/// It is also, unlike a header, something a device-bound session can protect — see
/// <see cref="DeviceBoundSessionEndpoints"/>, which today guards a refresh cookie while the
/// credential that actually authorises calls travels beside it in the clear.</para>
///
/// <para><b>Both channels are read, bearer first.</b> Native clients have no cookie jar and must
/// keep sending the header, and a browser holding an older bundle is still sending one too. The
/// cookie is what a current web build relies on; the header is what everything else uses.</para>
/// </remarks>
public static class WebAccessCookie
{
    /// <remarks>
    /// Expires with the token it carries. The two must not disagree: a cookie outliving its token
    /// buys a round trip that fails, and a cookie dying first ends a session that is still valid.
    /// </remarks>
    public static void Write(HttpContext http, WebSessionOptions options, string accessToken,
        TimeSpan? lifetime = null)
        => http.Response.Cookies.Append(options.AccessCookieName, accessToken,
            Attributes(options, DateTimeOffset.UtcNow + (lifetime ?? options.AccessTokenLifetime)));

    /// <inheritdoc cref="WebSessionCookie.Clear"/>
    public static void Clear(HttpContext http, WebSessionOptions options)
        => http.Response.Cookies.Delete(options.AccessCookieName, Attributes(options, null));

    /// <summary>
    /// The access token this browser holds, if the request is one a browser could not have been
    /// tricked into making.
    /// </summary>
    /// <remarks>
    /// <para>Same <c>Sec-Fetch-Site</c> reasoning as <see cref="WebSessionCookie.Read"/>, with one
    /// difference: <c>none</c> is not accepted here. That value means a user-initiated navigation —
    /// a typed address, a bookmark — which is never how an RPC arrives. The refresh endpoint has to
    /// tolerate it because a sign-in can begin with one; an API call that claims to be one is
    /// either confused or lying.</para>
    /// </remarks>
    public static string? Read(HttpContext http, WebSessionOptions options)
    {
        if (!http.Request.Headers.TryGetValue("Sec-Fetch-Site", out var site))
            return null;

        if (site.ToString() is not ("same-origin" or "same-site"))
            return null;

        return http.Request.Cookies.TryGetValue(options.AccessCookieName, out var token)
            && !string.IsNullOrWhiteSpace(token)
                ? token
                : null;
    }

    private static CookieOptions Attributes(WebSessionOptions options, DateTimeOffset? expires)
        => new()
        {
            // __Host- conditions, as on the refresh cookie: no Domain, rooted at "/", Secure.
            Path        = "/",
            HttpOnly    = true,
            Secure      = true,
            SameSite    = options.SameSite,
            Expires     = expires,
            IsEssential = true
        };
}

/// <summary>
/// The access token a request carries, by whichever channel it has.
/// </summary>
/// <remarks>
/// <para>One place, because three of them ask: the Ion interceptor, the console interceptor, and
/// the realtime hub. They authorise the same credential and must agree about where it can come
/// from, or a session works over RPC and not over the socket.</para>
///
/// <para><b>Bearer wins.</b> A caller that went to the trouble of sending a header meant that
/// token: native clients have no cookie jar, and a browser on an older bundle still sends one.
/// Preferring the cookie would let a stale cookie quietly override a fresh header.</para>
/// </remarks>
public static class WebAccessToken
{
    private const string BearerPrefix = "Bearer ";

    /// <returns>The token, or <c>null</c> when the request carries none this server will accept.</returns>
    public static string? Read(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue("Authorization", out var auth)
            && auth.ToString() is { Length: > 0 } value
            && value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && value[BearerPrefix.Length..].Trim() is { Length: > 0 } bearer)
            return bearer;

        // Resolved per request rather than injected, so this stays a one-line change at each call
        // site. GetService, not Required: a role with no web sessions configured has no options and
        // simply has no cookie to read.
        return http.RequestServices.GetService<IOptions<WebSessionOptions>>()?.Value is { } options
            ? WebAccessCookie.Read(http, options)
            : null;
    }
}
