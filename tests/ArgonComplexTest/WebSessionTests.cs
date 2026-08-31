namespace ArgonComplexTest;

using System.Net;
using System.Net.WebSockets;
using Argon.Features;
using Argon.Features.Jwt;
using Argon.Features.WebSession;
using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

/// <summary>
/// The browser session: a refresh token in a cookie the page cannot read, and a device identity the
/// server hands out because a browser has none of its own.
/// </summary>
/// <remarks>
/// <para>Two halves, and they fail differently. The credential half is checked against a live server,
/// because what it has to prove is that a request arriving with nothing but a cookie mints an access
/// token — and that the same request started by another site does not.</para>
///
/// <para>The device half is checked in isolation, because its failure mode is silent. The cookie is a
/// hand-built query string that four separate readers parse, and a browser that rejects it — for a
/// missing attribute the <c>__Host-</c> prefix demands — is indistinguishable from a user who never
/// signed in: no error, no log line, just a login screen again.</para>
/// </remarks>
[TestFixture]
public class WebSessionTests : TestBase
{
    private static readonly WebSessionOptions Options = new()
    {
        CookieName         = "__Host-ArgonAuth",
        Lifetime           = TimeSpan.FromDays(30),
        SameSite           = SameSiteMode.Lax,
        DeviceCookieDomain = ".argon.gl",
        DeviceLifetime     = TimeSpan.FromDays(365)
    };

    // ── the credential half, against a live server ──────────────────────────────────────────────

    /// <summary>
    /// A browser holding only the cookie gets a new access token.
    /// </summary>
    /// <remarks>
    /// The refresh token is never passed as an argument, which is the whole point: for the web client
    /// it lives in an <c>HttpOnly</c> cookie, so the page has nothing to pass. Everything else on the
    /// refresh path — the machine binding, revocation, the lockdown check — has to keep working when
    /// the token arrives this way, which is why this goes through the real call rather than the
    /// cookie reader on its own.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_browser_refreshes_from_its_cookie_without_ever_holding_the_token(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var session = await CreateSessionAsync(ct);
        var browser = Browser();

        // Minted for the browser's own machine id: the refresh path checks the token's mh claim
        // against the caller's machine, so a token minted for anyone else would be refused for that
        // reason instead of the one under test.
        browser.Interceptor.Cookie = $"{Options.CookieName}={RefreshTokenFor(scope, session.UserId, browser.Interceptor.MachineId)}";

        var result = await browser.Identity.GetMyAuthorization("", null, ct);

        Assert.That(result, Is.InstanceOf<GoodAuthStatus>(),
            "a browser sending nothing but its cookie could not refresh, so a web session ends at the "
          + "first access-token expiry");
    }

    /// <summary>
    /// And the same cookie is ignored when another site is what made the request.
    /// </summary>
    /// <remarks>
    /// <c>SameSite</c> already stops the browser sending it, so this guards the case where a
    /// deployment has had to relax that to serve a front-end from elsewhere — at which point this
    /// check is the only thing between a cookie session and cross-site request forgery.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_cookie_is_not_honoured_on_a_request_another_site_started(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var session = await CreateSessionAsync(ct);
        var browser = Browser();

        browser.Interceptor.Cookie    = $"{Options.CookieName}={RefreshTokenFor(scope, session.UserId, browser.Interceptor.MachineId)}";
        browser.Interceptor.FetchSite = "cross-site";

        var result = await browser.Identity.GetMyAuthorization("", null, ct);

        Assert.That(result, Is.InstanceOf<BadAuthStatus>(),
            "a cookie was honoured on a request started by another origin, which is the whole of a "
          + "cross-site request forgery against the refresh endpoint");
    }

    /// <summary>
    /// The exchange refuses anything it was not given a signed token for.
    /// </summary>
    /// <remarks>
    /// It is the one door where a token from the identity server opens the Argon API, so what matters
    /// most about it is that it is shut by default — including on a deployment that registered the
    /// feature and configured no trusted audience, which is the state a fresh install is in.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_exchange_refuses_a_caller_with_no_token()
    {
        var anonymous = await HttpClient.PostAsync(WebSessionEndpoints.ExchangePath, new StringContent(""));
        var nonsense  = new HttpRequestMessage(HttpMethod.Post, WebSessionEndpoints.ExchangePath)
        {
            Content = new StringContent("")
        };

        nonsense.Headers.Add("Authorization", "Bearer not-a-token");

        var rejected = await HttpClient.SendAsync(nonsense);

        Assert.Multiple(() =>
        {
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "a token this server did not sign must not open a session");
        });
    }

    // ── the device half, in isolation ───────────────────────────────────────────────────────────

    /// <summary>
    /// What the server writes is what the request pipeline reads back.
    /// </summary>
    /// <remarks>
    /// The cookie is a query string assembled by hand here and parsed in three different places
    /// there, and none of those readers fails loudly: an unreadable <c>ner</c> throws inside the Ion
    /// interceptor and surfaces as <c>NO_AUTH</c> on every single call, which reads as an
    /// authentication bug rather than a formatting one.
    /// </remarks>
    [Test]
    public void The_device_cookie_is_one_the_request_pipeline_can_read()
    {
        var sessionId = Guid.CreateVersion7();

        var written   = IssueDeviceCookie(out var machineId, "app-under-test", sessionId);
        var incoming  = RequestCarrying(written);

        Assert.Multiple(() =>
        {
            Assert.That(incoming.GetMachineId(), Is.EqualTo(machineId));
            Assert.That(incoming.GetSessionId(), Is.EqualTo(sessionId));
            Assert.That(incoming.GetAppId(), Is.EqualTo("app-under-test"));
        });
    }

    /// <summary>
    /// A browser coming back keeps the machine it already was.
    /// </summary>
    /// <remarks>
    /// Tokens are bound to the machine id, so minting a new one on a second sign-in would invalidate
    /// the session the same browser is still holding in another tab — and the symptom would be a tab
    /// signing itself out whenever another one signed in.
    /// </remarks>
    [Test]
    public void Signing_in_again_does_not_give_the_browser_a_new_machine()
    {
        var first  = IssueDeviceCookie(out var machineId, "app", Guid.CreateVersion7());
        var second = new DefaultHttpContext { RequestServices = Deployed() };

        second.Request.Headers.Cookie = $"{ArgonSecureCookie.CookieName}={first}";

        ArgonSecureCookie.Issue(second, Options, "app", Guid.CreateVersion7());

        Assert.That(RequestCarrying(CookieValue(second, ArgonSecureCookie.CookieName)).GetMachineId(),
            Is.EqualTo(machineId), "the browser was handed a different machine id on its second sign-in");
    }

    /// <summary>
    /// The session cookie meets the conditions its own name claims.
    /// </summary>
    /// <remarks>
    /// A browser drops a <c>__Host-</c> cookie that carries a <c>Domain</c>, is not <c>Secure</c>, or
    /// is not rooted at <c>/</c> — silently, and the server sees a successful response either way.
    /// The prefix is what confines the credential to the API host, so losing it is both invisible and
    /// the point.
    /// </remarks>
    [Test]
    public void The_session_cookie_meets_the_conditions_of_the_host_prefix()
    {
        var http = new DefaultHttpContext();

        WebSessionCookie.Write(http, Options, "a-refresh-token");

        var cookie = http.Response.Headers.SetCookie.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(cookie, Does.Not.Contain("domain=").IgnoreCase,
                "a __Host- cookie with a Domain is rejected by the browser and the session never starts");
            Assert.That(cookie, Does.Contain("path=/").IgnoreCase);
            Assert.That(cookie, Does.Contain("secure").IgnoreCase);
            Assert.That(cookie, Does.Contain("httponly").IgnoreCase,
                "the refresh token is in a cookie precisely so that no script on the page can read it");
        });
    }

    /// <summary>
    /// Signing out removes the cookie, and leaves the device identity alone.
    /// </summary>
    /// <remarks>
    /// A deletion is matched by name, path and domain, so one written with different attributes
    /// leaves the cookie in place and the user signed in. The device half staying put is deliberate:
    /// it identifies a browser, not a session, and dropping it would cut a returning user off from
    /// the device history already recorded against them.
    /// </remarks>
    [Test]
    public void Signing_out_deletes_the_credential_and_keeps_the_device()
    {
        var http = new DefaultHttpContext();

        WebSessionCookie.Clear(http, Options);

        var cookies = http.Response.Headers.SetCookie.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(cookies, Does.Contain(Options.CookieName));
            Assert.That(cookies, Does.Contain("path=/").IgnoreCase,
                "a deletion whose path disagrees with the one it was written on removes nothing");
            Assert.That(cookies, Does.Not.Contain(ArgonSecureCookie.CookieName),
                "signing out is not a claim to be a different machine");
        });
    }

    /// <summary>
    /// The reader only answers for a request the site itself started.
    /// </summary>
    /// <remarks>
    /// <c>Sec-Fetch-Site</c> is set by the browser and cannot be written from script, which is what
    /// makes it usable as the gate. A request without it is not a browser, and a non-browser has no
    /// business authenticating from a cookie — it can send the token it holds.
    /// </remarks>
    [TestCase("same-origin", true)]
    [TestCase("same-site", true)]
    [TestCase("none", true)]
    [TestCase("cross-site", false)]
    [TestCase(null, false)]
    public void A_cookie_is_read_only_where_the_request_could_not_have_been_forged(string? fetchSite, bool expected)
    {
        var http = new DefaultHttpContext();

        http.Request.Headers.Cookie = $"{Options.CookieName}=a-refresh-token";

        if (fetchSite is not null)
            http.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        Assert.That(WebSessionCookie.Read(http, Options) is not null, Is.EqualTo(expected));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string RefreshTokenFor(IServiceScope scope, Guid userId, string machineId)
        => scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>()
                .GenerateRefreshToken(userId, machineId, ["argon.app"], Guid.CreateVersion7());

    private static string IssueDeviceCookie(out string machineId, string appId, Guid sessionId)
    {
        var http = new DefaultHttpContext { RequestServices = Deployed() };

        machineId = ArgonSecureCookie.Issue(http, Options, appId, sessionId);

        return CookieValue(http, ArgonSecureCookie.CookieName);
    }

    private static DefaultHttpContext RequestCarrying(string argonSecure)
    {
        var http = new DefaultHttpContext { RequestServices = Deployed() };

        http.Request.Headers.Cookie = $"{ArgonSecureCookie.CookieName}={argonSecure}";

        return http;
    }

    private static string CookieValue(HttpContext http, string name)
    {
        var header = http.Response.Headers.SetCookie
           .First(x => x!.StartsWith($"{name}=", StringComparison.Ordinal))!;

        return header[(name.Length + 1)..header.IndexOf(';')];
    }

    /// <summary>
    /// A service provider that says this is not a development host.
    /// </summary>
    /// <remarks>
    /// Every reader under test short-circuits to a constant in Development — machine <c>1234</c>,
    /// application <c>1234</c>, one session id for everybody — which would make this fixture agree
    /// with itself no matter what the cookie said.
    /// </remarks>
    private static IServiceProvider Deployed()
        => new ServiceCollection()
          .AddSingleton<IHostEnvironment>(new ProductionEnvironment())
          .BuildServiceProvider();

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "ArgonComplexTest";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; }
            = new NullFileProvider();
    }

    private BrowserClient Browser()
    {
        var interceptor = new BrowserInterceptor();
        var client      = IonClient.Create(HttpClient, NoWebSockets);

        client.WithInterceptor(interceptor);

        return new BrowserClient(interceptor, client.ForService<IIdentityInteraction>(FactoryAsp.Services));
    }

    private static Task<WebSocket> NoWebSockets(Uri uri, CancellationToken ct, string[]? protocols)
        => throw new NotSupportedException("this fixture only makes unary calls");

    private sealed record BrowserClient(BrowserInterceptor Interceptor, IIdentityInteraction Identity);

    /// <summary>
    /// A caller that behaves like a browser: a cookie jar, a fetch metadata header, and no bearer
    /// token at all — which is the state the web client is in whenever its access token has expired.
    /// </summary>
    private sealed class BrowserInterceptor : IIonInterceptor
    {
        private readonly Guid sessionId = Guid.CreateVersion7();

        public string  MachineId { get; }      = Guid.CreateVersion7().ToString();
        public string? Cookie    { get; set; }
        public string? FetchSite { get; set; } = "same-origin";

        public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next,
            CancellationToken ct)
        {
            context.RequestItems.Add("Sec-Ref", sessionId.ToString());
            context.RequestItems.Add("Sec-Ner", "1");
            context.RequestItems.Add("Sec-Carry", MachineId);

            if (Cookie is not null)
                context.RequestItems.Add("Cookie", Cookie);

            if (FetchSite is not null)
                context.RequestItems.Add("Sec-Fetch-Site", FetchSite);

            await next(context, ct);
        }
    }
}
