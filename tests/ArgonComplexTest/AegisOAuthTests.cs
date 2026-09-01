namespace ArgonComplexTest;

using System.Net;
using System.Net.Http.Json;
using Argon.Api.Features.Aegis;
using Argon.Entities;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Argon.Features.Aegis;
using Argon.Features.Clustering;
using ArgonComplexTest.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Argon.Grains.Interfaces;
using ArgonContracts;
using AccountContracts;

/// <summary>
/// Signing into an application through the identity server, over HTTP, as a browser would.
/// </summary>
/// <remarks>
/// The point of running this against a real <c>aegis</c> host rather than the co-hosted suite is
/// that the role is a client with no database: every answer it gives here — does this application
/// exist, may this person sign into it, what does its consent screen say — has to have crossed the
/// cluster to a grain and come back. A regression that broke that would still pass a test written
/// against a process that happens to contain the silo too.
/// </remarks>
[TestFixture]
public class AegisOAuthTests : TestBase
{
    private RoleHost host         = null!;
    private TestUserSession ownerSession = null!;
    private string          oauthClientId = null!;
    private string   clientId    = null!;
    private Guid     ownerId;
    private NewUserCredentialsInputForTest ownerCredentials = null!;

    [OneTimeSetUp]
    public async Task StartTheIdentityServerAndRegisterAnApplication()
    {
        host = new RoleHost(ArgonTestEnvironment.Instance.Host.Settings, ArgonRoleId.Aegis,
            siloPort: 0, ArgonClusterEndpoints.DefaultClusterId);

        // The application's owner. A freshly created app is neither public nor verified, so its own
        // team is the only audience it has — which is what makes the "someone else" case below a
        // refusal rather than a second happy path.
        var owner = await CreateSessionAsync();

        ownerId          = owner.UserId;
        ownerCredentials = owner.Credentials;

        var teams = GetGrainFactory().GetGrain<IDevTeamsGrain>(Guid.Empty);
        var team  = await teams.CreateTeamAsync(ownerId, $"aegis-test-{Guid.NewGuid():N}"[..24]);
        var app   = await teams.CreateClientAppAsync(team.teamId, "Aegis Test App", ClientAppPlatform.WebBased);

        clientId = app.clientId;

        // A second application, and a bot one, because only bots take part in OAuth: the credentials
        // the authorization endpoint resolves come from BotEntities alone, so a client app has no
        // client secret, no redirects and no scopes and is refused as an unknown client. The widget
        // tests above never noticed, because the widget's own API answers from a different lookup and
        // none of them reaches /connect.
        // The username has to end in "bot" -- the grain refuses anything else, and a bot is what this
        // has to be, because only bots carry OAuth credentials.
        var oauthApp = await teams.CreateBotAppAsync(team.teamId, "Aegis OAuth App",
            $"aegis{Guid.NewGuid():N}"[..12] + "bot");

        oauthClientId = oauthApp.clientId;

        await teams.AddRedirectAsync(team.teamId, oauthApp.appId, RedirectUri);
        await teams.UpdateScopeAsync(team.teamId, oauthApp.appId,
            new ScopeKeyValue(isRequired: true, ArgonScopes.UserRead, isLocked: false));

        ownerSession = owner;
    }

    /// <summary>Registered on the application above; the authorization endpoint checks it exactly.</summary>
    private const string RedirectUri = "https://app.test.local/callback";

    [OneTimeTearDown]
    public async Task StopTheIdentityServer()
        => await host.DisposeAsync();

    /// <summary>
    /// Which credential to ask for. The widget calls this before showing a field, so an account
    /// whose owner moved to passkeys is never shown a password box.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task The_widget_is_told_which_credential_an_account_uses()
    {
        using var client = AegisClient.For(host);

        var known = await Post(client, "/api/auth/scenario", new { email = ownerCredentials.email });
        var absent = await Post(client, "/api/auth/scenario", new { email = "nobody@test.local" });

        Assert.Multiple(() =>
        {
            Assert.That((string?)known["scenario"], Is.EqualTo("EmailPassword"));
            Assert.That((string?)absent["scenario"], Is.Empty,
                "an unknown mailbox must not be distinguishable from a known one by anything but this");
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task A_correct_password_opens_a_session_and_reaches_the_consent_screen()
    {
        using var client = AegisClient.For(host);

        var result = await SignIn(client, ownerCredentials);

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Success, Is.True);
            Assert.That(result.RequiresConsent, Is.True);
            Assert.That(result.ConsentInfo, Is.Not.Null);
            Assert.That(result.ConsentInfo!.AppName, Is.EqualTo("Aegis Test App"));

            // Resolved through IDevTeamsGrain, so a name here proves the whole hop worked.
            Assert.That(result.ConsentInfo.DeveloperName, Is.Not.Empty);
        });
    }

    /// <summary>
    /// The session is what lets a second application be authorized without asking for the password
    /// again, so it has to survive as a cookie and be recognised on the next request.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task The_session_survives_into_the_next_request()
    {
        using var client = AegisClient.For(host);

        await SignIn(client, ownerCredentials);

        var session = await Get<SessionCheckResponse>(client, $"/api/auth/session/check?clientId={clientId}");

        Assert.Multiple(() =>
        {
            Assert.That(session.HasSession, Is.True);
            Assert.That(session.AccessDenied, Is.False);
            Assert.That(session.RequiresConsent, Is.True);
            Assert.That(session.ConsentInfo!.AppName, Is.EqualTo("Aegis Test App"));
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task A_browser_that_has_not_signed_in_has_no_session()
    {
        using var client = AegisClient.For(host);

        var session = await Get<SessionCheckResponse>(client, "/api/auth/session/check");

        Assert.That(session.HasSession, Is.False);
    }

    /// <summary>
    /// <c>prompt=login</c> is an application saying it does not care what the browser remembers.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task Prompt_login_throws_the_session_away()
    {
        using var client = AegisClient.For(host);

        await SignIn(client, ownerCredentials);

        var forced = await Get<SessionCheckResponse>(client, "/api/auth/session/check?prompt=login");
        var after  = await Get<SessionCheckResponse>(client, "/api/auth/session/check");

        Assert.Multiple(() =>
        {
            Assert.That(forced.HasSession, Is.False);
            Assert.That(forced.RequiresLogin, Is.True);
            Assert.That(after.HasSession, Is.False, "and it must stay thrown away");
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task A_wrong_password_opens_nothing()
    {
        using var client = AegisClient.For(host);

        var wrong = ownerCredentials with { password = ownerCredentials.password + "x" };

        var result  = await SignIn(client, wrong);
        var session = await Get<SessionCheckResponse>(client, "/api/auth/session/check");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(AuthorizationError.BAD_CREDENTIALS.ToString()));
            Assert.That(result.ConsentInfo, Is.Null);
            Assert.That(session.HasSession, Is.False, "a failed attempt must not leave a session behind");
        });
    }

    /// <summary>
    /// An application that is neither public nor verified is for its own team, and nobody else's
    /// correct password changes that.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task Someone_outside_the_team_is_refused_the_application()
    {
        var stranger = await CreateSessionAsync();

        using var client = AegisClient.For(host);

        using var response = await client.PostAsJsonAsync("/api/auth/oauth/authorize", new
        {
            email    = stranger.Credentials.email,
            password = stranger.Credentials.password,
            clientId
        });

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((string?)body["error"], Is.EqualTo("access_denied"));
            Assert.That((string?)body["error_description"], Does.Contain("team membership"));
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task An_unknown_application_is_refused_before_anything_else()
    {
        using var client = AegisClient.For(host);

        using var response = await client.PostAsJsonAsync("/api/auth/oauth/authorize", new
        {
            email    = ownerCredentials.email,
            password = ownerCredentials.password,
            clientId = $"not-an-app-{Guid.NewGuid():N}"
        });

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((string?)body["error"], Is.EqualTo("invalid_client"));
        });
    }

    /// <summary>
    /// Switching accounts is allowed only among the accounts the browser has actually signed in, and
    /// the claim carrying that list is the whole authority for doing it without a password.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task An_account_this_browser_never_signed_into_cannot_be_switched_to()
    {
        var stranger = await CreateSessionAsync();

        using var client = AegisClient.For(host);
        await SignIn(client, ownerCredentials);

        using var response = await client.PostAsJsonAsync("/api/auth/accounts/select", new
        {
            userId = stranger.UserId
        });

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((string?)body["error"], Is.EqualTo("user_not_in_list"));
        });
    }

    // ── operator step-up ─────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(300_000)]
    public async Task The_operator_step_up_needs_a_session_before_it_needs_a_key()
    {
        using var client = AegisClient.For(host);

        using var anonymous = await client.PostAsync("/api/auth/operator/verify", null);

        Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "the certificate identifies an operator; the session says who is claiming to be one");
    }

    [Test, CancelAfter(300_000)]
    public async Task A_signed_in_user_without_a_certificate_is_told_so()
    {
        using var client = AegisClient.For(host);
        await SignIn(client, ownerCredentials);

        using var response = await client.PostAsync("/api/auth/operator/verify", null);

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((string?)body["error"], Is.EqualTo("no_certificate"));
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What an application actually receives about a user, all the way to <c>userinfo</c>.
    /// </summary>
    /// <remarks>
    /// <para>The whole leg is here — consent, the authorization code, the exchange at the token
    /// endpoint, the call to <c>userinfo</c> — because none of it was covered and because the field
    /// under test only exists at the end of it. Asserting that a handler would have added a claim
    /// proves nothing about what a third party is handed.</para>
    ///
    /// <para><c>avatarUrl</c> is the point: an application integrating over OIDC has no way to turn a
    /// file identifier into an address, so it gets the address. It names the API rather than a
    /// storage mirror, which is what makes it survive the storage behind it moving.</para>
    ///
    /// <para>Both states in one test, in order, rather than two. The account has to be the
    /// application's owner — a stranger is refused the application entirely, which is a different
    /// rule and one the fixture already covers — so "before" and "after" are the same account, and
    /// splitting them would leave two tests whose result depended on which ran first.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task Userinfo_gains_an_avatar_address_when_the_account_gains_an_avatar()
    {
        using var client = AegisClient.For(host);

        var before = await AuthorizeAndReadUserInfo(client);

        Assert.That(before.ContainsKey("avatarUrl"), Is.False,
            "an account with no avatar was given an address, which resolves to nothing — absent is "
          + "what lets a consumer tell the difference");

        var avatarFileId = await GiveTheOwnerAnAvatar();

        var after = await AuthorizeAndReadUserInfo(client);

        Assert.That(after.Value<string>("avatarUrl"), Is.EqualTo($"{AvatarBase}/files/{avatarFileId}"),
            "an application was given no avatar address, or one it cannot resolve");
    }

    /// <summary>The base the identity server is configured to build avatar addresses on.</summary>
    private const string AvatarBase = "https://api.test.local";

    /// <summary>
    /// Uploads a real avatar for the owner, through the ordinary client path.
    /// </summary>
    /// <remarks>
    /// Through the API rather than by writing the column, because the identifier userinfo reports has
    /// to be one the upload path actually produced — setting it directly would assert that this test
    /// can write to a database.
    /// </remarks>
    private async Task<string> GiveTheOwnerAnAvatar()
    {
        var users  = ownerSession.Client.ForService<IUserInteraction>(FactoryAsp.Services);
        var begin  = await users.BeginUploadAvatar();

        if (begin is not SuccessUploadFile ticket)
        {
            Assert.Fail($"could not start an avatar upload: {(begin as FailedUploadFile)?.error}");
            return string.Empty;
        }

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        using var stored = await TestObjectStore.UploadAsync(ticket.uploadUrl, png, "image/png",
            ticket.formFields.Select(f => (f.key, f.value)));

        Assert.That(stored.IsSuccessStatusCode, Is.True, "the object store refused the avatar upload");

        await users.CompleteUploadAvatar(ticket.blobId);

        return (await users.GetMe()).avatarFileId!;
    }

    /// <summary>
    /// Signs in, consents, redeems the code and reads <c>userinfo</c> — the flow an application runs.
    /// </summary>
    private async Task<JObject> AuthorizeAndReadUserInfo(
        HttpClient client, NewUserCredentialsInputForTest? credentials = null)
    {
        var verifier  = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var signIn = await SignIn(client, credentials ?? ownerCredentials, oauthClientId);
        Assert.That(signIn.Success, Is.True, $"sign-in failed: {signIn.Error}");

        // The widget posts the flow back to the authorization endpoint as a form once consent is
        // given; the code comes back on the redirect rather than in a body.
        using var authorize = await client.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]             = oauthClientId,
            ["redirect_uri"]          = RedirectUri,
            ["response_type"]         = "code",
            ["scope"]                 = $"openid {ArgonScopes.UserRead}",
            ["code_challenge"]        = challenge,
            ["code_challenge_method"] = "S256"
        }));

        Assert.That(authorize.Headers.Location, Is.Not.Null,
            $"the authorization endpoint did not redirect ({(int)authorize.StatusCode}): "
          + await authorize.Content.ReadAsStringAsync());

        var code = HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"];
        Assert.That(code, Is.Not.Null.And.Not.Empty, "the redirect carried no authorization code");

        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code!,
            ["redirect_uri"]  = RedirectUri,
            ["client_id"]     = oauthClientId,
            ["code_verifier"] = verifier
        }));

        var tokenBody = JObject.Parse(await token.Content.ReadAsStringAsync());
        var accessToken = tokenBody.Value<string>("access_token");

        Assert.That(accessToken, Is.Not.Null.And.Not.Empty,
            $"the token endpoint returned no access token: {tokenBody}");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var userInfo = await client.SendAsync(request);

        return JObject.Parse(await userInfo.Content.ReadAsStringAsync());
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private Task<OAuthAuthorizeResponse> SignIn(
        HttpClient client, NewUserCredentialsInputForTest credentials, string? forClientId = null)
        => Post<OAuthAuthorizeResponse>(client, "/api/auth/oauth/authorize", new
        {
            email    = credentials.email,
            password = credentials.password,
            clientId = forClientId ?? clientId
        });

    private static async Task<JObject> Post(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        return Read<JObject>(await response.Content.ReadAsStringAsync(), path, response.StatusCode);
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        return Read<T>(await response.Content.ReadAsStringAsync(), path, response.StatusCode);
    }

    private static async Task<T> Get<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return Read<T>(await response.Content.ReadAsStringAsync(), path, response.StatusCode);
    }

    /// <summary>
    /// Reads a response, and says what came back when it is not what was expected.
    /// </summary>
    /// <remarks>
    /// The deserializer's own message is "unexpected character 'S'", which is true and useless: the
    /// body is almost always a stack trace or a proxy error, and the first line of it is the answer.
    /// </remarks>
    private static T Read<T>(string payload, string path, HttpStatusCode status)
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(payload)
                ?? throw new JsonException("the body was empty");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"'{path}' answered {(int)status} {status} rather than a {typeof(T).Name} ({e.Message})." +
                $"{Environment.NewLine}{payload}");
        }
    }
}
