namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;

/// <summary>
/// Guards the space-scoped status <em>stream</em> against the space-scoped status <em>snapshot</em>:
/// what a member watching a space is told over <c>broadcastSpace</c> has to be the same thing a
/// member loading that space reads out of <c>GetMemberPresence</c>, and neither may ever name a
/// status its owner never asserted.
/// </summary>
/// <remarks>
/// <para>Every other layer of this campaign can be right while this one is wrong. The aggregate in
/// Redis can hold the correct value, the session grain can hold the correct value, and the roster a
/// client actually paints can still be wrong — because the roster is painted from two independent
/// sources that nothing reconciles: one snapshot at load time
/// (<c>SpaceReadGrain.GetPresence</c>, cached for a second) and a stream of
/// <c>UserChangedStatus</c> deltas afterwards. A delta that names a status the user does not have
/// is not corrected by anything: the next heartbeat that re-asserts the real status is a no-op in
/// <c>UserSessionGrain.HeartBeatAsync</c> (the preferred status already matches) and, even if it
/// were not, <c>UserGrain.AggregateAndBroadcastStatusAsync</c> would drop it against the
/// <c>status:user:{u}:lastbroadcast</c> hysteresis record. So a single wrong event is permanent for
/// every client that was watching, until that client reloads the space.</para>
///
/// <para>That is why the assertions here are phrased as "the observer never sees a status the user
/// did not assert" rather than "the observer eventually sees the right status". Eventual
/// convergence is not available in this design; the first event has to be right.</para>
///
/// <para>Every test uses fresh users and fresh spaces. Presence is per-user global state in Redis
/// and the fixtures in this campaign run in parallel, so sharing a user across tests would make a
/// failure impossible to attribute.</para>
/// </remarks>
[TestFixture]
public class PresenceRealtimeTests : TestBase
{
    private PresenceProbe probe = null!;

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    /// <summary>
    /// A user whose real status is DoNotDisturb quits and comes back. The space watching them must
    /// not be told they are Online in between.
    /// </summary>
    /// <remarks>
    /// <para>The status is established first and then re-asserted on the very next connection, which
    /// is exactly what the desktop client does: <c>meStore</c> persists DND across restarts and
    /// <c>realtimeWorker</c> sends a heartbeat carrying it immediately after connect. So the only
    /// thing the server ever learns about this user is "DoNotDisturb" — any other status in the
    /// stream was invented by the server.</para>
    ///
    /// <para><b>The contract this now guards (defect S3, fixed).</b> The first thing a space hears
    /// about a session is the status its owner asserted, never one the server assumed on their behalf.
    /// <c>AppHub.OnConnectedAsync</c> still calls <c>AttachConnectionAsync(Context.ConnectionId)</c>
    /// with no status — it has none to pass — but
    /// <c>UserSessionGrain.EnsureSessionStartedAsync</c> no longer answers that with
    /// <c>preferred ?? Online</c>. A statusless attach now takes the "alive" half only (presence key,
    /// refresh tick, friend seed, session accounting), leaves <c>PreferredStatus</c> null and writes
    /// neither <c>status:user:{u}:session:{sid}</c> nor a broadcast; the first heartbeat, which the
    /// desktop client sends immediately on connect, is what announces the real status once. The
    /// sequence <c>Online -&gt; DoNotDisturb</c> this test used to record is therefore impossible
    /// rather than merely unlikely.</para>
    ///
    /// <para>"Not named yet" is not allowed to become "never named": a deadline in the grain
    /// (<see cref="PresenceTimingOptions.StatusDeadline"/>) assumes Online and announces it if a
    /// connected session has still said nothing, so a
    /// client that connects and never heartbeats is visible rather than invisible. The last
    /// assertion below — that the returning session produced <em>some</em> status event — is what
    /// holds that end of the contract.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_returning_dnd_user_is_never_announced_online_to_the_space(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var dndUser  = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H1 Online flash", ct);
        await JoinAsync(observer, dndUser, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        // Establish the user's real status: they are a DND user, and the server has been told so.
        await using (var firstRun = await RealtimeClient.ConnectAsync(dndUser, ct))
        {
            await firstRun.Heartbeat(UserStatus.DoNotDisturb, ct);
            await watcher.WaitForAsync<UserChangedStatus>(
                e => e.userId == dndUser.UserId && e.status == UserStatus.DoNotDisturb,
                PresenceWaits.Settle, ct: ct);

            await firstRun.GoOffline(ct);
        }

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == dndUser.UserId && e.status == UserStatus.Offline,
            PresenceWaits.Settle, ct: ct);

        // The session grain self-destroys on GoOffline. Give the deactivation a moment to land, so
        // the reconnect below really is a fresh session start rather than a re-attach to the old
        // activation — the two take different paths and only the first one is the case under test.
        await Task.Delay(PresenceWaits.Slack, ct);

        var beforeReturn = watcher.Mark();

        await using var secondRun = await RealtimeClient.ConnectAsync(dndUser, ct);
        await secondRun.Heartbeat(UserStatus.DoNotDisturb, ct);

        // A fixed window on purpose: the claim is that a particular event never arrives, and an
        // absence has no edge to poll for. The window covers the statusless deadline, which is where
        // an unwanted Online would come from; everything the space was told about this user inside it
        // is then read back and judged as a whole.
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var announced = StatusesFor(watcher, dndUser.UserId, spaceId, beforeReturn);
        var sequence  = string.Join(" -> ", announced.Select(e => e.status));

        var snapshot = await Poll.ForValueAsync(
            () => SnapshotStatusAsync(observer, spaceId, dndUser.UserId, ct),
            status => status == UserStatus.DoNotDisturb,
            PresenceWaits.Settle, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(announced.Select(e => e.status), Has.None.EqualTo(UserStatus.Online),
                $"the space was told a DND user is Online. Sequence seen: [{sequence}]");
            Assert.That(announced.Select(e => e.status), Is.All.EqualTo(UserStatus.DoNotDisturb),
                $"the space was told a status this user never asserted. Sequence seen: [{sequence}]");
            Assert.That(announced, Is.Not.Empty,
                "the returning session produced no status event at all, so the observer's roster keeps " +
                "the Offline it was left with while the user is connected");
            Assert.That(snapshot, Is.EqualTo(UserStatus.DoNotDisturb),
                "GetMemberPresence disagrees with the status the user asserted");
        });
    }

    /// <summary>
    /// A DND user joining a second space is announced to that space as what they are, and a later
    /// heartbeat carrying the same status repairs the roster if the join did not.
    /// </summary>
    /// <remarks>
    /// <para>A join must announce the joiner's real status, and it has to get it right first time,
    /// because nothing downstream can repair it. The corrective heartbeat this test then sends is a
    /// no-op twice over — once in the session grain (<c>UserSessionGrain.HeartBeatAsync</c> only acts
    /// when <c>PreferredStatus != status</c>) and once in the hysteresis record
    /// (<c>MarkBroadcastIfChangedAsync</c> still holds DoNotDisturb) — so whatever the join said
    /// stands for as long as the observer keeps the space open. That is why the assertion is on the
    /// join itself, with the re-assert only there to prove no second chance exists.</para>
    ///
    /// <para>Defect S4 (DND leg), now fixed: <c>SpaceGrain.UserJoined</c> reads
    /// <c>GetAggregatedStatusAsync</c> and announces that to this space, instead of a flat
    /// <c>SetUserStatus(userId, UserStatus.Online)</c> that bypassed both the aggregate and the
    /// hysteresis record. Before the fix the space was told <c>[Online]</c> on the join while
    /// <c>status:user:{u}:lastbroadcast</c> and <c>GetMemberPresence</c> for that same space both
    /// read <c>DoNotDisturb</c> — stream and snapshot disagreeing permanently, and a user who had
    /// explicitly asked not to be disturbed shown as available to the room they had just entered. The
    /// read stays out of <c>UserGrain.AggregateAndBroadcastStatusAsync</c> on purpose: that path is
    /// guarded by the same per-user hysteresis record, so it would have fanned out nothing at all.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_dnd_member_joining_a_space_is_announced_as_dnd_and_a_later_heartbeat_repairs_it(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var joiner   = await CreateSessionAsync(ct);

        // The joiner is already living in a space of their own, with a live connection and a real
        // status, before the space under test knows they exist.
        var homeSpace = await CreateSpaceAsync(joiner, "H5a Home", ct);
        var newSpace  = await CreateSpaceAsync(observer, "H5a Target", ct);

        await using var member = await RealtimeClient.ConnectAsync(joiner, ct);
        await member.Heartbeat(UserStatus.DoNotDisturb, ct);

        var settled = await probe.WaitForAggregatedStatusAsync(
            joiner.UserId, UserStatus.DoNotDisturb, PresenceWaits.Settle, ct);
        Assert.That(settled, Is.EqualTo(UserStatus.DoNotDisturb),
            $"the joiner never reached DND (home space {homeSpace}), so the join case cannot be judged");

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeJoin = watcher.Mark();
        await JoinAsync(observer, joiner, newSpace, ct);

        // Fixed window: the assertion is about which events arrive, including the ones that must not.
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var onJoin = StatusesFor(watcher, joiner.UserId, newSpace, beforeJoin);

        // A client that re-asserts its unchanged status is the only repair mechanism this design has.
        var beforeRepair = watcher.Mark();
        await member.Heartbeat(UserStatus.DoNotDisturb, ct);
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var afterRepair = StatusesFor(watcher, joiner.UserId, newSpace, beforeRepair);
        var streamNow   = StatusesFor(watcher, joiner.UserId, newSpace, beforeJoin).LastOrDefault()?.status;
        var snapshot    = await SnapshotStatusAsync(observer, newSpace, joiner.UserId, ct);
        var hysteresis  = await probe.LastBroadcastAsync(joiner.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(onJoin.Select(e => e.status), Has.None.EqualTo(UserStatus.Online),
                $"joining a space announced a DND user as Online. On join: " +
                $"[{string.Join(" -> ", onJoin.Select(e => e.status))}], hysteresis record = {hysteresis}");
            Assert.That(snapshot, Is.EqualTo(UserStatus.DoNotDisturb),
                "GetMemberPresence for the space just joined does not show the status the user has");
            Assert.That(streamNow, Is.EqualTo(UserStatus.DoNotDisturb),
                $"the last thing the space was told about this member is {streamNow}, not the DND they " +
                $"have. A heartbeat re-asserting DND produced [{string.Join(" -> ", afterRepair.Select(e => e.status))}] " +
                "— nothing, because both the session grain and the hysteresis record treat an unchanged " +
                "status as nothing to say");
        });
    }

    /// <summary>
    /// Joining a space through the invite API while owning no realtime connection at all must not
    /// announce the joiner as online, and the snapshot must say Offline.
    /// </summary>
    /// <remarks>
    /// <para>The purest form of the join contract: this user has no session, no presence key and no
    /// aggregate anywhere, so any status in the stream could only have been manufactured by the join
    /// itself. Accepting an invite makes you a member, not a presence — the stream and
    /// <c>GetMemberPresence</c> have to agree on that in the same second.</para>
    ///
    /// <para>Defect S4 (ghost leg), now fixed: <c>SpaceGrain.UserJoined</c>
    /// (src/Argon.Api/Grains/SpaceGrain.cs, reached from <c>AddMemberAsync</c> on every join, on
    /// space creation and on bot install) reads the aggregate and stays silent when it is Offline,
    /// instead of firing <c>SetUserStatus(userId, UserStatus.Online)</c> without consulting presence
    /// at all. The phantom it used to produce was permanent: the user has no session to heartbeat
    /// with and the offline path (<c>UserSessionGrain.FinalizeOfflineAsync</c>) never runs for a
    /// session that never existed, so members watching the space kept a green dot next to somebody
    /// who had never opened the app until their client reloaded its snapshot. Silence rather than an
    /// explicit <c>Offline</c> is the intended shape here — the space has never heard of this member,
    /// so there is no stale value to correct.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_joiner_with_no_connection_is_not_announced_online(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var offline  = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H5b Invite", ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        Assert.That(await probe.IsUserOnlineAsync(offline.UserId, ct), Is.False,
            "the joiner already has presence keys, so this test cannot say anything about a user with none");

        var beforeJoin = watcher.Mark();
        await JoinAsync(observer, offline, spaceId, ct);

        // Fixed window: proving an event does not arrive.
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var announced = StatusesFor(watcher, offline.UserId, spaceId, beforeJoin);
        var snapshot  = await SnapshotStatusAsync(observer, spaceId, offline.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.EqualTo(UserStatus.Offline),
                "GetMemberPresence claims a user with no session at all is not offline");
            Assert.That(announced.Select(e => e.status), Has.None.EqualTo(UserStatus.Online),
                $"a user who has never connected was announced Online to the space on joining. " +
                $"Sequence seen: [{string.Join(" -> ", announced.Select(e => e.status))}]; " +
                $"snapshot says {snapshot}");
        });
    }

    /// <summary>
    /// Two windows of one session, then none, then a third within the grace: the space hears about
    /// the connection coming up and about nothing else.
    /// </summary>
    /// <remarks>
    /// Closing a window is not a status change, and neither is opening another one while the
    /// session is still inside its disconnect grace. A client that shows a flap here is showing the
    /// user something that did not happen, which is worse than showing them nothing.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Windows_of_one_session_open_and_close_without_a_status_event(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var user     = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H12 Windows", ct);
        await JoinAsync(observer, user, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeFirstWindow = watcher.Mark();
        var windowOne = await RealtimeClient.ConnectAsync(user, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.status == UserStatus.Online,
            PresenceWaits.Settle, beforeFirstWindow, ct);

        // A second window of the SAME session: same sid, same session grain, a second connection id.
        var beforeSecondWindow = watcher.Mark();
        var windowTwo = await RealtimeClient.ConnectAsync(user, ct);

        Assert.That(windowTwo.ConnectionId, Is.Not.EqualTo(windowOne.ConnectionId),
            "the two windows share a connection id, so this is one window and the test proves nothing");

        await watcher.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == user.UserId,
            PresenceWaits.NegativeWindow,
            "opening a second window of a session that is already online is not a status change",
            beforeSecondWindow, ct);

        // Closing one of two windows leaves the session with a live connection.
        var beforeFirstClose = watcher.Mark();
        await windowOne.StopAsync(ct);

        await watcher.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == user.UserId,
            PresenceWaits.NegativeWindow,
            "closing one window while another is open must not change the user's status",
            beforeFirstClose, ct);

        // Closing the last one arms the grace; it is not an offline.
        var beforeLastClose = watcher.Mark();
        await windowTwo.StopAsync(ct);

        await watcher.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == user.UserId,
            PresenceWaits.NegativeWindow,
            "the last window closing must ride out the disconnect grace, not announce an offline",
            beforeLastClose, ct);

        // Back inside ten seconds — the shape of a page reload.
        var beforeReturn = watcher.Mark();
        await using var windowThree = await RealtimeClient.ConnectAsync(user, ct);

        await watcher.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == user.UserId,
            PresenceWaits.NegativeWindow,
            "reconnecting inside the grace window must be invisible to the space: the session never " +
            "went offline, so there is no coming back online to announce",
            beforeReturn, ct);

        var snapshot = await SnapshotStatusAsync(observer, spaceId, user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.EqualTo(UserStatus.Online),
                "the snapshot lost the user across a window cycle the stream never mentioned");
            Assert.That(watcher.DecodeFailures, Is.Empty);
        });

        await windowOne.DisposeAsync();
        await windowTwo.DisposeAsync();
    }

    /// <summary>
    /// A heartbeat carrying <c>Offline</c> cannot make a user invisible — and must not silently
    /// change their status to something they did not choose either.
    /// </summary>
    /// <remarks>
    /// <para><c>UserSessionGrain.HeartBeatAsync</c> rewrites an incoming Offline to Online before it
    /// compares it with the preferred status. Refusing to go invisible over the heartbeat is right;
    /// what follows from the rewrite is not, and this test separates the two claims so the report
    /// can say which one holds.</para>
    ///
    /// <para><b>The contract this now guards (defect S12, fixed).</b> An Offline on the heartbeat is
    /// read as "the client reported no status": it keeps the liveness half — the presence refresh,
    /// the connection self-heal, the deactivation delay — and touches nothing else. It consumes no
    /// throttle token, never overwrites <c>PreferredStatus</c>, writes no Redis key and fans nothing
    /// out, which is why the assertion below is that <em>no</em> event followed it at all.
    /// <c>UserSessionGrain.HeartBeatAsync</c> used to rewrite the value to Online one line above the
    /// <c>PreferredStatus != status</c> comparison, so the rewrite was then treated as a deliberate
    /// change and a single stray beat cleared a DND user's status for everybody. The refusal to let a
    /// client go invisible this way is unchanged, and so is the one case where Offline still means
    /// something: a brand-new session whose only word was Offline still comes up Online, because a
    /// client that has spoken is not the same as one that has not
    /// (<c>PresenceSessionGrainTests.Heartbeating_Offline_never_makes_a_live_session_offline</c>).</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_heartbeat_carrying_offline_neither_hides_the_user_nor_changes_their_status(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var user     = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H13 Offline heartbeat", ct);
        await JoinAsync(observer, user, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var member  = await RealtimeClient.ConnectAsync(user, ct);

        await member.Heartbeat(UserStatus.DoNotDisturb, ct);
        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.status == UserStatus.DoNotDisturb,
            PresenceWaits.Settle, ct: ct);

        var beforeOfflineBeat = watcher.Mark();
        await member.Heartbeat(UserStatus.Offline, ct);

        // Fixed window: the primary claim is that an event does not arrive.
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var afterwards = StatusesFor(watcher, user.UserId, spaceId, beforeOfflineBeat);
        var sequence   = string.Join(" -> ", afterwards.Select(e => e.status));
        var snapshot   = await SnapshotStatusAsync(observer, spaceId, user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterwards.Select(e => e.status), Has.None.EqualTo(UserStatus.Offline),
                $"a client made itself invisible through the heartbeat. Sequence seen: [{sequence}]");
            Assert.That(afterwards, Is.Empty,
                $"a heartbeat carrying Offline changed a DND user's status for everyone watching. " +
                $"Sequence seen: [{sequence}]; snapshot now says {snapshot}");
            Assert.That(snapshot, Is.EqualTo(UserStatus.DoNotDisturb),
                "the snapshot lost the user's chosen status to a heartbeat that carried Offline");
        });
    }

    /// <summary>
    /// Six status changes in a burst: the space is never told a status the user did not set, and
    /// the last one the user asked for is the one it ends on.
    /// </summary>
    /// <remarks>
    /// The session grain's token bucket (capacity 5, refilling at one token every two seconds) is
    /// meant to cap the fan-out of a flapping client without corrupting where it ends up: a dropped
    /// change leaves the preferred status alone so the next heartbeat re-detects the difference and
    /// propagates it. Both halves matter — a bucket that dropped the change but kept the new status
    /// would leave the space permanently wrong.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_burst_of_status_changes_invents_nothing_and_ends_on_the_last_one(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var user     = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H11 Burst", ct);
        await JoinAsync(observer, user, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var member  = await RealtimeClient.ConnectAsync(user, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.status == UserStatus.Online,
            PresenceWaits.Settle, ct: ct);

        var beforeBurst = watcher.Mark();

        // Only statuses the aggregation fold recognises are used here; InGame/Listen/TouchGrass fold
        // to Offline (a separate, already-confirmed defect) and would make this test about that.
        UserStatus[] burst =
        [
            UserStatus.Away, UserStatus.DoNotDisturb, UserStatus.Away,
            UserStatus.DoNotDisturb, UserStatus.Away, UserStatus.DoNotDisturb
        ];

        foreach (var status in burst)
            await member.Heartbeat(status, ct);

        // Not a wait for a state change: a wait for a rate limiter. The bucket refills at 0.5/s — a
        // rate the grain owns and this campaign does not make configurable, so this really is three
        // seconds and not a ratio of anything — and the sixth change was thrown away, so the client's
        // next heartbeat has to land after the refill for the final status to propagate at all.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        await member.Heartbeat(UserStatus.DoNotDisturb, ct);

        var delivered = await Poll.ForValueAsync(
            () => Task.FromResult(StatusesFor(watcher, user.UserId, spaceId, beforeBurst).LastOrDefault()?.status),
            status => status == UserStatus.DoNotDisturb,
            PresenceWaits.Settle, ct: ct);

        var seen     = StatusesFor(watcher, user.UserId, spaceId, beforeBurst).Select(e => e.status).ToList();
        var snapshot = await Poll.ForValueAsync(
            () => SnapshotStatusAsync(observer, spaceId, user.UserId, ct),
            status => status == UserStatus.DoNotDisturb,
            PresenceWaits.Settle, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.SubsetOf(burst),
                $"the space was told a status the user never set during the burst. Sequence seen: " +
                $"[{string.Join(" -> ", seen)}]");
            Assert.That(delivered, Is.EqualTo(UserStatus.DoNotDisturb),
                $"the final status the user asked for never reached the space. Sequence seen: " +
                $"[{string.Join(" -> ", seen)}]");
            Assert.That(snapshot, Is.EqualTo(UserStatus.DoNotDisturb),
                "the snapshot did not converge on the last status of the burst");
        });
    }

    /// <summary>
    /// A scripted life of one session — heartbeats, an ungraceful drop and reconnect inside the
    /// grace, a deliberate offline, a fresh connection, a graceful close — with the space snapshot
    /// checked against the last event the space was given after every single step.
    /// </summary>
    /// <remarks>
    /// <para>This is the property the whole design rests on and the one no single-step test can
    /// establish: a client paints its roster from the snapshot once and from the stream forever
    /// after, so the two have to be the same picture at every instant, not merely at the end.</para>
    ///
    /// <para>Each step settles first (poll until the two agree, up to eight seconds, which covers
    /// the one-second presence cache and the grain round trip) and is then re-read after a further
    /// pause, so an event that lands late and breaks an agreement that had already been reached is
    /// still caught. Mismatches are collected rather than thrown, so a failure names every step that
    /// disagreed instead of only the first.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 4)]
    public async Task The_space_snapshot_agrees_with_the_last_status_event_at_every_step(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var user     = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H20 Script", ct);
        await JoinAsync(observer, user, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var mismatches = new List<string>();
        var step       = 0;

        // A client that has been told nothing about a member treats them as offline; that is the
        // desktop client's own default (userStore starts everyone Offline).
        async Task CheckAsync(string label)
        {
            step++;

            UserStatus stream   = UserStatus.Offline;
            UserStatus snapshot = UserStatus.Offline;

            await Poll.UntilAsync(async () =>
            {
                stream   = StatusesFor(watcher, user.UserId, spaceId).LastOrDefault()?.status ?? UserStatus.Offline;
                snapshot = await SnapshotStatusAsync(observer, spaceId, user.UserId, ct);
                return stream == snapshot;
            }, PresenceWaits.Settle, PresenceWaits.PollStep, ct);

            // Re-read after the dust settles: an event arriving after agreement was reached breaks it
            // just as badly, and only a second look finds that.
            await Task.Delay(PresenceWaits.Slack, ct);

            stream   = StatusesFor(watcher, user.UserId, spaceId).LastOrDefault()?.status ?? UserStatus.Offline;
            snapshot = await SnapshotStatusAsync(observer, spaceId, user.UserId, ct);

            if (stream != snapshot)
                mismatches.Add($"step {step} ({label}): stream says {stream}, GetMemberPresence says {snapshot}");
        }

        var member = await RealtimeClient.ConnectAsync(user, ct);
        try
        {
            await CheckAsync("connect");

            await member.Heartbeat(UserStatus.Away, ct);
            await CheckAsync("heartbeat Away");

            await member.Heartbeat(UserStatus.DoNotDisturb, ct);
            await CheckAsync("heartbeat DoNotDisturb");

            await member.AbortAsync(ct: ct);
            await CheckAsync("ungraceful drop");

            await member.RestartAsync(ct);
            await CheckAsync("reconnect inside the grace");

            await member.Heartbeat(UserStatus.DoNotDisturb, ct);
            await CheckAsync("re-assert DoNotDisturb after reconnect");

            await member.Heartbeat(UserStatus.Online, ct);
            await CheckAsync("heartbeat Online");

            await member.Heartbeat(UserStatus.Away, ct);
            await CheckAsync("heartbeat Away again");

            await member.GoOffline(ct);
            await CheckAsync("deliberate GoOffline");
        }
        finally
        {
            await member.DisposeAsync();
        }

        await using var returned = await RealtimeClient.ConnectAsync(user, ct);
        await CheckAsync("connect a new session after going offline");

        await returned.Heartbeat(UserStatus.DoNotDisturb, ct);
        await CheckAsync("heartbeat DoNotDisturb on the new connection");

        await returned.StopAsync(ct);
        await CheckAsync("graceful close without GoOffline");

        Assert.Multiple(() =>
        {
            Assert.That(mismatches, Is.Empty,
                $"the space stream and the space snapshot disagreed:{Environment.NewLine}" +
                string.Join(Environment.NewLine, mismatches) + Environment.NewLine +
                watcher.Dump());
            Assert.That(watcher.DecodeFailures, Is.Empty);
        });
    }

    /// <summary>
    /// A connected client that is not a member of a space is told nothing about that space's
    /// members.
    /// </summary>
    /// <remarks>
    /// Status is not public data: who is online, when, and in which mood is exactly the sort of
    /// thing a stranger should not be able to watch. The test proves the event really fired by
    /// having a genuine member observe it in the same window, so a green here is "the stranger was
    /// excluded", not "nothing happened".
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_non_member_is_told_nothing_about_a_space(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var user     = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H26 Scope", ct);
        await JoinAsync(observer, user, spaceId, ct);

        await using var watcher  = await RealtimeClient.ConnectAsync(observer, ct);
        await using var outsider = await RealtimeClient.ConnectAsync(stranger, ct);

        var beforeMember = watcher.Mark();
        var strangerMark = outsider.Mark();

        await using var member = await RealtimeClient.ConnectAsync(user, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.status == UserStatus.Online,
            PresenceWaits.Settle, beforeMember, ct);

        await member.Heartbeat(UserStatus.Away, ct);
        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.status == UserStatus.Away,
            PresenceWaits.Settle, beforeMember, ct);

        await outsider.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == user.UserId,
            PresenceWaits.NegativeWindow,
            "a client that is not a member of the space received a member's status",
            strangerMark, ct);

        Assert.That(outsider.EventsOfType<UserChangedStatus>(strangerMark)
           .Where(e => e.spaceId == spaceId), Is.Empty,
            "a client that is not a member of the space received events addressed to it");
    }

    /// <summary>
    /// A non-member cannot put itself on a space's stream by asking the hub to subscribe it.
    /// </summary>
    /// <remarks>
    /// <para><c>AppHub.SubscribeToSpace</c> is the client-driven half of the routing, and a stranger
    /// who knows a space id is exactly the caller it has to refuse. Every space id in this product
    /// travels through invite previews and shared links, so "knows the id" is not a secret worth
    /// resting an access decision on.</para>
    ///
    /// <para><b>The contract this now guards (defect S11, fixed).</b> The client-driven half of the
    /// routing gates on the same source of truth as the server-driven half.
    /// <c>AppHub.SubscribeToSpace</c> (src/Argon.Core/Features/Transport/AppHub.cs) was
    /// <c>await Groups.AddToGroupAsync(Context.ConnectionId, "spaces/" + spaceId)</c> with no
    /// membership check at all, so a stranger who knew a space id received everything published to
    /// that group — not only presence but messages, typing, member and channel changes — while
    /// <c>OnConnectedAsync</c> joined only the caller's own spaces and <c>Resume</c> replayed only
    /// spaces they were still in. It now checks <c>IUserGrain.GetMyServersIds</c> first and throws
    /// <c>HubException</c> otherwise, which is the branch this test hopes for; the assertion is
    /// written so that a call which is accepted but inert would also pass, and a call that is
    /// "refused" while the events still arrive would not. <c>SubscribeToChannel</c> carries the same
    /// gate through <c>IUserGrain.ResolveChannelSpaceIfMemberAsync</c>, which is the more damaging
    /// half because a channel group carries message content.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_non_member_cannot_subscribe_itself_onto_a_space_stream(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var user     = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "H26 Subscribe", ct);
        await JoinAsync(observer, user, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);
        await using var member  = await RealtimeClient.ConnectAsync(user, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.status == UserStatus.Online,
            PresenceWaits.Settle, ct: ct);

        await using var outsider = await RealtimeClient.ConnectAsync(stranger, ct);

        // The hub may refuse this outright — that is the outcome this test hopes for. If it does
        // not, the subscription has to at least be inert.
        var refused = false;
        try
        {
            await outsider.SubscribeToSpace(spaceId, ct);
        }
        catch (Exception)
        {
            refused = true;
        }

        var strangerMark = outsider.Mark();
        await member.Heartbeat(UserStatus.DoNotDisturb, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.status == UserStatus.DoNotDisturb,
            PresenceWaits.Settle, ct: ct);

        var leaked = await outsider.FirstWithinAsync<UserChangedStatus>(
            e => e.spaceId == spaceId, PresenceWaits.NegativeWindow, strangerMark, ct);

        Assert.That(leaked, Is.Null,
            $"a non-member subscribed itself onto a space stream and is now watching its members' " +
            $"presence (SubscribeToSpace {(refused ? "was refused but the events arrived anyway" : "was accepted")}): " +
            $"{leaked}");
    }

    /// <summary>
    /// A member of two spaces produces exactly one status event per space per transition: no
    /// duplicates inside a space, and no space left out.
    /// </summary>
    /// <remarks>
    /// The fan-out iterates the user's spaces and sends one <c>SetUserStatus</c> to each, so a
    /// duplicate would mean the same client renders the same transition twice (and, for a client
    /// that animates status changes, shows it twice) while a missing one would leave a whole space
    /// stale until it is reloaded.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_member_of_two_spaces_gets_one_event_per_space_per_transition(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var user     = await CreateSessionAsync(ct);

        var first  = await CreateSpaceAsync(observer, "H26 Space one", ct);
        var second = await CreateSpaceAsync(observer, "H26 Space two", ct);

        await JoinAsync(observer, user, first, ct);
        await JoinAsync(observer, user, second, ct);

        // The observer connects after both joins, so the join broadcasts cannot be mistaken for the
        // transitions under test.
        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeConnect = watcher.Mark();
        await using var member = await RealtimeClient.ConnectAsync(user, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.spaceId == second && e.status == UserStatus.Online,
            PresenceWaits.Settle, beforeConnect, ct);

        // Both fan-outs are issued together, so once the later space has been served the earlier one
        // has been too; a short settle covers the ordering rather than the delivery.
        await Task.Delay(PresenceWaits.Slack, ct);

        var onConnect = StatusesFor(watcher, user.UserId, first, beforeConnect)
           .Concat(StatusesFor(watcher, user.UserId, second, beforeConnect)).ToList();

        var beforeAway = watcher.Mark();
        await member.Heartbeat(UserStatus.Away, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.spaceId == first && e.status == UserStatus.Away,
            PresenceWaits.Settle, beforeAway, ct);
        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == user.UserId && e.spaceId == second && e.status == UserStatus.Away,
            PresenceWaits.Settle, beforeAway, ct);

        // Fixed window: the remaining claim is that no further copy of the transition arrives.
        await Task.Delay(PresenceWaits.NegativeWindow, ct);

        var inFirst  = StatusesFor(watcher, user.UserId, first, beforeAway);
        var inSecond = StatusesFor(watcher, user.UserId, second, beforeAway);

        Assert.Multiple(() =>
        {
            Assert.That(onConnect.Select(e => e.spaceId).Distinct().Count(), Is.EqualTo(2),
                $"connecting did not reach both of the user's spaces: {watcher.Dump(beforeConnect)}");
            Assert.That(inFirst, Has.Count.EqualTo(1),
                $"one transition produced {inFirst.Count} events in the first space: " +
                $"[{string.Join(" -> ", inFirst.Select(e => e.status))}]");
            Assert.That(inSecond, Has.Count.EqualTo(1),
                $"one transition produced {inSecond.Count} events in the second space: " +
                $"[{string.Join(" -> ", inSecond.Select(e => e.status))}]");
            Assert.That(inFirst.Concat(inSecond).Select(e => e.status), Is.All.EqualTo(UserStatus.Away),
                "a transition to Away carried some other status into one of the spaces");
        });
    }

    private static IReadOnlyList<UserChangedStatus> StatusesFor(
        RealtimeClient client, Guid userId, Guid spaceId, int from = 0)
        => client.EventsOfType<UserChangedStatus>(from)
           .Where(e => e.userId == userId && e.spaceId == spaceId)
           .ToList();

    private static async Task<UserStatus> SnapshotStatusAsync(
        TestUserSession viewer, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var presence = await viewer.Servers.GetMemberPresence(spaceId, ct);
        return presence.Values.FirstOrDefault(m => m.userId == userId)?.status ?? UserStatus.Offline;
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(
            new CreateServerRequest(name, "Presence realtime", string.Empty), ct);

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
