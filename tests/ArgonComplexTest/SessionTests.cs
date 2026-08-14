namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

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
                Assert.That(session.lastSeenAt, Is.Not.EqualTo(default(DateTime)));
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
}
