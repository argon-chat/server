namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
using System.IdentityModel.Tokens.Jwt;

/// <summary>
/// The «Устройства» screen: which sessions a user has, and ending them.
/// </summary>
/// <remarks>
/// <para>Ending a session is the half worth guarding. Access tokens are short, but refresh tokens
/// are stateless and long-lived, so "revoked" cannot mean "dropped the socket" — the session would
/// mint itself a new access token on the next refresh. What it means instead is a tombstone the
/// request path checks, and the tests below pin the behaviour that tombstone has to preserve.</para>
///
/// <para>The two refusals matter more than the successes: revoking your own current session would
/// throw you out of the screen you are standing on, and revoking by a guessed id would turn this
/// into a way of signing strangers out.</para>
/// </remarks>
[TestFixture]
public class SessionTests : TestBase
{
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task GetSessions_MarksNoMoreThanOneSessionAsCurrent(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var sessions = await GetSecurityService(scope.ServiceProvider).GetSessions(ct);

        // Whatever the list holds, "current" is a property of the caller, and the screen puts that
        // row first and hides its «Выйти» button. Two of them would mean two different rows claiming
        // to be the phone in the user's hand.
        Assert.That(sessions.Count(x => x.isCurrent), Is.LessThanOrEqualTo(1));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task GetSessions_DescribesEveryRowItReturns(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var sessions = await GetSecurityService(scope.ServiceProvider).GetSessions(ct);

        Assert.Multiple(() =>
        {
            foreach (var session in sessions)
            {
                // A row the user cannot recognise is a row they cannot act on. The id has to be
                // real, because it is what RevokeSession takes.
                Assert.That(session.sessionId, Is.Not.EqualTo(Guid.Empty));
                Assert.That(session.lastSeenAt, Is.Not.EqualTo(default(DateTimeOffset)));
            }
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task RevokeSession_WithUnknownId_ReturnsNotFound(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var result = await GetSecurityService(scope.ServiceProvider).RevokeSession(Guid.NewGuid(), ct);

        Assert.That(result, Is.InstanceOf<FailedRevokeSession>());
        Assert.That(((FailedRevokeSession)result).error, Is.EqualTo(SessionError.NOT_FOUND));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task RevokeSession_WithAnotherUsersSession_ReturnsNotFound(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var stranger = await CreateSessionAsync(ct);
        var theirs   = await stranger.Security.GetSessions(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        foreach (var session in theirs)
        {
            var result = await GetSecurityService(scope.ServiceProvider).RevokeSession(session.sessionId, ct);

            // Scoped to the caller's own sessions, so someone else's id is indistinguishable from a
            // guessed one. Anything but NOT_FOUND here is a way to sign strangers out.
            Assert.That(result, Is.InstanceOf<FailedRevokeSession>());
            Assert.That(((FailedRevokeSession)result).error, Is.EqualTo(SessionError.NOT_FOUND));
        }
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task RevokeAllSessions_LeavesTheCallerSignedIn(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var security = GetSecurityService(scope.ServiceProvider);
        var result   = await security.RevokeAllSessions(ct);

        Assert.That(result, Is.InstanceOf<SuccessRevokeSession>());

        // The button lives on the devices screen next to the caller's own row. Signing them out of
        // the screen they are using to tidy up their sessions is not what they pressed — so the
        // call spares the current session, and the next request still works.
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);
        Assert.That(me.userId, Is.Not.EqualTo(Guid.Empty));

        Assert.That(
            async () => await security.GetSessions(ct),
            Throws.Nothing,
            "revoking every other session must not end the caller's own");
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task RevokeAllSessions_DoesNotStrandAnotherUsersSession(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var bystander = await CreateSessionAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        await GetSecurityService(scope.ServiceProvider).RevokeAllSessions(ct);

        // «Выйти на остальных устройствах» means the caller's other devices, not everyone's.
        var them = await bystander.Users.GetMe(ct);
        Assert.That(them.userId, Is.EqualTo(bystander.UserId));
    }

    /// <summary>
    /// «Выйти на остальных устройствах» reaches every other device, not the first one in the list.
    /// </summary>
    /// <remarks>
    /// <para>Defect S8. The loop over the user's sessions was wrapped in one try/catch, so the first
    /// device whose sign-out threw took every device after it down with it — they stayed fully signed
    /// in, holding ten-year refresh tokens — and the caller was handed <c>FailedRevokeSession</c>, so
    /// the screen said nothing had happened at all. Both readings a user can take from that are
    /// wrong: "it failed, they are still in" understates it for the devices that were ended, and
    /// "let me press it again" hits the same device again.</para>
    ///
    /// <para>Each session is guarded on its own now and the outcome is counted. The result is still
    /// binary because the ion contract has no shape for "three of five" — a partial run reports
    /// <c>INTERNAL_ERROR</c>, which is the pessimistic and honest reading, and the counts go to the
    /// log. This test pins the ordinary case: with nothing failing, every other device is ended and
    /// the answer is success.</para>
    ///
    /// <para>Two extra devices rather than one, because one device cannot tell "the loop ran" from
    /// "the loop stopped after the first element".</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task RevokeAllSessions_EndsEveryOtherDevice(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);
        var tablet = await SecondDeviceAsync(laptop, ct);

        // A session reaches the devices screen only once it holds a presence key, and
        // RevokeAllSessions iterates exactly what that screen shows.
        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);
        await using var onPhone  = await RealtimeClient.ConnectAsync(phone, ct);
        await using var onTablet = await RealtimeClient.ConnectAsync(tablet, ct);

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(phone.SessionId) && ids.Contains(tablet.SessionId),
            TimeSpan.FromSeconds(30), ct: ct);

        Assert.That(listed, Is.SupersetOf(new[] { phone.SessionId, tablet.SessionId }),
            "both extra devices have to be on the screen before the button can be said to have reached them");

        var result = await laptop.Security.RevokeAllSessions(ct);

        Assert.That(result, Is.InstanceOf<SuccessRevokeSession>(),
            $"signing every other device out reported {(result as FailedRevokeSession)?.error}");

        var remaining = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => !ids.Contains(phone.SessionId) && !ids.Contains(tablet.SessionId),
            TimeSpan.FromSeconds(30), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain(phone.SessionId),
                "the first of the two other devices is still signed in");
            Assert.That(remaining, Does.Not.Contain(tablet.SessionId),
                "the second device outlived the sign-out — the loop stopped short of it");
            Assert.That(remaining, Does.Contain(laptop.SessionId),
                "the caller's own device was signed out, which is the one thing this button must not do");
        });
    }

    /// <summary>
    /// A client that names its session the way a deployed one does — <c>Sec-Ref</c>, and no dev-only
    /// header — keeps the session id it sent.
    /// </summary>
    /// <remarks>
    /// <para>Defect S22. <c>HttpContextExtensions.GetSessionId</c> returned <c>Guid.AllBitsSet</c> as
    /// soon as a Development host saw no <c>ArgonSecure</c> cookie and no <c>X-Ctt</c>, without ever
    /// reaching the <c>Sec-Ref</c> fallback below it — so on a dev stand every session of a user
    /// collapsed into one id: one session grain, one presence key, one row on the devices screen, and
    /// one entry in the tombstone set that the Ion gate, the hub gate and the session grain all key
    /// on. Signing one "device" out signed out every session of that account, and kept doing so for
    /// the ten years a revocation is retained.</para>
    ///
    /// <para>Asserted through the hub ticket because that is where the value is load-bearing: the
    /// <c>sid</c> claim is what <c>AppHub</c> builds the session grain key out of, so a ticket naming
    /// the placeholder is a connection sharing one grain with every other client of the account. The
    /// suite's own <c>DefaultHeaderInterceptor</c> sends both headers and cannot see this; a browser
    /// (<c>WebSessionTests.BrowserInterceptor</c>) and every real client send only <c>Sec-Ref</c>.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task AClientThatSendsOnlySecRef_KeepsTheSessionIdItSent(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var interceptor = new SecRefOnlyInterceptor();
        var client      = IonClient.Create(HttpClient, WebSocketFactory);

        client.WithInterceptor(interceptor);

        var creds  = GenerateCredentials();
        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Registration(
            new NewUserCredentialsInput(
                creds.email, creds.username, creds.password, creds.displayName,
                creds.argreeTos, creds.birthDate, creds.argreeOptionalEmails, creds.captchaToken, "1.0", "1.0"),
            ct);

        if (result is not SuccessRegistration registered)
        {
            Assert.Fail($"Registration failed: {(result as FailedRegistration)?.error}");
            return;
        }

        interceptor.SetToken(registered.token);

        var ticket = new JwtSecurityTokenHandler()
           .ReadJwtToken(await client.ForService<IEventBus>(scope.ServiceProvider).PickTicket(ct));

        var sid = ticket.Claims.FirstOrDefault(c => c.Type == "sid")?.Value;

        Assert.Multiple(() =>
        {
            Assert.That(sid, Is.Not.EqualTo(Guid.AllBitsSet.ToString()),
                "the session id this client sent was discarded for the development placeholder, so every "
              + "session of this account shares one grain, one presence key and one tombstone");
            Assert.That(sid, Is.EqualTo(interceptor.SessionId.ToString()),
                "the hub ticket names a session the client never claimed");
        });
    }

    /// <summary>
    /// A caller that identifies its session the way a deployed client does: <c>Sec-Ref</c> only.
    /// </summary>
    /// <remarks>
    /// <see cref="DefaultHeaderInterceptor"/> also sends <c>X-Ctt</c>, deliberately, so that the rest
    /// of the suite behaves like a deployed server; this one leaves it out precisely to exercise the
    /// path that used to throw the header away.
    /// </remarks>
    private sealed class SecRefOnlyInterceptor : IIonInterceptor
    {
        private readonly string          machineId = Guid.CreateVersion7().ToString();
        private volatile string?         authToken;

        public Guid SessionId { get; } = Guid.CreateVersion7();

        public void SetToken(string? token) => authToken = token;

        public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next,
            CancellationToken ct)
        {
            context.RequestItems.Add("Sec-Ref", SessionId.ToString());
            context.RequestItems.Add("Sec-Ner", "1");
            context.RequestItems.Add("Sec-Carry", machineId);

            if (!string.IsNullOrEmpty(authToken))
                context.RequestItems.Add("Authorization", $"Bearer {authToken}");

            await next(context, ct);
        }
    }

    /// <summary>
    /// Signs the same account in again on a client of its own — a second device, not a second
    /// account.
    /// </summary>
    /// <remarks>
    /// <see cref="TestBase.CreateSessionAsync"/> registers a fresh user each time, which is two
    /// accounts and cannot express "my other laptop". This logs the existing account in on a new
    /// client, so the session has its own sid, its own machine id and its own refresh token, exactly
    /// as a phone signing into an account the laptop is already on does.
    /// </remarks>
    private async Task<TestUserSession> SecondDeviceAsync(TestUserSession account, CancellationToken ct)
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WebSocketFactory);

        client.WithInterceptor(interceptor);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Authorize(
            new UserCredentialsInput(account.Credentials.email, null, null, account.Credentials.password, null, null),
            ct);

        if (result is not SuccessAuthorize authorized)
        {
            Assert.Fail($"Could not sign the account in on a second device: {(result as FailedAuthorize)?.error}");
            return null!;
        }

        interceptor.SetToken(authorized.token);

        var session = new TestUserSession(client, FactoryAsp.Services, account.Credentials, authorized.token,
            interceptor.SessionId);

        session.UserId = (await session.Users.GetMe(ct)).userId;

        Assert.Multiple(() =>
        {
            Assert.That(session.UserId, Is.EqualTo(account.UserId),
                "the second client signed in as a different user, so it is not a second device of this account");
            Assert.That(session.SessionId, Is.Not.EqualTo(account.SessionId),
                "the second device claims the first device's sid, so both would share one session grain");
        });

        return session;
    }

    private Task<System.Net.WebSockets.WebSocket> WebSocketFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = FactoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }
}
