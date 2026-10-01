namespace ArgonSharedLogicTest.Connections;

using System.Net;
using System.Security.Cryptography;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Providers;
using ArgonContracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static ConnectionsTestOptions;
using static ProviderParsingTests;

/// <summary>
/// Whole handshakes against a fake provider: the authorize URL each adapter builds, the exchange,
/// the identity read, and what is kept afterwards.
/// </summary>
[TestFixture]
public class ProviderFlowTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Dictionary<string, string> Query(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Test]
    public async Task GitHub_links_with_a_never_expiring_token_and_no_pkce()
    {
        var handler = new FakeHttpHandler()
           .On(HttpMethod.Post, "https://github.com/login/oauth/access_token", HttpStatusCode.OK, """{"access_token":"gho_abc","scope":"","token_type":"bearer"}""")
           .On(HttpMethod.Get, "https://api.github.com/user", HttpStatusCode.OK, GitHubUser);

        var provider = new GitHubConnectionProvider(handler.Client(), Wrap(Default()));
        var ctx      = new HandshakeContext("state-1", "https://api.argon.test/connections/callback/github", null);
        var url      = provider.BuildAuthorizationUrl(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(provider.UsesPkce, Is.False);
            Assert.That(provider.KeepsTokens, Is.True);
            Assert.That(provider.Capabilities, Is.EqualTo(ConnectionCapability.DETAILS | ConnectionCapability.TROPHY));
            Assert.That(url, Does.StartWith("https://github.com/login/oauth/authorize?"));
            Assert.That(url, Does.Contain("client_id=gh-id"));
            Assert.That(url, Does.Contain("state=state-1"));
            Assert.That(url, Does.Contain("redirect_uri=https%3A%2F%2Fapi.argon.test%2Fconnections%2Fcallback%2Fgithub"));
            Assert.That(url, Does.Contain("allow_signup=false"));
            Assert.That(url, Does.Not.Contain("code_challenge"));
            Assert.That(url, Does.Not.Contain("scope="), "the public profile needs no scope");
        });

        var result = await provider.CompleteAsync(ctx, Query(("code", "abc"), ("state", "state-1")), None);

        var (exchange, form) = handler.CallTo("https://github.com/login/oauth/access_token");
        var (read, _)        = handler.CallTo("https://api.github.com/user");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(HandshakeOutcome.Linked));
            Assert.That(result.Identity?.ExternalId, Is.EqualTo("583231"));
            Assert.That(result.Token, Is.EqualTo(new ProviderToken("gho_abc", null, null, "")));
            Assert.That(result.Details, Has.Count.EqualTo(3));
            Assert.That(exchange.Headers.Authorization, Is.Null, "GitHub takes the secret in the body");
            Assert.That(OAuthCodeFlowTests.Form(form)["client_secret"], Is.EqualTo("gh-secret"));
            Assert.That(read.Headers.Authorization?.Parameter, Is.EqualTo("gho_abc"));
            Assert.That(read.Headers.UserAgent.ToString(), Does.Contain("Argon"), "GitHub refuses requests without a user agent");
        });

        Assert.That(await provider.RefreshAsync(result.Token!, None), Is.EqualTo(result.Token), "nothing to refresh, nothing lost");
    }

    [Test]
    public async Task Spotify_asks_for_pkce_and_the_playback_scopes_and_treats_a_revoked_grant_as_gone()
    {
        var handler = new FakeHttpHandler()
           .On(HttpMethod.Post, "https://accounts.spotify.com/api/token", HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Refresh token revoked"}""");

        var provider = new SpotifyConnectionProvider(handler.Client(), Wrap(Default()));
        var ctx      = new HandshakeContext("s", "https://api.argon.test/connections/callback/spotify", "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
        var url      = provider.BuildAuthorizationUrl(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(url, Does.StartWith("https://accounts.spotify.com/authorize?"));
            Assert.That(url, Does.Contain("code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
            Assert.That(url, Does.Contain("code_challenge_method=S256"));
            Assert.That(url, Does.Contain("user-modify-playback-state"));
            Assert.That(provider.Capabilities.HasFlag(ConnectionCapability.LISTEN_ALONG), Is.True);
        });

        var denied = await provider.CompleteAsync(ctx, Query(("error", "access_denied"), ("state", "s")), None);

        Assert.Multiple(() =>
        {
            Assert.That(denied.Outcome, Is.EqualTo(HandshakeOutcome.Denied));
            Assert.That(handler.Calls, Is.Empty, "a refusal costs no request");
        });

        var refreshed = await provider.RefreshAsync(new ProviderToken("a", "r", DateTimeOffset.UtcNow, ""), None);

        Assert.That(refreshed, Is.Null);
    }

    [Test]
    public async Task A_refresh_that_omits_the_refresh_token_keeps_the_old_one()
    {
        var handler = new FakeHttpHandler()
           .On(HttpMethod.Post, "https://accounts.spotify.com/api/token", HttpStatusCode.OK, """{"access_token":"new","expires_in":3600,"scope":"user-read-private"}""");

        var provider = new SpotifyConnectionProvider(handler.Client(), Wrap(Default()));
        var before   = DateTimeOffset.UtcNow;

        var refreshed = await provider.RefreshAsync(new ProviderToken("old", "keep-me", before, "user-read-private"), None);

        var (request, form) = handler.CallTo("https://accounts.spotify.com/api/token");

        Assert.Multiple(() =>
        {
            Assert.That(refreshed?.AccessToken, Is.EqualTo("new"));
            Assert.That(refreshed?.RefreshToken, Is.EqualTo("keep-me"));
            Assert.That(refreshed?.ExpiresAt, Is.GreaterThanOrEqualTo(before.AddSeconds(3599)));
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
            Assert.That(OAuthCodeFlowTests.Form(form)["grant_type"], Is.EqualTo("refresh_token"));
        });
    }

    [Test]
    public async Task Telegram_reads_the_identity_from_a_verified_id_token_and_keeps_nothing()
    {
        using var rsa = RSA.Create(2048);

        var key   = new RsaSecurityKey(rsa) { KeyId = "k1" };
        var parts = rsa.ExportParameters(false);
        var jwks  = $$"""{"keys":[{"kty":"RSA","kid":"k1","use":"sig","alg":"RS256","n":"{{OAuthCodeFlow.Base64Url(parts.Modulus)}}","e":"{{OAuthCodeFlow.Base64Url(parts.Exponent)}}"}]}""";

        string IdToken(string audience) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer             = TelegramConnectionProvider.Issuer,
            Audience           = audience,
            IssuedAt           = DateTime.UtcNow,
            Expires            = DateTime.UtcNow.AddHours(1),
            Claims             = new Dictionary<string, object> { ["sub"] = "777", ["preferred_username"] = "durov", ["name"] = "Pavel", ["picture"] = "https://t.me/i/userpic/x.jpg" },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });

        var handler = new FakeHttpHandler()
           .On(HttpMethod.Get, TelegramConnectionProvider.JwksUrl, HttpStatusCode.OK, jwks)
           .On(HttpMethod.Post, "https://oauth.telegram.org/token", request =>
            {
                var form = OAuthCodeFlowTests.Form(request.Content!.ReadAsStringAsync().Result);
                var body = $$"""{"access_token":"tg-at","token_type":"bearer","expires_in":3600,"id_token":"{{IdToken(form["code"] == "wrong-aud" ? "999" : "123456")}}"}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            });

        var provider = new TelegramConnectionProvider(handler.Client(), new JwksKeyCache(new FakeHttpClientFactory(handler)), Wrap(Default()));
        var ctx      = new HandshakeContext("s", "https://api.argon.test/connections/callback/telegram", OAuthCodeFlow.NewPkceVerifier());
        var url      = provider.BuildAuthorizationUrl(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(url, Does.StartWith("https://oauth.telegram.org/auth?"));
            Assert.That(url, Does.Contain("client_id=123456"));
            Assert.That(url, Does.Contain("scope=openid%20profile"));
            Assert.That(url, Does.Contain("code_challenge_method=S256"));
            Assert.That(provider.KeepsTokens, Is.False);
            Assert.That(provider.Capabilities, Is.EqualTo(ConnectionCapability.NONE));
        });

        var result = await provider.CompleteAsync(ctx, Query(("code", "good"), ("state", "s")), None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(HandshakeOutcome.Linked));
            Assert.That(result.Identity, Is.EqualTo(new ConnectionIdentity("777", "durov", "https://t.me/durov", "https://t.me/i/userpic/x.jpg")));
            Assert.That(result.Token, Is.Null, "there is no API behind the access token");
            Assert.That(result.Details, Is.Empty);
            Assert.That(handler.CallTo("https://oauth.telegram.org/token").Request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
        });

        Assert.That(async () => await provider.CompleteAsync(ctx, Query(("code", "wrong-aud"), ("state", "s")), None),
            Throws.TypeOf<ProviderCallException>().With.Property(nameof(ProviderCallException.Status)).EqualTo(HttpStatusCode.Unauthorized),
            "an id_token for another client is refused");

        Assert.That(await provider.RefreshAsync(new ProviderToken("x", null, null, ""), None), Is.Null);
    }

    [Test]
    public async Task Steam_carries_the_state_in_return_to_and_verifies_the_assertion_with_steam()
    {
        var handler = new FakeHttpHandler()
           .On(HttpMethod.Post, "https://steamcommunity.com/openid/login", HttpStatusCode.OK, "ns:http://specs.openid.net/auth/2.0\nis_valid:true\n", "text/plain")
           .On(HttpMethod.Get, "https://api.steampowered.com/ISteamUser/GetPlayerSummaries", HttpStatusCode.OK, "{\"response\":{\"players\":[" + SteamPlayer + "]}}")
           .On(HttpMethod.Get, "https://api.steampowered.com/IPlayerService/GetOwnedGames", HttpStatusCode.OK, """{"response":{"game_count":245,"games":[]}}""")
           .On(HttpMethod.Get, "https://api.steampowered.com/IPlayerService/GetSteamLevel", HttpStatusCode.OK, """{"response":{"player_level":42}}""");

        var provider = new SteamConnectionProvider(handler.Client(), Wrap(Default()));
        var ctx      = new HandshakeContext("st", "https://api.argon.test/connections/callback/steam", null);
        var url      = provider.BuildAuthorizationUrl(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(provider.KeepsTokens, Is.False);
            Assert.That(url, Does.StartWith("https://steamcommunity.com/openid/login?"));
            Assert.That(url, Does.Contain("openid.mode=checkid_setup"));
            Assert.That(url, Does.Contain("openid.return_to=https%3A%2F%2Fapi.argon.test%2Fconnections%2Fcallback%2Fsteam%3Fstate%3Dst"));
            Assert.That(url, Does.Contain("openid.realm=https%3A%2F%2Fapi.argon.test"));
        });

        var query = Query(
            ("openid.mode", "id_res"),
            ("openid.return_to", "https://api.argon.test/connections/callback/steam?state=st"),
            ("openid.claimed_id", "https://steamcommunity.com/openid/id/76561197960435530"),
            ("openid.sig", "sig"),
            ("state", "st"));

        var result = await provider.CompleteAsync(ctx, query, None);
        var verify = OAuthCodeFlowTests.Form(handler.CallTo("https://steamcommunity.com/openid/login").Body);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(HandshakeOutcome.Linked));
            Assert.That(result.Token, Is.Null);
            Assert.That(result.Identity?.ExternalId, Is.EqualTo("76561197960435530"));
            Assert.That(result.Identity?.Name, Is.EqualTo("Robin"));
            Assert.That(result.Details.Single(d => d.key == ConnectionDetailKeys.SteamGames).value, Is.EqualTo("245"));
            Assert.That(result.Details.Single(d => d.key == ConnectionDetailKeys.SteamLevel).value, Is.EqualTo("42"));
            Assert.That(result.Details.Single(d => d.key == ConnectionDetailKeys.Since).value, Is.EqualTo("2003-09-12"));
            Assert.That(verify["openid.mode"], Is.EqualTo("check_authentication"));
            Assert.That(verify["openid.sig"], Is.EqualTo("sig"));
            Assert.That(verify.ContainsKey("state"), Is.False, "only openid.* goes back to Steam");
        });

        var cancelled = await provider.CompleteAsync(ctx, Query(("openid.mode", "cancel"), ("state", "st")), None);
        var foreign   = await provider.CompleteAsync(ctx, new Dictionary<string, string>(query) { ["openid.return_to"] = "https://evil.test/cb?state=st" }, None);

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Outcome, Is.EqualTo(HandshakeOutcome.Denied));
            Assert.That(foreign.Outcome, Is.EqualTo(HandshakeOutcome.Failed));
        });
    }

    [Test]
    public async Task Steam_refuses_an_assertion_steam_does_not_vouch_for()
    {
        var handler = new FakeHttpHandler()
           .On(HttpMethod.Post, "https://steamcommunity.com/openid/login", HttpStatusCode.OK, "ns:http://specs.openid.net/auth/2.0\nis_valid:false\n", "text/plain");

        var provider = new SteamConnectionProvider(handler.Client(), Wrap(Default()));
        var ctx      = new HandshakeContext("st", "https://api.argon.test/connections/callback/steam", null);

        var result = await provider.CompleteAsync(ctx, Query(
            ("openid.mode", "id_res"),
            ("openid.return_to", "https://api.argon.test/connections/callback/steam?state=st"),
            ("openid.claimed_id", "https://steamcommunity.com/openid/id/76561197960435530")), None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(HandshakeOutcome.Failed));
            Assert.That(handler.Calls, Has.Count.EqualTo(1), "nothing is read about an unverified id");
        });
    }

    [Test]
    public async Task A_private_steam_profile_links_with_the_persona_alone()
    {
        var handler = new FakeHttpHandler()
           .On(HttpMethod.Post, "https://steamcommunity.com/openid/login", HttpStatusCode.OK, "is_valid:true\n", "text/plain")
           .On(HttpMethod.Get, "https://api.steampowered.com/ISteamUser/GetPlayerSummaries", HttpStatusCode.OK,
                """{"response":{"players":[{"steamid":"76561197960435530","personaname":"Robin","communityvisibilitystate":1}]}}""")
           .On(HttpMethod.Get, "https://api.steampowered.com/IPlayerService/GetOwnedGames", HttpStatusCode.OK, """{"response":{}}""")
           .On(HttpMethod.Get, "https://api.steampowered.com/IPlayerService/GetSteamLevel", HttpStatusCode.Forbidden, "", "text/plain");

        var provider = new SteamConnectionProvider(handler.Client(), Wrap(Default()));
        var ctx      = new HandshakeContext("st", "https://api.argon.test/connections/callback/steam", null);

        var result = await provider.CompleteAsync(ctx, Query(
            ("openid.mode", "id_res"),
            ("openid.return_to", "https://api.argon.test/connections/callback/steam?state=st"),
            ("openid.claimed_id", "https://steamcommunity.com/openid/id/76561197960435530")), None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(HandshakeOutcome.Linked));
            Assert.That(result.Identity?.Url, Is.EqualTo("https://steamcommunity.com/profiles/76561197960435530"));
            Assert.That(result.Details, Is.Empty);
        });
    }
}
