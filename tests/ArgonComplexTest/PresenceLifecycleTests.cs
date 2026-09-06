namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime.client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net.WebSockets;

/// <summary>
/// Presence over its whole real lifetime: a client connects, drops the way a real network drops it,
/// comes back — or does not — and every observer of that user has to end up agreeing with the
/// server about what happened.
/// </summary>
/// <remarks>
/// <para>Every other presence fixture in this campaign works in seconds, because it drives a grain
/// or a Redis key directly. That is the right way to pin a fold or a TTL, and it is the wrong way to
/// find out what a member of a space actually sees, because the parts of this system that decide
/// that are exactly the parts with clocks in them: the 15 s refresh tick, the 30 s heartbeat
/// debounce, the 120 s presence TTL, the disconnect-grace reminder whose period Orleans will not let
/// be shorter than a minute, and the two-minute <c>DelayDeactivation</c> that keeps the session
/// grain alive between them. A test that does not spend that time cannot see them interact.</para>
///
/// <para>So the fixture is deliberately slow, and it buys back the time by overlapping: the control
/// session for the reconnect scenario runs inside the same wall clock as the session under test
/// rather than in a test of its own, and the "reconnect after the grace already finalized" case is
/// the tail of the ungraceful-drop case, because that is the only way to reach the state it needs.
/// Each test spends real seconds only where an <em>absence</em> is being asserted — no Offline for
/// twenty seconds, no flap for thirty — and polls with a deadline everywhere else.</para>
///
/// <para>What the whole fixture is really guarding is one property: a user's status as observers see
/// it on the wire, and their status as the snapshot APIs report it, never disagree for longer than
/// it takes one event to travel. Presence is the one feature where a stale answer is
/// indistinguishable from a correct one until somebody tries to talk to a person who is not there.</para>
/// </remarks>
[TestFixture]
public class PresenceLifecycleTests : TestBase
{
    private PresenceProbe probe = null!;

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    /// <summary>
    /// A connection that dies without a close frame takes its user offline exactly once, and only
    /// after the grace has really expired — then a client that comes back afterwards comes back
    /// Online exactly once, with the snapshot agreeing at every step.
    /// </summary>
    /// <remarks>
    /// <para>This is the ordinary shape of a laptop lid closing, and it has two halves that only make
    /// sense together. The first is that nothing may be announced while the session might still come
    /// back: an Offline inside the grace window is the flap users complain about, and it is why the
    /// first twenty seconds after the drop are spent proving silence rather than polling for
    /// something. The second is that the offline must eventually arrive, once, and take the session
    /// with it — the devices screen, the live-session index and the space snapshot all have to stop
    /// naming a session nobody is on the other end of.</para>
    ///
    /// <para>The grace is accelerated by expiring the session's presence key rather than by waiting
    /// out its 120 s TTL, because the reminder that finalizes is what the test is about and its tick
    /// cannot be pulled forward — the key going early only decides which tick is the one that
    /// finalizes. The single-Offline assertion then keeps a fifteen-second tail: a duplicate
    /// broadcast from a second reminder tick or a second activation would arrive there, and counting
    /// immediately after the first event would miss it.</para>
    ///
    /// <para>The reconnect at the end is the case the grace does not cover: the session was really
    /// finalized, so this is a new session on an old sid, and the user has to come back Online
    /// exactly once — not twice, and not silently, which would leave everyone who saw the Offline
    /// with a permanently dead-looking member.</para>
    /// </remarks>
    [Test, Category("Slow"), CancelAfter(300_000)]
    public async Task An_ungraceful_drop_goes_offline_exactly_once_after_the_grace_and_a_later_reconnect_returns_online(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var joiner   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Lifecycle Drop", ct);
        await JoinAsync(observer, joiner, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeConnect = watcher.Mark();
        var member        = await RealtimeClient.ConnectAsync(joiner, ct);
        var beats         = Heartbeats.Start(member, UserStatus.Online);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeConnect, ct);

        var presenceKey = PresenceProbe.PresenceSessionKey(joiner.UserId, joiner.SessionId);

        Assert.That(await probe.Exists(presenceKey), Is.True,
            $"no presence key ({presenceKey}) for the session that just connected — the sid is wrong and " +
            "nothing below would mean anything");

        // The drop. Heartbeats stop first, because a client whose socket is dead does not send any.
        await beats.DisposeAsync();

        var beforeDrop = watcher.Mark();
        var droppedAt  = DateTimeOffset.UtcNow;

        await member.AbortAsync(ct: ct);

        // A fixed window on purpose: the assertion is that nothing happens, and an absence has no
        // edge to poll for. Twenty seconds is comfortably inside the grace and well past the 15 s
        // tick that would have noticed the connection is gone.
        await watcher.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(20),
            "a dead socket must ride out the disconnect grace instead of announcing the user offline",
            beforeDrop, ct);

        var duringGrace = await SnapshotStatusAsync(observer, spaceId, joiner.UserId, UserStatus.Online, ct);

        Assert.That(duringGrace, Is.EqualTo(UserStatus.Online),
            "the member went offline in the space snapshot while still inside the grace window");

        // Nothing can pull an Orleans reminder tick forward, but the tick only finalizes once the
        // presence key has lapsed — so expiring the key early decides which of the minute ticks is
        // the one that ends the session, and turns a 120 s wait into a 60 s one.
        Assert.That(await probe.ForceExpire(presenceKey, TimeSpan.FromSeconds(1)), Is.True,
            "the presence key was already gone, so the drop was not treated as a grace at all");

        var offline = await watcher.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(130), beforeDrop, ct);

        var offlineAfter = (offline.ReceivedAt - droppedAt).TotalSeconds;

        // A duplicate would come from a second reminder tick or a second activation finalizing again,
        // both a minute apart, so the tail has to be spent rather than sampled.
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        var offlines = watcher.EventsOfType<UserChangedStatus>(beforeDrop)
           .Count(e => e.userId == joiner.UserId && e.status == UserStatus.Offline);

        var sessionsAfterOffline = await joiner.Security.GetSessions(ct);
        var activeAfterOffline   = await probe.ActiveSessionIdsAsync(joiner.UserId, ct);
        var onlineAfterOffline   = await probe.IsUserOnlineAsync(joiner.UserId, ct);
        var snapshotAfterOffline = await SnapshotStatusAsync(observer, spaceId, joiner.UserId, UserStatus.Offline, ct);

        Assert.Multiple(() =>
        {
            Assert.That(offline.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace),
                "the offline reached the observer on the wrong stream");
            Assert.That(offlineAfter, Is.LessThan(130),
                "the offline took longer than the grace it was meant to be finalizing");
            Assert.That(offlines, Is.EqualTo(1),
                $"the drop produced {offlines} Offline broadcasts instead of one. {watcher.Dump(beforeDrop)}");
            Assert.That(onlineAfterOffline, Is.False,
                "the user still reads online after their only session was finalized offline");
            Assert.That(activeAfterOffline, Does.Not.Contain(joiner.SessionId.ToString()),
                "the finalized session is still in the live-session index");
            Assert.That(sessionsAfterOffline.Values.Select(s => s.sessionId), Does.Not.Contain(joiner.SessionId),
                "the devices screen still lists a session that was finalized offline");
            Assert.That(snapshotAfterOffline, Is.EqualTo(UserStatus.Offline),
                "the space snapshot disagrees with the Offline the space was just told");
        });

        await member.DisposeAsync();

        // The session was really finalized, so this is a new session wearing an old sid — the case
        // the grace explicitly does not cover.
        var beforeRevival = watcher.Mark();

        await using var revived      = await RealtimeClient.ConnectAsync(joiner, ct);
        await using var revivedBeats = Heartbeats.Start(revived, UserStatus.Online);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(20), beforeRevival, ct);

        // Long enough for a second broadcast from the reconnect path or from the first heartbeat
        // behind it, both of which land within a couple of seconds.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        var revivalEvents = watcher.EventsOfType<UserChangedStatus>(beforeRevival)
           .Where(e => e.userId == joiner.UserId)
           .ToArray();

        var snapshotAfterRevival = await SnapshotStatusAsync(observer, spaceId, joiner.UserId, UserStatus.Online, ct);
        var sessionsAfterRevival = await joiner.Security.GetSessions(ct);

        Assert.Multiple(() =>
        {
            Assert.That(revivalEvents.Count(e => e.status == UserStatus.Online), Is.EqualTo(1),
                $"a reconnect after the grace finalized produced {revivalEvents.Count(e => e.status == UserStatus.Online)} " +
                $"Online broadcasts instead of one. {watcher.Dump(beforeRevival)}");
            Assert.That(revivalEvents.Any(e => e.status == UserStatus.Offline), Is.False,
                $"the reconnect broadcast an Offline as well as an Online. {watcher.Dump(beforeRevival)}");
            Assert.That(snapshotAfterRevival, Is.EqualTo(UserStatus.Online),
                "the space snapshot disagrees with the Online the space was just told");
            Assert.That(sessionsAfterRevival.Values.Select(s => s.sessionId), Does.Contain(joiner.SessionId),
                "the revived session is not on the devices screen");
            Assert.That(watcher.DecodeFailures, Is.Empty);
        });
    }

    /// <summary>
    /// A session that drops and reconnects inside the grace has to keep renewing its presence exactly
    /// like one that never dropped — proved against a control session in the same wall clock that
    /// never drops at all.
    /// </summary>
    /// <remarks>
    /// <para>This is H2 at the level the user lives on. Nothing is broadcast when the renewal stops,
    /// which is what makes it so unpleasant: the client is connected, it is heartbeating, no event
    /// says otherwise, and every snapshot API quietly starts answering Offline once the status keys
    /// lapse two minutes later. The observer's event stream and the space snapshot disagree, and only
    /// the snapshot is wrong, so a client that reloads a space is the one that sees it.</para>
    ///
    /// <para>Two minutes is not a number the test can shorten. The status keys carry a 120 s TTL, the
    /// only thing that renews them is the session grain's 15 s tick, and the question being asked is
    /// whether that tick is still running — so the answer only exists once the TTL that was standing
    /// when the tick stopped has run out. The three snapshot reads at 30 s, 90 s and 130 s after the
    /// reconnect are placed either side of that edge deliberately: a failure only at 130 s says the
    /// renewal stopped, while a failure at 30 s would say something far worse happened at the
    /// reconnect itself.</para>
    ///
    /// <para>The control session is here because a bare "it went Offline after two minutes" is not
    /// evidence on its own — a broken environment, a paused container or a stalled reminder service
    /// would produce the same reading. The control connects at the same moment, heartbeats the same
    /// status on the same schedule, never drops, and is read at the end of the same window. If the
    /// control is Online with fresh TTLs and the subject is not, the difference is the detach.</para>
    ///
    /// <para>The control is also the whole of the long-lived-session case: 150 s is past the 120 s
    /// presence TTL and past the 2 min <c>DelayDeactivation</c>, so a session that is still Online
    /// there with TTLs refreshed inside the last tick has proved that the timer, the deactivation
    /// delay and the TTL renewal all survive a quiet session that never changes anything.</para>
    ///
    /// <para><b>The contract this now guards (defect S2, fixed).</b> The 15 s tick belongs to "this
    /// session has live connections", not to "this session has just started".
    /// <c>UserSessionGrain.DetachConnectionAsync</c> still disposes <c>refreshTimer</c> when the last
    /// connection drops — that is what lets the presence TTL lapse so the grace reminder can finalize
    /// — and the extracted <c>EnsureRefreshTimer()</c> is now called from
    /// <c>AttachConnectionAsync</c> and from every <c>HeartBeatAsync</c> as well as from
    /// <c>OnActivateAsync</c> and <c>EnsureSessionStartedAsync</c>. Before that, both of the latter
    /// two were the only re-arm sites and <c>EnsureSessionStartedAsync</c> returns on its first
    /// statement once <c>SessionStarted</c> is set, so a reconnect left the timer dead for good.</para>
    ///
    /// <para>Why that had no symptom for two minutes and then had every symptom at once: the tick is
    /// the only caller of <c>UserPresenceService.RefreshSessionStatusTtlAsync</c>, and that is the
    /// only thing that renews <c>status:user:{u}:session:{sid}</c> and
    /// <c>status:user:{u}:aggregated</c>. <c>UserPresenceService.HeartbeatAsync</c> renews the
    /// presence key, the last-seen stamp and the session index and touches neither status key, so
    /// <c>IsUserOnlineAsync</c> went on saying true while the snapshot went Offline. Observed before
    /// the fix: Online at 30 s and 90 s after the reconnect, Offline at 130 s with both status keys
    /// gone and the presence key still carrying 92 s, against a control that was Online with both
    /// TTLs above 100 s at the same instant. Nothing is broadcast when that happens, so observers
    /// keep the Online they were last told and only a reload, a newcomer or <c>GetSpaceStats</c> ever
    /// sees the disagreement — which is what makes this a test worth keeping rather than a timing
    /// curiosity.</para>
    /// </remarks>
    [Test, Category("Slow"), CancelAfter(300_000)]
    public async Task A_session_that_reconnects_within_the_grace_keeps_refreshing_its_status_like_one_that_never_dropped(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var subject  = await CreateSessionAsync(ct);
        var control  = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Lifecycle Reconnect", ct);
        await JoinAsync(observer, subject, spaceId, ct);
        await JoinAsync(observer, control, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeConnect = watcher.Mark();

        var dropper        = await RealtimeClient.ConnectAsync(subject, ct);
        var dropperBeats   = Heartbeats.Start(dropper, UserStatus.Online);
        await using var steady      = await RealtimeClient.ConnectAsync(control, ct);
        await using var steadyBeats = Heartbeats.Start(steady, UserStatus.Online);

        var controlConnectedAt = Stopwatch.StartNew();

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == subject.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeConnect, ct);
        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == control.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeConnect, ct);

        // Established for twenty seconds before the drop, so at least one refresh tick has run and the
        // session is unambiguously a settled one rather than one still in its first moments.
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        await dropperBeats.DisposeAsync();

        var beforeDrop = watcher.Mark();
        await dropper.AbortAsync(ct: ct);
        await dropper.DisposeAsync();

        // Well inside the grace — this is the reconnect the grace exists to make invisible.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        await using var rejoined      = await RealtimeClient.ConnectAsync(subject, ct);
        await using var rejoinedBeats = Heartbeats.Start(rejoined, UserStatus.Online);

        var reconnectedAt = Stopwatch.StartNew();

        await DelayUntilAsync(reconnectedAt, TimeSpan.FromSeconds(30), ct);
        var at30 = await SnapshotStatusAsync(observer, spaceId, subject.UserId, UserStatus.Online, ct);

        await DelayUntilAsync(reconnectedAt, TimeSpan.FromSeconds(90), ct);
        var at90 = await SnapshotStatusAsync(observer, spaceId, subject.UserId, UserStatus.Online, ct);

        await DelayUntilAsync(reconnectedAt, TimeSpan.FromSeconds(130), ct);
        var at130 = await SnapshotStatusAsync(observer, spaceId, subject.UserId, UserStatus.Online, ct);

        var subjectStatusTtl     = await probe.TtlOf(PresenceProbe.SessionStatusKey(subject.UserId, subject.SessionId));
        var subjectAggregatedTtl = await probe.TtlOf(PresenceProbe.AggregatedStatusKey(subject.UserId));
        var subjectPresenceTtl   = await probe.TtlOf(PresenceProbe.PresenceSessionKey(subject.UserId, subject.SessionId));

        var controlStatus        = await SnapshotStatusAsync(observer, spaceId, control.UserId, UserStatus.Online, ct);
        var controlStatusTtl     = await probe.TtlOf(PresenceProbe.SessionStatusKey(control.UserId, control.SessionId));
        var controlAggregatedTtl = await probe.TtlOf(PresenceProbe.AggregatedStatusKey(control.UserId));
        var controlUptime        = controlConnectedAt.Elapsed;

        var offlineEvents = watcher.EventsOfType<UserChangedStatus>(beforeDrop)
           .Where(e => e.userId == subject.UserId && e.status == UserStatus.Offline)
           .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(dropperBeats.Failures, Is.Empty, "the subject's heartbeats failed before the drop");
            Assert.That(rejoinedBeats.Failures, Is.Empty, "the reconnected subject could not heartbeat");
            Assert.That(steadyBeats.Failures, Is.Empty, "the control session could not heartbeat");

            Assert.That(offlineEvents, Is.Empty,
                $"a session that reconnected inside the grace was broadcast Offline. {watcher.Dump(beforeDrop)}");

            Assert.That(at30, Is.EqualTo(UserStatus.Online),
                "30 s after the reconnect the space snapshot already disagrees with the connected, heartbeating client");
            Assert.That(at90, Is.EqualTo(UserStatus.Online),
                "90 s after the reconnect the space snapshot disagrees with the connected, heartbeating client");
            Assert.That(at130, Is.EqualTo(UserStatus.Online),
                $"130 s after reconnecting, a client that is connected and heartbeating Online reads {at130} in the "
              + $"space snapshot. status TTL={Describe(subjectStatusTtl)}, aggregated TTL={Describe(subjectAggregatedTtl)}, "
              + $"presence TTL={Describe(subjectPresenceTtl)} — the presence key is being renewed and the status keys "
              + "are not, which is the refresh timer that DetachConnectionAsync disposed and nothing re-armed");

            Assert.That(subjectStatusTtl, Is.Not.Null,
                $"the reconnected session's status key has lapsed (aggregated TTL={Describe(subjectAggregatedTtl)}, "
              + $"presence TTL={Describe(subjectPresenceTtl)})");
            Assert.That(subjectAggregatedTtl, Is.Not.Null,
                $"the reconnected session's aggregated status key has lapsed (session status TTL={Describe(subjectStatusTtl)}, "
              + $"presence TTL={Describe(subjectPresenceTtl)})");

            Assert.That(controlStatus, Is.EqualTo(UserStatus.Online),
                $"the control session, connected and heartbeating for {controlUptime.TotalSeconds:F0} s without dropping, "
              + "is not Online — the environment, not the detach, is what this test is measuring");
            Assert.That(controlStatusTtl?.TotalSeconds ?? -1, Is.GreaterThan(100),
                $"the control session's status TTL is {Describe(controlStatusTtl)} after {controlUptime.TotalSeconds:F0} s; "
              + "a 15 s tick renewing a 120 s TTL never leaves it below 100 s");
            Assert.That(controlAggregatedTtl?.TotalSeconds ?? -1, Is.GreaterThan(100),
                $"the control user's aggregated TTL is {Describe(controlAggregatedTtl)} after {controlUptime.TotalSeconds:F0} s");
        });
    }

    /// <summary>
    /// One account on two devices: the device holding Do Not Disturb dies without a close frame, and
    /// the account must fall back to the other device's Online — never through Offline, and never
    /// before the grace is really over.
    /// </summary>
    /// <remarks>
    /// <para>The two halves are separate promises. During the grace, the DND has to linger: the
    /// phone might be back in a moment, and a status that flickered to Online and back every time a
    /// device blinked would make DND useless as a signal. After the grace, the DND has to be
    /// released, because the device asserting it is gone and the user is still sitting at the other
    /// one — a DND that outlives its device is the version of this bug people notice, since it
    /// silences notifications on a machine nobody asked to silence.</para>
    ///
    /// <para>Offline is the failure both halves share. The account never stops having a live device,
    /// so no observer may ever be told the user left, not even for the instant it takes the aggregate
    /// to be recomputed. That is the assertion that would catch a finalize that removed the wrong
    /// session's status, or one that folded over an index it had already emptied.</para>
    ///
    /// <para>The second device is a second sign-in of the same account on a client of its own, so it
    /// carries its own sid — which is what the presence keys, the session grain and the devices
    /// screen are all keyed on. Registering a second account would not do: the whole scenario is one
    /// user's aggregate over two of their devices.</para>
    /// </remarks>
    [Test, Category("Slow"), CancelAfter(300_000)]
    public async Task A_dnd_device_that_dies_ungracefully_releases_its_status_after_the_grace_and_never_through_offline(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var deviceA  = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Lifecycle Devices", ct);
        await JoinAsync(observer, deviceA, spaceId, ct);

        var deviceB = await SecondDeviceAsync(deviceA, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        await using var desktop      = await RealtimeClient.ConnectAsync(deviceA, ct);
        await using var desktopBeats = Heartbeats.Start(desktop, UserStatus.Online);

        var phone      = await RealtimeClient.ConnectAsync(deviceB, ct);
        var phoneBeats = Heartbeats.Start(phone, UserStatus.DoNotDisturb);

        Assert.That(desktop.SessionId, Is.Not.EqualTo(phone.SessionId),
            "both clients claimed the same sid, so this is one device pretending to be two");

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == deviceA.UserId && e.status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(20), ct: ct);

        Assert.That(await probe.AggregatedStatusAsync(deviceA.UserId, ct), Is.EqualTo(UserStatus.DoNotDisturb),
            "the stored aggregate never reached DoNotDisturb, so there is nothing for the drop to release");

        await phoneBeats.DisposeAsync();

        var beforeDrop = watcher.Mark();
        await phone.AbortAsync(ct: ct);

        // Fixed, because the assertion is an absence: for as long as the grace is running the
        // account's status must not move at all — not to Online, and certainly not to Offline.
        await watcher.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == deviceA.UserId,
            TimeSpan.FromSeconds(20),
            "a device dropping must not move the account's status while its grace is still running",
            beforeDrop, ct);

        var duringGrace = await SnapshotStatusAsync(observer, spaceId, deviceA.UserId, UserStatus.DoNotDisturb, ct);

        var phonePresenceKey = PresenceProbe.PresenceSessionKey(deviceB.UserId, deviceB.SessionId);

        Assert.That(await probe.ForceExpire(phonePresenceKey, TimeSpan.FromSeconds(1)), Is.True,
            "the dropped device's presence key was already gone, so its grace was never armed");

        var released = await watcher.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == deviceA.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(130), beforeDrop, ct);

        // A late Offline — the finalize folding over an index it emptied first — would arrive here.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        var statuses = watcher.EventsOfType<UserChangedStatus>(beforeDrop)
           .Where(e => e.userId == deviceA.UserId)
           .Select(e => e.status)
           .ToArray();

        var afterRelease   = await SnapshotStatusAsync(observer, spaceId, deviceA.UserId, UserStatus.Online, ct);
        var activeSessions = await probe.ActiveSessionIdsAsync(deviceA.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(desktopBeats.Failures, Is.Empty, "the surviving device could not heartbeat");
            Assert.That(duringGrace, Is.EqualTo(UserStatus.DoNotDisturb),
                "the space snapshot dropped the DoNotDisturb while its device was still inside the grace");
            Assert.That(statuses, Does.Not.Contain(UserStatus.Offline),
                $"the account was broadcast Offline while one of its devices was connected and heartbeating: " +
                $"[{string.Join(", ", statuses)}]. {watcher.Dump(beforeDrop)}");
            Assert.That(released.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace));
            Assert.That(afterRelease, Is.EqualTo(UserStatus.Online),
                "the space snapshot still carries the dead device's DoNotDisturb after the grace finalized");
            Assert.That(activeSessions, Does.Contain(deviceA.SessionId.ToString()),
                "the surviving device is no longer in the live-session index");
            Assert.That(activeSessions, Does.Not.Contain(deviceB.SessionId.ToString()),
                "the finalized device is still in the live-session index");
        });

        await phone.DisposeAsync();
    }

    /// <summary>
    /// One window of a session signing out while another window of the same session is still open and
    /// heartbeating must not flap the user through Offline and back.
    /// </summary>
    /// <remarks>
    /// <para>Two windows of one client share a sid, so they share a session grain and its connection
    /// set — which is exactly why "log out of this window" is ambiguous in a way the product has to
    /// resolve one way or the other. Either the session belongs to the remaining window and nothing
    /// visible happens, or the sign-out ends the session and the other window is disconnected with
    /// it. Both are defensible. What is not is announcing the user Offline and then Online again
    /// seconds later, which is what an observer's roster, notification badges and "who is here" list
    /// all react to.</para>
    ///
    /// <para>The surviving window beats every five seconds rather than the client's usual fifteen.
    /// That is still an honest client — the contract is a heartbeat at least that often, and the real
    /// one also beats immediately on connect — and it puts the resurrection well inside the thirty
    /// second window instead of leaving the test unable to say whether the Online was coming.</para>
    ///
    /// <para>Thirty seconds is spent rather than polled because the whole claim is an absence, and it
    /// is long enough to cover both a debounced heartbeat (30 s) and two of the surviving window's
    /// beats.</para>
    ///
    /// <para><b>The contract this now guards (defect S9, fixed).</b> Signing out is scoped to the
    /// connection that asked for it. <c>AppHub.GoOffline</c> now passes <c>Context.ConnectionId</c>
    /// to <c>IUserSessionGrain.GoOfflineAsync(string)</c>, which removes that one connection and
    /// returns without touching anything else while others remain — the same guard
    /// <c>DetachConnectionAsync</c> always had — and finalizes immediately, with no grace, only when
    /// it was the last one. That is the first of the two defensible behaviours above: no Offline is
    /// ever published, so there is no Online to follow it.</para>
    ///
    /// <para>What it replaced: <c>GoOfflineAsync()</c> cleared the entire connection set and called
    /// <c>FinalizeOfflineAsync</c> unconditionally, which deleted the presence and status keys and
    /// broadcast the aggregate as Offline; the surviving window's next heartbeat then landed on a
    /// fresh activation whose <c>SessionStarted</c> was false (the activation state is volatile, so
    /// nothing carries it across the deactivation) and brought the user straight back. Observed:
    /// <c>UserChangedStatus(Offline)</c> and <c>UserChangedStatus(Online)</c> 12 ms apart here, and
    /// up to a full 15 s heartbeat interval of "offline" in the field. The parameterless overload
    /// keeps its old session-wide meaning on purpose — <c>SecurityGrain.EndSessionAsync</c> means
    /// exactly that by signing a device out.</para>
    /// </remarks>
    [Test, Category("Slow"), CancelAfter(300_000)]
    public async Task A_window_signing_out_beside_a_live_window_of_the_same_session_does_not_flap_the_user(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var joiner   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Lifecycle Windows", ct);
        await JoinAsync(observer, joiner, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeConnect = watcher.Mark();

        await using var windowA = await RealtimeClient.ConnectAsync(joiner, ct);
        await using var windowB = await RealtimeClient.ConnectAsync(joiner, ct);

        await windowA.Heartbeat(UserStatus.Online, ct);
        await windowB.Heartbeat(UserStatus.Online, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeConnect, ct);

        Assert.Multiple(() =>
        {
            Assert.That(windowA.SessionId, Is.EqualTo(windowB.SessionId),
                "the two windows are not on one session, so this is a two-device test by accident");
            Assert.That(windowA.ConnectionId, Is.Not.EqualTo(windowB.ConnectionId),
                "the two windows share a connection id, so only one of them is really attached");
        });

        var beforeSignOut = watcher.Mark();

        await windowA.GoOffline(ct);

        await using (Heartbeats.Start(windowB, UserStatus.Online, TimeSpan.FromSeconds(5)))
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

        var statuses = watcher.EventsOfType<UserChangedStatus>(beforeSignOut)
           .Where(e => e.userId == joiner.UserId)
           .Select(e => e.status)
           .ToArray();

        var finalStatus = await SnapshotStatusAsync(observer, spaceId, joiner.UserId, UserStatus.Online, ct);
        var stillOnline = await probe.IsUserOnlineAsync(joiner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Does.Not.Contain(UserStatus.Offline),
                $"one window signing out took the whole session offline while another window was connected and "
              + $"heartbeating; observers saw [{string.Join(" -> ", statuses)}]. {watcher.Dump(beforeSignOut)}");
            Assert.That(finalStatus, Is.EqualTo(UserStatus.Online),
                "the surviving window's session is not Online in the space snapshot");
            Assert.That(stillOnline, Is.True,
                "the user reads offline while one of their windows is connected and heartbeating");
        });
    }

    // -------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The status the space snapshot reports for a member, given every chance to be the expected one.
    /// </summary>
    /// <remarks>
    /// <c>SpaceReadGrain.GetPresence</c> is cached for a second and the fan-out that precedes it is
    /// asynchronous, so a single read can be a second stale. Polling for the expected value and
    /// returning whatever was last seen means a green run costs one round trip and a red one still
    /// reports the real value rather than a timeout.
    /// </remarks>
    private static async Task<UserStatus?> SnapshotStatusAsync(
        TestUserSession reader, Guid spaceId, Guid userId, UserStatus expected, CancellationToken ct)
        => await Poll.ForValueAsync(
            async () => (await reader.Servers.GetMemberPresence(spaceId, ct))
               .Values.FirstOrDefault(m => m.userId == userId)?.status,
            status => status == expected,
            TimeSpan.FromSeconds(3), ct: ct);

    /// <summary>Waits until <paramref name="since"/> reads at least <paramref name="mark"/>.</summary>
    private static Task DelayUntilAsync(Stopwatch since, TimeSpan mark, CancellationToken ct)
    {
        var remaining = mark - since.Elapsed;
        return remaining > TimeSpan.Zero ? Task.Delay(remaining, ct) : Task.CompletedTask;
    }

    private static string Describe(TimeSpan? ttl)
        => ttl is null ? "gone" : $"{ttl.Value.TotalSeconds:F0}s";

    /// <summary>
    /// A second device of an account that is already signed in: the same credentials, signed in
    /// again on a client of its own, with its own machine id and its own sid.
    /// </summary>
    /// <remarks>
    /// <para><see cref="TestBase.CreateSessionAsync"/> registers a new user every time, which is two
    /// accounts and not two devices — and multi-device presence is entirely about one account's
    /// sessions folding into one aggregate, so a second account would make the scenario vanish. A
    /// real second sign-in is used rather than lending the first device's token to a second client,
    /// because a token carries a hash of the machine it was minted for and reusing one would make
    /// the two "devices" one machine as far as the server is concerned.</para>
    ///
    /// <para>The sid, which is what every presence key, the session grain and the devices screen are
    /// keyed on, comes from the new client's own <see cref="DefaultHeaderInterceptor"/>, so the two
    /// devices are two sessions from the first request onwards. Both assertions here are premises
    /// rather than subjects: if either fails, the test that called this was never testing two
    /// devices at all.</para>
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
            Assert.Fail($"Could not sign the account in on a second device: {(result as FailedAuthorize)!.error}");
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

    private Task<WebSocket> WebSocketFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = FactoryAsp.Server.CreateWebSocketClient();

        foreach (var protocol in protocols ?? [])
            socket.SubProtocols.Add(protocol);

        return socket.ConnectAsync(uri, ct);
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Presence lifecycle", string.Empty), ct);

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

    /// <summary>
    /// The desktop client's heartbeat loop: one immediately, then one every fifteen seconds, for as
    /// long as the connection lives.
    /// </summary>
    /// <remarks>
    /// <para>Nothing in this fixture can be asserted about a session that is not heartbeating,
    /// because a silent client is indistinguishable from a dead one after the presence TTL — and the
    /// bugs being hunted here are precisely the ones where a client <em>is</em> talking and the
    /// server has stopped listening. Driving the beats by hand from each test would mean interleaving
    /// them with the two-minute waits the tests are made of.</para>
    ///
    /// <para>A failed beat is recorded rather than thrown: the loop runs beside the test, so an
    /// exception on it would surface as an unobserved task fault with no relation to the assertion
    /// that failed. Tests read <see cref="Failures"/> instead, which turns "the client could not talk
    /// to the hub" into a stated reason rather than a mysterious red.</para>
    /// </remarks>
    private sealed class Heartbeats : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop     = new();
        private readonly List<string>            failures = [];
        private readonly Lock                    gate     = new();

        private Task loop = Task.CompletedTask;

        public static Heartbeats Start(RealtimeClient client, UserStatus status, TimeSpan? period = null)
        {
            var pump = new Heartbeats();
            pump.loop = pump.RunAsync(client, status, period ?? TimeSpan.FromSeconds(15));
            return pump;
        }

        /// <summary>Beats that could not be delivered, in arrival order. Empty is what a test expects.</summary>
        public IReadOnlyList<string> Failures
        {
            get
            {
                lock (gate) return failures.ToArray();
            }
        }

        private async Task RunAsync(RealtimeClient client, UserStatus status, TimeSpan period)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (client.IsConnected)
                        await client.Heartbeat(status, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    lock (gate)
                        failures.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {e.GetType().Name}: {e.Message}");
                }

                try
                {
                    await Task.Delay(period, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();

            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Cancelling the pump is how it is stopped; the tests own everything else.
            }

            stop.Dispose();
        }
    }
}
