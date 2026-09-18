namespace ArgonSharedLogicTest;

using Argon.Features.WebSession;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// That a browser authorises out of its cookie, and only where a browser could have meant to.
/// </summary>
/// <remarks>
/// <para><b>Why this is worth pinning.</b> Moving the access token out of the <c>Authorization</c>
/// header and into a cookie trades one exposure for another: a header is only ever sent by code
/// that decided to send it, while a cookie is sent by the browser on any request that reaches the
/// host — including one another site caused. <c>SameSite</c> stops most of that before it is sent,
/// but a deployment that has had to relax it, or a same-site subdomain, gets past it. The
/// <c>Sec-Fetch-Site</c> check is what is left, and nothing else in the suite would notice if it
/// were dropped: every test would still pass and every same-origin call would still work.</para>
///
/// <para>The bearer-wins rule is here for a different reason. Native clients have no cookie jar and
/// a browser on an older bundle still sends a header, so both channels stay open — and if the
/// cookie ever won, a stale one would silently override the fresh token a caller just sent.</para>
/// </remarks>
[TestFixture]
public class WebAccessCookieTests
{
    private static WebSessionOptions Settings() => new();

    /// <param name="fetchSite">The <c>Sec-Fetch-Site</c> value, or null to omit the header.</param>
    private static HttpContext Request(string? cookie = null, string? bearer = null, string? fetchSite = "same-origin")
    {
        var options = Settings();
        var http    = new DefaultHttpContext();

        // WebAccessToken resolves the options per request rather than taking them as an argument.
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<WebSessionOptions>>(new OptionsWrapper<WebSessionOptions>(options));
        http.RequestServices = services.BuildServiceProvider();

        if (cookie is not null)
            http.Request.Headers["Cookie"] = $"{options.AccessCookieName}={cookie}";
        if (bearer is not null)
            http.Request.Headers["Authorization"] = $"Bearer {bearer}";
        if (fetchSite is not null)
            http.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        return http;
    }

    [Test]
    public void ReadsTheCookieWhenNoHeaderWasSent()
        => Assert.That(WebAccessToken.Read(Request(cookie: "cookie-token")), Is.EqualTo("cookie-token"));

    [Test]
    public void PrefersTheHeaderOverTheCookie()
        => Assert.That(WebAccessToken.Read(Request(cookie: "stale", bearer: "fresh")), Is.EqualTo("fresh"));

    [Test]
    public void ReadsNothingFromARequestThatCarriesNeither()
        => Assert.That(WebAccessToken.Read(Request()), Is.Null);

    /// <summary>The case the check exists for: another site caused this request.</summary>
    [Test]
    public void IgnoresTheCookieOnACrossSiteRequest()
        => Assert.That(WebAccessToken.Read(Request(cookie: "cookie-token", fetchSite: "cross-site")), Is.Null);

    /// <remarks>
    /// No <c>Sec-Fetch-Site</c> means this is not a browser, and a non-browser has no business
    /// authorising out of a cookie — it can send the token it holds.
    /// </remarks>
    [Test]
    public void IgnoresTheCookieWhenTheBrowserSaidNothingAboutWhoAskedForIt()
        => Assert.That(WebAccessToken.Read(Request(cookie: "cookie-token", fetchSite: null)), Is.Null);

    /// <remarks>
    /// <c>none</c> is a user-initiated navigation — a typed address or a bookmark — which is never
    /// how an RPC arrives. The refresh cookie tolerates it because a sign-in can begin with one.
    /// </remarks>
    [Test]
    public void IgnoresTheCookieOnAUserInitiatedNavigation()
        => Assert.That(WebAccessToken.Read(Request(cookie: "cookie-token", fetchSite: "none")), Is.Null);

    [Test]
    public void StillAcceptsAHeaderOnACrossSiteRequest()
        => Assert.That(WebAccessToken.Read(Request(bearer: "native", fetchSite: "cross-site")),
            Is.EqualTo("native"));

    /// <summary>The two cookies must not collide, or each sign-in would end the session it just made.</summary>
    [Test]
    public void TheAccessCookieIsNotTheRefreshCookie()
        => Assert.That(Settings().AccessCookieName, Is.Not.EqualTo(Settings().CookieName));

    [Test]
    public void TheAccessCookieIsHostScoped()
        => Assert.That(Settings().AccessCookieName, Does.StartWith("__Host-"));
}
