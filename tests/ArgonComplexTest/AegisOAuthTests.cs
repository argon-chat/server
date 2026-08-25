namespace ArgonComplexTest;

using System.Net;
using System.Net.Http.Json;
using Argon.Api.Features.Aegis;
using Argon.Entities;
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
    private RoleHost host        = null!;
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
    }

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

    private Task<OAuthAuthorizeResponse> SignIn(HttpClient client, NewUserCredentialsInputForTest credentials)
        => Post<OAuthAuthorizeResponse>(client, "/api/auth/oauth/authorize", new
        {
            email    = credentials.email,
            password = credentials.password,
            clientId
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
