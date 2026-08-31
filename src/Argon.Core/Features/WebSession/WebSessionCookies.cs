namespace Argon.Features.WebSession;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

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

    private static string? ReadMachineId(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var cookie) || string.IsNullOrWhiteSpace(cookie))
            return null;

        return QueryHelpers.ParseQuery(cookie).TryGetValue("colt", out var colt) && !string.IsNullOrWhiteSpace(colt)
            ? colt.ToString()
            : null;
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
    public static void Write(HttpContext http, WebSessionOptions options, string refreshToken)
        => http.Response.Cookies.Append(options.CookieName, refreshToken, Attributes(options,
            DateTimeOffset.UtcNow + options.Lifetime));

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
