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
            e => e.userId == player.UserId, TimeSpan.FromSeconds(5), announced, ct);
        var changed = (OnUserPresenceActivityChanged)record.Event;

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == activity.titleName, TimeSpan.FromSeconds(10), ct);

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
            e => e.userId == player.UserId, TimeSpan.FromSeconds(5), beforeRemoval, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, TimeSpan.FromSeconds(10), ct);

        Assert.Multiple(() =>
        {
            Assert.That(removed.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace));
            Assert.That(removed.SpaceId, Is.EqualTo(spaceId));
            Assert.That(snapshot, Is.Null,
                $"the cleared activity is still in the space snapshot ({snapshot?.titleName})");
        });
    }

    /// <summary>
    /// The ten-minute activity key is kept alive for as long as the session that announced it is
    /// alive and heartbeating.
    /// </summary>
    /// <remarks>
    /// <para>A session that is still connected, still ticking and still playing the same game is the
    /// most ordinary state this system has, and the activity's lifetime has to survive it. The desktop
    /// client dedupes identical presence and never re-sends, so nothing outside the server will
    /// refresh this key on its behalf; if the server does not, the activity dies under a running game.
    /// The TTL is sampled at the broadcast and again forty seconds later — long enough for two of the
    /// grain's fifteen-second ticks and several client heartbeats.</para>
    ///
    /// <para>The contract (defect S16, fixed): the session's keep-alive owns the activity's lifetime.
    /// <c>UserPresenceService.RefreshSessionStatusTtlAsync</c> — the call the session grain's 15 s tick
    /// and the bot gateway's 30 s tick both make — re-arms <c>activity:user:{u}:session:{sid}</c>
    /// alongside the status keys, so ten minutes stopped being how long a game may last and became how
    /// long an orphaned entry lingers after its session stopped ticking.</para>
    ///
    /// <para>The threshold below follows that cadence rather than the sample: a key re-armed to its
    /// full ten minutes on every 15 s tick reads somewhere in 585–600 s whenever it is looked at, so
    /// ≥ 580 s is "renewed within the last tick, with slack for a loaded worker". It is not a weakened
    /// assertion — a build that never renews reads 560 s at this sample point and fails by twenty
    /// seconds. An earlier ≥ 590 s here demanded renewal within the last ten seconds, which a
    /// fifteen-second tick cannot promise, and missed by 45 ms deterministically.</para>
    /// </remarks>
    [Test, Category("Slow"), CancelAfter(1000 * 60 * 3)]
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
        var alive = Stopwatch.StartNew();
        while (alive.Elapsed < TimeSpan.FromSeconds(40))
        {
            await playing.Heartbeat(UserStatus.Online, ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }

        var ttlAfterLiving = await probe.TtlOf(key);
        var stillAlive     = await probe.IsSessionAliveAsync(player.UserId, player.SessionId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stillAlive, Is.True,
                "the session died during the test, so the TTL says nothing about a live session");
            Assert.That(ttlAtBroadcast, Is.Not.Null, "no activity key was written by BroadcastPresence");
            Assert.That(ttlAtBroadcast!.Value.TotalSeconds, Is.GreaterThan(580).And.LessThanOrEqualTo(601),
                "the activity key was not written with the documented ten-minute lifetime");
            Assert.That(ttlAfterLiving, Is.Not.Null, "the activity key vanished while the session was alive");
            Assert.That(ttlAfterLiving!.Value.TotalSeconds, Is.GreaterThanOrEqualTo(580),
                $"the activity's lifetime is draining under a live, heartbeating session " +
                $"({ttlAtBroadcast.Value.TotalSeconds:F0}s at the broadcast, {ttlAfterLiving.Value.TotalSeconds:F0}s " +
                $"{alive.Elapsed.TotalSeconds:F0}s later) — the session keep-alive is no longer renewing it, so the " +
                "activity dies on the clock under a running game");
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

        // Shortened and then polled for absence, and retried: the session's 15 s tick re-arms this key
        // now that S16 is fixed, so a shortening that lands just before a tick is undone once. Each
        // attempt gives the one-second TTL five seconds to actually lapse.
        var lapsed = false;
        for (var attempt = 0; attempt < 3 && !lapsed; attempt++)
        {
            await probe.ForceExpire(key, TimeSpan.FromSeconds(1));
            lapsed = await Poll.UntilAsync(async () => !await probe.Exists(key), TimeSpan.FromSeconds(5), ct: ct);
        }

        Assert.That(lapsed, Is.True, "the activity key would not expire, so this test cannot say anything");

        // The session never stopped announcing: it is connected and its presence key is alive.
        var sessionAlive = await probe.IsSessionAliveAsync(player.UserId, player.SessionId, ct);

        await JoinAsync(observer, newcomer, spaceId, ct);

        var snapshot = await SnapshotOf(newcomer, spaceId, player.UserId, ct);

        // Fixed window: proving that an event the product never emits does not arrive. Generous
        // enough to cover a session tick and a grace reminder, either of which could carry a sweep.
        var retraction = await watcher.FirstWithinAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, TimeSpan.FromSeconds(20), beforeLapse, ct);

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

        var game  = new UserActivityPresence(ActivityPresenceKind.GAME, 100, "Factorio");
        var music = new UserActivityPresence(ActivityPresenceKind.LISTEN, 200, "Rammstein - Sonne");

        await AnnounceAndAwaitAsync(player, watcher, game, ct);

        var beforeMusic = watcher.Mark();
        await phone.Users.BroadcastPresence(music, ct);

        var representative = await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.startTimestampSeconds == music.startTimestampSeconds,
            TimeSpan.FromSeconds(5), beforeMusic, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == music.titleName, TimeSpan.FromSeconds(10), ct);

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

        var game  = new UserActivityPresence(ActivityPresenceKind.GAME, 100, "Factorio");
        var music = new UserActivityPresence(ActivityPresenceKind.LISTEN, 200, "Rammstein - Sonne");

        await AnnounceAndAwaitAsync(player, watcher, game, ct);

        var beforeMusic = watcher.Mark();
        await phone.Users.BroadcastPresence(music, ct);
        await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.startTimestampSeconds == music.startTimestampSeconds,
            TimeSpan.FromSeconds(5), beforeMusic, ct);

        var beforeMusicStops = watcher.Mark();
        await phone.Users.RemoveBroadcastPresence(ct);

        var fellBack = await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.startTimestampSeconds == game.startTimestampSeconds,
            TimeSpan.FromSeconds(5), beforeMusicStops, ct);

        // Cheap and free of extra wall clock: the fall-back event has already arrived, so anything
        // that told the observer to drop the activity entirely is by now in the recorded log.
        var prematureRemoval = watcher.EventsOfType<OnUserPresenceActivityRemoved>(beforeMusicStops)
           .Where(e => e.userId == player.UserId)
           .ToList();

        var afterFallback = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == game.titleName, TimeSpan.FromSeconds(10), ct);

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
            e => e.userId == player.UserId, TimeSpan.FromSeconds(5), beforeDesktopLeaves, ct);

        var afterEverything = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, TimeSpan.FromSeconds(10), ct);

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
            e => e.userId == player.UserId, TimeSpan.FromSeconds(5), beforeGoodbye, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, TimeSpan.FromSeconds(10), ct);

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
    /// <para>The grace cannot be hurried — an Orleans reminder has a one-minute floor — but the
    /// presence key can be shortened so that the first reminder tick already finds the session dead
    /// and finalizes it. That is why this test is <c>Slow</c>: roughly a minute of it is the product's
    /// own clock.</para>
    /// </remarks>
    [Test, Category("Slow"), CancelAfter(1000 * 60 * 4)]
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
            TimeSpan.FromSeconds(2));

        var offline = await watcher.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == player.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(150), beforeDrop, ct);

        var removed = await watcher.WaitForRecordAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, TimeSpan.FromSeconds(30), beforeDrop, ct);

        var snapshot = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found is null, TimeSpan.FromSeconds(10), ct);

        Assert.Multiple(() =>
        {
            Assert.That(removed.ReceivedAt, Is.LessThanOrEqualTo(offline.ReceivedAt + TimeSpan.FromSeconds(2)),
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
            e => e.userId == player.UserId, TimeSpan.FromSeconds(5), beforeDrop, ct);

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
        Assert.That(await probe.ForceExpire(key, TimeSpan.FromSeconds(1)), Is.True);

        var lapsed = await Poll.UntilAsync(async () => !await probe.Exists(key), TimeSpan.FromSeconds(10), ct: ct);
        Assert.That(lapsed, Is.True, "the activity key would not expire");

        var beforeClear = watcher.Mark();
        await player.Users.RemoveBroadcastPresence(ct);

        var removed = await watcher.WaitForRecordAsync<OnUserPresenceActivityRemoved>(
            e => e.userId == player.UserId, TimeSpan.FromSeconds(5), beforeClear, ct);

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
            TimeSpan.FromSeconds(10), beforeAnnounce, ct);

        var snapshotBeforeConnect = await SnapshotOf(observer, spaceId, player.UserId, ct);

        // The handshake completes: AttachConnectionAsync writes the presence key and indexes the sid.
        await using var playing = await RealtimeClient.ConnectAsync(player, ct);
        await playing.Heartbeat(UserStatus.Online, ct);

        await Poll.UntilAsync(
            async () => (await SnapshotOf(observer, spaceId, player.UserId, ct))?.status != UserStatus.Offline,
            TimeSpan.FromSeconds(15), ct: ct);

        var snapshotAfterConnect = await WaitForSnapshotActivityAsync(observer, spaceId, player.UserId,
            found => found?.titleName == activity.titleName, TimeSpan.FromSeconds(15), ct);

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
        // the activity key, with its ten minutes, is left exactly as the product wrote it.
        await probe.ForceExpire(PresenceProbe.PresenceSessionKey(player.UserId, player.SessionId), TimeSpan.FromSeconds(1));
        await probe.ForceExpire(PresenceProbe.SessionStatusKey(player.UserId, player.SessionId), TimeSpan.FromSeconds(1));
        await probe.ForceExpire(PresenceProbe.AggregatedStatusKey(player.UserId), TimeSpan.FromSeconds(1));

        var snapshot = await Poll.ForValueAsync(
            () => SnapshotOf(observer, spaceId, player.UserId, ct),
            found => found?.status == UserStatus.Offline,
            TimeSpan.FromSeconds(15), ct: ct);

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

    /// <summary>Announces an activity and returns once the watching observer has seen it.</summary>
    private static async Task AnnounceAndAwaitAsync(TestUserSession player, RealtimeClient watcher,
        UserActivityPresence activity, CancellationToken ct)
    {
        var announced = watcher.Mark();
        await player.Users.BroadcastPresence(activity, ct);

        await watcher.WaitForAsync<OnUserPresenceActivityChanged>(
            e => e.userId == player.UserId && e.presence.titleName == activity.titleName,
            TimeSpan.FromSeconds(10), announced, ct);
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
