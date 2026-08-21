namespace ArgonComplexTest.Tests;

using Argon.Features.Auth;
using Argon.Services;
using Argon.Features.Jwt;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Shutting a refresh token out, which is the only kind of revocation that means anything here.
/// </summary>
/// <remarks>
/// <para>Access tokens lapse on their own, so ending a session is really about the refresh token
/// issued beside it — stateless, dated ten years out, and good for minting a fresh access token on
/// every request until something stops it. <c>GetMyAuthorization</c> is that mint.</para>
///
/// <para>The check has to read the session id out of the <em>signed</em> token. The rest of the
/// pipeline takes it from the <c>ArgonSecure</c> cookie, which the caller writes, so a revocation
/// matched against that is defeated by not sending it — the first test below is that hole, and it
/// is the reason this file exists rather than an extension of <c>SessionTests</c>.</para>
/// </remarks>
[TestFixture]
public class RefreshRevocationTests : TestBase
{
    private ClassicJwtFlow Flow(IServiceProvider provider)
        => provider.GetRequiredService<ClassicJwtFlow>();

    private IArgonCacheDatabase Cache(IServiceProvider provider)
        => provider.GetRequiredService<IArgonCacheDatabase>();

    [Test, CancelAfter(120_000)]
    public async Task RefreshToken_CarriesItsOwnSessionId(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var flow    = Flow(scope.ServiceProvider);
        var userId  = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();

        var token = flow.GenerateRefreshToken(userId, MachineId, ["user"], session);

        flow.ValidateRefreshTokenSession(token, MachineId, out var sid, out var issuedAt, out _);

        Assert.Multiple(() =>
        {
            // Signed, so a caller cannot choose it — which is the entire difference between this and
            // the session id the interceptor reads out of a cookie.
            Assert.That(sid, Is.EqualTo(session));
            Assert.That(issuedAt, Is.Not.Null);
            Assert.That(issuedAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(1)));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task ARevokedSession_StopsItsRefreshTokenMinting(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var flow    = Flow(scope.ServiceProvider);
        var session = Guid.CreateVersion7();
        var refresh = flow.GenerateRefreshToken(me.userId, MachineId, ["user"], session);

        var before = await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", refresh, ct);
        Assert.That(before, Is.InstanceOf<GoodAuthStatus>(), "a fresh token should mint");

        await Cache(scope.ServiceProvider).SetAddAsync(
            SessionRevocation.RevokedKey(me.userId), session.ToString(), ct);

        var after = await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", refresh, ct);

        Assert.That(after, Is.InstanceOf<BadAuthStatus>());
        Assert.That(((BadAuthStatus)after).error, Is.EqualTo(BadAuthKind.SESSION_EXPIRED));
    }

    [Test, CancelAfter(120_000)]
    public async Task ARevokedSession_CannotBeEscapedBySendingNoSessionCookie(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var flow    = Flow(scope.ServiceProvider);
        var session = Guid.CreateVersion7();
        var refresh = flow.GenerateRefreshToken(me.userId, MachineId, ["user"], session);

        await Cache(scope.ServiceProvider).SetAddAsync(
            SessionRevocation.RevokedKey(me.userId), session.ToString(), ct);

        // The test harness sends no ArgonSecure cookie at all, so the interceptor's own check has
        // nothing to match on. If the refresh path leaned on that check instead of reading the sid
        // out of the signature, a stolen token would keep minting simply by omitting a header.
        var result = await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", refresh, ct);

        Assert.That(result, Is.InstanceOf<BadAuthStatus>(), "revocation must not depend on a caller-supplied id");
    }

    [Test, CancelAfter(120_000)]
    public async Task RevokingOneSession_LeavesTheOthersAlone(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var flow  = Flow(scope.ServiceProvider);
        var doomed = Guid.CreateVersion7();
        var spared = Guid.CreateVersion7();

        var doomedToken = flow.GenerateRefreshToken(me.userId, MachineId, ["user"], doomed);
        var sparedToken = flow.GenerateRefreshToken(me.userId, MachineId, ["user"], spared);

        await Cache(scope.ServiceProvider).SetAddAsync(
            SessionRevocation.RevokedKey(me.userId), doomed.ToString(), ct);

        // The whole reason this is a list and not a version counter on the user: ending one device
        // must not sign out the others.
        Assert.Multiple(async () =>
        {
            Assert.That(await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", doomedToken, ct),
                Is.InstanceOf<BadAuthStatus>());
            Assert.That(await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", sparedToken, ct),
                Is.InstanceOf<GoodAuthStatus>());
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task AFloor_KillsEveryTokenOlderThanIt(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var flow  = Flow(scope.ServiceProvider);
        var older = flow.GenerateRefreshToken(me.userId, MachineId, ["user"], Guid.CreateVersion7());

        await Cache(scope.ServiceProvider).StringSetAsync(
            SessionRevocation.FloorKey(me.userId),
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString(),
            SessionRevocation.Window, ct);

        var result = await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", older, ct);

        // This is what a password change writes: no per-session bookkeeping can reach a token whose
        // session was never registered, and by date every one of them is covered.
        Assert.That(result, Is.InstanceOf<BadAuthStatus>());
        Assert.That(((BadAuthStatus)result).error, Is.EqualTo(BadAuthKind.SESSION_EXPIRED));
    }

    [Test, CancelAfter(120_000)]
    public async Task AFloor_LeavesTokensIssuedAfterItWorking(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);

        await Cache(scope.ServiceProvider).StringSetAsync(
            SessionRevocation.FloorKey(me.userId),
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(),
            SessionRevocation.Window, ct);

        // Signing back in after a password change has to work, or the floor is a lockout rather
        // than a revocation.
        var fresh = Flow(scope.ServiceProvider)
           .GenerateRefreshToken(me.userId, MachineId, ["user"], Guid.CreateVersion7());

        Assert.That(await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", fresh, ct),
            Is.InstanceOf<GoodAuthStatus>());
    }

    [Test, CancelAfter(120_000)]
    public async Task ChangingThePassword_WritesAFloor(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var session = await CreateSessionAsync(ct);

        var before = Flow(scope.ServiceProvider)
           .GenerateRefreshToken(session.UserId, MachineId, ["user"], Guid.CreateVersion7());

        var changed = await session.Security.ChangePassword(
            session.Credentials.password, $"Nw!{Guid.NewGuid():N}"[..20], ct);

        Assert.That(changed, Is.InstanceOf<SuccessChangePassword>());

        // The point of the whole mechanism: after a leak, the person who changed the password wants
        // every other holder of their credentials to stop, and per-session tombstones cannot reach
        // a token whose session nobody registered.
        var result = await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", before, ct);

        Assert.That(result, Is.InstanceOf<BadAuthStatus>());
    }
}
