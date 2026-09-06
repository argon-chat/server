namespace ArgonComplexTest.Tests;

using System.Collections.Concurrent;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;

/// <summary>
/// Presence when two things happen at the same instant: a device switch, two devices arguing about a
/// status, a crowd connecting together. Everything here is asserted from the outside — what an
/// observer sitting in the space actually receives, and what the snapshot it would load says.
/// </summary>
/// <remarks>
/// <para>Every write in the presence path is a read-fold-write over Redis with no lock around it:
/// <c>RecalculateAggregatedStatusAsync</c> folds a user's session index and stores the result,
/// <c>MarkBroadcastIfChangedAsync</c> reads the hysteresis record and writes it back, and the grain
/// that drives both — <see cref="Argon.Grains.IUserGrain"/> — is a <c>[StatelessWorker]</c>, so
/// several activations of it can be inside those sequences for one user at once. Sequentially none
/// of that matters and every fixture in this campaign that acts one step at a time passes. The
/// failures users report are the ones that need two actors: closing the laptop as the phone wakes
/// up, a manual Do-Not-Disturb landing while the other device is still saying Online, a space full
/// of people arriving on the same reconnect storm.</para>
///
/// <para>The assertions are deliberately about the account, not about a session. A user does not
/// care which of their devices Redis folded last; they care that the roster says Online while
/// something of theirs is connected, that the last event their friends received agrees with the
/// snapshot those friends would reload, and that arriving once produces one line in the roster
/// rather than two. So each test pins three things together — the stored aggregate, the space
/// snapshot (<c>GetMemberPresence</c>) and the last <see cref="UserChangedStatus"/> the observer
/// saw — and a disagreement between any two of them is the defect, whichever one is "right".</para>
///
/// <para>The second device of an account is driven through its session grain rather than through a
/// second hub connection. The sid is what presence is keyed on and the grain is what the hub calls,
/// so an <c>AttachConnectionAsync</c> + <c>HeartBeatAsync</c> pair on a fresh sid is the same
/// session start the transport would have produced, minus a login and a WebSocket per iteration —
/// and this fixture needs forty of them. The observer is always a real client on a real hub
/// connection, because the events it receives are the thing under test.</para>
/// </remarks>
[TestFixture]
public class PresenceRaceTests : TestBase
{
    private PresenceProbe probe = null!;

    /// <summary>
    /// Grain-driven sessions this fixture started, so a red test does not leave a 15 s refresh tick
    /// running against Redis for the rest of the process.
    /// </summary>
    private readonly ConcurrentBag<(Guid userId, Guid sid)> startedSessions = [];

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    [TearDown]
    public async Task ReleaseStartedSessionsAsync()
    {
        foreach (var (userId, sid) in startedSessions)
        {
            try
            {
                await probe.SessionGrain(userId, sid).GoOfflineAsync();
            }
            catch
            {
                // Best effort. A session the test already finalized throws nothing a report could
                // use, and failing to clean up must never be reported as the test's own failure.
            }
        }

        startedSessions.Clear();
    }

    // ── the scenarios ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Twenty device switches, each one a fresh pair of sids: the old device says goodbye at the same
    /// instant the new one says hello, and the account must be Online when the dust settles — in the
    /// stored aggregate, in the space snapshot, and in the last status event the space was sent.
    /// </summary>
    /// <remarks>
    /// <para>This is the shape of "I closed the laptop and picked up the phone", and it is the one
    /// case where the two halves of the presence fold genuinely overlap: <c>GoOfflineAsync</c> is
    /// deleting session A's status and re-folding while session B's start is adding its own and
    /// re-folding too. Both folds end in a plain <c>StringSet</c> of
    /// <c>status:user:{u}:aggregated</c>, so the one that reads first and writes last wins, and if
    /// that is A's the account is stored Offline with a live, heartbeating session attached.</para>
    ///
    /// <para>Nothing recovers from that on its own, which is why it is worth twenty rounds rather
    /// than one. B's later heartbeats carry the status it already has, so the grain never
    /// recalculates; the refresh tick renews the TTL of the wrong value every 15 s. The account
    /// stays Offline to every roster until the user changes status by hand.</para>
    ///
    /// <para>Counted rather than asserted per round: a race that reproduces one time in twenty is a
    /// different bug report from one that reproduces every time, and stopping at the first failure
    /// would throw that number away.</para>
    /// </remarks>
    // Twenty rounds cost about three seconds when the switches are clean; the six-minute budget is
    // there for the run where they are not, because each failing round spends its whole settle window.
    [Test, CancelAfter(1000 * 60 * 6)]
    public async Task Switching_device_never_leaves_the_account_offline_while_the_new_device_is_online(
        CancellationToken ct = default)
    {
        const int iterations = 20;

        var observer = await CreateSessionAsync(ct);
        var actor    = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Race Device Switch", ct);
        await JoinAsync(observer, actor, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var failures      = new List<string>();
        var setupFailures = new List<string>();
        var flickers      = 0;

        for (var round = 0; round < iterations; round++)
        {
            var sidA = Guid.CreateVersion7();
            var sidB = Guid.CreateVersion7();

            var sessionA = OwnedSession(actor.UserId, sidA);
            var sessionB = OwnedSession(actor.UserId, sidB);

            var roundMark = watcher.Mark();

            // The old device, established and announced, is the baseline the switch is measured
            // against: without an Online in the log first, a green round could just mean nothing
            // ever happened.
            await sessionA.AttachConnectionAsync($"conn-a-{round}");

            // Scoped to this round rather than to the whole log: the previous round's Offline event
            // can still be in flight when its aggregate already reads Offline, and a whole-log check
            // would then accept the previous round's Online as this round's baseline.
            var baseline = await Poll.UntilAsync(
                async () => (await probe.AggregatedStatusAsync(actor.UserId, ct)) == UserStatus.Online
                         && watcher.EventsOfType<UserChangedStatus>(roundMark)
                               .Any(e => e.userId == actor.UserId && e.status == UserStatus.Online),
                TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), ct);

            if (!baseline)
            {
                setupFailures.Add(
                    $"round {round}: the first device never reached Online (aggregate=" +
                    $"{await probe.AggregatedStatusAsync(actor.UserId, ct)}, lastEvent={LastStatusFor(watcher, actor.UserId, roundMark)})");
                await QuietlyReleaseAsync(actor.UserId, sidA, sidB);
                continue;
            }

            var switchMark = watcher.Mark();

            // Both sides scheduled onto the pool rather than one being started inline, so neither is
            // systematically first — the race is meant to be a race.
            var leaving  = Task.Run(async () => await sessionA.GoOfflineAsync(), ct);
            var arriving = Task.Run(async () =>
            {
                await sessionB.AttachConnectionAsync($"conn-b-{round}");
                await sessionB.HeartBeatAsync($"conn-b-{round}", UserStatus.Online);
            }, ct);

            await Task.WhenAll(leaving, arriving);

            var settled = await Poll.UntilAsync(
                async () => (await probe.AggregatedStatusAsync(actor.UserId, ct)) == UserStatus.Online
                         && (await SnapshotStatusAsync(observer, spaceId, actor.UserId, ct)) == UserStatus.Online
                         && LastStatusFor(watcher, actor.UserId, roundMark) == UserStatus.Online,
                TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(150), ct);

            // A transient Offline in the middle of a switch is a visible blink for everyone in the
            // space even when the end state is right, so it is counted and reported — it is not what
            // this test fails on.
            flickers += watcher.EventsOfType<UserChangedStatus>(switchMark)
               .Count(e => e.userId == actor.UserId && e.status == UserStatus.Offline);

            if (!settled)
            {
                failures.Add(
                    $"round {round}: aggregate={await probe.AggregatedStatusAsync(actor.UserId, ct)}, " +
                    $"snapshot={await SnapshotStatusAsync(observer, spaceId, actor.UserId, ct)}, " +
                    $"lastEvent={LastStatusFor(watcher, actor.UserId, roundMark)}, " +
                    $"sessionA={await probe.SessionStatusAsync(actor.UserId, sidA)}, " +
                    $"sessionB={await probe.SessionStatusAsync(actor.UserId, sidB)}, " +
                    $"aliveB={await probe.IsSessionAliveAsync(actor.UserId, sidB, ct)}, " +
                    $"index=[{string.Join(",", await probe.SessionIndexAsync(actor.UserId))}]");
            }

            await QuietlyReleaseAsync(actor.UserId, sidA, sidB);

            var clean = await Poll.UntilAsync(
                async () => (await probe.AggregatedStatusAsync(actor.UserId, ct)) == UserStatus.Offline,
                TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), ct);

            if (!clean)
                setupFailures.Add($"round {round}: the account did not return to Offline before the next round");
        }

        Assert.Multiple(() =>
        {
            Assert.That(setupFailures, Is.Empty,
                "rounds that could not be set up or torn down — the failure count below is out of that many fewer:\n"
              + string.Join("\n", setupFailures));

            Assert.That(failures, Is.Empty,
                $"{failures.Count}/{iterations} device switches left the account not Online while a live, " +
                $"heartbeating session was attached ({flickers} transient Offline events were broadcast to the " +
                $"space across the run). Every one of these is a user who is shown offline to their whole space " +
                $"until they change status by hand:\n" + string.Join("\n", failures));
        });
    }

    /// <summary>
    /// Two devices of one account changing status at each other for ten rounds: whatever order the
    /// writes land in, the account ends on the priority fold of the two devices' final statuses, and
    /// the last event the space received says the same thing as the snapshot.
    /// </summary>
    /// <remarks>
    /// <para>The fold is <c>DoNotDisturb &gt; Online &gt; Away</c> and it is not a matter of taste:
    /// it is what <c>UserPresenceService.RecalculateAggregatedStatusAsync</c> computes and what every
    /// roster in the product displays. Here the desk client ends on Away and the phone ends on
    /// Online, so the answer is Online — which means the test fails if either device's last write was
    /// lost (Away or Do-Not-Disturb would surface instead) and also if the aggregate is right but the
    /// space was last told something else.</para>
    ///
    /// <para>The rounds are fired with <c>Task.WhenAll</c>, so two independent session grains are
    /// inside <c>SetSessionStatusAsync</c> — read the index, read each status, store the fold — at the
    /// same time, ten times over. The status token bucket (capacity 5, refilling at one every two
    /// seconds) drops some of the intermediate changes on purpose, which is exactly what it is for;
    /// the final status is then re-asserted the way a real client re-asserts it on its 15 s heartbeat
    /// until the two session keys hold it, so what is left to measure is the fold and not the
    /// throttle.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Two_devices_toggling_status_at_once_settle_on_the_priority_fold_of_their_final_statuses(
        CancellationToken ct = default)
    {
        const int rounds = 10;

        var observer = await CreateSessionAsync(ct);
        var actor    = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Race Status Toggles", ct);
        await JoinAsync(observer, actor, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var sidA = Guid.CreateVersion7();
        var sidB = Guid.CreateVersion7();

        var sessionA = OwnedSession(actor.UserId, sidA);
        var sessionB = OwnedSession(actor.UserId, sidB);

        const string connA = "conn-desk";
        const string connB = "conn-phone";

        await sessionA.AttachConnectionAsync(connA);
        await sessionB.AttachConnectionAsync(connB);

        var bothOnline = await Poll.UntilAsync(
            async () => (await probe.SessionStatusAsync(actor.UserId, sidA)) == UserStatus.Online
                     && (await probe.SessionStatusAsync(actor.UserId, sidB)) == UserStatus.Online,
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.That(bothOnline, Is.True, "the two devices did not both come up before the toggling started");

        var mark = watcher.Mark();

        // Desk: Online, Away, Online, … ending Away. Phone: DoNotDisturb, Online, … ending Online.
        for (var round = 0; round < rounds; round++)
        {
            await Task.WhenAll(
                sessionA.HeartBeatAsync(connA, DeskRound(round)).AsTask(),
                sessionB.HeartBeatAsync(connB, PhoneRound(round)).AsTask());
        }

        var finalDesk  = DeskRound(rounds - 1);
        var finalPhone = PhoneRound(rounds - 1);
        var expected   = Fold(finalDesk, finalPhone);

        // The client's own recovery from a throttled change: keep re-asserting the current status on
        // the heartbeat until it takes. Polled rather than slept so a run where the bucket already
        // has tokens costs one round trip.
        var converged = await Poll.UntilAsync(
            async () =>
            {
                await sessionA.HeartBeatAsync(connA, finalDesk);
                await sessionB.HeartBeatAsync(connB, finalPhone);

                return (await probe.SessionStatusAsync(actor.UserId, sidA)) == finalDesk
                    && (await probe.SessionStatusAsync(actor.UserId, sidB)) == finalPhone;
            },
            TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1), ct);

        Assert.That(converged, Is.True,
            $"the two devices never settled on the statuses they last sent (desk wanted {finalDesk}, has " +
            $"{await probe.SessionStatusAsync(actor.UserId, sidA)}; phone wanted {finalPhone}, has " +
            $"{await probe.SessionStatusAsync(actor.UserId, sidB)})");

        var aggregate = await Poll.ForValueAsync(
            () => probe.AggregatedStatusAsync(actor.UserId, ct),
            status => status == expected, TimeSpan.FromSeconds(5), ct: ct);

        var snapshot = await Poll.ForValueAsync(
            () => SnapshotStatusAsync(observer, spaceId, actor.UserId, ct),
            status => status == expected, TimeSpan.FromSeconds(5), ct: ct);

        var lastEvent = await Poll.ForValueAsync(
            () => Task.FromResult(LastStatusFor(watcher, actor.UserId)),
            status => status == expected, TimeSpan.FromSeconds(5), ct: ct);

        var seen = string.Join(", ", watcher.EventsOfType<UserChangedStatus>(mark)
           .Where(e => e.userId == actor.UserId)
           .Select(e => e.status));

        Assert.Multiple(() =>
        {
            Assert.That(aggregate, Is.EqualTo(expected),
                $"two devices holding {finalDesk} and {finalPhone} must fold to {expected}; the stored aggregate " +
                $"says {aggregate}, so one device's last write was lost in the race");

            Assert.That(snapshot, Is.EqualTo(expected),
                $"the space snapshot says {snapshot} for an account whose devices hold {finalDesk} and {finalPhone}");

            Assert.That(lastEvent, Is.EqualTo(snapshot),
                $"the last status the space was sent ({lastEvent}) disagrees with the snapshot a client would " +
                $"load ({snapshot}); the events seen during the toggling were: [{seen}]");
        });
    }

    /// <summary>
    /// Ten members of one space connecting at the same instant: the eleventh, already watching, is
    /// told about each of them exactly once, and the snapshot it loads afterwards lists all ten
    /// Online.
    /// </summary>
    /// <remarks>
    /// <para>This is the reconnect storm — a deploy, a network coming back — and it is where a
    /// missing event and a duplicated one look the same from the client's side: the roster is wrong
    /// and nothing else will correct it, because the next status broadcast for those users only
    /// happens when they change something. "Exactly once" is therefore the assertion rather than "at
    /// least once": a duplicate is proof the hysteresis record was read by two activations before
    /// either wrote it, and the client would double-count anything keyed on the event.</para>
    ///
    /// <para>All ten join the space before the observer connects. <c>SpaceGrain.UserJoined</c> no
    /// longer announces Online unconditionally — it broadcasts the joiner's real aggregate, and
    /// nothing at all when that is Offline (defect S4) — but a member who is already connected still
    /// produces one on joining, and counting those would measure the join path instead of the
    /// connections.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Ten_members_connecting_at_the_same_instant_are_each_announced_online_exactly_once(
        CancellationToken ct = default)
    {
        const int crowd = 10;

        var observer = await CreateSessionAsync(ct);
        var spaceId  = await CreateSpaceAsync(observer, "Race Thundering Herd", ct);

        var members = new List<TestUserSession>();
        for (var i = 0; i < crowd; i++)
        {
            var member = await CreateSessionAsync(ct);
            await JoinAsync(observer, member, spaceId, ct);
            members.Add(member);
        }

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var mark    = watcher.Mark();
        var clients = new List<RealtimeClient>();

        try
        {
            clients.AddRange(await Task.WhenAll(members.Select(m => RealtimeClient.ConnectAsync(m, ct))));

            var announced = await Poll.UntilAsync(
                () => Task.FromResult(members.All(m => OnlineCountFor(watcher, m.UserId, mark) >= 1)),
                TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200), ct);

            // A duplicate arrives after the first, never before it, so the only way to assert its
            // absence is to spend a window waiting for one. Two seconds is many times the observed
            // fan-out latency of this harness.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var missing    = members.Where(m => OnlineCountFor(watcher, m.UserId, mark) == 0).ToList();
            var duplicated = members.Where(m => OnlineCountFor(watcher, m.UserId, mark) > 1).ToList();

            var presence = await Poll.ForValueAsync(
                async () => (await observer.Servers.GetMemberPresence(spaceId, ct)).Values
                   .Where(m => members.Any(x => x.UserId == m.userId))
                   .ToList(),
                rows => rows.Count == crowd && rows.All(r => r.status == UserStatus.Online),
                TimeSpan.FromSeconds(10), ct: ct);

            var offlineInSnapshot = presence.Where(p => p.status != UserStatus.Online).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(announced, Is.True,
                    $"only {members.Count(m => OnlineCountFor(watcher, m.UserId, mark) >= 1)} of {crowd} members " +
                    $"were announced online within 15 s of connecting together");

                Assert.That(missing, Is.Empty,
                    $"{missing.Count} of {crowd} members connected and were never announced online to the space: " +
                    string.Join(", ", missing.Select(m => m.UserId)));

                Assert.That(duplicated, Is.Empty,
                    $"{duplicated.Count} of {crowd} members were announced online more than once: " +
                    string.Join(", ", duplicated.Select(m => $"{m.UserId} x{OnlineCountFor(watcher, m.UserId, mark)}")));

                Assert.That(presence, Has.Count.EqualTo(crowd),
                    "the space snapshot does not list every member that just connected");

                Assert.That(offlineInSnapshot, Is.Empty,
                    "members the space was told are online read as something else in the snapshot: " +
                    string.Join(", ", offlineInSnapshot.Select(p => $"{p.userId}={p.status}")));

                Assert.That(watcher.DecodeFailures, Is.Empty);
            });
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    /// <summary>
    /// The same crowd leaving together: ten deliberate <c>GoOffline</c> calls at one instant produce
    /// exactly one Offline per member, and the snapshot agrees.
    /// </summary>
    /// <remarks>
    /// A deliberate offline skips the disconnect grace and runs the whole finalize path — remove the
    /// status, remove the session, re-fold, re-broadcast — so ten of them at once put ten
    /// <c>UserGrain</c> activations into the aggregate-and-broadcast sequence simultaneously. A user
    /// who is announced twice or not at all here leaves a stuck row in everyone else's roster; a
    /// snapshot that still says Online after the event said Offline is the same bug seen from the
    /// other side.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Ten_members_going_offline_at_the_same_instant_are_each_announced_offline_exactly_once(
        CancellationToken ct = default)
    {
        const int crowd = 10;

        var observer = await CreateSessionAsync(ct);
        var spaceId  = await CreateSpaceAsync(observer, "Race Mass Exit", ct);

        var members = new List<TestUserSession>();
        for (var i = 0; i < crowd; i++)
        {
            var member = await CreateSessionAsync(ct);
            await JoinAsync(observer, member, spaceId, ct);
            members.Add(member);
        }

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var clients = new List<RealtimeClient>();

        try
        {
            foreach (var member in members)
                clients.Add(await RealtimeClient.ConnectAsync(member, ct));

            var allOnline = await Poll.UntilAsync(
                async () =>
                {
                    var rows = (await observer.Servers.GetMemberPresence(spaceId, ct)).Values;
                    return members.All(m => rows.FirstOrDefault(r => r.userId == m.UserId)?.status == UserStatus.Online);
                },
                TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(200), ct);

            Assert.That(allOnline, Is.True, "the crowd was not fully online before it was asked to leave");

            var mark = watcher.Mark();

            await Task.WhenAll(clients.Select(c => c.GoOffline(ct)));

            var announced = await Poll.UntilAsync(
                () => Task.FromResult(members.All(m => OfflineCountFor(watcher, m.UserId, mark) >= 1)),
                TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200), ct);

            // Same reasoning as the arrival test: a duplicate can only be proven absent by waiting.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var missing    = members.Where(m => OfflineCountFor(watcher, m.UserId, mark) == 0).ToList();
            var duplicated = members.Where(m => OfflineCountFor(watcher, m.UserId, mark) > 1).ToList();

            var stillOnline = await Poll.ForValueAsync(
                async () => (await observer.Servers.GetMemberPresence(spaceId, ct)).Values
                   .Where(r => members.Any(m => m.UserId == r.userId) && r.status != UserStatus.Offline)
                   .ToList(),
                rows => rows.Count == 0, TimeSpan.FromSeconds(10), ct: ct);

            Assert.Multiple(() =>
            {
                Assert.That(announced, Is.True,
                    $"only {members.Count(m => OfflineCountFor(watcher, m.UserId, mark) >= 1)} of {crowd} members " +
                    "were announced offline within 15 s of leaving together");

                Assert.That(missing, Is.Empty,
                    $"{missing.Count} of {crowd} members said goodbye and the space was never told: " +
                    string.Join(", ", missing.Select(m => m.UserId)));

                Assert.That(duplicated, Is.Empty,
                    $"{duplicated.Count} of {crowd} members were announced offline more than once: " +
                    string.Join(", ", duplicated.Select(m => $"{m.UserId} x{OfflineCountFor(watcher, m.UserId, mark)}")));

                Assert.That(stillOnline, Is.Empty,
                    "the snapshot still shows members the space was told had gone offline: " +
                    string.Join(", ", stillOnline.Select(p => $"{p.userId}={p.status}")));
            });
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Two windows of one session opening at the same moment announce the user online once, not
    /// twice.
    /// </summary>
    /// <remarks>
    /// Two connections of one sid are two windows of one desktop client, and they land on the same
    /// session grain, whose turn-based scheduling ought to make the second attach a no-op. The
    /// broadcast is decided one hop further out though — in <c>UserGrain</c>, a
    /// <c>[StatelessWorker]</c> — through <c>MarkBroadcastIfChangedAsync</c>, which is a read of
    /// <c>status:user:{u}:lastbroadcast</c> followed by a write with nothing in between. This is the
    /// cheapest way to ask whether that gap is reachable from the transport.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Two_windows_of_one_session_connecting_at_once_announce_the_user_online_once(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var actor    = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Race Two Windows", ct);
        await JoinAsync(observer, actor, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var mark    = watcher.Mark();
        var clients = new List<RealtimeClient>();

        try
        {
            clients.AddRange(await Task.WhenAll(
                RealtimeClient.ConnectAsync(actor, ct),
                RealtimeClient.ConnectAsync(actor, ct)));

            await watcher.WaitForAsync<UserChangedStatus>(
                e => e.userId == actor.UserId && e.status == UserStatus.Online,
                TimeSpan.FromSeconds(15), mark, ct);

            // The duplicate, if there is one, is milliseconds behind the first; five seconds is the
            // window in which its absence is worth asserting.
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            Assert.That(OnlineCountFor(watcher, actor.UserId, mark), Is.EqualTo(1),
                "two windows of one session opening together announced the user online more than once — every " +
                "observer applied the same transition twice:\n" + watcher.Dump(mark));
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Two devices of one account coming online at the same moment announce the account online once.
    /// The account transitioned from Offline to Online exactly once, so the space is entitled to
    /// exactly one event.
    /// </summary>
    /// <remarks>
    /// <para>Unlike two windows of one session, this genuinely puts two <c>UserGrain</c> activations
    /// into <c>AggregateAndBroadcastStatusAsync</c> for the same user at once, which is where the
    /// hysteresis record has to collapse the pair into a single broadcast.</para>
    ///
    /// <para>A duplicate here is not cosmetic. The same fan-out writes the replay stream every
    /// reconnecting client reads, and it is one <c>SetUserStatus</c> per space per activation, so the
    /// cost is multiplied by the number of spaces a popular account belongs to.</para>
    ///
    /// <para>The contract it guards (defect S19, fixed): <c>UserPresenceService.MarkBroadcastIfChangedAsync</c>
    /// (<c>src/Argon.Core/Features/Logic/IUserPresenceService.cs</c>) records the status and answers
    /// "was that a change?" in one atomic <c>SET … EX … GET</c> instead of a read, a comparison and a
    /// write. Its only caller, <c>UserGrain.AggregateAndBroadcastStatusAsync</c>, runs on a
    /// <c>[StatelessWorker]</c> grain, so two activations for one user do execute it at the same
    /// instant when two of that user's sessions start together — and only the one whose write landed
    /// first is told anything changed. This test used to see two identical
    /// <c>UserChangedStatus(Online)</c> events four milliseconds apart in one space.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Two_devices_of_one_account_connecting_at_once_announce_the_user_online_once(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var actor    = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Race Two Devices", ct);
        await JoinAsync(observer, actor, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var sidA = Guid.CreateVersion7();
        var sidB = Guid.CreateVersion7();

        var sessionA = OwnedSession(actor.UserId, sidA);
        var sessionB = OwnedSession(actor.UserId, sidB);

        var mark = watcher.Mark();

        await Task.WhenAll(
            Task.Run(async () => await sessionA.AttachConnectionAsync("conn-desk"), ct),
            Task.Run(async () => await sessionB.AttachConnectionAsync("conn-phone"), ct));

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == actor.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), mark, ct);

        // Fixed by design: the assertion is that a second Online does NOT arrive.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        Assert.That(OnlineCountFor(watcher, actor.UserId, mark), Is.EqualTo(1),
            "one account going from offline to online on two devices at once announced the transition more than " +
            "once; the hysteresis record that exists to collapse this was read by both broadcasters before either " +
            "wrote it:\n" + watcher.Dump(mark));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The desk client's status in a given toggling round: Online, Away, Online, …</summary>
    private static UserStatus DeskRound(int round)
        => round % 2 == 0 ? UserStatus.Online : UserStatus.Away;

    /// <summary>The phone's status in a given toggling round: DoNotDisturb, Online, DoNotDisturb, …</summary>
    private static UserStatus PhoneRound(int round)
        => round % 2 == 0 ? UserStatus.DoNotDisturb : UserStatus.Online;

    /// <summary>The priority the product folds a user's sessions with: DND beats Online beats Away.</summary>
    private static UserStatus Fold(params UserStatus[] statuses)
    {
        if (statuses.Contains(UserStatus.DoNotDisturb))
            return UserStatus.DoNotDisturb;
        if (statuses.Contains(UserStatus.Online))
            return UserStatus.Online;
        if (statuses.Contains(UserStatus.Away))
            return UserStatus.Away;
        return UserStatus.Offline;
    }

    /// <summary>
    /// The status an observer currently believes a user has: the last <see cref="UserChangedStatus"/>
    /// it received about them, over the whole recorded log rather than a window.
    /// </summary>
    /// <remarks>
    /// The window starts before the scenario begins rather than after it, because presence
    /// broadcasts are suppressed when the aggregate has not changed: a correct device switch can
    /// produce no event of its own, and a window opened after the switch would answer "nothing" for
    /// the healthy case, making it indistinguishable from a silent failure.
    /// </remarks>
    private static UserStatus? LastStatusFor(RealtimeClient observer, Guid userId, int from = 0)
        => observer.EventsOfType<UserChangedStatus>(from)
           .LastOrDefault(e => e.userId == userId)?.status;

    private static int OnlineCountFor(RealtimeClient observer, Guid userId, int from)
        => observer.EventsOfType<UserChangedStatus>(from)
           .Count(e => e.userId == userId && e.status == UserStatus.Online);

    private static int OfflineCountFor(RealtimeClient observer, Guid userId, int from)
        => observer.EventsOfType<UserChangedStatus>(from)
           .Count(e => e.userId == userId && e.status == UserStatus.Offline);

    private static async Task<UserStatus?> SnapshotStatusAsync(
        TestUserSession reader, Guid spaceId, Guid userId, CancellationToken ct)
        => (await reader.Servers.GetMemberPresence(spaceId, ct)).Values
           .FirstOrDefault(m => m.userId == userId)?.status;

    /// <summary>A session grain this fixture drives, registered so teardown can end it.</summary>
    private Argon.Grains.Interfaces.IUserSessionGrain OwnedSession(Guid userId, Guid sid)
    {
        startedSessions.Add((userId, sid));
        return probe.SessionGrain(userId, sid);
    }

    /// <summary>Ends sessions without letting a cleanup failure be reported as a test failure.</summary>
    private async Task QuietlyReleaseAsync(Guid userId, params Guid[] sids)
    {
        foreach (var sid in sids)
        {
            try
            {
                await probe.SessionGrain(userId, sid).GoOfflineAsync();
            }
            catch
            {
                // A session that already finalized itself throws nothing worth reporting.
            }
        }
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Presence race", string.Empty), ct);

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
