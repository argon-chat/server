namespace ArgonComplexTest.Tests;

using System.Diagnostics;
using System.Net.WebSockets;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime.client;

/// <summary>
/// Guards the activity half of presence — "playing X", "listening to Y" — as an observer of a space
/// actually experiences it: the event that announces it, the snapshot that a client paints the space
/// from, and the moment it is supposed to disappear.
/// </summary>
/// <remarks>
/// <para>Status and activity are two different stories the product tells about the same person, and
/// they are told through two different mechanisms: status through the aggregate in Redis that every
/// heartbeat refreshes, activity through a per-session key with a ten-minute lifetime that nothing
/// refreshes at all. A user reading a member list sees them as one thing, so any disagreement
/// between the two — a game badge under an offline name, an activity that a newcomer cannot see
/// while everyone already in the room still can — is a bug the user experiences directly even though
/// no single component is obviously broken.</para>
///
/// <para>Every assertion here is written from the observer's side on purpose. The service-level and
/// grain-level fixtures in this campaign already pin what the Redis keys do; what they cannot say is
/// whether the space stream and <c>GetMemberPresence</c> ever contradict each other, because a client
/// takes its first paint from the snapshot and every correction from the stream. The invariant this
/// fixture is really about is that the two never disagree, and that both stop talking about an
/// activity at the moment the session announcing it stops being alive — not before, not after.</para>
///
/// <para>Two devices of one account are built by signing in a second time with a second header
/// interceptor, because the sid is minted client-side: two <see cref="RealtimeClient"/>s over one
/// <see cref="TestUserSession"/> are two windows of one session and would exercise nothing about the
/// per-session activity keys this fixture is here to check.</para>
///
/// <para>Every wait for an event that <em>must</em> arrive is <see cref="PresenceWaits.Settle"/>,
/// never <see cref="PresenceWaits.NegativeWindow"/>. The two are not interchangeable even though
/// both are a few seconds: the negative window is a length of time to watch for something that
/// should not happen, and using it for something that should makes the assertion "this arrived
/// within four seconds" rather than "this arrived". Announcing an activity from a second device is
/// an Ion RPC, a relational read for the announcer's spaces, a grain hop per space and a SignalR
/// fan-out, all of it against cold activations on a loaded box, so the budget wants to be generous;
/// a passing wait spends nothing, so it costs the suite no time at all.</para>
///
/// <para>A longer budget is not, however, what made the two-device tests stop flaking, and it is
/// worth saying so here because the symptom invites the wrong fix. They failed because the second
/// device announced before its hub attach had put it in the live-session index, so the event they
/// were waiting for was never going to arrive however long they waited — see
/// <see cref="RequireLiveDevicesAsync"/>, which is what establishes that premise now.</para>
/// </remarks>
[TestFixture]
public class PresenceActivityTests : TestBase
{
    private PresenceProbe probe = null!;

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    /// <summary>
    /// A broadcast activity reaches the members of the space over the space stream, verbatim, and the
    /// snapshot a client loads the space with says the same thing.
    /// </summary>
    /// <remarks>
    /// The baseline for everything below: if the event does not carry the same title the client sent,
    /// or the snapshot does not agree with the event, no later assertion about when an activity
    /// disappears would mean anything.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 2)]
    public async Task An_activity_reaches_the_space_stream_and_the_snapshot(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Contract", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var announced = watcher.Mark();
        var activity  = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Deep Rock Galactic");

        await player.Users.BroadcastPresence(activity, ct);

        var record  = await watcher.WaitForRecordAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId, PresenceWaits.Settle, announced, ct);
        var changed = (OnUserPresenceActivityChanged)record.Event;

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == activity.titleName, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(record.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace),
                "an activity has to reach the space group, not only the announcing client's own stream");
            Assert.That(record.SpaceId, Is.EqualTo(spaceId));
            Assert.That(changed.spaceId, Is.EqualTo(spaceId),
                "the event names a different space than the one it was delivered to");
            Assert.That(changed.presence.titleName, Is.EqualTo(activity.titleName),
                "the title the observer was told is not the one the player announced");
            Assert.That(changed.presence.kind, Is.EqualTo(activity.kind));
            Assert.That(changed.presence.startTimestampSeconds, Is.EqualTo(activity.startTimestampSeconds),
                "the start timestamp is what the client renders the elapsed time from");
            Assert.That(snapshot?.titleName, Is.EqualTo(activity.titleName),
                "GetMemberPresence disagrees with the activity the space was just told about over the stream");
            Assert.That(watcher.DecodeFailures, Is.Empty);
        });
    }

    /// <summary>
    /// Clearing an activity is announced as a removal and empties the snapshot.
    /// </summary>
    /// <remarks>
    /// The other half of the contract: a client that closed its game must stop being shown as playing
    /// it, both to the observers already in the room (the event) and to anyone painting the room
    /// afterwards (the snapshot).
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 2)]
    public async Task Clearing_an_activity_removes_it_from_the_stream_and_the_snapshot(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Removal", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.SOFTWARE, StartedNow(), "Rider");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var beforeRemoval = watcher.Mark();
        await player.Users.RemoveBroadcastPresence(ct);

        var removed = await watcher.WaitForRecordAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, PresenceWaits.Settle, beforeRemoval, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(removed.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace));
            Assert.That(removed.SpaceId, Is.EqualTo(spaceId));
            Assert.That(snapshot, Is.Null,
                $"the cleared activity is still in the space snapshot ({snapshot?.titleName})");
        });
    }

    /// <summary>
    /// The activity key is kept alive for as long as the session that announced it is alive and
    /// heartbeating.
    /// </summary>
    /// <remarks>
    /// <para>A session that is still connected, still ticking and still playing the same game is the
    /// most ordinary state this system has, and the activity's lifetime has to survive it. The desktop
    /// client dedupes identical presence and never re-sends, so nothing outside the server will
    /// refresh this key on its behalf; if the server does not, the activity dies under a running game.
    /// The TTL is sampled at the broadcast and again three refresh periods later — long enough for
    /// several of the grain's ticks and several client heartbeats.</para>
    ///
    /// <para>The contract (defect S16, fixed): the session's keep-alive owns the activity's lifetime.
    /// <c>UserPresenceService.RefreshSessionStatusTtlAsync</c> — the call the session grain's tick and
    /// the bot gateway's both make — re-arms <c>activity:user:{u}:session:{sid}</c> alongside the
    /// status keys, so <see cref="PresenceTimingOptions.ActivityTtl"/> stopped being how long a game
    /// may last and became how long an orphaned entry lingers after its session stopped ticking.</para>
    ///
    /// <para>The threshold follows that cadence rather than the sample: a key re-armed to its full
    /// lifetime on every tick is never more than a tick below full whenever it is looked at, so
    /// <see cref="PresenceWaits.FreshActivityTtlFloor"/> — two ticks below full — is "renewed within
    /// the last tick, with slack for a loaded worker". It is not a weakened assertion: a build that
    /// never renews has lost the whole sampling window off the TTL by this point and fails by a
    /// margin of one more tick. Allowing only one tick would demand a renewal the tick itself cannot
    /// promise, which is how an earlier form of this missed by 45 ms deterministically.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_activity_keeps_its_lifetime_refreshed_while_the_session_lives(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Lifetime", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Elden Ring");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var key            = PresenceProbe.ActivitySessionKey(player.UserId, player.SessionId);
        var ttlAtBroadcast = await probe.TtlOf(key);

        // A fixed wait on purpose: the claim is that a value does NOT decay while the session lives,
        // and there is no state change to poll for. The client heartbeat cadence is the real one.
        var alive  = Stopwatch.StartNew();
        var living = PresenceWaits.Ticks(3);

        while (alive.Elapsed < living)
        {
            await playing.Heartbeat(UserStatus.Online, ct);
            await Task.Delay(PresenceWaits.Tick, ct);
        }

        var ttlAfterLiving = await probe.TtlOf(key);
        var stillAlive     = await probe.IsSessionAliveAsync(player.UserId, player.SessionId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stillAlive, Is.True,
                "the session died during the test, so the TTL says nothing about a live session");
            Assert.That(ttlAtBroadcast, Is.Not.Null, "no activity key was written by BroadcastPresence");
            Assert.That(ttlAtBroadcast!.Value,
                Is.GreaterThan(PresenceWaits.FreshActivityTtlFloor)
                   .And.LessThanOrEqualTo(PresenceWaits.ActivityTtl + PresenceWaits.Immediately),
                "the activity key was not written with the configured activity lifetime");
            Assert.That(ttlAfterLiving, Is.Not.Null, "the activity key vanished while the session was alive");
            Assert.That(ttlAfterLiving!.Value, Is.GreaterThanOrEqualTo(PresenceWaits.FreshActivityTtlFloor),
                $"the activity's lifetime is draining under a live, heartbeating session " +
                $"({ttlAtBroadcast.Value.TotalSeconds:F0}s at the broadcast, {ttlAfterLiving.Value.TotalSeconds:F0}s " +
                $"{alive.Elapsed.TotalSeconds:F0}s later) — the session keep-alive is no longer renewing it, so the " +
                "activity dies on the clock under a running game");
        });
    }

    /// <summary>
    /// An activity the client keeps announcing outlives the lifetime it was written with.
    /// </summary>
    /// <remarks>
    /// <para>The renewal above is bounded — <see cref="PresenceTimingOptions.ActivityReassertWindow"/>
    /// — and this is the half of that bound which must keep working: a client that is still there
    /// re-announces a live activity every few minutes, and every announcement re-signs the lease, so
    /// the entry never reaches the far side of its own <see cref="PresenceTimingOptions.ActivityTtl"/>.
    /// Nothing shorter than the TTL can prove it: a sample taken inside the lifetime the entry was
    /// written with cannot distinguish a renewal from the original write, which is exactly why the
    /// test above (three ticks of samples) says nothing about the bound.</para>
    ///
    /// <para>The re-announcement cadence is the shipped ratio, a third of the window, and the
    /// heartbeat is the ordinary one — the tick is what renews, so a session that stopped being heard
    /// from would take the activity down with it and the failure would name the wrong thing.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_activity_that_keeps_being_announced_outlives_its_own_lifetime(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Lease Held", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Outer Wilds");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var key      = PresenceProbe.ActivitySessionKey(player.UserId, player.SessionId);
        var reassert = PresenceWaits.Timings.ActivityReassertWindow / 3;

        // Fixed on purpose: the claim is that something does NOT expire, and there is no edge to poll
        // for. Long enough that a key nothing renewed is certainly gone.
        var alive          = Stopwatch.StartNew();
        var lastReasserted = TimeSpan.Zero;

        while (alive.Elapsed < PresenceWaits.PastActivityTtl)
        {
            await playing.Heartbeat(UserStatus.Online, ct);

            if (alive.Elapsed - lastReasserted >= reassert)
            {
                await player.Users.BroadcastPresence(activity, ct);
                lastReasserted = alive.Elapsed;
            }

            await Task.Delay(PresenceWaits.Tick, ct);
        }

        var stillThere   = await probe.Exists(key);
        var ttl          = await probe.TtlOf(key);
        var sessionAlive = await probe.IsSessionAliveAsync(player.UserId, player.SessionId, ct);
        var snapshot     = await SnapshotOf(observer, spaceId, player.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(sessionAlive, Is.True,
                "the announcing session died during the test, so its activity was allowed to go and this says "
              + "nothing about the lease");
            Assert.That(stillThere, Is.True,
                $"an activity re-announced every {reassert} is gone {alive.Elapsed.TotalSeconds:F0}s later, past its "
              + $"{PresenceWaits.ActivityTtl} lifetime: the lease the session tick renews on is not being re-signed "
              + "by the client's announcements, so a game outlives its badge");
            Assert.That(ttl, Is.Not.Null.And.GreaterThanOrEqualTo(PresenceWaits.FreshActivityTtlFloor),
                $"the activity survived but is draining ({ttl}), so it is living on one write rather than on a lease");
            Assert.That(snapshot?.activity?.titleName, Is.EqualTo(activity.titleName),
                "the space snapshot lost the activity of a session that never stopped announcing it");
        });
    }

    /// <summary>
    /// An activity nobody re-announces lapses once the window closes — the client that died does not
    /// keep playing for ever.
    /// </summary>
    /// <remarks>
    /// <para>The other half of the bound, and the failure it was introduced to prevent. Renewing the
    /// activity for the life of the session cured a game that lapsed under a running client and made
    /// its mirror image permanent: the only thing that erases an activity is the client's explicit
    /// <c>RemoveBroadcastPresence</c>, and a client that is killed outright — or whose removal is lost
    /// to a token refresh or a dropped connection — never sends it, so the tick renewed a finished
    /// game for the rest of the session, hours after it closed.</para>
    ///
    /// <para>Both bounds are asserted, because only the pair distinguishes the fix from either thing
    /// it sits between: the entry must survive past its own <see cref="PresenceTimingOptions.ActivityTtl"/>
    /// (the renewal really was running while the lease held, so this is not the un-renewed behaviour
    /// with a new name) and must be gone within the window plus a lifetime (the renewal really did
    /// stop). The session is kept alive and heartbeating throughout: the only thing that stops is the
    /// announcement, which is precisely what a killed client stops doing.</para>
    /// </remarks>
    [Test, CancelAfter(150_000)]
    public async Task An_activity_nobody_re_announces_lapses_once_the_window_closes(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Lease Lapsed", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Subnautica");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var key       = PresenceProbe.ActivitySessionKey(player.UserId, player.SessionId);
        var window    = PresenceWaits.Timings.ActivityReassertWindow;
        var announced = Stopwatch.StartNew();

        // The game closes without saying so. The client is still there — still connected, still
        // heartbeating — which is the whole point: nothing but the announcement stops.
        using var stopBeating = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var beating = HeartbeatUntilAsync(playing, stopBeating.Token);

        var lapsed = await Poll.UntilAsync(
            async () => !await probe.Exists(key),
            window + PresenceWaits.PastActivityTtl,
            ct: ct);

        var lapsedAfter  = announced.Elapsed;
        var sessionAlive = await probe.IsSessionAliveAsync(player.UserId, player.SessionId, ct);

        await stopBeating.CancelAsync();
        await beating;

        Assert.Multiple(() =>
        {
            Assert.That(sessionAlive, Is.True,
                "the session died before the lease did, so what expired here was a session's activity rather than "
              + "an unattended one");
            Assert.That(lapsed, Is.True,
                $"an activity nobody has announced for {lapsedAfter.TotalSeconds:F0}s — past the {window} re-assert "
              + $"window and a further {PresenceWaits.ActivityTtl} lifetime — is still being renewed by the session "
              + "tick. A client that was killed mid-game leaves the user playing it for the rest of the session");
            Assert.That(lapsedAfter, Is.GreaterThan(PresenceWaits.ActivityTtl),
                $"the activity lapsed after {lapsedAfter.TotalSeconds:F0}s, inside its own "
              + $"{PresenceWaits.ActivityTtl} lifetime: nothing renewed it at all, which is the defect the lease "
              + "replaced rather than the bound on it");
        });
    }

    /// <summary>
    /// When an activity key lapses under a live session, the room is told — so nobody is left
    /// rendering something the snapshot no longer knows about.
    /// </summary>
    /// <remarks>
    /// <para>The ten-minute key is now re-armed by the session's keep-alive (defect S16), so it is a
    /// safety net for an orphan rather than a limit on how long a game may last. A safety net still
    /// fires: a session whose grain stopped ticking while its client kept rendering, a Redis eviction,
    /// a key force-expired here. What must not happen is that it fires silently.</para>
    ///
    /// <para>The asymmetry is the whole point. Once the key is gone,
    /// <c>UserPresenceService.GetUserActivitiesAsync</c> reads nothing for that session and
    /// <c>SpaceReadGrain.GetPresence</c> hands every arriving client <c>activity = null</c> — so the
    /// snapshot is coherent. The members already in the room are not: the only writers of
    /// <c>OnUserPresenceActivityRemoved</c> are <c>UserGrain.RemoveBroadcastPresenceAsync</c> (the
    /// explicit clear) and <c>UserSessionGrain.FinalizeOfflineAsync</c>, and a TTL lapse calls
    /// neither, so they keep the badge for the rest of their client session. Two people in one room
    /// then disagree about whether a third is playing, permanently.</para>
    ///
    /// <para><b>Open defect, still red (S16, residual half).</b> There is no Redis keyspace-expiry
    /// subscriber anywhere in <c>src/</c>, so nothing observes the lapse and nothing emits the
    /// retraction. Closing it needs either that subscriber or a server-side sweep that notices a live
    /// session whose activity key has gone and fans the removal out. The earlier form of this test
    /// asked instead for the newcomer to still SEE the activity, which no fix can satisfy — the
    /// payload lived only in the deleted key and nothing else holds a copy — so it is the retraction
    /// that is pinned here.</para>
    /// </remarks>
    [Test, Category("KnownPresenceBug"), CancelAfter(1000 * 60 * 2)]
    public async Task An_activity_that_lapses_under_a_live_session_is_retracted_from_the_room(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);
        var newcomer = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Lapse", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Hollow Knight");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var key         = PresenceProbe.ActivitySessionKey(player.UserId, player.SessionId);
        var beforeLapse = watcher.Mark();

        // Shortened and then polled for absence, and retried: the session's tick re-arms this key now
        // that S16 is fixed, so a shortening that lands just before a tick is undone once. Each
        // attempt gives the shortened TTL a tick and more to actually lapse.
        var lapsed = false;
        for (var attempt = 0; attempt < 3 && !lapsed; attempt++)
        {
            await probe.ForceExpire(key, PresenceWaits.Immediately);
            lapsed = await Poll.UntilAsync(async () => !await probe.Exists(key), PresenceWaits.OneTick, ct: ct);
        }

        Assert.That(lapsed, Is.True, "the activity key would not expire, so this test cannot say anything");

        // The session never stopped announcing: it is connected and its presence key is alive.
        var sessionAlive = await probe.IsSessionAliveAsync(player.UserId, player.SessionId, ct);

        await JoinAsync(observer, newcomer, spaceId, ct);

        var snapshot = await SnapshotOf(newcomer, spaceId, player.UserId, ct);

        // Fixed window: proving that an event the product never emits does not arrive. Generous
        // enough to cover a session tick and a grace reminder, either of which could carry a sweep.
        var retraction = await watcher.FirstWithinAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, PresenceWaits.GraceAndABit, beforeLapse, ct);

        Assert.Multiple(() =>
        {
            Assert.That(sessionAlive, Is.True,
                "the announcing session was not alive any more, so its activity was allowed to go");
            Assert.That(playing.IsConnected, Is.True);
            Assert.That(snapshot?.activity, Is.Null,
                $"the snapshot still carries the lapsed activity ({snapshot?.activity?.titleName}), so the read "
              + "side and the key disagree");
            Assert.That(retraction, Is.Not.Null,
                "the activity key lapsed under a live session and nobody was told: the members already in the "
              + "room keep rendering a game the snapshot no longer knows about, for the rest of their client "
              + "session, while anyone opening the space afterwards sees nothing");
        });
    }

    /// <summary>
    /// Two devices of one account: the activity observers see is the one that started most recently.
    /// </summary>
    /// <remarks>
    /// The wire carries a single activity per user while the server keeps one per session, so
    /// something has to choose. The choice the product documents is "the latest start wins", and it is
    /// the one a user expects: picking up the phone and starting music while a game runs on the
    /// desktop should show the music.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_newest_activity_across_two_devices_is_the_one_observers_see(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Two Devices", ct);
        await JoinAsync(observer, player, spaceId, ct);

        var phone = await AddDeviceAsync(player, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var desktop = await RealtimeClient.ConnectAsync(player, ct);
        await using var mobile  = await RealtimeClient.ConnectAsync(phone, ct);

        Assert.That(phone.SessionId, Is.Not.EqualTo(player.SessionId),
            "the second device claims the same sid as the first, so this is one session, not two");

        await RequireLiveDevicesAsync(player.UserId, ct, player.SessionId, phone.SessionId);

        var game  = new UserActivityPresence(ActivityPresenceKind.GAME, 100, "Factorio");
        var music = new UserActivityPresence(ActivityPresenceKind.LISTEN, 200, "Rammstein - Sonne");

        await AnnounceAndAwaitAsync(player, watcher, game, ct);

        var beforeMusic = watcher.Mark();
        await phone.Users.BroadcastPresence(music, ct);

        var representative = await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.startTimestampSeconds == music.startTimestampSeconds,
            PresenceWaits.Settle, beforeMusic, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == music.titleName, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(representative.presence.titleName, Is.EqualTo(music.titleName),
                "the observer was told about the older activity after a newer one started on another device");
            Assert.That(snapshot?.titleName, Is.EqualTo(music.titleName),
                "the snapshot still shows the older device's activity");
        });
    }

    /// <summary>
    /// When one device stops, observers fall back to what the other device is still doing — and only
    /// see a removal once nothing is left.
    /// </summary>
    /// <remarks>
    /// Closing the music on the phone while the game is still running on the desktop must leave the
    /// game showing, not blank the activity out. The final step is the opposite case: the last
    /// activity of the account ending is the one that legitimately removes it.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Ending_one_devices_activity_falls_back_to_the_other_device(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Device Fallback", ct);
        await JoinAsync(observer, player, spaceId, ct);

        var phone = await AddDeviceAsync(player, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var desktop = await RealtimeClient.ConnectAsync(player, ct);
        await using var mobile  = await RealtimeClient.ConnectAsync(phone, ct);

        await RequireLiveDevicesAsync(player.UserId, ct, player.SessionId, phone.SessionId);

        var game  = new UserActivityPresence(ActivityPresenceKind.GAME, 100, "Factorio");
        var music = new UserActivityPresence(ActivityPresenceKind.LISTEN, 200, "Rammstein - Sonne");

        await AnnounceAndAwaitAsync(player, watcher, game, ct);

        var beforeMusic = watcher.Mark();
        await phone.Users.BroadcastPresence(music, ct);
        await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.startTimestampSeconds == music.startTimestampSeconds,
            PresenceWaits.Settle, beforeMusic, ct);

        var beforeMusicStops = watcher.Mark();
        await phone.Users.RemoveBroadcastPresence(ct);

        var fellBack = await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.startTimestampSeconds == game.startTimestampSeconds,
            PresenceWaits.Settle, beforeMusicStops, ct);

        // Cheap and free of extra wall clock: the fall-back event has already arrived, so anything
        // that told the observer to drop the activity entirely is by now in the recorded log.
        var prematureRemoval = watcher.EventsOfType<OnUserPresenceActivityRemoved>(beforeMusicStops)
           .Where(e => e.userId == player.UserId)
           .ToList();

        var afterFallback = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == game.titleName, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(fellBack.presence.titleName, Is.EqualTo(game.titleName));
            Assert.That(prematureRemoval, Is.Empty,
                "observers were told the user has no activity at all while another device was still announcing one");
            Assert.That(afterFallback?.titleName, Is.EqualTo(game.titleName),
                "the snapshot lost the surviving device's activity");
        });

        var beforeDesktopLeaves = watcher.Mark();
        await desktop.GoOffline(ct);

        var removed = await watcher.WaitForRecordAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, PresenceWaits.Settle, beforeDesktopLeaves, ct);

        var afterEverything = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(removed.SpaceId, Is.EqualTo(spaceId));
            Assert.That(afterEverything, Is.Null,
                $"the last device's activity survived it going offline ({afterEverything?.titleName})");
        });
    }

    /// <summary>
    /// A session that says goodbye takes its activity with it.
    /// </summary>
    /// <remarks>
    /// Signing out or closing the client is the one disconnect with no ambiguity in it, so the
    /// activity has to go immediately — both as an event to the room and out of the snapshot. Anything
    /// slower leaves a game badge under a name that is already greyed out.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 2)]
    public async Task A_deliberate_offline_clears_the_sessions_activity(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Goodbye", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.STREAMING, StartedNow(), "Some Stream");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var beforeGoodbye = watcher.Mark();
        await playing.GoOffline(ct);

        await watcher.WaitForAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, PresenceWaits.Settle, beforeGoodbye, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, PresenceWaits.Settle, ct);

        var keyLeft = await probe.Exists(PresenceProbe.ActivitySessionKey(player.UserId, player.SessionId));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Null,
                $"a member who went offline is still shown playing something ({snapshot?.titleName})");
            Assert.That(keyLeft, Is.False,
                "the session's activity key outlived the session that owned it");
        });
    }

    /// <summary>
    /// A connection that dies without a close frame loses its activity no later than it loses its
    /// presence — never a game badge under an offline name.
    /// </summary>
    /// <remarks>
    /// <para>The interesting case is not whether the activity eventually goes but whether it goes in
    /// the right order. Status and activity are cleared by two different mechanisms with two different
    /// lifetimes (two minutes against ten), so a drop is exactly where they can come apart, and the
    /// direction that hurts is the activity surviving the presence.</para>
    ///
    /// <para>The grace cannot be hurried — an Orleans reminder is floored at
    /// <see cref="PresenceTimingOptions.ReminderFloor"/> — but the presence key can be shortened so
    /// that the first reminder tick already finds the session dead and finalizes it. What is left is
    /// the product's own clock, and the whole of what this test spends.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_ungraceful_drop_takes_the_activity_no_later_than_the_presence(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Drop", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Deep Rock Galactic");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var beforeDrop = watcher.Mark();
        await playing.AbortAsync(ct: ct);

        // Nothing refreshes a detached session's presence key, so shortening it is safe and makes the
        // first grace tick find the session already gone instead of waiting out the full TTL.
        await probe.ForceExpire(PresenceProbe.PresenceSessionKey(player.UserId, player.SessionId),
            PresenceWaits.Immediately);

        var offline = await watcher.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == player.UserId && e.status == UserStatus.Offline,
            PresenceWaits.GraceAndABit, beforeDrop, ct);

        var removed = await watcher.WaitForRecordAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, PresenceWaits.Settle, beforeDrop, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(removed.ReceivedAt, Is.LessThanOrEqualTo(offline.ReceivedAt + PresenceWaits.Slack),
                $"the activity outlived the presence: Offline at {offline.ReceivedAt:HH:mm:ss.fff}, " +
                $"activity removed at {removed.ReceivedAt:HH:mm:ss.fff}");
            Assert.That(snapshot, Is.Null,
                $"the dropped session's activity is still in the snapshot ({snapshot?.titleName})");
        });
    }

    /// <summary>
    /// A reconnect inside the disconnect grace keeps the activity: no removal is announced and the
    /// snapshot does not change.
    /// </summary>
    /// <remarks>
    /// A lid closing or a tunnel is not the end of a game. The whole point of the grace is that a
    /// transient drop is invisible to everyone else, and an activity that blinked out and back would
    /// be as visible as a status flap — more so, because the client renders it as a line of text.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 2)]
    public async Task A_reconnect_inside_the_grace_keeps_the_activity(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Grace", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Baldur's Gate 3");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var beforeDrop = watcher.Mark();
        await playing.AbortAsync(ct: ct);

        // Same sid, new connection — the shape a client has after a network blip.
        await using var reconnected = await RealtimeClient.ConnectAsync(player, ct);
        Assert.That(reconnected.SessionId, Is.EqualTo(player.SessionId));

        // A fixed window because the claim is that nothing happens; five seconds covers the detach,
        // the re-attach and any fan-out either of them could have triggered.
        var removal = await watcher.FirstWithinAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, PresenceWaits.Settle, beforeDrop, ct);

        var snapshot = await SnapshotOf(observer, spaceId, player.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(removal, Is.Null,
                $"a reconnect inside the grace announced the activity as gone: {removal}");
            Assert.That(snapshot?.activity?.titleName, Is.EqualTo(activity.titleName),
                "the activity did not survive a drop-and-reconnect of the session announcing it");
        });
    }

    /// <summary>
    /// Clearing an activity whose key has already lapsed still tells the room about it.
    /// </summary>
    /// <remarks>
    /// This is the escape hatch from the lifetime problem: observers who have been showing a stale
    /// activity since it expired server-side have no other way of learning it is over, so the explicit
    /// user action must broadcast unconditionally rather than short-circuit on "there was nothing to
    /// remove".
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 2)]
    public async Task Clearing_an_activity_whose_key_already_lapsed_still_tells_the_room(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Activity Lapsed Clear", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.LISTEN, StartedNow(), "Boards of Canada");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        var key = PresenceProbe.ActivitySessionKey(player.UserId, player.SessionId);
        Assert.That(await probe.ForceExpire(key, PresenceWaits.Immediately), Is.True);

        var lapsed = await Poll.UntilAsync(async () => !await probe.Exists(key), PresenceWaits.Settle, ct: ct);
        Assert.That(lapsed, Is.True, "the activity key would not expire");

        var beforeClear = watcher.Mark();
        await player.Users.RemoveBroadcastPresence(ct);

        var removed = await watcher.WaitForRecordAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, PresenceWaits.Settle, beforeClear, ct);

        Assert.That(removed.SpaceId, Is.EqualTo(spaceId),
            "the removal was announced to a different space than the one the user is in");
    }

    /// <summary>
    /// An activity announced before the socket has attached still reaches the room, and the space
    /// snapshot catches up as soon as the session connects.
    /// </summary>
    /// <remarks>
    /// <para><c>BroadcastPresence</c> is an ordinary Ion RPC and is deliberately not gated on a live
    /// realtime connection, because the desktop client reaches it that way: <c>loadUserData()</c>
    /// posts <c>{type:"connect"}</c> to the realtime worker and does not await the handshake, then
    /// calls <c>useActivity().init()</c>, whose IPC listener fires immediately for an already-running
    /// game. Boot-with-a-game-running, and any announce landing in a reconnect gap, therefore arrive
    /// while the sid is not yet in <c>presence:user:{u}:sessions</c> —
    /// <c>UserSessionGrain.SetSessionOnlineAsync</c> is what puts it there, and only
    /// <c>AppHub.OnConnectedAsync</c> reaches it.</para>
    ///
    /// <para>So <c>UserGrain.BroadcastPresenceAsync</c>'s <c>?? presence</c> fallback is load-bearing
    /// rather than an oversight (design question S17): the client dedupes on
    /// <c>lastPublishedPresence</c> and never re-sends, so dropping the announce would mean observers
    /// never see that game at all. What the two sides owe each other is convergence, not simultaneity
    /// — the stream leads by the length of the handshake, the snapshot follows, and neither is left
    /// permanently wrong. That is what is pinned here, in the order a real client produces it.</para>
    ///
    /// <para>The announce stays clearable throughout: <c>RemoveActivityPresence</c> addresses the key
    /// directly and <c>alwaysBroadcast: true</c> retracts it from every space even if the key has
    /// already lapsed, which is what <c>Clearing_an_activity_whose_key_already_lapsed_still_tells_the_room</c>
    /// guards.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 2)]
    public async Task An_activity_announced_before_the_hub_attaches_reaches_the_room_then_the_snapshot(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Announce Before Connect", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        // The announcing session has signed in and joined, but its socket is not up yet.
        var aliveBeforeAnnounce = await probe.IsSessionAliveAsync(player.UserId, player.SessionId, ct);

        var beforeAnnounce = watcher.Mark();
        var activity       = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Nothing At All");

        await player.Users.BroadcastPresence(activity, ct);

        var announced = await watcher.FirstWithinAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.titleName == activity.titleName,
            PresenceWaits.Settle, beforeAnnounce, ct);

        var snapshotBeforeConnect = await SnapshotOf(observer, spaceId, player.UserId, ct);

        // The handshake completes: AttachConnectionAsync writes the presence key and indexes the sid.
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);
        await playing.Heartbeat(UserStatus.Online, ct);

        await Poll.UntilAsync(
            async () => (await SnapshotOf(observer, spaceId, player.UserId, ct))?.status != UserStatus.Offline,
            PresenceWaits.Settle, ct: ct);

        var snapshotAfterConnect = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == activity.titleName, PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(aliveBeforeAnnounce, Is.False,
                "the announcing session was already attached, so this test says nothing about the window it is about");
            Assert.That(announced, Is.Not.Null,
                "an activity announced before the socket attached was swallowed — the client dedupes and never "
              + "re-sends, so the room would never learn about that game at all");
            Assert.That(snapshotBeforeConnect?.activity, Is.Null,
                $"the space snapshot folds live sessions only, so it cannot yet name this one "
              + $"({snapshotBeforeConnect?.activity?.titleName})");
            Assert.That(snapshotAfterConnect?.titleName, Is.EqualTo(activity.titleName),
                "the stream and the snapshot never converged: once the session attached, the space snapshot has "
              + "to name the activity the room was already told about");
        });
    }

    /// <summary>
    /// A member the space shows as Offline is never shown with an activity next to that.
    /// </summary>
    /// <remarks>
    /// <para>Status and activity are read out of Redis independently and their lifetimes differ by a
    /// factor of five, so there is a window — up to a whole grace period after an ungraceful drop —
    /// in which the status keys have lapsed and the activity key has not. Whatever the mechanism, the
    /// answer the client is handed has to be coherent: "Offline, playing Portal 2" is not a state a
    /// user can make sense of.</para>
    ///
    /// <para>The window is reached honestly rather than manufactured: the connection is aborted and
    /// the presence and status keys are shortened to what they would read a couple of minutes later.
    /// Nothing writes them back — the refresh path only ever extends keys that still exist.</para>
    ///
    /// <para>Defect S18, now fixed in the projection: <c>src/Argon.Api/Grains/SpaceReadGrain.cs</c>
    /// used to build each <c>MemberPresence</c> from two independent reads,
    /// <c>BatchGetAggregatedStatusAsync</c> and <c>BatchGetUsersActivityPresence</c>, with no
    /// cross-check (<c>GetMembers()</c> did the same); it now clears the activity whenever the status
    /// it resolved is <c>Offline</c>. Observed before the fix: after an ungraceful drop and the
    /// two-minute status/presence TTLs expiring, the snapshot read
    /// <c>status = Offline, activity = "Portal 2"</c> — a state that lasted until the one-minute grace
    /// reminder finalized the session, and for ever for a session whose grain never got to run its
    /// grace. The root cause lives one layer down and is fixed there too
    /// (<c>UserPresenceService.GetUserActivitiesAsync</c> folded over
    /// <c>presence:user:{u}:sessions</c> without checking each session was still alive); this test
    /// guards the projection, which is the layer the client actually reads, so the space snapshot
    /// cannot be self-contradictory whatever the store returns.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 2)]
    public async Task An_offline_member_is_never_shown_with_an_activity(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var player   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Offline With Activity", ct);
        await JoinAsync(observer, player, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);

        var activity = new UserActivityPresence(ActivityPresenceKind.GAME, StartedNow(), "Portal 2");
        await AnnounceAndAwaitAsync(player, watcher, activity, ct);

        await playing.AbortAsync(ct: ct);

        // Everything that says "this session is here" is brought forward to the far side of its TTL;
        // the activity key, whose lifetime is the longer of the two, is left exactly as the product
        // wrote it — the gap between the two clocks is the window under test.
        await probe.ForceExpire(PresenceProbe.PresenceSessionKey(player.UserId, player.SessionId), PresenceWaits.Immediately);
        await probe.ForceExpire(PresenceProbe.SessionStatusKey(player.UserId, player.SessionId), PresenceWaits.Immediately);
        await probe.ForceExpire(PresenceProbe.AggregatedStatusKey(player.UserId), PresenceWaits.Immediately);

        var snapshot = await Poll.ForValueAsync(
            () => SnapshotOf(observer, spaceId, player.UserId, ct),
            found => found?.status == UserStatus.Offline,
            PresenceWaits.Settle, ct: ct);

        var activityKeyAlive = await probe.Exists(PresenceProbe.ActivitySessionKey(player.UserId, player.SessionId));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot?.status, Is.EqualTo(UserStatus.Offline),
                "the member never read Offline, so the coherence this test is about was never at stake");
            Assert.That(activityKeyAlive, Is.True,
                "the activity key was already gone, so this test proves nothing about the window");
            Assert.That(snapshot?.activity, Is.Null,
                $"the space shows an offline member as playing '{snapshot?.activity?.titleName}' — status and " +
                "activity are read independently and lapse on different clocks");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------

    private static ulong StartedNow()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Heartbeats a connected client until told to stop — the client that is still there.
    /// </summary>
    /// <remarks>
    /// Needed by anything that waits longer than
    /// <see cref="PresenceTimingOptions.StaleConnectionAfter"/>: the session counts a connection only
    /// while it is heard from, and <see cref="RealtimeClient"/> sends nothing of its own, so a test
    /// that merely waits is modelling a dead transport rather than a quiet one.
    /// </remarks>
    private static async Task HeartbeatUntilAsync(RealtimeClient client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await client.Heartbeat(UserStatus.Online, ct);
                await Task.Delay(PresenceWaits.Tick, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The only way out.
        }
    }

    /// <summary>Announces an activity and returns once the watching observer has seen it.</summary>
    private static async Task AnnounceAndAwaitAsync(TestUserSession player, RealtimeClient watcher,
        UserActivityPresence activity, CancellationToken ct)
    {
        var announced = watcher.Mark();
        await player.Users.BroadcastPresence(activity, ct);

        await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.titleName == activity.titleName,
            PresenceWaits.Settle, announced, ct);
    }

    private static async Task<MemberPresence?> SnapshotOf(TestUserSession reader, Guid spaceId, Guid userId,
        CancellationToken ct)
        => (await reader.Servers.GetMemberPresence(spaceId, ct)).Values.FirstOrDefault(m => m.userId == userId);

    /// <summary>
    /// Polls the space snapshot (cached for a second server-side) until the member's activity looks
    /// the way the caller wants, and hands back whatever it read last so the failure can name it.
    /// </summary>
    private static Task<UserActivityPresence?> WaitForSnapshotActivityAsync(TestUserSession reader, Guid spaceId,
        Guid userId, Func<UserActivityPresence?, bool> accept, TimeSpan timeout, CancellationToken ct)
        => Poll.ForValueAsync(
            async () => (await SnapshotOf(reader, spaceId, userId, ct))?.activity,
            accept, timeout, ct: ct);

    /// <summary>
    /// Blocks until every named device of an account is in the live-session index, which is what the
    /// activity fold reads.
    /// </summary>
    /// <remarks>
    /// <para><see cref="RealtimeClient.ConnectAsync"/> returns when the SignalR handshake completes,
    /// and the hub's <c>OnConnectedAsync</c> — which is what calls <c>AttachConnectionAsync</c> and
    /// so writes the session's presence key and its entry in <c>presence:user:{u}:sessions</c> — runs
    /// after that. A device that announces an activity inside that window is not in the index yet,
    /// and <c>UserPresenceService.GetUserActivitiesAsync</c> folds over exactly that index: the
    /// announcement is stored under its own key but contributes nothing to the representative, so
    /// observers are told the OTHER device's older activity and nothing ever corrects them — no
    /// re-broadcast happens when the attach lands.</para>
    ///
    /// <para>That is the accepted read-side contract (the fold counts live sessions only; the same
    /// property is pinned green by
    /// <c>PresenceAggregationTests.AnActivityForASessionOutsideTheIndex_IsInvisibleToReadersAndStillClearable</c>),
    /// so a two-device test has to establish its own premise rather than assume it. This waits for
    /// that premise and asserts nothing about the behaviour under test — it is the reason those two
    /// tests used to fail perhaps one run in three on a loaded box, always with the first device's
    /// activity in the event the second device's announcement produced.</para>
    /// </remarks>
    private async Task RequireLiveDevicesAsync(Guid userId, CancellationToken ct, params Guid[] sids)
    {
        var wanted = sids.Select(sid => sid.ToString()).ToArray();

        var live = await Poll.ForValueAsync(
            () => probe.ActiveSessionIdsAsync(userId, ct),
            ids => wanted.All(ids.Contains),
            PresenceWaits.Settle, ct: ct);

        Assert.That(live, Is.SupersetOf(wanted),
            "a device's hub attach never reached the live-session index, so the activity fold could not have "
          + "seen anything that device announced");
    }

    /// <summary>
    /// A second device of an account that already has one: a fresh client, a fresh sid and its own
    /// sign-in.
    /// </summary>
    /// <remarks>
    /// The sid is minted client-side and travels in a header, so two <see cref="RealtimeClient"/>s
    /// over one <see cref="TestUserSession"/> are two windows of one session — same session grain, same
    /// activity key. Anything about per-device activity needs a genuinely separate client, which means
    /// a separate interceptor and therefore a separate sign-in.
    /// </remarks>
    private async Task<TestUserSession> AddDeviceAsync(TestUserSession first, CancellationToken ct)
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WebSocketFactory);

        client.WithInterceptor(interceptor);

        var authorized = await client.ForService<IIdentityInteraction>(FactoryAsp.Services).Authorize(
            new UserCredentialsInput(first.Credentials.email, null, null, first.Credentials.password, null, null), ct);

        if (authorized is not SuccessAuthorize success)
        {
            Assert.Fail($"the second device could not sign in: {(authorized as FailedAuthorize)?.error}");
            return null!;
        }

        interceptor.SetToken(success.token);

        var session = new TestUserSession(client, FactoryAsp.Services, first.Credentials, success.token,
            interceptor.SessionId);
        session.UserId = (await session.Users.GetMe(ct)).userId;

        Assert.That(session.UserId, Is.EqualTo(first.UserId),
            "the second device signed in as somebody else");

        return session;
    }

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
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Presence activity", string.Empty), ct);

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
