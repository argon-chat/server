namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;

/// <summary>
/// The devices screen and the sign-out button behind it, measured against what the rest of the
/// system believes about the same sessions.
/// </summary>
/// <remarks>
/// <para>"Sign this device out" is the one presence action a user takes for a security reason, and it
/// is the only one where being wrong is not cosmetic: the row a user did not recognise and pressed
/// the button on is, in their reading, a stranger who has just been thrown out. So the promise the
/// screen makes is narrow and total — the session is gone, it stays gone, and nothing it does from
/// then on reaches anybody. Everything in this fixture is that promise, taken apart into the pieces
/// an observer can actually see: the row on the screen, the presence keys, the status the user's
/// spaces are told, and the credentials the ended device still holds.</para>
///
/// <para>Two devices of one account is the shape every test here needs, and it is not the shape
/// <see cref="TestBase.CreateSessionAsync"/> makes — that registers a new user each time, which is
/// two accounts, not two devices. <see cref="SecondDeviceAsync"/> logs the same account in again on
/// a client of its own, so the second session has its own sid and its own refresh token exactly the
/// way a phone signing into an account the laptop is already on does. Without that every test below
/// would be revoking a session belonging to somebody else and would pass for the wrong reason.</para>
///
/// <para>The observing user is always a member of a shared space and always connects <em>before</em>
/// the devices do. <c>SpaceGrain.UserJoined</c> announces the joiner's real aggregate now rather than
/// an unconditional Online (defect S4), but a device that is already connected when it joins still
/// produces a status event, and a watcher that saw it could not tell that Online from the one a
/// connection produces.</para>
/// </remarks>
[TestFixture]
public class PresenceRevocationTests : TestBase
{
    private PresenceProbe probe = null!;

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    /// <summary>
    /// The narrowest thing the sign-out button has to do: pressed on a live device of the caller's
    /// own account, it reports success.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately asserts nothing about presence. Every other revocation test in this fixture
    /// has to press this button first, and a failure here makes all of them red for a reason none of
    /// them is about — so this is the one to read first and the one to run against a fix.</para>
    ///
    /// <para>Guards the TTL on the tombstone (defect S5). <c>SecurityGrain.EndSessionAsync</c> writes
    /// <c>session:revoked:{userId}</c> with <c>SetAddAsync</c>, which makes it a Redis SET, and the
    /// retention on it therefore has to be put on with a type-agnostic <c>EXPIRE</c>
    /// (<c>IArgonCacheDatabase.UpdateStringExpirationAsync</c>) rather than with <c>KeyExpireAsync</c>,
    /// which is <c>GETEX</c> underneath and answers <c>WRONGTYPE</c> against anything but a string.
    /// It used to be the latter, and the throw unwound out of <c>EndSessionAsync</c> before
    /// <c>GoOfflineAsync</c>, <c>RemoveSessionAsync</c> and <c>RemoveSessionStatusAsync</c> ever ran —
    /// so the sign-out reported <c>INTERNAL_ERROR</c> and the device stayed connected, on the screen
    /// and counted in the user's presence, in the only case that matters: an account with a second
    /// live session. Nothing on this path may put a TTL on a set with <c>KeyExpireAsync</c> again.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Signing_another_device_out_succeeds(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);
        await using var onPhone  = await RealtimeClient.ConnectAsync(phone, ct);

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(phone.SessionId), TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(listed, Does.Contain(phone.SessionId),
            "the second device is not on the devices screen, so there is no row to press the button on");

        var revoked = await laptop.Security.RevokeSession(phone.SessionId, ct);

        Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
            $"signing out a live device of the caller's own account failed with {(revoked as FailedRevokeSession)?.error}");
    }

    /// <summary>
    /// Signing a second device out ends its presence and keeps it ended: the row leaves the devices
    /// screen, the user's status falls back to what the remaining device is actually set to, and
    /// forty seconds of heartbeats from the ended device change none of that.
    /// </summary>
    /// <remarks>
    /// <para>The two devices are deliberately set to different statuses — the survivor Away, the
    /// doomed one Online — because the aggregate is then a direct readout of which sessions the
    /// server still counts. While both are alive the user is Online (Online outranks Away); the
    /// moment the second device is genuinely gone the user has to read Away. That makes "did the
    /// revoked session come back?" a question the user's own space can answer, rather than one only
    /// Redis can.</para>
    ///
    /// <para>The heartbeats are the whole point of the test. A signed-out client does not know it has
    /// been signed out; the desktop client sends a heartbeat every fifteen seconds regardless, and
    /// the question is whether the server treats those as noise from a dead session or as the
    /// arrival of a live one.</para>
    ///
    /// <para><b>The contract this now guards (defects S5 and S6, both fixed).</b> Two independent
    /// failures met here and the test needed both closed. S5: <c>SecurityGrain.EndSessionAsync</c>
    /// threw <c>WRONGTYPE</c> out of <c>cache.KeyExpireAsync</c> on the tombstone set it had just
    /// created (see <see cref="Signing_another_device_out_succeeds"/>), so the sign-out answered
    /// <c>INTERNAL_ERROR</c> and never reached <c>GoOfflineAsync</c> or the presence cleanup at all.
    /// S6: even with the session properly ended, nothing stopped it coming back — <c>AppHub</c>
    /// authenticates a socket once, from a ticket minted before the tombstone existed, and no hub
    /// method consulted <c>SessionRevocation.RevokedKey(userId)</c>, so the next heartbeat re-created
    /// the presence keys through <c>EnsureSessionStartedAsync</c> and put the row back on the
    /// screen.</para>
    ///
    /// <para>Now the hub checks the revocation set on connect and on every state-changing call
    /// (aborting the connection and throwing on a hit), and <c>UserSessionGrain</c> refuses to
    /// <em>start</em> a tombstoned sid at all — read uncached, because it only runs on a session
    /// start, and because a cached answer would leave a window in which the sign-out the user is
    /// watching for has not taken effect. Both layers are here on purpose: the hub gate is what cuts
    /// the feed, the grain gate is what makes "a revoked sid can never hold presence" true for every
    /// caller including the Ion path. The heartbeats in this test are the whole point — a signed-out
    /// client does not know it has been signed out and keeps beating every fifteen seconds — and the
    /// two devices hold different statuses so that the aggregate falling back to Away is a direct
    /// readout of which sessions the server still counts.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 5), Category("Slow")]
    public async Task Revoking_a_device_ends_its_presence_and_its_heartbeats_do_not_bring_it_back(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var laptop   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Revoked Devices", ct);
        await JoinAsync(observer, laptop, spaceId, ct);

        var phone = await SecondDeviceAsync(laptop, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);
        await using var onPhone  = await RealtimeClient.ConnectAsync(phone, ct);

        await onLaptop.Heartbeat(UserStatus.Away, ct);
        await onPhone.Heartbeat(UserStatus.Online, ct);

        var laptopStatus = await Poll.ForValueAsync(
            () => probe.SessionStatusAsync(laptop.UserId, laptop.SessionId),
            status => status == UserStatus.Away, TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(laptopStatus, Is.EqualTo(UserStatus.Away),
            "the surviving device never took the Away it was told to hold, so the aggregate below proves nothing");

        var bothOnline = await probe.WaitForAggregatedStatusAsync(
            laptop.UserId, UserStatus.Online, TimeSpan.FromSeconds(15), ct);

        Assert.That(bothOnline, Is.EqualTo(UserStatus.Online),
            "with an Online device attached the user has to read Online whatever the other device is set to");

        var listedBefore = (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray();

        Assert.That(listedBefore, Does.Contain(phone.SessionId),
            "the device about to be revoked is not on the devices screen, so there is nothing to press the button on");

        var presenceKey = PresenceProbe.PresenceSessionKey(phone.UserId, phone.SessionId);
        var beforeRevoke = watcher.Mark();

        var revoked = await laptop.Security.RevokeSession(phone.SessionId, ct);

        // Every observation below is taken whatever the call answered, and the answer is asserted at
        // the end beside them. A revocation that reports failure and a revocation that reports
        // success while changing nothing are different defects, and a test that stopped at the
        // return value could not tell which one it was looking at.

        // The user's spaces have to hear the truth as soon as the device is gone: the only session
        // left is Away, so Away is what the space is owed.
        var awayEvent = await watcher.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == laptop.UserId && e.status == UserStatus.Away,
            TimeSpan.FromSeconds(20), beforeRevoke, ct);

        var goneFromScreen = await Poll.UntilAsync(
            async () => !(await laptop.Security.GetSessions(ct)).Any(x => x.sessionId == phone.SessionId),
            TimeSpan.FromSeconds(20), ct: ct);

        var presenceRightAfter = await probe.Exists(presenceKey);

        // A signed-out client has no way of knowing it was signed out, so it keeps doing what it
        // always does. Three heartbeats fifteen seconds apart is exactly what the desktop client
        // would send over the next forty seconds.
        var heartbeatFailures = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            try
            {
                await onPhone.Heartbeat(UserStatus.Online, ct);
            }
            catch (Exception e)
            {
                heartbeatFailures.Add($"#{i}: {e.GetType().Name}: {e.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }

        var listedAfter    = (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray();
        var aggregateAfter = await probe.AggregatedStatusAsync(laptop.UserId, ct);
        var presenceAfter  = await probe.Exists(presenceKey);
        var resurrections  = watcher.EventsOfType<UserChangedStatus>(beforeRevoke)
           .Where(e => e.userId == laptop.UserId && e.status == UserStatus.Online)
           .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
                $"signing the other device out reported failure: {(revoked as FailedRevokeSession)?.error}");

            Assert.That(awayEvent, Is.Not.Null,
                "the space was never told the user's status fell back to the one device that is left");

            Assert.That(goneFromScreen, Is.True,
                "the revoked device is still on the devices screen twenty seconds after being signed out");

            Assert.That(presenceRightAfter, Is.False,
                $"the revoked session's presence key ({presenceKey}) survived the revocation");

            Assert.That(listedAfter, Does.Not.Contain(phone.SessionId),
                $"the revoked device is on the devices screen after forty seconds of its own heartbeats "
              + $"(hub calls that failed: {(heartbeatFailures.Count == 0 ? "none" : string.Join("; ", heartbeatFailures))})");

            Assert.That(presenceAfter, Is.False,
                "the revoked session's presence key is alive after its heartbeats, so the session it belongs to is being counted");

            Assert.That(aggregateAfter, Is.EqualTo(UserStatus.Away),
                "the user reads Online, which can only come from the revoked device still being counted as live");

            Assert.That(resurrections, Is.Empty,
                $"the space was told the user is Online by a device that was signed out: {watcher.Dump(beforeRevoke)}");
        });
    }

    /// <summary>
    /// Once a revocation has actually reached presence, it holds: the ended session does not put
    /// itself back with a heartbeat.
    /// </summary>
    /// <remarks>
    /// <para>This is the second half of the sign-out story, reached deliberately rather than through
    /// the button. <c>SecurityGrain.EndSessionAsync</c> ends a session in four steps — tombstone,
    /// <c>GoOfflineAsync</c>, <c>RemoveSessionAsync</c>, <c>RemoveSessionStatusAsync</c> — and today
    /// it throws after the first one, so the button never gets to the other three and no test that
    /// goes through it can say anything about them. Here the tombstone is written by pressing the
    /// button as usual, and the remaining three steps are then applied through the very same grain
    /// and service methods the grain would have called. The state that leaves behind is exactly the
    /// state a working revocation leaves behind, which is what makes the question below askable at
    /// all.</para>
    ///
    /// <para>And the question is the one that decides whether fixing the throw is enough: a client
    /// that has been signed out does not know it, keeps its connection, and heartbeats. Nothing on
    /// the hub consults the revocation set, and <c>UserSessionGrain.HeartBeatAsync</c> is written to
    /// self-heal a session it does not recognise — so the heartbeat is not ignored, it is treated as
    /// a session starting.</para>
    ///
    /// <para><b>The contract this now guards (defect S6, fixed).</b> A revoked sid cannot hold
    /// presence, whatever asks on its behalf. <c>UserSessionGrain.EnsureSessionStartedAsync</c> reads
    /// <c>SessionRevocation.RevokedKey(userId)</c> before starting a session and refuses a tombstoned
    /// one: <c>HeartBeatAsync</c> then answers <c>false</c> and <c>AttachConnectionAsync</c>
    /// deactivates instead of attaching. Uncached, because it only runs when a session is not already
    /// started — one <c>SMEMBERS</c> per session start, nothing per heartbeat — and because a cached
    /// answer would leave a window in which the sign-out has not taken effect yet.
    /// <c>AppHub.Heartbeat</c> also consults the set itself (cached, the way
    /// <c>ArgonTransactionInterceptor</c> does) and turns the grain's <c>false</c> into an abort plus
    /// a <c>HubException</c>, so the connection goes as well as the call.</para>
    ///
    /// <para>This is the layer that matters independently of S5: the grain is the last gate, and it
    /// covers callers the hub never sees — notably <c>IEventBus.Dispatch(HeartBeatEvent)</c>, which
    /// reaches the same grain over Ion. Before it, one <c>Heartbeat</c> on the still-open socket
    /// re-ran <c>SetSessionOnlineAsync</c> + <c>SetSessionStatusAsync</c> +
    /// <c>AggregateAndBroadcastStatus</c> + <c>PushFriendPresence</c> and the device was simply back:
    /// presence key alive, row on the devices screen, events flowing.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_signed_out_device_stays_signed_out_once_the_revocation_reaches_presence(
        CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);
        await using var onPhone  = await RealtimeClient.ConnectAsync(phone, ct);

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(phone.SessionId), TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(listed, Does.Contain(phone.SessionId), "the second device never reached the devices screen");

        // Step one of EndSessionAsync, through the product: the tombstone lands even though the call
        // then fails, which is why the other three are applied by hand below rather than faked.
        await laptop.Security.RevokeSession(phone.SessionId, ct);

        var sid = phone.SessionId.ToString();

        await probe.SessionGrain(phone.UserId, sid).GoOfflineAsync();
        await probe.Presence.RemoveSessionAsync(phone.UserId, sid, ct);
        await probe.Presence.RemoveSessionStatusAsync(phone.UserId, sid, ct);

        var presenceKey = PresenceProbe.PresenceSessionKey(phone.UserId, phone.SessionId);

        var endedProperly = await Poll.UntilAsync(
            async () => !await probe.Exists(presenceKey)
                     && !(await laptop.Security.GetSessions(ct)).Any(x => x.sessionId == phone.SessionId),
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.That(endedProperly, Is.True,
            "the session did not end even when the revocation's own steps were applied to it, so the test below "
          + "cannot tell a resurrection from a revocation that never happened");

        string? heartbeatError = null;

        try
        {
            await onPhone.Heartbeat(UserStatus.Online, ct);
        }
        catch (Exception e)
        {
            heartbeatError = $"{e.GetType().Name}: {e.Message}";
        }

        // Fixed wait: the claim is that the heartbeat changes nothing, and an absence has no edge to
        // poll for. A resurrection happens inside the heartbeat call itself, so this is generous.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var backOnScreen  = (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray();
        var presenceAlive = await probe.Exists(presenceKey);

        Assert.Multiple(() =>
        {
            Assert.That(presenceAlive, Is.False,
                $"a heartbeat from a signed-out device recreated its presence key ({presenceKey}), so the session is "
              + $"live again (the hub call {(heartbeatError is null ? "was accepted" : $"failed with {heartbeatError}")})");

            Assert.That(backOnScreen, Does.Not.Contain(phone.SessionId),
                $"a signed-out device put itself back on the devices screen with one heartbeat: "
              + $"[{string.Join(", ", backOnScreen)}]");
        });
    }

    /// <summary>
    /// A signed-out device does not keep a working realtime connection: either the server closes it
    /// or the calls it makes on it are refused.
    /// </summary>
    /// <remarks>
    /// <para>The tombstone <c>SecurityGrain.EndSessionAsync</c> writes shuts the session's credentials
    /// out of the Ion path, but the hub connection was authenticated by a ticket minted before the
    /// tombstone existed and nothing on the hub re-checks it. This test does not care which of the
    /// two remedies the product picks — closing the socket and refusing the call are equally good
    /// answers to "this device is signed out" — only that it picks one of them.</para>
    ///
    /// <para><b>The contract this now guards (defect S6, fixed).</b> The product picks both remedies
    /// rather than one. <c>AppHub</c> consults <c>SessionRevocation.RevokedKey(userId)</c> for the
    /// ticket's <c>sid</c> in <c>OnConnectedAsync</c> — which is what closes the window a long-lived
    /// ticket leaves open for brand-new sockets — and in every state-changing call: <c>Heartbeat</c>,
    /// <c>GoOffline</c>, <c>Resume</c>, <c>SubscribeToSpace</c> and <c>SubscribeToChannel</c>. On a
    /// hit it calls <c>Context.Abort()</c> and throws <c>HubException</c>, so the call fails and the
    /// transport goes with it. The read is cached with the same 15 s / 5 s entry
    /// <c>ArgonTransactionInterceptor</c> uses for the same key, so the Ion path and the hub cannot
    /// disagree about when a sign-out takes effect, and it fails <em>open</em> for the same reason
    /// the interceptor does — a Redis incident must not disconnect the whole instance.</para>
    ///
    /// <para>Before it, twenty seconds after the sign-out the revoked device's connection was still
    /// <c>Connected</c> and both <c>Heartbeat</c> and <c>GoOffline</c> were accepted without error:
    /// the hub authenticated once, from a ticket minted before the tombstone existed, and nothing
    /// anywhere aborted the connections belonging to an ended sid.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_revoked_devices_realtime_connection_is_closed_or_its_calls_are_refused(
        CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);
        await using var onPhone  = await RealtimeClient.ConnectAsync(phone, ct);

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(phone.SessionId), TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(listed, Does.Contain(phone.SessionId), "the second device never reached the devices screen");

        var revoked = await laptop.Security.RevokeSession(phone.SessionId, ct);

        // Spending the whole twenty seconds is the point when the connection is not closed: the
        // claim under test is that a revoked device stops being able to talk, and only an elapsed
        // window can show that it never stopped.
        var closed = await onPhone.WaitForCloseAsync(TimeSpan.FromSeconds(20), ct);

        string? heartbeatError  = null;
        string? goOfflineError  = null;

        if (!closed)
        {
            try
            {
                await onPhone.Heartbeat(UserStatus.Online, ct);
            }
            catch (Exception e)
            {
                heartbeatError = $"{e.GetType().Name}: {e.Message}";
            }

            try
            {
                await onPhone.GoOffline(ct);
            }
            catch (Exception e)
            {
                goOfflineError = $"{e.GetType().Name}: {e.Message}";
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
                $"signing the other device out reported failure: {(revoked as FailedRevokeSession)?.error}");

            Assert.That(closed || heartbeatError is not null || goOfflineError is not null, Is.True,
                "a signed-out device kept a working realtime connection: the server neither closed it nor refused "
              + $"its Heartbeat or GoOffline (connection state {onPhone.State})");
        });
    }

    /// <summary>
    /// Signing a device out stops the credentials it is holding: its refresh token no longer mints
    /// access tokens.
    /// </summary>
    /// <remarks>
    /// <para>Presence is the visible half of a revocation and the credentials are the half that
    /// matters. A refresh token in this system is dated ten years out and is good for a fresh access
    /// token on every request, so a device whose row left the screen while its token still mints is
    /// not signed out in any sense a user would accept — it is one HTTP call away from being back.
    /// </para>
    ///
    /// <para>The sanity mint before the revocation is there so a red result cannot be blamed on the
    /// token, the machine id or the login flow: the same call is made twice, and only the revocation
    /// happens in between.</para>
    ///
    /// <para>Guards the bridge between the two id spaces (defect S7). The screen lists the
    /// <em>presence</em> sid — the client's own <c>scid</c>/<c>Sec-Ref</c>, read by
    /// <c>HttpContextExtensions.GetSessionId()</c> — while the refresh token carries a <c>sid</c> the
    /// server mints for itself, and the two can never be equal for a client that writes its own device
    /// cookie. So the sign-out has to reach both: the paths that hold the pair record it
    /// (<c>ArgonAuthorizationService.GenerateJwt</c>, <c>QrLoginService.ApproveAsync</c> and
    /// <c>IdentityInteraction.GetMyAuthorization</c>, under
    /// <c>SessionRevocation.CredentialsKey</c>), and <c>SecurityGrain.EndSessionAsync</c> tombstones
    /// both ids into <c>session:revoked:{userId}</c>. While only the presence sid was written, the row
    /// left the screen and the device kept a ten-year credential that minted access tokens on demand —
    /// with the desktop's <c>scid</c> rotating on every launch, its next start came back as a brand
    /// new, fully signed-in row. The tombstone dump in the failure message is what tells "nothing was
    /// revoked" apart from "the wrong id was revoked"; keep it.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Revoking_a_device_stops_its_refresh_token_from_minting(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var (phone, refreshToken) = await SecondDeviceWithRefreshAsync(laptop, ct);

        Assert.That(refreshToken, Is.Not.Null.And.Not.Empty,
            "signing in on the second device returned no refresh token, so there is nothing to revoke");

        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);
        await using var onPhone  = await RealtimeClient.ConnectAsync(phone, ct);

        var before = await phone.Identity.GetMyAuthorization("", refreshToken, ct);

        Assert.That(before, Is.InstanceOf<GoodAuthStatus>(),
            $"the second device could not mint before it was revoked ({before.UnionKey}), so the test below means nothing");

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(phone.SessionId), TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(listed, Does.Contain(phone.SessionId), "the second device never reached the devices screen");

        var revoked = await laptop.Security.RevokeSession(phone.SessionId, ct);

        var after = await phone.Identity.GetMyAuthorization("", refreshToken, ct);

        // What the revocation actually wrote, read straight out of the store the refresh path checks.
        // It separates "nothing was tombstoned" from "the wrong id was tombstoned", and those want
        // very different fixes.
        var tombstoned = await probe.Redis.SetMembersAsync($"session:revoked:{laptop.UserId}");

        Assert.Multiple(() =>
        {
            Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
                $"signing the other device out reported failure: {(revoked as FailedRevokeSession)?.error}");

            Assert.That(after, Is.InstanceOf<BadAuthStatus>(),
                $"the revoked device's refresh token still mints access tokens ({after.UnionKey}) — the row left the "
              + $"devices screen but the credentials behind it did not. Tombstoned session ids: "
              + $"[{string.Join(", ", tombstoned.Select(x => x.ToString()))}]; the revoked device's sid is {phone.SessionId}");

            Assert.That((after as BadAuthStatus)?.error, Is.EqualTo(BadAuthKind.SESSION_EXPIRED),
                "a revoked session must be refused as expired rather than as a bad token");
        });
    }

    /// <summary>
    /// "Sign out everywhere else" keeps the device it was pressed on, ends the other two, and leaves
    /// the user at the status the surviving device is actually set to — never Offline.
    /// </summary>
    /// <remarks>
    /// <para>The caller being spared is deliberate product behaviour (<c>RevokeAllSessionsAsync</c>
    /// skips the current sid), and it has a presence consequence the screen never states: the user is
    /// still online, so nobody may be told they went offline. The surviving device is held Away so
    /// that "the aggregate followed the sessions that are left" and "the aggregate stayed Online by
    /// accident" cannot be confused for one another.</para>
    ///
    /// <para>The two ended devices then heartbeat once each, because the interesting failure is not
    /// the instant after the button but the fifteen seconds after that.</para>
    ///
    /// <para>The contract (defects S5 and S6, fixed). <c>RevokeAllSessionsAsync</c> calls the same
    /// <c>SecurityGrain.EndSessionAsync</c>, which used to throw <c>WRONGTYPE</c> out of
    /// <c>cache.KeyExpireAsync</c> — a <c>GETEX</c>, aimed at the tombstone SET — before it reached
    /// <c>GoOffline</c> or the presence cleanup, and because the throw escaped the loop it abandoned
    /// every session after the first. The TTL now goes on through the type-agnostic
    /// <c>UpdateStringExpirationAsync</c> and is guarded, so no bookkeeping step can stand between the
    /// tombstone and the sign-out. The heartbeats afterwards are the second half: a revoked session's
    /// next heartbeat must not restart it, which <c>EnsureSessionStartedAsync</c>'s uncached read of
    /// <c>session:revoked:{userId}</c> is what prevents.</para>
    ///
    /// <para>Deliberately fail-fast rather than best-effort: the loop has no per-session
    /// <c>try</c>/<c>catch</c> because every statement in it addresses the per-user tombstone or the
    /// shared Redis pool, so a throw would be identical on every iteration and swallowing it would let
    /// the call answer <c>SuccessRevokeSession</c> while devices stayed signed in — which is exactly
    /// what <c>ActiveSessions.vue</c> reads as "it worked". Revocation is idempotent, so a failed call
    /// is retried whole. (Design question S8: making the loop best-effort would need a wire change
    /// that can report partial success.)</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 4)]
    public async Task Revoking_every_other_session_spares_the_caller_and_lands_on_the_callers_own_status(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var laptop   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Sign Out Everywhere", ct);
        await JoinAsync(observer, laptop, spaceId, ct);

        var phone  = await SecondDeviceAsync(laptop, ct);
        var tablet = await SecondDeviceAsync(laptop, ct);

        await using var watcher  = await RealtimeClient.ConnectAsync(observer, ct);
        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);
        await using var onPhone  = await RealtimeClient.ConnectAsync(phone, ct);
        await using var onTablet = await RealtimeClient.ConnectAsync(tablet, ct);

        await onLaptop.Heartbeat(UserStatus.Away, ct);

        var laptopStatus = await Poll.ForValueAsync(
            () => probe.SessionStatusAsync(laptop.UserId, laptop.SessionId),
            status => status == UserStatus.Away, TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(laptopStatus, Is.EqualTo(UserStatus.Away),
            "the surviving device never took the Away it was told to hold");

        var allThree = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(laptop.SessionId) && ids.Contains(phone.SessionId) && ids.Contains(tablet.SessionId),
            TimeSpan.FromSeconds(20), ct: ct);

        Assert.That(allThree, Has.Length.EqualTo(3),
            $"the devices screen does not show the three connected devices: [{string.Join(", ", allThree)}]");

        var beforeRevoke = watcher.Mark();

        var revoked = await laptop.Security.RevokeAllSessions(ct);

        var onlyTheCaller = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Length == 1 && ids[0] == laptop.SessionId,
            TimeSpan.FromSeconds(20), ct: ct);

        var settled = await probe.WaitForAggregatedStatusAsync(
            laptop.UserId, UserStatus.Away, TimeSpan.FromSeconds(20), ct);

        var offlineEvents = watcher.EventsOfType<UserChangedStatus>(beforeRevoke)
           .Where(e => e.userId == laptop.UserId && e.status == UserStatus.Offline)
           .ToArray();

        // The signed-out clients do not know they were signed out. One heartbeat each is what they
        // would send within fifteen seconds of the button being pressed.
        foreach (var client in new[] { onPhone, onTablet })
        {
            try
            {
                await client.Heartbeat(UserStatus.Online, ct);
            }
            catch (Exception)
            {
                // A refused call is one of the two correct outcomes; the assertion below covers both.
            }
        }

        // Fixed wait: the claim is that nothing comes back, and an absence has no edge to poll for.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var afterHeartbeats = (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
                $"signing out everywhere reported failure: {(revoked as FailedRevokeSession)?.error}");

            Assert.That(onlyTheCaller, Is.EqualTo(new[] { laptop.SessionId }).AsCollection,
                "the devices screen after signing out everywhere must hold exactly the device it was pressed on");

            Assert.That(onLaptop.IsConnected, Is.True,
                "signing the other devices out closed the caller's own connection");

            Assert.That(settled, Is.EqualTo(UserStatus.Away),
                "the user's status did not fall back to the one device that is left");

            Assert.That(offlineEvents, Is.Empty,
                $"the space was told the user went offline while their own device is still connected: {watcher.Dump(beforeRevoke)}");

            Assert.That(afterHeartbeats, Is.EqualTo(new[] { laptop.SessionId }).AsCollection,
                $"a signed-out device is on the devices screen after a heartbeat: [{string.Join(", ", afterHeartbeats)}]");
        });
    }

    /// <summary>
    /// The devices screen lists the sessions that are connected and only those: a client that asked
    /// for a realtime ticket but never connected is not a device, and a client that said goodbye
    /// stops being one immediately.
    /// </summary>
    /// <remarks>
    /// A ticket is minted by <c>IEventBus.PickTicket</c>, which is also where a session gets its name
    /// and its description for this screen — so everything the screen needs to draw a row exists for
    /// a session that never opened a connection. Listing it would put a device on the screen that is
    /// not signed in anywhere, and the user's only reading of that is an intruder.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_devices_screen_lists_exactly_the_connected_sessions(CancellationToken ct = default)
    {
        var laptop     = await CreateSessionAsync(ct);
        var ticketOnly = await SecondDeviceAsync(laptop, ct);

        // Everything a device does short of connecting: sign in, ask for a realtime ticket, stop.
        await ticketOnly.Bus.PickTicket(ct);

        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Select(x => x.sessionId).ToArray(),
            ids => ids.Contains(laptop.SessionId), TimeSpan.FromSeconds(15), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(listed, Does.Contain(laptop.SessionId),
                "the connected device is missing from the devices screen");
            Assert.That(listed, Does.Not.Contain(ticketOnly.SessionId),
                "a session that asked for a ticket and never connected is shown as a signed-in device");
            Assert.That(listed, Has.Length.EqualTo(1),
                $"the devices screen shows sessions that are not connected: [{string.Join(", ", listed)}]");
        });

        // The absence above has to be about liveness, not about a missing record: the screen has
        // everything it needs to draw that row and correctly declines to.
        Assert.That(await probe.SessionMetaAsync(ticketOnly.UserId, ticketOnly.SessionId, ct), Is.Not.Null,
            "the ticket-only session left no description behind, so its absence from the screen proves nothing");

        var current = (await laptop.Security.GetSessions(ct)).Single(x => x.sessionId == laptop.SessionId);

        Assert.That(current.isCurrent, Is.True, "the caller's own row is not marked as the current session");

        await onLaptop.GoOffline(ct);

        var gone = await Poll.UntilAsync(
            async () => !(await laptop.Security.GetSessions(ct)).Any(x => x.sessionId == laptop.SessionId),
            TimeSpan.FromSeconds(2), ct: ct);

        Assert.That(gone, Is.True,
            "a device that said goodbye is still listed as signed in two seconds later");
    }

    /// <summary>
    /// A device that dies without saying goodbye keeps its row for the grace window, but the row
    /// stops pretending: its last-seen time freezes at the moment the connection died.
    /// </summary>
    /// <remarks>
    /// <para>Last-seen is the only field on this screen a user can audit a session with. While a
    /// device is connected it has to move — otherwise a live session looks abandoned — and once the
    /// device is gone it has to stop, because a last-seen that keeps advancing on a dead session is
    /// the screen telling the user somebody is using it right now.</para>
    ///
    /// <para>Both halves are asserted in one test on purpose: "it advanced" and "it stopped" are only
    /// meaningful next to each other, and measuring them on the same session removes any question of
    /// the two devices being treated differently.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task An_ungraceful_drop_keeps_the_row_but_freezes_its_last_seen(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);

        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);

        var firstSeen = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct))
               .FirstOrDefault(x => x.sessionId == laptop.SessionId)?.lastSeenAt,
            seen => seen is not null, TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(firstSeen, Is.Not.Null, "the connected device never reached the devices screen");

        // The session's own 15 s tick is what moves this; twenty-five seconds is one tick plus room.
        var advanced = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct))
               .FirstOrDefault(x => x.sessionId == laptop.SessionId)?.lastSeenAt,
            seen => seen > firstSeen, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1), ct);

        Assert.That(advanced, Is.Not.Null, "the device fell off the devices screen while it was still connected");

        Assert.That(advanced!.Value, Is.GreaterThan(firstSeen!.Value),
            "a connected device's last-seen never moved, so the screen shows a live session as abandoned");

        await onLaptop.AbortAsync(ct: ct);

        Assert.That(onLaptop.IsConnected, Is.False, "the aborted connection still reports itself connected");

        var atDeath = (await laptop.Security.GetSessions(ct))
           .FirstOrDefault(x => x.sessionId == laptop.SessionId)?.lastSeenAt;

        Assert.That(atDeath, Is.Not.Null,
            "an ungraceful drop took the row off the screen immediately, so the disconnect grace did not happen");

        // Fixed wait: the claim is that a value does NOT change, and there is no edge to poll for.
        // Longer than the 15 s tick so a tick that should not have fired would have fired by now.
        await Task.Delay(TimeSpan.FromSeconds(25), ct);

        var afterDeath = (await laptop.Security.GetSessions(ct))
           .FirstOrDefault(x => x.sessionId == laptop.SessionId);

        Assert.Multiple(() =>
        {
            Assert.That(afterDeath, Is.Not.Null,
                "the row vanished inside the disconnect grace, so a train tunnel reads as a sign-out");
            Assert.That(afterDeath?.lastSeenAt, Is.EqualTo(atDeath),
                "the last-seen of a device whose connection is dead kept advancing, so the screen claims it is in use");
        });
    }

    /// <summary>
    /// Logging out of one window while another window of the same session is open does not tell the
    /// user's spaces that they went offline.
    /// </summary>
    /// <remarks>
    /// <para>Two windows are one session in this product — one sid, one session grain, two connection
    /// ids — so "log out" pressed in one of them is not a statement about the account, and the other
    /// window is still there receiving events. Whatever the product decides to do about the surviving
    /// window, the observers in the user's spaces must not be shown a departure that did not happen;
    /// an Offline followed moments later by an Online is the worst of the options, because every
    /// member's roster reorders twice for nothing.</para>
    ///
    /// <para>The surviving window heartbeats through the whole window the way a real client does, so
    /// what the test measures is the sequence a member of the space would actually have seen.</para>
    ///
    /// <para><b>The contract this now guards (defect S9, fixed).</b> A logout is scoped to the
    /// connection that sent it: <c>AppHub.GoOffline</c> passes <c>Context.ConnectionId</c> to
    /// <c>IUserSessionGrain.GoOfflineAsync(string)</c>, which removes that connection and returns
    /// while any other remains, so the space is told nothing at all — the assertion below is that no
    /// status event followed the logout, not merely that no Offline did. It finalizes immediately,
    /// with no grace, only when the connection that logged out was the last one.</para>
    ///
    /// <para>What it replaced: <c>GoOfflineAsync()</c> cleared the whole <c>Connections</c> set and
    /// called <c>FinalizeOfflineAsync</c> unconditionally, broadcasting Offline to every space; the
    /// surviving window's next heartbeat then landed on a fresh activation, self-healed through
    /// <c>Connections.Add</c> + <c>EnsureSessionStartedAsync</c>, and broadcast Online again. The
    /// observing member saw <c>[Offline, Online]</c> while the second window never disconnected —
    /// a departure the user never made, immediately undone, reordering every member's roster twice
    /// for nothing. The parameterless overload keeps its session-wide meaning, which is what
    /// <c>SecurityGrain.EndSessionAsync</c> needs it for.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_logout_in_one_window_does_not_tell_the_space_the_user_left(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var member   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Two Windows", ct);
        await JoinAsync(observer, member, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeConnect = watcher.Mark();

        // Same TestUserSession twice: one sid, one session grain, two connections — two windows.
        await using var firstWindow  = await RealtimeClient.ConnectAsync(member, ct);
        await using var secondWindow = await RealtimeClient.ConnectAsync(member, ct);

        Assert.That(firstWindow.ConnectionId, Is.Not.EqualTo(secondWindow.ConnectionId),
            "the two windows share a connection id, so this is one window and the test is meaningless");

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == member.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeConnect, ct);

        var beforeLogout = watcher.Mark();

        await firstWindow.GoOffline(ct);

        // The surviving window carries on exactly as a connected client does.
        for (var i = 0; i < 6; i++)
        {
            await secondWindow.Heartbeat(UserStatus.Online, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        var seen = watcher.EventsOfType<UserChangedStatus>(beforeLogout)
           .Where(e => e.userId == member.UserId)
           .Select(e => e.status)
           .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(secondWindow.IsConnected, Is.True,
                "the surviving window was disconnected too — that is a defensible answer, but then the Offline below is honest");
            Assert.That(seen, Does.Not.Contain(UserStatus.Offline),
                $"a member of the space was told the user went offline while another window of the same session was "
              + $"connected and heartbeating; the sequence it saw was [{string.Join(", ", seen)}]");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Two devices of one account.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Signs the given account in again on a client of its own, which is what a second device is.
    /// </summary>
    /// <remarks>
    /// <see cref="TestBase.CreateSessionAsync"/> registers a new user, so it makes two accounts
    /// rather than two devices — no revocation test can be written with it, because a session of
    /// another account is exactly what <c>RevokeSession</c> is supposed to refuse. The new client
    /// carries its own <see cref="DefaultHeaderInterceptor"/>, so it claims a sid and a machine id of
    /// its own; the sid is what the presence keys, the session grain and the devices screen are all
    /// keyed on, and without a second one the two clients would be the same session.
    /// </remarks>
    private async Task<TestUserSession> SecondDeviceAsync(TestUserSession account, CancellationToken ct)
        => (await SecondDeviceWithRefreshAsync(account, ct)).Session;

    /// <inheritdoc cref="SecondDeviceAsync"/>
    /// <remarks>
    /// Also hands back the refresh token the login minted, which is the credential a revocation is
    /// ultimately meant to stop and the only thing that can tell whether it did.
    /// </remarks>
    private async Task<(TestUserSession Session, string? RefreshToken)> SecondDeviceWithRefreshAsync(
        TestUserSession account, CancellationToken ct)
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
            Assert.Fail($"Could not sign the account in on a second device: {(result as FailedAuthorize)!.error}");
            return (null!, null);
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

        return (session, authorized.refreshToken);
    }

    /// <summary>
    /// The WebSocket factory an <see cref="IonClient"/> needs, over the in-memory test server.
    /// </summary>
    /// <remarks>
    /// <see cref="TestBase"/> has one of these but keeps it private, and a client built by this
    /// fixture still has to be able to open a duplex Ion stream. Same two lines, same test server.
    /// </remarks>
    private Task<WebSocket> WebSocketFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = FactoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols)
            socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Presence revocation", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private static async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Guest could not join the space: {(joined as FailedJoin)?.error}");
    }
}
