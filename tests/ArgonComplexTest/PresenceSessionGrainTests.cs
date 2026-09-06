namespace ArgonComplexTest.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using ArgonContracts;
using Argon.Features.Auth;
using Argon.Features.Logic;
using ArgonComplexTest.Infrastructure.Presence;
using Argon.Grains.Interfaces;
using Argon.Services;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// Presence as the session grain itself sees it: <see cref="IUserSessionGrain"/> driven directly,
/// with no hub and no client in the way.
/// </summary>
/// <remarks>
/// <para>Every presence bug a user reports arrives as "it says I am offline" or "it says they are
/// online", and both of those are three or four hops away from the code that decided it — a SignalR
/// connection, a hub method, a grain, a fold over Redis keys, a cache with a one second window.
/// Driving the grain removes the first two hops, so a red test here names the grain or the presence
/// service and nothing else. The transport-level half of the campaign lives in its own fixture; this
/// one owns the state machine.</para>
///
/// <para>The three observables it asserts on are the three a user actually meets. The aggregated
/// status (<c>status:user:{u}:aggregated</c>) is what every roster, snapshot and friends list reads.
/// <c>IsUserOnlineAsync</c> is what "is this account reachable" means everywhere else. And the raw
/// Redis TTLs are the only way to tell a session that is being kept alive from one that is merely
/// not dead yet — a distinction that is invisible for the first two minutes and then decides
/// everything.</para>
///
/// <para>Sessions here are addressed by a synthetic sid rather than the fixture client's own,
/// because the grain key is <c>"{userId}:{sid}"</c> and nothing else about a sid matters to this
/// layer: it must be a guid (the devices screen refuses to list a row it cannot offer to end) and it
/// must be unique. The one test that needs the client's real sid — the Ion <c>Dispatch</c> path —
/// takes it from <see cref="TestUserSession.SessionId"/>, because there the server derives the same
/// key from the <c>Sec-Ref</c> header and the two have to agree.</para>
///
/// <para>Connection ids are arbitrary strings for the same reason: the grain treats them as opaque
/// set members, and the only property under test is how many of them are left.</para>
/// </remarks>
[TestFixture]
public class PresenceSessionGrainTests : TestBase
{
    /// <summary>The Cache profile's logical database — see <c>ArgonServerTargetHost</c>.</summary>
    private const int CacheDb = 0;

    // Presence and status keys live PresenceTimingOptions.SessionTtl and are renewed every
    // RefreshPeriod. Neither is restated here: the integration host runs the shipped presence code
    // against compressed clocks (TestPresenceTimings), so a constant in this file would be a number
    // nothing uses. Every wait below is a ratio of those two, spelled out in PresenceWaits, which is
    // what makes this fixture mean the same thing at either scale.

    /// <summary>
    /// Sessions this fixture started, so a test that ends red does not leave a grain ticking against
    /// Redis for the rest of the run.
    /// </summary>
    private readonly ConcurrentBag<(Guid userId, string sid)> started = [];

    private IUserPresenceService Presence
        => FactoryAsp.Services.GetRequiredService<IUserPresenceService>();

    [TearDown]
    public async Task ReleaseSessionsAsync()
    {
        foreach (var (userId, sid) in started)
        {
            try
            {
                await SessionGrain(userId, sid).GoOfflineAsync();
            }
            catch
            {
                // Best effort: a session the test already finalized throws nothing useful here, and a
                // failure to clean up must never be reported as the test's own failure.
            }
        }

        started.Clear();
    }

    // ── addressing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fresh sid. A guid rather than any string because <c>SecurityGrain.GetSessionsAsync</c> drops
    /// rows whose sid will not parse as one, and a test that used "sid-1" would silently exclude
    /// itself from the devices screen.
    /// </summary>
    private static string NewSid() => Guid.CreateVersion7().ToString();

    private IUserSessionGrain SessionGrain(Guid userId, string sid)
        => GetGrainFactory().GetGrain<IUserSessionGrain>($"{userId}:{sid}");

    /// <summary>Grain for a session this fixture owns, registered for teardown.</summary>
    private IUserSessionGrain OwnedSession(Guid userId, string sid)
    {
        started.Add((userId, sid));
        return SessionGrain(userId, sid);
    }

    // ── Redis, raw ───────────────────────────────────────────────────────────────────────────────
    //
    // The key builders below mirror UserPresenceService's private ones. Duplicated rather than made
    // public on the service: they are an implementation detail everywhere except here, and a test
    // that read them through the service could not tell a refreshed key from a rewritten one.

    private static string PresenceKey(Guid userId, string sid) => $"presence:user:{userId}:session:{sid}";
    private static string SessionsSetKey(Guid userId)           => $"presence:user:{userId}:sessions";
    private static string StatusKey(Guid userId, string sid)    => $"status:user:{userId}:session:{sid}";
    private static string AggregatedKey(Guid userId)            => $"status:user:{userId}:aggregated";

    private async Task<TimeSpan?> TtlOfAsync(string key)
    {
        var pool = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.Cache);

        await using var scope = pool.Rent();

        return await scope.GetDatabase(CacheDb).KeyTimeToLiveAsync(key);
    }

    /// <summary>
    /// Pulls a key's expiry forward, so a test can reach the moment a 120 s TTL lapses without
    /// spending 120 s getting there. Only the deadline moves; the value and every other behaviour
    /// stay exactly as the product left them.
    /// </summary>
    private async Task ForceExpireAsync(string key, TimeSpan within)
    {
        var pool = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.Cache);

        await using var scope = pool.Rent();

        await scope.GetDatabase(CacheDb).KeyExpireAsync(key, within);
    }

    /// <summary>
    /// The sid the <em>server</em> ended up using for a session, read back out of the live-session
    /// index rather than assumed.
    /// </summary>
    /// <remarks>
    /// Not <see cref="TestUserSession.SessionId"/>, deliberately. The Ion side resolves a sid through
    /// <c>HttpContextExtensions.GetSessionId</c>, which prefers the <c>scid</c> of an <c>ArgonSecure</c>
    /// cookie, then — on a Development host, which is what the test server boots as — an <c>X-Ctt</c>
    /// header, and only then the <c>Sec-Ref</c> the client stamps. Which of those wins is not this
    /// test's subject, and it has changed underneath it before: with no <c>X-Ctt</c> every client of
    /// the process collapsed onto <c>Guid.AllBitsSet</c>, and a test addressing the grain by the
    /// client's own sid was quietly reading a session the Ion call had never touched. The index
    /// answers the only question that matters here — which session did the server just bring online
    /// for this user — and the test prints both so a future divergence is visible rather than silent.
    /// </remarks>
    private async Task<string> ServerSideSidAsync(Guid userId, CancellationToken ct)
    {
        var index = Array.Empty<string>();

        await PollAsync(
            async () => (index = await SessionIndexAsync(userId)).Length > 0,
            PresenceWaits.Settle,
            ct: ct);

        Assert.That(index, Has.Length.EqualTo(1),
            $"expected exactly one live session for {userId}, found: {string.Join(", ", index)}");

        return index[0];
    }

    private async Task<string[]> SessionIndexAsync(Guid userId)
    {
        var pool = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.Cache);

        await using var scope = pool.Rent();

        return (await scope.GetDatabase(CacheDb).SetMembersAsync(SessionsSetKey(userId)))
           .Select(x => x.ToString())
           .ToArray();
    }

    // ── polling ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<bool> PollAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? interval = null,
        CancellationToken ct = default)
    {
        var step  = interval ?? TimeSpan.FromMilliseconds(150);
        var clock = Stopwatch.StartNew();

        while (true)
        {
            if (await condition())
                return true;
            if (clock.Elapsed >= timeout)
                return false;
            await Task.Delay(step, ct);
        }
    }

    /// <summary>Waits for the aggregate to reach <paramref name="expected"/> and returns what it actually is.</summary>
    private async Task<UserStatus> AwaitAggregateAsync(
        Guid userId,
        UserStatus expected,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var observed = UserStatus.Offline;

        await PollAsync(
            async () => (observed = await Presence.GetAggregatedStatusAsync(userId, ct)) == expected,
            timeout,
            ct: ct);

        return observed;
    }

    /// <summary>
    /// Brings a session up the way the hub does — attach, then heartbeat a status — and waits for the
    /// aggregate to reflect it, so a test never starts measuring before its own setup landed.
    /// </summary>
    private async Task<IUserSessionGrain> StartSessionAsync(
        Guid userId,
        string sid,
        string connectionId,
        UserStatus status,
        CancellationToken ct)
    {
        var grain = OwnedSession(userId, sid);

        await grain.AttachConnectionAsync(connectionId);
        await grain.HeartBeatAsync(connectionId, status);

        var reached = await AwaitAggregateAsync(userId, status, PresenceWaits.Settle, ct);

        Assert.That(reached, Is.EqualTo(status),
            $"setup: session {sid} of user {userId} never reached {status} (aggregate is {reached})");

        return grain;
    }

    // ── caller context ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a grain call as <paramref name="userId"/>. The space read side reads the caller off the
    /// request context, which a direct grain call from a test does not otherwise carry.
    /// </summary>
    private static async Task<T> AsCallerAsync<T>(Guid userId, Func<Task<T>> call)
    {
        Orleans.Runtime.RequestContext.Set("$caller_user_id", userId);
        try
        {
            return await call();
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }
    }

    private async Task<Guid> CreateSpaceForAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(
            new CreateServerRequest("Presence Space", "Presence fixture", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
            Assert.Fail($"setup: could not create a space for {owner.UserId}: {(result as FailedCreateSpace)?.error}");

        return ((SuccessCreateSpace)result).space.spaceId;
    }

    // ── revocation, as the product writes it ─────────────────────────────────────────────────────

    private IArgonCacheDatabase Cache
        => FactoryAsp.Services.GetRequiredService<IArgonCacheDatabase>();

    /// <summary>
    /// Tombstones one session id for a user — the first thing <c>SecurityGrain.EndSessionAsync</c>
    /// does, written through the product's own key builder.
    /// </summary>
    /// <remarks>
    /// The grain gate is driven directly rather than through <c>ISecurityInteraction.RevokeSession</c>
    /// because that call refuses a sid it cannot find on the devices screen, and two of the three
    /// cases below are about ids that were never a row: a credential id the server minted, and a
    /// session that has not connected yet. The tombstone is the same either way.
    /// </remarks>
    private Task TombstoneAsync(Guid userId, string sessionId)
        => Cache.SetAddAsync(SessionRevocation.RevokedKey(userId), sessionId);

    /// <summary>
    /// Records a credential session id against a presence session, as the sign-in and refresh paths do.
    /// </summary>
    private Task RememberCredentialAsync(Guid userId, string presenceSessionId, Guid credentialSessionId)
        => Cache.SetAddAsync(
            SessionRevocation.CredentialsKey(userId, Guid.Parse(presenceSessionId)),
            credentialSessionId.ToString());

    private Task<List<MemberPresence>> RosterPresenceAsync(Guid spaceId, Guid callerId)
        => AsCallerAsync(callerId, () => GetGrainFactory().GetGrain<ISpaceReadGrain>(spaceId).GetPresence());

    private Task<RealtimeServerMember> RosterMemberAsync(Guid spaceId, Guid userId)
        => AsCallerAsync(userId, () => GetGrainFactory().GetGrain<ISpaceGrain>(spaceId).GetMember(userId));

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  Grace semantics
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Losing the last connection is not going offline; saying "I am going offline" is.
    /// </summary>
    /// <remarks>
    /// The whole grace mechanism exists for the gap between those two. A laptop lid closing, a train
    /// tunnel, a Wi-Fi handover — every one of them drops the transport and reconnects seconds later,
    /// and a user whose avatar greys out and comes back on every one of those has a client that looks
    /// broken. So the drop must change nothing observable, and the deliberate exit must change it at
    /// once, with no window in which a logged-out user still reads as present.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Dropping_the_last_connection_holds_presence_while_GoOffline_ends_it(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);
        var sid  = NewSid();

        var grain = await StartSessionAsync(user.UserId, sid, "c1", UserStatus.Online, ct);

        await grain.DetachConnectionAsync("c1");

        // A fixed wait, not a poll: the assertion is that nothing happens. Longer than any
        // synchronous path off DetachConnectionAsync, and well inside the session TTL — which is what
        // the grace reminder waits for before it finalizes anything, so nothing here can go offline.
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var graceAggregate = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var graceOnline    = await Presence.IsUserOnlineAsync(user.UserId, ct);
        var graceAlive     = await Presence.IsSessionAliveAsync(user.UserId, sid, ct);

        await grain.GoOfflineAsync();

        var wentOffline = await PollAsync(
            async () => await Presence.GetAggregatedStatusAsync(user.UserId, ct) == UserStatus.Offline
                     && !await Presence.IsUserOnlineAsync(user.UserId, ct),
            PresenceWaits.Immediately,
            PresenceWaits.PollStep,
            ct);

        var leftAlive    = await Presence.IsSessionAliveAsync(user.UserId, sid, ct);
        var leftSessions = await Presence.GetActiveSessionIdsAsync(user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(graceAggregate, Is.EqualTo(UserStatus.Online),
                "a dropped transport greyed the user out immediately — the grace window did nothing");
            Assert.That(graceOnline, Is.True, "IsUserOnline went false the moment the socket dropped");
            Assert.That(graceAlive, Is.True, "the session's presence key was dropped with its connection");

            Assert.That(wentOffline, Is.True,
                "GoOffline did not take the user offline within a second — a signed-out user still reads as present");
            Assert.That(leftAlive, Is.False, "GoOffline left the session's presence key behind");
            Assert.That(leftSessions, Does.Not.Contain(sid), "GoOffline left the sid in the live-session index");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H13 — Heartbeat(Offline)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A client cannot make itself invisible by heartbeating <c>Offline</c>.
    /// </summary>
    /// <remarks>
    /// Invisibility is a product decision, not something a status byte on the heartbeat should be
    /// able to buy: a session that is connected and receiving events but reads as Offline to everyone
    /// is indistinguishable from a ghost, and nothing else in the system is built to expect it. The
    /// grain maps the value to Online, and the case worth pinning is the awkward one — <c>Offline</c>
    /// arriving as the very first thing a session ever says, before any attach, where it also seeds
    /// the session's preferred status.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Heartbeating_Offline_never_makes_a_live_session_offline(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        // (a) Offline as the opening move: no attach has run, so this call both starts the session
        //     and asks it to be invisible.
        var firstSid   = NewSid();
        var firstGrain = OwnedSession(user.UserId, firstSid);

        await firstGrain.HeartBeatAsync("c1", UserStatus.Offline);

        var openingAggregate = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var openingOnline    = await Presence.IsUserOnlineAsync(user.UserId, ct);

        await firstGrain.GoOfflineAsync();

        // (b) Offline from an established Online session.
        var secondSid   = NewSid();
        var secondGrain = await StartSessionAsync(user.UserId, secondSid, "c1", UserStatus.Online, ct);

        await secondGrain.HeartBeatAsync("c1", UserStatus.Offline);

        var establishedAggregate = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var establishedOnline    = await Presence.IsUserOnlineAsync(user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(openingAggregate, Is.EqualTo(UserStatus.Online),
                "a session whose first heartbeat said Offline came up invisible");
            Assert.That(openingOnline, Is.True, "the session was not counted as online at all");

            Assert.That(establishedAggregate, Is.EqualTo(UserStatus.Online),
                "an Offline heartbeat took a live session's status offline");
            Assert.That(establishedOnline, Is.True, "an Offline heartbeat unlisted a live session");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H3 — statuses the aggregate does not know about
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A connected session that reports TouchGrass, InGame or Listen is not offline.
    /// </summary>
    /// <remarks>
    /// <para>These three are real statuses: the enum has them, the desktop client has a label and a
    /// colour for each, it persists TouchGrass as a preferred status across restarts, and its member
    /// list sorts them into the online buckets. So a user who picks TouchGrass has picked a status,
    /// not a way of vanishing.</para>
    ///
    /// <para>The failure this pins is worse than a wrong colour: the aggregate is what the space
    /// roster, the snapshot and the friends fan-out all read, so the user is offline everywhere at
    /// once while their client sits there connected and heartbeating. The space read is included for
    /// exactly that reason — it is the surface the user's friends are looking at.</para>
    ///
    /// <para><b>The contract (defect S1, fixed).</b>
    /// <c>UserPresenceService.RecalculateAggregatedStatusAsync</c>
    /// (<c>src/Argon.Core/Features/Logic/IUserPresenceService.cs</c>) is a total precedence fold: it
    /// ranks every declared status, carries the winning session's value verbatim instead of
    /// normalising it, and ranks a value it does not recognise (a newer peer's enum member) at the
    /// Online tier rather than dropping it. It used to have branches for <c>DoNotDisturb</c>,
    /// <c>Online</c> and <c>Away</c> only, starting the fold at <c>Offline</c>, so <c>TouchGrass</c>,
    /// <c>InGame</c> and <c>Listen</c> matched no branch and the fold wrote <c>Offline</c> to
    /// <c>status:user:{u}:aggregated</c> for a connected, heartbeating user. The invariant that
    /// removes the whole class: <b>no connected session can fold to Offline.</b> The space reads are
    /// asserted alongside the aggregate because they are the surfaces this reached.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_session_reporting_TouchGrass_InGame_or_Listen_is_not_read_as_offline(CancellationToken ct = default)
    {
        var user    = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceForAsync(user, ct);
        var sid     = NewSid();

        var grain = await StartSessionAsync(user.UserId, sid, "c1", UserStatus.Online, ct);

        var observed = new List<(UserStatus sent, UserStatus aggregate, bool online)>();

        foreach (var status in new[] { UserStatus.TouchGrass, UserStatus.InGame, UserStatus.Listen })
        {
            await grain.HeartBeatAsync("c1", status);

            // The status write is synchronous inside the call; this only absorbs Redis latency, and
            // gives a correct implementation every chance to land the value before it is read.
            await PollAsync(
                async () => await Presence.GetAggregatedStatusAsync(user.UserId, ct) == status,
                PresenceWaits.Converge,
                ct: ct);

            observed.Add((status,
                await Presence.GetAggregatedStatusAsync(user.UserId, ct),
                await Presence.IsUserOnlineAsync(user.UserId, ct)));
        }

        // What the user's own space says about them while all of the above is true. GetPresence is
        // held for a second, so the read is retried across that window rather than raced with it.
        await PollAsync(
            async () => (await RosterPresenceAsync(spaceId, user.UserId))
               .FirstOrDefault(x => x.userId == user.UserId)?.status != UserStatus.Offline,
            PresenceWaits.Settle,
            ct: ct);

        var roster = (await RosterPresenceAsync(spaceId, user.UserId)).FirstOrDefault(x => x.userId == user.UserId);
        var member = await RosterMemberAsync(spaceId, user.UserId);

        Assert.Multiple(() =>
        {
            foreach (var (sent, aggregate, online) in observed)
            {
                Assert.That(online, Is.True, $"the session stopped counting as online after heartbeating {sent}");
                Assert.That(aggregate, Is.Not.EqualTo(UserStatus.Offline),
                    $"a connected session heartbeating {sent} aggregates to Offline — it is hidden everywhere");
                Assert.That(aggregate, Is.EqualTo(sent),
                    $"{sent} did not survive the aggregate; the client has a label for it and will never see it");
            }

            Assert.That(roster, Is.Not.Null, "the user is missing from their own space's presence list");
            Assert.That(roster!.status, Is.Not.EqualTo(UserStatus.Offline),
                "the space roster shows a connected, heartbeating member as offline");
            Assert.That(member.status, Is.Not.EqualTo(UserStatus.Offline),
                "SpaceGrain.GetMember shows a connected, heartbeating member as offline");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H12 — two windows on one session
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Closing one window of a two-window session changes nothing, and reopening one within the grace
    /// does not reset the user to Online.
    /// </summary>
    /// <remarks>
    /// Two windows of the same client share a sid and therefore a session grain; only the connection
    /// set differs. That makes three separate promises, and this walks all three: a detach that is not
    /// the last one is invisible, the last detach opens the grace rather than closing the session, and
    /// a reconnect inside the grace resumes the status the session already had. The last is the one
    /// that bites — a DND user whose reconnect flashes them Online has just been announced as
    /// available to everyone they were hiding from.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_second_window_closing_and_reopening_never_changes_the_sessions_status(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);
        var sid  = NewSid();

        var grain = await StartSessionAsync(user.UserId, sid, "c1", UserStatus.DoNotDisturb, ct);

        await grain.AttachConnectionAsync("c2");
        await grain.DetachConnectionAsync("c1");

        // Fixed waits throughout: every assertion here is that a state did NOT move, and each is a
        // negative window rather than a poll for that reason.
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var afterFirstClose = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var aliveFirstClose = await Presence.IsSessionAliveAsync(user.UserId, sid, ct);

        await grain.DetachConnectionAsync("c2");
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var duringGrace      = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var onlineDuringGrace = await Presence.IsUserOnlineAsync(user.UserId, ct);

        await grain.AttachConnectionAsync("c3");
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var afterReconnect = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var onlineAfter    = await Presence.IsUserOnlineAsync(user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterFirstClose, Is.EqualTo(UserStatus.DoNotDisturb),
                "closing one of two windows changed the session's status");
            Assert.That(aliveFirstClose, Is.True, "closing one of two windows dropped the session's presence key");

            Assert.That(duringGrace, Is.EqualTo(UserStatus.DoNotDisturb),
                "the last window closing took the user offline instead of opening the grace window");
            Assert.That(onlineDuringGrace, Is.True, "the last window closing ended the session outright");

            Assert.That(afterReconnect, Is.EqualTo(UserStatus.DoNotDisturb),
                "reconnecting inside the grace reset a Do-Not-Disturb user to Online");
            Assert.That(onlineAfter, Is.True, "the reconnect did not restore the session");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H14 — GoOffline with a second window still open
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Signing out of one window while another is open must not make the user blink offline and back.
    /// </summary>
    /// <remarks>
    /// <para>Both windows share one session grain. Sign-out used to clear the whole connection set and
    /// finalize without asking how many connections were attached, so the aggregate was published as
    /// Offline; the window that was still open kept heartbeating on its 15 s timer, and the next
    /// heartbeat hit the self-heal path (<c>Connections.Add</c> + <c>EnsureSessionStartedAsync</c>) and
    /// brought the session straight back. Everyone watching got an Offline followed by an Online for a
    /// user who never went anywhere.</para>
    ///
    /// <para><b>The contract (defect S9, fixed).</b> Sign-out is connection-scoped:
    /// <c>AppHub.GoOffline</c> calls <c>IUserSessionGrain.GoOfflineAsync(Context.ConnectionId)</c>,
    /// which removes that one connection and finalizes only when it was the last. The remaining window
    /// keeps the session alive, so no Offline is ever published and there is nothing to resurrect. The
    /// same shape is pinned end to end over the real hub in
    /// <c>PresenceLifecycleTests.A_window_signing_out_beside_a_live_window_of_the_same_session_does_not_flap_the_user</c>
    /// and <c>PresenceRevocationTests.A_logout_in_one_window_does_not_tell_the_space_the_user_left</c>;
    /// this one drives the grain directly, so a regression is attributable without the hub in the
    /// picture.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Signing_out_of_one_window_does_not_flap_the_session_offline_then_online(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);
        var sid  = NewSid();

        var grain = await StartSessionAsync(user.UserId, sid, "c1", UserStatus.Online, ct);
        await grain.AttachConnectionAsync("c2");

        // What the hub does when one window signs out: end that connection, not the session.
        await grain.GoOfflineAsync("c1");

        var afterSignOut = await Presence.GetAggregatedStatusAsync(user.UserId, ct);

        // The window that stayed open, on its next timer tick.
        await grain.HeartBeatAsync("c2", UserStatus.Online);

        var settled = await Presence.GetAggregatedStatusAsync(user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterSignOut, Is.Not.EqualTo(UserStatus.Offline),
                "signing out of one window published Offline while another window of the same session was still "
              + "attached — observers see an offline/online pair for a user who never left");
            Assert.That(settled, Is.EqualTo(UserStatus.Online),
                $"and the surviving window's next heartbeat left the user at {settled}");
        });
    }

    /// <summary>
    /// The parameterless sign-out still means "end this whole device", because that is what revoking a
    /// session needs it to mean.
    /// </summary>
    /// <remarks>
    /// <para>The two overloads are deliberately different operations.
    /// <c>GoOfflineAsync(connectionId)</c> is a window closing.
    /// <c>GoOfflineAsync()</c> is the one <c>SecurityGrain.EndSessionAsync</c> calls, and it has to end
    /// the session however many windows it has: making it per-connection would leave a revoked device
    /// alive for as long as it had a second tab open.</para>
    ///
    /// <para>What stops the surviving window resurrecting a revoked session is not this call but the
    /// tombstone <c>EndSessionAsync</c> writes first — <c>EnsureSessionStartedAsync</c> reads
    /// <c>session:revoked:{userId}</c> uncached on every session start, which is why
    /// <c>PresenceRevocationTests.Revoking_a_device_ends_its_presence_and_its_heartbeats_do_not_bring_it_back</c>
    /// is green. A bare <c>GoOfflineAsync()</c> with no tombstone, as below, is not a state the product
    /// reaches: it is asserted here only so that the session-wide meaning of the overload cannot be
    /// quietly narrowed to the connection-scoped one.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_parameterless_sign_out_ends_the_whole_session_however_many_windows_it_has(
        CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);
        var sid  = NewSid();

        var grain = await StartSessionAsync(user.UserId, sid, "c1", UserStatus.Online, ct);
        await grain.AttachConnectionAsync("c2");

        await grain.GoOfflineAsync();

        var aggregated = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var alive      = await Presence.IsSessionAliveAsync(user.UserId, sid, ct);

        Assert.Multiple(() =>
        {
            Assert.That(aggregated, Is.EqualTo(UserStatus.Offline),
                "ending the device left the user online, so revoking a session with two windows open would not "
              + "take it off the air");
            Assert.That(alive, Is.False, "and the session's presence key survived the sign-out");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H11 — the status-change token bucket
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A burst of status changes ends on the status the session actually last sent, and observers
    /// never see one it did not send.
    /// </summary>
    /// <remarks>
    /// <para>The bucket (capacity 5, refilling one token every two seconds) exists so that a client
    /// flapping its status cannot amplify into a broadcast storm across every space the user is in.
    /// Dropping the sixth change is therefore correct. What would not be correct is dropping it and
    /// keeping it: if a throttled change moved the session's preferred status without writing it,
    /// the session would believe it had already published a status it never did, and the next
    /// heartbeat carrying the same value would be a no-op — leaving the user permanently displaying
    /// something they set two statuses ago.</para>
    ///
    /// <para>So the test asserts convergence on the last value once the bucket has refilled, and
    /// samples the aggregate throughout to prove no value the session never sent was ever readable.
    /// The burst is issued as fast as the grain will take it: at half a token per second, a burst
    /// spread over two seconds would have earned the sixth token and the throttle would never
    /// engage.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_throttled_burst_of_status_changes_still_settles_on_the_last_one(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);
        var sid  = NewSid();

        var grain = await StartSessionAsync(user.UserId, sid, "c1", UserStatus.Online, ct);

        var sent     = new[] { UserStatus.Away, UserStatus.Online, UserStatus.Away, UserStatus.Online, UserStatus.Away, UserStatus.DoNotDisturb };
        var everSent = new HashSet<UserStatus>(sent) { UserStatus.Online };

        var observed = new ConcurrentQueue<UserStatus>();

        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                try
                {
                    observed.Enqueue(await Presence.GetAggregatedStatusAsync(user.UserId, sampling.Token));
                    await Task.Delay(50, sampling.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);

        foreach (var status in sent)
            await grain.HeartBeatAsync("c1", status);

        // The bucket refills one token every two seconds — a rate the grain owns and this campaign
        // does not make configurable, so this wait is genuinely three seconds and not a ratio of
        // anything. The client re-asserts its status on its own periodic heartbeat, and the call
        // below is that heartbeat arriving after the burst.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        await grain.HeartBeatAsync("c1", sent[^1]);

        var converged = await PollAsync(
            async () => await Presence.GetAggregatedStatusAsync(user.UserId, ct) == sent[^1],
            PresenceWaits.Settle,
            ct: ct);

        var final = await Presence.GetAggregatedStatusAsync(user.UserId, ct);

        await sampling.CancelAsync();
        await sampler;

        var invented = observed.Distinct().Where(x => !everSent.Contains(x)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(converged, Is.True,
                $"the status the session last sent ({sent[^1]}) never propagated once the bucket refilled — "
              + $"the aggregate settled on {final}, so the throttle swallowed the change permanently");
            Assert.That(invented, Is.Empty,
                $"observers could read {string.Join(", ", invented)} — statuses this session never sent");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H15 — multi-device aggregation
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two devices, one user: Do-Not-Disturb outranks Online outranks Away, and a device leaving
    /// hands the aggregate straight back to whoever is left.
    /// </summary>
    /// <remarks>
    /// The priority itself is a product rule — a user who set DND on their phone has said something
    /// about themselves, not about that device, so the desktop being merely Online must not override
    /// it. The half that is easy to get wrong is the release: when the DND device signs out, the
    /// remaining device's Online has to surface immediately, because "I closed the app that was set
    /// to DND" is exactly the moment a user expects to become reachable again.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_aggregate_of_two_devices_follows_DND_over_Online_over_Away(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        // Online + DND ⇒ DND
        var onlineSid = NewSid();
        var dndSid    = NewSid();

        await StartSessionAsync(user.UserId, onlineSid, "a", UserStatus.Online, ct);
        var dnd = OwnedSession(user.UserId, dndSid);
        await dnd.AttachConnectionAsync("b");
        await dnd.HeartBeatAsync("b", UserStatus.DoNotDisturb);

        var withDnd = await AwaitAggregateAsync(user.UserId, UserStatus.DoNotDisturb, PresenceWaits.Settle, ct);

        // The DND device signs out ⇒ the Online device's status takes over at once.
        await dnd.GoOfflineAsync();

        var releasedImmediately = await PollAsync(
            async () => await Presence.GetAggregatedStatusAsync(user.UserId, ct) == UserStatus.Online,
            PresenceWaits.Immediately,
            PresenceWaits.PollStep,
            ct);
        var afterRelease = await Presence.GetAggregatedStatusAsync(user.UserId, ct);

        await SessionGrain(user.UserId, onlineSid).GoOfflineAsync();

        // Away + Online ⇒ Online
        var awaySid   = NewSid();
        var secondSid = NewSid();

        await StartSessionAsync(user.UserId, awaySid, "a", UserStatus.Away, ct);
        var second = OwnedSession(user.UserId, secondSid);
        await second.AttachConnectionAsync("b");
        await second.HeartBeatAsync("b", UserStatus.Online);

        var awayPlusOnline = await AwaitAggregateAsync(user.UserId, UserStatus.Online, PresenceWaits.Settle, ct);

        await second.GoOfflineAsync();
        await SessionGrain(user.UserId, awaySid).GoOfflineAsync();

        // Away + Away ⇒ Away
        var firstAway  = NewSid();
        var secondAway = NewSid();

        await StartSessionAsync(user.UserId, firstAway, "a", UserStatus.Away, ct);
        var other = OwnedSession(user.UserId, secondAway);
        await other.AttachConnectionAsync("b");
        await other.HeartBeatAsync("b", UserStatus.Away);

        var bothAway = await AwaitAggregateAsync(user.UserId, UserStatus.Away, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(withDnd, Is.EqualTo(UserStatus.DoNotDisturb),
                "a Do-Not-Disturb device was overridden by an Online one");
            Assert.That(releasedImmediately, Is.True,
                $"the Online device's status did not surface within a second of the DND device signing out (read {afterRelease})");
            Assert.That(awayPlusOnline, Is.EqualTo(UserStatus.Online),
                "an idle device held the user at Away while another device was active");
            Assert.That(bothAway, Is.EqualTo(UserStatus.Away),
                "two idle devices did not read as Away");
        });
    }

    /// <summary>
    /// A Do-Not-Disturb device that dies without saying goodbye stops holding the user at DND once
    /// its grace expires.
    /// </summary>
    /// <remarks>
    /// <para>An ungraceful drop is the normal case, not the exotic one: a killed process, a laptop
    /// suspended, a phone that went into a lift. Nothing calls GoOffline, so the only thing that ends
    /// that session is the grace reminder noticing its presence key has lapsed. Until it does, the
    /// dead device's DND is still folded into the aggregate, and the user's other device — sitting
    /// right there, Online — reads as Do-Not-Disturb to everyone.</para>
    ///
    /// <para>The presence key is force-expired rather than waited out, which is what the device's
    /// absence would have done to it one session TTL later; everything after that is the product's
    /// own timing — the grace reminder, whose period is <see cref="PresenceTimingOptions.GracePeriod"/>
    /// bounded below by Orleans' own reminder floor.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_DND_device_that_dies_ungracefully_stops_holding_the_user_at_DND(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        var survivorSid = NewSid();
        var doomedSid   = NewSid();

        await StartSessionAsync(user.UserId, survivorSid, "survivor", UserStatus.Online, ct);

        var doomed = OwnedSession(user.UserId, doomedSid);
        await doomed.AttachConnectionAsync("doomed");
        await doomed.HeartBeatAsync("doomed", UserStatus.DoNotDisturb);

        var held = await AwaitAggregateAsync(user.UserId, UserStatus.DoNotDisturb, PresenceWaits.Settle, ct);
        Assert.That(held, Is.EqualTo(UserStatus.DoNotDisturb), "setup: the DND device never took the aggregate");

        // The device is gone. The transport notices and detaches; nothing else is called.
        await doomed.DetachConnectionAsync("doomed");

        // And its presence key lapses, which is what would have happened on its own one session TTL
        // later. Only the deadline moves; every other behaviour is the product's.
        await ForceExpireAsync(PresenceKey(user.UserId, doomedSid), PresenceWaits.Immediately);

        var clock = Stopwatch.StartNew();

        var released = await PollAsync(
            async () => await Presence.GetAggregatedStatusAsync(user.UserId, ct) == UserStatus.Online,
            PresenceWaits.OfflineDeadline,
            PresenceWaits.Slack,
            ct);

        var elapsed = clock.Elapsed;
        var final   = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
        var index   = await SessionIndexAsync(user.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(released, Is.True,
                $"a dead Do-Not-Disturb device held the user at {final} for {elapsed.TotalSeconds:F0}s while another "
              + "device sat there Online — the grace reminder never finalized it");
            Assert.That(index, Does.Not.Contain(doomedSid),
                "the dead session is still in the live-session index, so its status keeps being folded in");
        });

        TestContext.Out.WriteLine($"aggregate released after {elapsed.TotalSeconds:F1}s");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H2 + H16 — the refresh timer across a reconnect
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A session that drops and reconnects keeps renewing its presence exactly like one that never
    /// dropped.
    /// </summary>
    /// <remarks>
    /// <para>This is the shape of the bug that has no symptom for two minutes and then has every
    /// symptom at once. The status keys carry a 120 s TTL and only the grain's 15 s tick renews them;
    /// heartbeats do not, because they are debounced and only write when the status actually changes.
    /// So a session whose tick stopped looks perfectly healthy — connected, heartbeating, presence key
    /// alive — right up to the moment its status key lapses, at which point the user reads Offline in
    /// every roster, every snapshot and every friends list while <c>IsUserOnline</c> still says they
    /// are there.</para>
    ///
    /// <para>Two users, not two sessions of one user: the aggregated key is per user, so a healthy
    /// session of the same user would keep renewing it and hide half the failure.</para>
    ///
    /// <para>The control session is the H16 case in its own right — a plain long-lived session with
    /// heartbeats and no status changes — and it is here because "the TTL is low" only means something
    /// next to a session that was never disturbed. The last phase pulls both users' keys in to just
    /// over one tick instead of idling to the TTL cliff: a live tick restores them to the full TTL
    /// well inside that, a dead one lets them lapse, and the whole cliff is reached in two ticks
    /// rather than in a session TTL.</para>
    ///
    /// <para><b>The contract this now guards (defect S2, fixed).</b> The refresh tick is a function of
    /// "this session has live connections", not of "this session has just started".
    /// <c>UserSessionGrain.DetachConnectionAsync</c> still disposes <c>refreshTimer</c> when the last
    /// connection goes — that is what lets the TTL lapse so the grace can finalize — and
    /// <c>EnsureRefreshTimer()</c> is now called from <c>AttachConnectionAsync</c> and from every
    /// <c>HeartBeatAsync</c>, not only from <c>OnActivateAsync</c> and <c>EnsureSessionStartedAsync</c>
    /// (which returns immediately once <c>SessionStarted</c> is set, and was why a reconnect left the
    /// timer dead). Re-arming on a draining session is harmless because <c>UserSessionTickAsync</c>
    /// returns at once while the connection set is empty. What this test refuses to let back in: a
    /// reconnected session whose status key TTL drains against a control's (78.9 s vs 108.9 s was the
    /// original measurement), and an aggregate that reads Offline past the 120 s mark while the
    /// presence key is alive, <c>IsUserOnlineAsync</c> says true and the user is sitting in the app
    /// heartbeating.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_session_that_reconnects_within_grace_keeps_renewing_like_one_that_never_dropped(CancellationToken ct = default)
    {
        var subject = await CreateSessionAsync(ct);
        var control = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceForAsync(subject, ct);

        var subjectSid = NewSid();
        var controlSid = NewSid();

        // The control never drops anything. It is the H16 case: one session, heartbeats, no status
        // changes, expected to stay Online indefinitely.
        await StartSessionAsync(control.UserId, controlSid, "ctl", UserStatus.Online, ct);
        var subjectGrain = await StartSessionAsync(subject.UserId, subjectSid, "c1", UserStatus.Online, ct);

        // One full tick has to have run before the drop, or "the timer was lost on reconnect" and
        // "the timer never started" would be the same observation.
        await Task.Delay(PresenceWaits.OneTick, ct);

        // The drop and the reconnect, inside the grace window.
        await subjectGrain.DetachConnectionAsync("c1");
        await subjectGrain.AttachConnectionAsync("c2");
        await subjectGrain.HeartBeatAsync("c2", UserStatus.Online);
        await Task.Delay(PresenceWaits.Immediately, ct);
        await subjectGrain.HeartBeatAsync("c2", UserStatus.Online);

        // Two ticks' worth of quiet. A session whose timer survived has renewed both keys inside the
        // last tick; one whose timer was lost has not touched them since before the drop. Fixed,
        // because the assertion is about a renewal that must have happened, not one to wait for.
        await Task.Delay(PresenceWaits.TwoTicks, ct);

        var subjectStatusTtl = await TtlOfAsync(StatusKey(subject.UserId, subjectSid));
        var subjectAggTtl    = await TtlOfAsync(AggregatedKey(subject.UserId));
        var controlStatusTtl = await TtlOfAsync(StatusKey(control.UserId, controlSid));
        var controlAggTtl    = await TtlOfAsync(AggregatedKey(control.UserId));

        // Fresh means renewed inside the last tick or two. Two ticks of tolerance rather than one
        // keeps a scheduler hiccup from reading as a lost timer; a timer that stopped loses every one.
        var fresh = PresenceWaits.FreshTtlFloor;

        // Reach the TTL cliff early: pull every status key in to just over one tick. A session whose
        // tick is alive puts them back to the full TTL before they lapse.
        var cliff = PresenceWaits.OneTick;

        await ForceExpireAsync(StatusKey(subject.UserId, subjectSid), cliff);
        await ForceExpireAsync(AggregatedKey(subject.UserId), cliff);
        await ForceExpireAsync(StatusKey(control.UserId, controlSid), cliff);
        await ForceExpireAsync(AggregatedKey(control.UserId), cliff);

        await Task.Delay(PresenceWaits.TwoTicks, ct);

        var subjectAggregate = await Presence.GetAggregatedStatusAsync(subject.UserId, ct);
        var subjectOnline    = await Presence.IsUserOnlineAsync(subject.UserId, ct);
        var subjectAlive     = await Presence.IsSessionAliveAsync(subject.UserId, subjectSid, ct);
        var controlAggregate = await Presence.GetAggregatedStatusAsync(control.UserId, ct);

        var roster = (await RosterPresenceAsync(spaceId, subject.UserId))
           .FirstOrDefault(x => x.userId == subject.UserId);

        Assert.Multiple(() =>
        {
            // H16: the undisturbed session.
            Assert.That(controlStatusTtl, Is.Not.Null.And.GreaterThan(fresh),
                $"an undisturbed session is not renewing its status key (ttl {controlStatusTtl}) — the tick is not running at all");
            Assert.That(controlAggTtl, Is.Not.Null.And.GreaterThan(fresh),
                $"an undisturbed session is not renewing the aggregated key (ttl {controlAggTtl})");
            Assert.That(controlAggregate, Is.EqualTo(UserStatus.Online),
                "an undisturbed, heartbeating session dropped to Offline past the TTL cliff");

            // H2: the session that dropped and came back.
            Assert.That(subjectStatusTtl, Is.Not.Null.And.GreaterThan(fresh),
                $"after a detach/attach cycle the session stopped renewing status:user:*:session:* (ttl {subjectStatusTtl}, "
              + $"control {controlStatusTtl}) — the refresh timer was disposed on detach and never re-armed");
            Assert.That(subjectAggTtl, Is.Not.Null.And.GreaterThan(fresh),
                $"after a detach/attach cycle nothing renews status:user:*:aggregated (ttl {subjectAggTtl}, control {controlAggTtl})");

            Assert.That(subjectAggregate, Is.EqualTo(UserStatus.Online),
                $"a connected, heartbeating session reads as {subjectAggregate} past the TTL cliff "
              + $"(its presence key is still alive: {subjectAlive}, IsUserOnline: {subjectOnline}) — "
              + "every roster and snapshot shows this user offline while they are sitting in the app");
            Assert.That(roster?.status, Is.EqualTo(UserStatus.Online),
                $"the user's own space shows them as {roster?.status} while they are connected and heartbeating");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H18 — the Ion Dispatch pseudo-connection
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A heartbeat sent over the Ion <c>Dispatch</c> RPC must not keep a session alive after its real
    /// transport is gone.
    /// </summary>
    /// <remarks>
    /// <para><c>EventBusImpl.DispatchTree</c> has no transport connection id to work with, so it
    /// heartbeats the session grain using the sid itself as a pseudo-connection. Nothing ever detaches
    /// it. Every real connection can then drop and the grain still counts one attached connection: no
    /// grace is armed, the 15 s tick keeps renewing the presence key and extending the activation, and
    /// the user is online for as long as the silo lives.</para>
    ///
    /// <para>The desktop client does not call Dispatch, but the RPC is exposed to every authenticated
    /// client, so this is a one-line way for anything holding a token to pin a user online. The grain
    /// is addressed by the sid the server itself recorded rather than the one the client sent — see
    /// <see cref="ServerSideSidAsync"/> for why those differ against a Development host.</para>
    ///
    /// <para><b>The contract this now guards (defect S10, fixed).</b> Only the transport layer may put
    /// anything into <c>UserSessionActivationState.Connections</c>, because only the transport layer
    /// can take it out again. <c>EventBusImpl.DispatchTree</c> used to heartbeat
    /// <c>HeartBeatAsync(sessionId.ToString(), status)</c>, making the sid itself a member of that set
    /// that nothing ever removed — <c>AppHub.OnDisconnectedAsync</c> detaches the SignalR
    /// ConnectionId, and no other caller passes the sid — so <c>DetachConnectionAsync</c> saw
    /// <c>Count &gt; 0</c> for every real disconnect, armed no grace, and left the 15 s tick renewing
    /// the presence key and calling <c>DelayDeactivation</c> for the life of the silo. It now calls
    /// <c>IUserSessionGrain.TouchAsync(status)</c>, which does everything a heartbeat does except join
    /// the connection set. That bounds an Ion keep-alive at one 120 s TTL rather than for ever: with
    /// no attached transport the tick no-ops, so this test's draining presence key and the grace that
    /// finally finalizes the session are both the designed behaviour, not an accident of timing.</para>
    ///
    /// <para><b>And the other half, which is where the bound comes from.</b> A keep-alive may not
    /// <em>start</em> a session either. <c>TouchAsync</c> used to run the whole start path for a sid
    /// that had never connected — presence key, live-session index, a row on the user's own devices
    /// screen — so one RPC from anything holding a token minted a device that does not exist, that
    /// nobody can detach and that no grace will ever be armed for. It answers false for an unstarted
    /// session now, which is what the first step below pins; the TTL evidence after it is about a
    /// session a real transport did start.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_Ion_dispatched_heartbeat_does_not_keep_a_disconnected_session_online(CancellationToken ct = default)
    {
        var user   = await CreateSessionAsync(ct);
        var events = user.Client.ForService<IEventBus>(FactoryAsp.Services);

        // Nothing has ever connected as this user. The Ion keep-alive must refuse rather than invent a
        // session: the RPC fails (EventBusImpl turns the grain's "no" into a dropped connection) and,
        // more to the point, nothing is written.
        string? refusal = null;

        try
        {
            await events.Dispatch(new HeartBeatEvent(UserStatus.Online), ct);
        }
        catch (Exception e)
        {
            refusal = $"{e.GetType().Name}: {e.Message}";
        }

        var indexAfterRefusal = await SessionIndexAsync(user.UserId);

        Assert.That(indexAfterRefusal, Is.Empty,
            "an Ion heartbeat started a session with no transport behind it: a live row nothing can ever detach, "
          + $"on the user's own devices screen, for as long as the caller keeps calling (the RPC {(refusal is null
              ? "was accepted" : $"failed with {refusal}")})");

        // A real transport starts the session, the way the hub does. Addressed by the sid the Ion side
        // will resolve for this client, because the two have to be the same session for the rest of
        // this test to mean anything — ServerSideSidAsync below is what proves they are.
        var sid   = user.SessionId.ToString();
        var grain = OwnedSession(user.UserId, sid);

        await grain.AttachConnectionAsync("transport-1");

        var startedOnline = await AwaitAggregateAsync(user.UserId, UserStatus.Online, PresenceWaits.Settle, ct);
        Assert.That(startedOnline, Is.EqualTo(UserStatus.Online), "setup: the session never came online");

        var serverSideSid = await ServerSideSidAsync(user.UserId, ct);

        TestContext.Out.WriteLine($"client sid {user.SessionId}, server-side sid {serverSideSid}");

        Assert.That(serverSideSid, Is.EqualTo(sid),
            "the server resolved a different sid for this client than the one it sent, so the Dispatch below would "
          + "reach a session this test never started — see ServerSideSidAsync");

        // Now the Ion path, which heartbeats the session grain with no connection id at all.
        await events.Dispatch(new HeartBeatEvent(UserStatus.Online), ct);

        // The device really disconnects.
        await grain.DetachConnectionAsync("transport-1");

        var ttlAtDrop = await TtlOfAsync(PresenceKey(user.UserId, sid));

        // Two ticks and more. A fixed wait because the assertion is that nothing renewed the key.
        await Task.Delay(PresenceWaits.TwoTicks, ct);

        var ttlAfter = await TtlOfAsync(PresenceKey(user.UserId, sid));

        // A draining key has lost those two ticks off its TTL. A renewed one is back at the full TTL.
        Assert.That(ttlAfter, Is.Not.Null, "the presence key vanished outright; the grace should have owned this");
        Assert.That(ttlAfter!.Value, Is.LessThanOrEqualTo(PresenceWaits.DrainedTtlCeiling),
            $"two ticks after the last real connection dropped, the presence key is still being renewed "
          + $"(ttl {ttlAfter} vs {ttlAtDrop} at the drop). The Dispatch pseudo-connection is never detached, "
          + "so the grain still counts a live connection, arms no grace and keeps the user online for ever");

        var wentOffline = await PollAsync(
            async () => !await Presence.IsUserOnlineAsync(user.UserId, ct)
                     && await Presence.GetAggregatedStatusAsync(user.UserId, ct) == UserStatus.Offline,
            PresenceWaits.OfflineDeadline,
            PresenceWaits.Slack,
            ct);

        Assert.That(wentOffline, Is.True,
            "the session never went offline after its last real connection dropped — the grace was never armed");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H7 — the device-switch race
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Switching device — the old session signing out while the new one signs in — never leaves the
    /// user offline.
    /// </summary>
    /// <remarks>
    /// <para>Closing the desktop and picking up the phone is two independent writes to the same
    /// aggregate, and <c>RecalculateAggregatedStatusAsync</c> is a read-fold-write with nothing
    /// serialising it — while <c>UserGrain</c> is a <c>[StatelessWorker]</c>, so several activations of
    /// it can be folding at once. The losing fold can be the one that read the world before the new
    /// session existed and after the old one was removed, and it writes Offline over a user who is
    /// sitting there on their phone. Nothing recomputes afterwards, so the state is not transient: it
    /// is where the user stays until they next change something.</para>
    ///
    /// <para>A race needs repetition to be evidence, so this runs the switch thirty times on fresh
    /// sids and reports how many left the aggregate wrong. One failure is a real failure — this is a
    /// state a user can land in and not get out of.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Switching_device_never_leaves_the_user_reading_offline(CancellationToken ct = default)
    {
        const int rounds = 30;

        var user     = await CreateSessionAsync(ct);
        var failures = new List<string>();

        for (var round = 0; round < rounds; round++)
        {
            var leavingSid  = NewSid();
            var arrivingSid = NewSid();

            var leaving  = SessionGrain(user.UserId, leavingSid);
            var arriving = SessionGrain(user.UserId, arrivingSid);

            await leaving.AttachConnectionAsync("old");
            await leaving.HeartBeatAsync("old", UserStatus.Online);

            var ready = await PollAsync(
                async () => await Presence.GetAggregatedStatusAsync(user.UserId, ct) == UserStatus.Online,
                PresenceWaits.Settle,
                ct: ct);

            Assert.That(ready, Is.True, $"setup: round {round} never got the leaving session online");

            var signOut = leaving.GoOfflineAsync().AsTask();
            var signIn = Task.Run(async () =>
            {
                await arriving.AttachConnectionAsync("new");
                await arriving.HeartBeatAsync("new", UserStatus.Online);
            }, CancellationToken.None);

            await Task.WhenAll(signOut, signIn);

            // Generous, so a slow fold is not counted as a lost one. The failure mode is permanent:
            // nothing recomputes the aggregate after this point.
            var stillOnline = await PollAsync(
                async () => await Presence.GetAggregatedStatusAsync(user.UserId, ct) == UserStatus.Online,
                PresenceWaits.Converge,
                ct: ct);

            if (!stillOnline)
            {
                var observed = await Presence.GetAggregatedStatusAsync(user.UserId, ct);
                var online   = await Presence.IsUserOnlineAsync(user.UserId, ct);
                failures.Add($"round {round}: aggregate={observed}, IsUserOnline={online}");
            }

            await arriving.GoOfflineAsync();
        }

        Assert.That(failures, Is.Empty,
            $"{failures.Count}/{rounds} device switches left the user reading offline while their new device "
          + $"was connected and heartbeating:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  H21 — the devices screen
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The devices screen lists sessions that are connected, and only those.
    /// </summary>
    /// <remarks>
    /// <para>This is the screen a user opens when they think somebody else is in their account, so
    /// both errors are bad in a specific way. A row for a session that never connected — a ticket
    /// picked up and abandoned, a client that crashed during startup — is an unexplained device on a
    /// security screen, which is exactly the alarm that screen exists to raise honestly. A missing row
    /// for a session that <em>is</em> connected is a device the user cannot sign out.</para>
    ///
    /// <para>The three states walked here are the three the list is built from: described but never
    /// present (PickTicket writes the naming record and no presence), present, and deliberately
    /// ended.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_devices_screen_lists_connected_sessions_and_only_those(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        // A ticket is picked up and nothing ever connects with it.
        await user.Client.ForService<IEventBus>(FactoryAsp.Services).PickTicket(ct);

        var afterTicketOnly = (await user.Security.GetSessions(ct)).Select(x => x.sessionId).ToList();

        // A session that actually connects.
        var connectedSid = Guid.CreateVersion7();
        var connected    = OwnedSession(user.UserId, connectedSid.ToString());

        await connected.AttachConnectionAsync("c1");
        await connected.HeartBeatAsync("c1", UserStatus.Online);

        var whileConnected = (await user.Security.GetSessions(ct)).Select(x => x.sessionId).ToList();

        await connected.GoOfflineAsync();

        var afterSignOut = (await user.Security.GetSessions(ct)).Select(x => x.sessionId).ToList();

        Assert.Multiple(() =>
        {
            // Empty rather than "does not contain the client's sid": the server may have recorded the
            // ticket under a sid of its own choosing, and the promise is that no never-connected
            // session appears at all, whatever it was called.
            Assert.That(afterTicketOnly, Is.Empty,
                $"a session that only asked for a ticket and never connected is listed as a device "
              + $"(client sid {user.SessionId}, listed: {string.Join(", ", afterTicketOnly)})");
            Assert.That(whileConnected, Does.Contain(connectedSid),
                "a connected session is missing from the devices screen — the user cannot sign it out");
            Assert.That(afterSignOut, Does.Not.Contain(connectedSid),
                "a session that signed out is still listed as a live device");
        });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  The liveness floor under the connection set
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A connection nobody has heard from stops counting, and takes the session offline through the
    /// grace exactly as a reported disconnect would.
    /// </summary>
    /// <remarks>
    /// <para>Connection ids go into the session's set on an attach and come out on a detach, and the
    /// detach is the half that is not guaranteed: <c>AppHub.OnDisconnectedAsync</c> is a callback that
    /// can fault on its grain call or never run at all when a node goes down under it, and nothing
    /// else in the product removes an id. What that leaves is self-sustaining in the worst direction —
    /// the session's tick renews the presence key and re-extends the activation for as long as the set
    /// is non-empty, and a per-connection sign-out finds it non-empty and declines to finalize — so
    /// one lost callback pinned a user Online to every roster, with a device row they could not get
    /// rid of, until the silo restarted.</para>
    ///
    /// <para>So this test never detaches anything. It attaches a connection, lets it speak once, and
    /// then goes quiet in exactly the way a dead transport does. The evidence is in two parts because
    /// they fail differently: the presence key ceasing to be renewed says the connection was actually
    /// dropped rather than merely ignored, and the user reaching Offline says the drop was routed
    /// through the same grace a detach uses instead of being a second, quieter way to be online for
    /// ever. Both are read against the host's own configured floor
    /// (<c>PresenceTimingOptions.StaleConnectionAfter</c>) rather than against a number written here,
    /// like every other wait in this fixture.</para>
    ///
    /// <para><b>And a control, because the floor cuts both ways.</b> A second account is attached
    /// beside the ghost and speaks on a cadence inside the floor — the shape of a client whose page is
    /// backgrounded and whose timers the browser has throttled to a crawl, which is the most ordinary
    /// state a web client has. It must be untouched by the sweep: still attached, still renewing, still
    /// Online. Without it this test is one-sided, and a floor low enough to pass it — or a sweep that
    /// reads "quiet" as "gone" too eagerly — would take every minimized window offline mid-session
    /// while its socket is open, which is a far worse bug than the ghost connection the floor is for.
    /// A separate account rather than a second session of the same one, so that the ghost's own
    /// account really does reach Offline rather than being held up by its control.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_connection_that_stops_being_heard_from_is_dropped_like_one_that_detached(
        CancellationToken ct = default)
    {
        var ghostUser = await CreateSessionAsync(ct);
        var liveUser  = await CreateSessionAsync(ct);

        var floor = PresenceWaits.Timings.StaleConnectionAfter;

        var ghostSid = NewSid();
        var liveSid  = NewSid();

        var ghost = OwnedSession(ghostUser.UserId, ghostSid);
        var live  = OwnedSession(liveUser.UserId, liveSid);

        await ghost.AttachConnectionAsync("ghost");
        await ghost.HeartBeatAsync("ghost", UserStatus.Online);

        await live.AttachConnectionAsync("live");
        await live.HeartBeatAsync("live", UserStatus.Online);

        var ghostOnline = await AwaitAggregateAsync(ghostUser.UserId, UserStatus.Online, PresenceWaits.Settle, ct);
        var liveOnline  = await AwaitAggregateAsync(liveUser.UserId, UserStatus.Online, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ghostOnline, Is.EqualTo(UserStatus.Online), "setup: the ghost session never came online");
            Assert.That(liveOnline, Is.EqualTo(UserStatus.Online), "setup: the control session never came online");
        });

        // The control keeps speaking at half the floor: often enough to hold its lease, rarely enough
        // that it is the lease being renewed rather than the ordinary fifteen-second cadence.
        using var stopControl = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var beating = KeepAliveAsync(live, "live", floor / 2, stopControl.Token);

        // The ghost's transport dies here. No detach, no sign-out, no further heartbeat — the
        // connection id is all that is left of it, which is the whole defect.
        var quiet = Stopwatch.StartNew();

        // The floor plus a whole TTL: the session has to notice the silence AND stop renewing before
        // the key can drain to a reading that says nothing renewed it.
        var stoppedRenewing = await PollAsync(
            async () => await TtlOfAsync(PresenceKey(ghostUser.UserId, ghostSid)) is { } ttl
                     && ttl <= PresenceWaits.DrainedTtlCeiling,
            floor + PresenceWaits.PastTtl,
            ct: ct);

        var noticedAfter = quiet.Elapsed;

        var wentOffline = await PollAsync(
            async () => !await Presence.IsUserOnlineAsync(ghostUser.UserId, ct)
                     && await Presence.GetAggregatedStatusAsync(ghostUser.UserId, ct) == UserStatus.Offline,
            PresenceWaits.OfflineDeadline,
            ct: ct);

        var index = await SessionIndexAsync(ghostUser.UserId);

        // Read while the control is still beating, so the readings describe a live session rather than
        // one this test stopped a moment ago.
        var controlTtl       = await TtlOfAsync(PresenceKey(liveUser.UserId, liveSid));
        var controlAggregate = await Presence.GetAggregatedStatusAsync(liveUser.UserId, ct);
        var controlIndex     = await SessionIndexAsync(liveUser.UserId);

        await stopControl.CancelAsync();
        await beating;

        Assert.Multiple(() =>
        {
            Assert.That(stoppedRenewing, Is.True,
                $"{noticedAfter.TotalSeconds:F0}s after its last heartbeat — past the {floor} floor — the session "
              + "was still renewing its presence key for a connection nothing has heard from. A connection id that "
              + "outlives its transport keeps the user online for ever, because nothing else ever removes one");
            Assert.That(wentOffline, Is.True,
                "the stale connection was dropped but the session never finalized: dropping the last connection has "
              + "to arm the grace exactly like a detach, or the user is left present with no session to end");
            Assert.That(index, Does.Not.Contain(ghostSid),
                "the finalized session is still in the live-session index, so its status keeps being folded in");

            Assert.That(controlAggregate, Is.EqualTo(UserStatus.Online),
                $"a session heard from every {floor / 2} — inside the {floor} floor — was taken offline by the "
              + "stale-connection sweep. That is a client sitting in a backgrounded tab with its socket open, "
              + "announced Offline to every space and every friend");
            Assert.That(controlTtl, Is.Not.Null.And.GreaterThanOrEqualTo(PresenceWaits.FreshTtlFloor),
                $"the control session's presence key stopped being renewed (ttl {controlTtl}), so its connection was "
              + "swept out from under it and the grace is already counting");
            Assert.That(controlIndex, Does.Contain(liveSid),
                "the control session left the live-session index while it was still speaking");
        });

        TestContext.Out.WriteLine($"stale connection dropped within {noticedAfter.TotalSeconds:F1}s of a {floor} floor");
    }

    /// <summary>
    /// Heartbeats one session on a fixed cadence until told to stop — a client that is still there.
    /// </summary>
    /// <remarks>
    /// The cancellation is the only ending: a failure inside the loop would otherwise be an unobserved
    /// exception on a background task, which NUnit reports against whichever test happens to be
    /// running when the finalizer runs.
    /// </remarks>
    private static async Task KeepAliveAsync(
        IUserSessionGrain session, string connectionId, TimeSpan every, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(every, ct);
                await session.HeartBeatAsync(connectionId, UserStatus.Online);
            }
        }
        catch (OperationCanceledException)
        {
            // The only way out.
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    //  S6 — a signed-out session, at the grain
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An attach on a signed-out session is refused, and says so, whichever of the session's two ids
    /// the sign-out named.
    /// </summary>
    /// <remarks>
    /// <para>The hub gates on the revocation set before it joins a single group, and this is the
    /// second line behind it: the hub's answer can be a few seconds old, and the grain re-reads
    /// uncached. What made that second line worth little was that it was mute — <c>AttachConnection
    /// Async</c> returned nothing, so the grain refused the session while the connection stayed open
    /// on every one of the user's space groups, receiving messages, presence and typing until the
    /// client's next heartbeat noticed. The refusal is now an answer the hub aborts the connection on,
    /// and this fixture pins the answer; <c>PresenceRevocationTests</c> pins what the socket does with
    /// it.</para>
    ///
    /// <para><b>Three cases, because a session has two ids and a user has a floor.</b> The presence
    /// sid is the row on the devices screen and the caller writes it, so a gate that tests only it is
    /// escaped by generating a new one before reconnecting; the credential sid is minted server-side
    /// inside the token, and <c>SecurityGrain.EndSessionAsync</c> tombstones every one recorded
    /// against the row through <c>SessionRevocation.CredentialsKey</c>. The third case is the one the
    /// gate could not reach at all: a session that was already running when the button was pressed
    /// short-circuits the start path, so it was asked nothing when its next window attached.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_signed_out_session_is_refused_when_it_tries_to_attach(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        // (a) The row the user pressed the button on.
        var revokedSid = NewSid();
        await TombstoneAsync(user.UserId, revokedSid);

        var refusedOutright = await OwnedSession(user.UserId, revokedSid).AttachConnectionAsync("c1");

        // (b) The same device having rotated its presence sid: the label is brand new and nothing has
        // ever tombstoned it, but the credential it is holding was signed out.
        var rotatedSid   = NewSid();
        var credentialId = Guid.CreateVersion7();

        await RememberCredentialAsync(user.UserId, rotatedSid, credentialId);
        await TombstoneAsync(user.UserId, credentialId.ToString());

        var refusedByCredential = await OwnedSession(user.UserId, rotatedSid).AttachConnectionAsync("c1");

        // (c) A session that was live when the sign-out landed, opening a second window.
        var liveSid = NewSid();
        var live    = await StartSessionAsync(user.UserId, liveSid, "c1", UserStatus.Online, ct);

        await TombstoneAsync(user.UserId, liveSid);

        var refusedAfterTheFact = await live.AttachConnectionAsync("c2");

        var revokedAlive = await Presence.IsSessionAliveAsync(user.UserId, revokedSid, ct);
        var rotatedAlive = await Presence.IsSessionAliveAsync(user.UserId, rotatedSid, ct);
        var liveAlive    = await Presence.IsSessionAliveAsync(user.UserId, liveSid, ct);
        var index        = await SessionIndexAsync(user.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(refusedOutright, Is.False,
                "a session whose sid is tombstoned was allowed to attach, so the hub is told nothing and the "
              + "connection stays open on every space group it just joined");
            Assert.That(refusedByCredential, Is.False,
                "a signed-out device came back by minting a fresh presence sid: the gate has to test the "
              + "server-minted credential ids recorded against the session, not only the label the caller chose");
            Assert.That(refusedAfterTheFact, Is.False,
                "a session that was already running when it was signed out accepted another connection — the "
              + "started-session path skips the start gate, so nothing asked");

            Assert.That(revokedAlive, Is.False, "the refused session wrote a presence key anyway");
            Assert.That(rotatedAlive, Is.False, "the rotated-sid session wrote a presence key anyway");
            Assert.That(liveAlive, Is.False,
                "the revoked session kept its presence key: a refused attach on a live session has to end it, "
              + "not merely decline to add a connection");

            Assert.That(index, Does.Not.Contain(revokedSid), "a refused session is in the live-session index");
            Assert.That(index, Does.Not.Contain(rotatedSid), "a refused session is in the live-session index");
            Assert.That(index, Does.Not.Contain(liveSid), "the ended session is still in the live-session index");
        });
    }
}
