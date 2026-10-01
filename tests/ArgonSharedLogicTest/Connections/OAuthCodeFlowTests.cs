namespace ArgonSharedLogicTest.Connections;

using System.Net;
using System.Text.RegularExpressions;
using Argon.Features.Integrations.Connections;

/// <summary>
/// The plumbing shared by every code-flow provider: PKCE, the authorize URL, the token answer in
/// its two shapes, and the one refusal that means the grant is gone.
/// </summary>
[TestFixture]
public class OAuthCodeFlowTests
{
    private static readonly ProviderConnectionOptions App = new() { ClientId = "id", ClientSecret = "secret" };

    [Test]
    public void The_pkce_challenge_matches_the_rfc_7636_vector()
        => Assert.That(OAuthCodeFlow.PkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
            Is.EqualTo("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));

    [Test]
    public void States_and_verifiers_are_url_safe_and_fresh()
    {
        var state    = OAuthCodeFlow.NewState();
        var verifier = OAuthCodeFlow.NewPkceVerifier();

        Assert.Multiple(() =>
        {
            Assert.That(state, Does.Match("^[A-Za-z0-9_-]{43}$"));
            Assert.That(verifier, Does.Match("^[A-Za-z0-9_-]{43,128}$"));
            Assert.That(OAuthCodeFlow.NewState(), Is.Not.EqualTo(state));
        });
    }

    [Test]
    public void The_authorize_url_encodes_its_parameters()
    {
        var url = OAuthCodeFlow.BuildUrl("https://provider.test/auth",
        [
            new("redirect_uri", "https://api.argon.test/connections/callback/x?state=s"),
            new("scope", "a b")
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(url, Does.StartWith("https://provider.test/auth?"));
            Assert.That(url, Does.Contain("redirect_uri=https%3A%2F%2Fapi.argon.test%2Fconnections%2Fcallback%2Fx%3Fstate%3Ds"));
            Assert.That(url, Does.Contain("scope=a%20b"));
        });
    }

    [Test]
    public void A_token_answer_is_read_in_both_scope_shapes()
    {
        var spotify = OAuthCodeFlow.ParseToken("""{"access_token":"a","token_type":"Bearer","expires_in":3600,"refresh_token":"r","scope":"x y"}""", "t");
        var twitch  = OAuthCodeFlow.ParseToken("""{"access_token":"a","expires_in":"14400","scope":["chat:read","user:read:email"]}""", "t");
        var oidc    = OAuthCodeFlow.ParseToken("""{"access_token":"a","id_token":"eyJ"}""", "t");

        Assert.Multiple(() =>
        {
            Assert.That(spotify, Is.EqualTo(new OAuthTokenResponse("a", "r", 3600, "x y", null)));
            Assert.That(twitch, Is.EqualTo(new OAuthTokenResponse("a", null, 14400, "chat:read user:read:email", null)));
            Assert.That(oidc.IdToken, Is.EqualTo("eyJ"));
            Assert.That(oidc.ExpiresIn, Is.Null);
        });
    }

    [Test]
    public void An_answer_without_a_token_is_a_provider_error()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => OAuthCodeFlow.ParseToken("""{"token_type":"bearer"}""", "t"), Throws.TypeOf<ProviderCallException>());
            Assert.That(() => OAuthCodeFlow.ParseToken("""{"error":"bad_verification_code"}""", "t"), Throws.TypeOf<ProviderCallException>());
            Assert.That(() => OAuthCodeFlow.ParseToken("<html>", "t"), Throws.TypeOf<ProviderCallException>());
        });
    }

    [Test]
    public void ToToken_keeps_what_a_refresh_answer_leaves_out()
    {
        var now      = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var previous = new ProviderToken("old", "refresh-1", now, "a b");
        var answer   = new OAuthTokenResponse("new", null, 60, "", null);

        var token = answer.ToToken(now, previous);

        Assert.Multiple(() =>
        {
            Assert.That(token.AccessToken, Is.EqualTo("new"));
            Assert.That(token.RefreshToken, Is.EqualTo("refresh-1"), "Spotify omits the refresh token on a refresh");
            Assert.That(token.Scopes, Is.EqualTo("a b"));
            Assert.That(token.ExpiresAt, Is.EqualTo(now.AddSeconds(60)));
            Assert.That(token.ExpiresWithin(TimeSpan.FromMinutes(2), now), Is.True);
            Assert.That(token.ExpiresWithin(TimeSpan.FromSeconds(30), now), Is.False);
            Assert.That(token.HasScope("b"), Is.True);
            Assert.That(token.HasScope("c"), Is.False);
        });
    }

    [Test]
    public async Task A_refresh_the_provider_refuses_as_invalid_grant_answers_null()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "https://provider.test/token", HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Refresh token revoked"}""");

        var refreshed = await OAuthCodeFlow.RefreshAsync(handler.Client(), "https://provider.test/token", App, TokenClientAuth.Basic, "r", CancellationToken.None);

        Assert.That(refreshed, Is.Null);
    }

    [Test]
    public void A_refresh_the_provider_cannot_serve_is_an_error_not_a_state()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "https://provider.test/token", HttpStatusCode.InternalServerError, "boom", "text/plain");

        Assert.That(
            async () => await OAuthCodeFlow.RefreshAsync(handler.Client(), "https://provider.test/token", App, TokenClientAuth.Basic, "r", CancellationToken.None),
            Throws.TypeOf<ProviderCallException>().With.Property(nameof(ProviderCallException.Status)).EqualTo(HttpStatusCode.InternalServerError));
    }

    [Test]
    public async Task The_exchange_sends_basic_credentials_and_the_verifier()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "https://provider.test/token", HttpStatusCode.OK, """{"access_token":"a","expires_in":10}""");

        var answer = await OAuthCodeFlow.ExchangeCodeAsync(handler.Client(), "https://provider.test/token", App, TokenClientAuth.Basic,
            "the-code", "https://api.argon.test/cb", "the-verifier", CancellationToken.None);

        var (request, body) = handler.CallTo("https://provider.test/token");
        var form = Form(body);

        Assert.Multiple(() =>
        {
            Assert.That(answer.AccessToken, Is.EqualTo("a"));
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo(Convert.ToBase64String("id:secret"u8.ToArray())));
            Assert.That(form["grant_type"], Is.EqualTo("authorization_code"));
            Assert.That(form["code"], Is.EqualTo("the-code"));
            Assert.That(form["redirect_uri"], Is.EqualTo("https://api.argon.test/cb"));
            Assert.That(form["code_verifier"], Is.EqualTo("the-verifier"));
            Assert.That(form["client_id"], Is.EqualTo("id"));
            Assert.That(form.ContainsKey("client_secret"), Is.False, "the secret travels in the header, not the body");
        });
    }

    [Test]
    public async Task Body_authentication_puts_the_secret_in_the_form()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "https://provider.test/token", HttpStatusCode.OK, """{"access_token":"a"}""");

        await OAuthCodeFlow.ExchangeCodeAsync(handler.Client(), "https://provider.test/token", App, TokenClientAuth.Body,
            "c", "https://api.argon.test/cb", null, CancellationToken.None);

        var (request, body) = handler.CallTo("https://provider.test/token");
        var form = Form(body);

        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.Authorization, Is.Null);
            Assert.That(form["client_secret"], Is.EqualTo("secret"));
            Assert.That(form.ContainsKey("code_verifier"), Is.False);
        });
    }

    internal static Dictionary<string, string> Form(string body)
        => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
           .Select(pair => pair.Split('=', 2))
           .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "");
}
