namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime.client;
using System.Net.WebSockets;

/// <summary>
/// The friends half of presence: what a user learns about the people on their friends list when
/// they share no space with any of them.
/// </summary>
/// <remarks>
/// <para>Space presence has a group behind it — a status reaches <c>spaces/{id}</c> and every member
/// of that space is in it — so it either works for everyone or for nobody. The friends list has no
/// group. It is assembled per transition out of a friend-id query and a per-user fan-out
/// (<c>UserGrain.BroadcastStatusToFriendsAsync</c>), and it is seeded once per session start out of a
/// second, differently-shaped path (<c>UserGrain.PushFriendPresenceAsync</c>). Two independent
/// mechanisms have to agree for a friends list to be right, and nothing in the product compares them.
/// This fixture is that comparison.</para>
///
/// <para>Every scenario here deliberately gives the two users <em>no shared space</em>, with one
/// exception that says so in its own name. That is not a corner case: it is the ordinary shape of a
/// friends list, and it is the only shape in which a failure of the friends path is visible at all —
/// with a space in common the space broadcast covers for it and the bug hides.</para>
///
/// <para>What a user reasonably expects is the yardstick throughout: a friend who is online reads
/// online, a friend who changed their status reads the status they chose, a friend who left reads
/// offline, an ex-friend reads nothing, and a client that has just connected — for the first time or
/// after its connection died — knows where its friends stand without having to wait for one of them
/// to move.</para>
/// </remarks>
[TestFixture]
public class PresenceFriendsTests : TestBase
{
    private PresenceProbe probe = null!;

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    /// <summary>
    /// Two friends who share no space: the one already connected is told when the other arrives, and
    /// the one arriving is told about the one already there.
    /// </summary>
    /// <remarks>
    /// The two directions are different code — a live transition fans out from
    /// <c>AggregateAndBroadcastStatusAsync</c>, the seed comes from <c>PushFriendPresenceAsync</c> on
    /// session start — and a friends list is only correct when both work. Asserting them in one test
    /// keeps them from drifting apart: a green half and a red half is exactly the state in which a
    /// user sees a friend as online in one window and offline in another.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Friends_with_no_shared_space_learn_of_each_other_in_both_directions(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);

        // Alice has to actually be online before Bob connects, otherwise the push has nothing to
        // carry and a green run would prove nothing.
        Assert.That(await probe.WaitForAggregatedStatusAsync(alice.UserId, UserStatus.Online, TimeSpan.FromSeconds(10), ct),
            Is.EqualTo(UserStatus.Online), "Alice never reached Online, so there is nothing to push to Bob");

        var beforeBob = aliceClient.Mark();

        await using var bobClient = await RealtimeClient.ConnectAsync(bob, ct);

        var toAlice = await aliceClient.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(10), beforeBob, ct);

        var toBob = await bobClient.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == alice.UserId,
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(toAlice.Stream, Is.EqualTo(RealtimeStream.ForSelf),
                "a friend's status with no space in common can only travel on the personal stream");
            Assert.That(((UserChangedStatus)toAlice.Event).spaceId, Is.EqualTo(Guid.Empty),
                "the friends channel names no space; a space id here would send the client looking for a roster");

            Assert.That(toBob.Stream, Is.EqualTo(RealtimeStream.ForSelf));
            Assert.That(((UserChangedStatus)toBob.Event).status, Is.EqualTo(UserStatus.Online),
                "the status pushed to a connecting client is not the one their friend actually has");

            Assert.That(aliceClient.DecodeFailures, Is.Empty);
            Assert.That(bobClient.DecodeFailures, Is.Empty);
        });
    }

    /// <summary>
    /// A client connecting is pushed each friend's real aggregate — a friend on Do Not Disturb reads
    /// Do Not Disturb and one who is Away reads Away, and neither is announced as plain Online.
    /// </summary>
    /// <remarks>
    /// This is the friends-list counterpart of the Online-flash problem. The seed is the only thing a
    /// freshly opened client knows about its friends until one of them moves, so a default of Online
    /// here is not a brief cosmetic error: a friend who set Do Not Disturb reads as available for as
    /// long as they hold that status, which is precisely the state they were trying to avoid.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_status_pushed_on_connect_is_each_friends_real_aggregate(CancellationToken ct = default)
    {
        var dnd     = await CreateSessionAsync(ct);
        var away    = await CreateSessionAsync(ct);
        var arriver = await CreateSessionAsync(ct);

        await BefriendAsync(arriver, dnd, ct);
        await BefriendAsync(arriver, away, ct);

        await using var dndClient  = await RealtimeClient.ConnectAsync(dnd, ct);
        await using var awayClient = await RealtimeClient.ConnectAsync(away, ct);

        await dndClient.Heartbeat(UserStatus.DoNotDisturb, ct);
        await awayClient.Heartbeat(UserStatus.Away, ct);

        var dndAggregate  = await probe.WaitForAggregatedStatusAsync(dnd.UserId, UserStatus.DoNotDisturb, TimeSpan.FromSeconds(10), ct);
        var awayAggregate = await probe.WaitForAggregatedStatusAsync(away.UserId, UserStatus.Away, TimeSpan.FromSeconds(10), ct);

        Assert.Multiple(() =>
        {
            Assert.That(dndAggregate, Is.EqualTo(UserStatus.DoNotDisturb),
                "the DND friend never reached DND, so the push cannot be judged");
            Assert.That(awayAggregate, Is.EqualTo(UserStatus.Away),
                "the Away friend never reached Away, so the push cannot be judged");
        });

        await using var arriverClient = await RealtimeClient.ConnectAsync(arriver, ct);

        var pushedDnd = await arriverClient.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == dnd.UserId, TimeSpan.FromSeconds(10), ct: ct);
        var pushedAway = await arriverClient.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == away.UserId, TimeSpan.FromSeconds(10), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(((UserChangedStatus)pushedDnd.Event).status, Is.EqualTo(UserStatus.DoNotDisturb),
                "a Do Not Disturb friend was announced to a connecting client as something else");
            Assert.That(pushedDnd.Stream, Is.EqualTo(RealtimeStream.ForSelf));

            Assert.That(((UserChangedStatus)pushedAway.Event).status, Is.EqualTo(UserStatus.Away),
                "an Away friend was announced to a connecting client as something else");
            Assert.That(pushedAway.Stream, Is.EqualTo(RealtimeStream.ForSelf));

            // The push is one event per friend, so the whole log is the evidence: an Online in it is
            // a status neither friend ever had.
            Assert.That(
                arriverClient.EventsOfType<UserChangedStatus>()
                   .Any(e => (e.userId == dnd.UserId || e.userId == away.UserId) && e.status == UserStatus.Online),
                Is.False,
                "a connecting client was told a friend is Online when that friend is DND or Away");
        });
    }

    /// <summary>
    /// Every status a friend moves through arrives on the friends stream, once each and in the order
    /// it happened.
    /// </summary>
    /// <remarks>
    /// A friends list is a fold over this stream, so a lost event leaves a wrong status on screen
    /// until the friend moves again, a duplicated one is harmless, and a reordered pair is the worst
    /// of the three — it leaves the list showing a status the friend has already left, with nothing
    /// to correct it.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Every_status_transition_of_a_friend_arrives_once_and_in_order(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

        // Measure from a quiet boundary, not from the first Online: Bob's session start is not
        // guaranteed to be one broadcast (see the helper), and this test is about the transitions
        // that follow it.
        var beforeTransitions = await MarkAfterConnectStormSettlesAsync(aliceClient, ct);

        // Three changes, well inside the session grain's five-token budget, each awaited so the order
        // on the wire is the order the user made them in.
        await bobClient.Heartbeat(UserStatus.DoNotDisturb, ct);
        await bobClient.Heartbeat(UserStatus.Away, ct);
        await bobClient.Heartbeat(UserStatus.Online, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeTransitions, ct);

        var seen = aliceClient.EventsOfType<UserChangedStatus>(beforeTransitions)
           .Where(e => e.userId == bob.UserId)
           .Select(e => e.status)
           .ToArray();

        Assert.That(seen, Is.EqualTo(new[] { UserStatus.DoNotDisturb, UserStatus.Away, UserStatus.Online }),
            $"the friend's transitions did not arrive once each in order. {aliceClient.Dump(beforeTransitions)}");
    }

    /// <summary>
    /// A friend who signs out is reported offline at once, not after the disconnect grace.
    /// </summary>
    /// <remarks>
    /// <c>GoOffline</c> is what the client sends on quit and on account switch, and it is the one
    /// disconnect the server is allowed to believe. Making a friends list wait out a grace window for
    /// it would show someone as available for a minute after they closed the app.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_friend_who_signs_out_is_reported_offline_at_once(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

        var beforeOffline = aliceClient.Mark();
        await bobClient.GoOffline(ct);

        var offline = await aliceClient.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(5), beforeOffline, ct);

        Assert.That(offline.Stream, Is.EqualTo(RealtimeStream.ForSelf),
            "the offline reached the friend through some other channel than the friends stream");
    }

    /// <summary>
    /// A friend whose connection dies without a close frame is not announced offline inside the
    /// grace window.
    /// </summary>
    /// <remarks>
    /// The grace exists so a lid closing or a tunnel does not flap everybody's friends list. The
    /// friends stream has to honour it for the same reason the space stream does — an offline that
    /// is retracted ten seconds later is worse than no event at all, because the client has already
    /// re-sorted the list and moved the person to the bottom.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_friends_transport_drop_is_not_announced_offline_inside_the_grace(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

        var beforeDrop = aliceClient.Mark();
        await bobClient.AbortAsync(ct: ct);

        Assert.That(bobClient.IsConnected, Is.False, "the aborted connection still reports itself connected");

        // A fixed window on purpose: the assertion is that nothing happens, and an absence has no
        // edge to poll for.
        await aliceClient.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(10),
            "a friend's transport drop must ride out the disconnect grace instead of emptying the friends list",
            beforeDrop, ct);
    }

    /// <summary>
    /// A friend whose connection died does eventually read offline: once the presence key has lapsed
    /// the grace reminder finalizes the session and the friends stream is told.
    /// </summary>
    /// <remarks>
    /// <para>The companion to the test above, and the reason that one is not enough on its own:
    /// "no offline inside the grace" is also satisfied by a system that never sends one at all, which
    /// would leave a dead client on every friends list until the observer restarts.</para>
    ///
    /// <para>Slow by construction. Nothing can pull an Orleans reminder forward and the floor is one
    /// minute, so the presence key is force-expired through the probe — legitimate here because the
    /// session is already detached and nothing is refreshing it — and the next reminder tick does the
    /// rest.</para>
    /// </remarks>
    [Test, Category("Slow"), CancelAfter(1000 * 60 * 4)]
    public async Task A_friends_dead_connection_becomes_offline_once_the_grace_expires(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

        var beforeDrop   = aliceClient.Mark();
        var presenceKey  = PresenceProbe.PresenceSessionKey(bob.UserId, bob.SessionId);

        await bobClient.AbortAsync(ct: ct);

        Assert.That(await probe.ForceExpire(presenceKey, TimeSpan.FromSeconds(2)), Is.True,
            $"no presence key to expire for the session that just dropped ({presenceKey})");

        var offline = await aliceClient.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(150), beforeDrop, ct);

        var aggregate = await probe.AggregatedStatusAsync(bob.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(offline.Stream, Is.EqualTo(RealtimeStream.ForSelf));
            Assert.That(aggregate, Is.EqualTo(UserStatus.Offline),
                "the friends stream said offline but the stored aggregate disagrees");
        });
    }

    /// <summary>
    /// Two people who become friends while both are online see each other immediately, without
    /// either of them having to change anything.
    /// </summary>
    /// <remarks>
    /// Presence is only ever pushed forward, so a friendship made after both sides connected has
    /// missed every event the other ever fired. Without the exchange at acceptance both would sit at
    /// the client's default for an unknown user — offline — until one of them happened to move,
    /// which for two people who just added each other is the first impression the feature makes.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_new_friendship_exchanges_presence_in_both_directions(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        var aliceAggregate = await probe.WaitForAggregatedStatusAsync(alice.UserId, UserStatus.Online, TimeSpan.FromSeconds(10), ct);
        var bobAggregate   = await probe.WaitForAggregatedStatusAsync(bob.UserId, UserStatus.Online, TimeSpan.FromSeconds(10), ct);

        Assert.Multiple(() =>
        {
            Assert.That(aliceAggregate, Is.EqualTo(UserStatus.Online));
            Assert.That(bobAggregate, Is.EqualTo(UserStatus.Online));
        });

        var beforeFriendship = (alice: aliceClient.Mark(), bob: bobClient.Mark());

        await BefriendAsync(alice, bob, ct);

        var toAliceTask = aliceClient.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(10), beforeFriendship.alice, ct);
        var toBobTask = bobClient.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == alice.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(10), beforeFriendship.bob, ct);

        var toAlice = await toAliceTask;
        var toBob   = await toBobTask;

        Assert.Multiple(() =>
        {
            Assert.That(toAlice, Is.Not.Null,
                $"the new friend's status never reached the requester. {aliceClient.Dump(beforeFriendship.alice)}");
            Assert.That(toBob, Is.Not.Null,
                $"the new friend's status never reached the accepter. {bobClient.Dump(beforeFriendship.bob)}");
        });
    }

    /// <summary>
    /// After a friendship is removed the ex-friend's status changes stop arriving.
    /// </summary>
    /// <remarks>
    /// Removing someone is the only control a user has over what that person can see of them, and
    /// presence is the most continuous thing the product publishes. A stream that keeps flowing after
    /// the relationship is gone is a leak, not a stale cache — the client would keep folding the
    /// events into a list the person is no longer on.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_removed_friend_receives_no_further_status_changes(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

        await alice.Friends.RemoveFriend(bob.UserId, ct);

        var afterRemoval = aliceClient.Mark();
        await bobClient.Heartbeat(UserStatus.DoNotDisturb, ct);

        // The change really happened — otherwise the silence below would prove nothing about the
        // friends path.
        Assert.That(await probe.WaitForAggregatedStatusAsync(bob.UserId, UserStatus.DoNotDisturb, TimeSpan.FromSeconds(10), ct),
            Is.EqualTo(UserStatus.DoNotDisturb), "the ex-friend's status never changed, so there was nothing to leak");

        // Fixed window: proving an absence.
        await aliceClient.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId,
            TimeSpan.FromSeconds(10),
            "a removed friend's status is still being delivered to the person who removed them",
            afterRemoval, ct);
    }

    /// <summary>
    /// Blocking someone you share a space with ends the friends stream between you and leaves the
    /// space stream alone.
    /// </summary>
    /// <remarks>
    /// <para>The two channels answer different questions and the block only speaks to one of them.
    /// The friends stream is a relationship, and a block dissolves the relationship — the block
    /// deletes the friendship rows, so nothing should travel that way afterwards. The space stream is
    /// a membership: both people are still members, the roster still lists both of them, and a member
    /// whose presence silently stopped updating for one viewer would make that viewer's roster
    /// disagree with <c>GetMemberPresence</c> and with everybody else's.</para>
    ///
    /// <para>Worth recording explicitly rather than leaving implicit, because it means blocking is
    /// <em>not</em> a way to hide your online status from someone: share a space with them and they
    /// keep seeing it. That is a product decision — there is no privacy rule for status today — and
    /// this test is where it is written down, so a future decision to change it fails here rather
    /// than silently.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Blocking_a_space_mate_ends_the_friends_stream_and_leaves_the_space_stream(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        var spaceId = await CreateSpaceAsync(alice, "Friends And Blocks", ct);
        await JoinAsync(alice, bob, spaceId, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        Assert.That(await probe.WaitForAggregatedStatusAsync(alice.UserId, UserStatus.Online, TimeSpan.FromSeconds(10), ct),
            Is.EqualTo(UserStatus.Online));

        await alice.Friends.BlockUser(bob.UserId, ct);

        var afterBlock = bobClient.Mark();
        await aliceClient.Heartbeat(UserStatus.DoNotDisturb, ct);

        var viaSpace = await bobClient.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == alice.UserId && e.status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(10), afterBlock, ct);

        Assert.That(viaSpace.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace),
            "a co-member's status has to keep reaching the space group; blocking is not leaving the space");
        Assert.That(viaSpace.SpaceId, Is.EqualTo(spaceId));

        // Fixed window: proving an absence. The space copy has already arrived, so anything on the
        // personal stream would be the friends fan-out still running for a dissolved friendship.
        await AssertNoRecordWithinAsync(bobClient,
            r => r.Stream == RealtimeStream.ForSelf && r.Event is UserChangedStatus e && e.userId == alice.UserId,
            TimeSpan.FromSeconds(5),
            "a blocked user is still being fed the blocker's status on the friends stream",
            afterBlock, ct);
    }

    /// <summary>
    /// A friend whose only session reports TouchGrass reads as TouchGrass — on the transition, and to
    /// a device of theirs that connects afterwards.
    /// </summary>
    /// <remarks>
    /// <para>The contract (defect S1, fixed). <c>UserPresenceService.RecalculateAggregatedStatusAsync</c>
    /// in <c>src/Argon.Core/Features/Logic/IUserPresenceService.cs</c> is a total precedence fold that
    /// carries the winning session's status verbatim; it used to recognise DoNotDisturb, Online and
    /// Away only, so TouchGrass, InGame and Listen contributed nothing and a user whose only session
    /// carried one of them aggregated to <see cref="UserStatus.Offline"/>. The friends path is where
    /// that hurt twice over, and both halves are asserted below: the transition that reaches
    /// <c>UserGrain.BroadcastStatusToFriendsAsync</c> has to carry TouchGrass rather than Offline, and
    /// <c>UserGrain.PushFriendPresenceAsync</c> — which skips friends whose aggregate is Offline — has
    /// to tell a newly connected client about them at all.</para>
    ///
    /// <para>It matters more here than anywhere else because TouchGrass is a status the desktop client
    /// <em>persists</em> as a preferred status: a user who sets it once keeps sending it on every
    /// connect, so a fold that dropped it produced a permanent wrong value rather than a transient
    /// one. The invariant behind it: a connected session with a non-Offline status is never Offline,
    /// and the status a friend sees is the one that was set.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_friend_who_is_touching_grass_is_reported_as_touching_grass(CancellationToken ct = default)
    {
        var aliceMachine = Guid.CreateVersion7();
        var alice        = await CreateSessionOnMachineAsync(aliceMachine, ct);
        var bob          = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

        var beforeTouchGrass = aliceClient.Mark();
        await bobClient.Heartbeat(UserStatus.TouchGrass, ct);

        var transition = await aliceClient.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId, TimeSpan.FromSeconds(10), beforeTouchGrass, ct);

        // A second session of Alice's: a session start, and therefore the friend-presence push that a
        // client relies on to seed its list.
        var secondSession = await SecondSessionOfAsync(alice, aliceMachine, ct);
        await using var aliceSecond = await RealtimeClient.ConnectAsync(secondSession, ct);

        var pushed    = await aliceSecond.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId, TimeSpan.FromSeconds(10), ct: ct);
        var aggregate = await probe.AggregatedStatusAsync(bob.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(transition, Is.Not.Null,
                $"a friend switching to TouchGrass produced no event at all. {aliceClient.Dump(beforeTouchGrass)}");
            Assert.That((transition?.Event as UserChangedStatus)?.status, Is.EqualTo(UserStatus.TouchGrass),
                "a connected friend on TouchGrass was announced as something else");

            Assert.That(pushed, Is.Not.Null,
                "a newly connected client was told nothing about a friend who is connected and on TouchGrass. " +
                aliceSecond.Dump());
            Assert.That((pushed?.Event as UserChangedStatus)?.status, Is.EqualTo(UserStatus.TouchGrass),
                "the status pushed on connect is not the friend's real status");

            Assert.That(aggregate, Is.EqualTo(UserStatus.TouchGrass),
                "the stored aggregate of a connected TouchGrass session is not TouchGrass");
        });
    }

    /// <summary>
    /// A friend with two devices reads as the aggregate of both: Do Not Disturb wins while the DND
    /// device is up, and the moment it signs out the remaining Online device is what friends see.
    /// </summary>
    /// <remarks>
    /// <para>The second half is the one that goes wrong quietly. Signing out of one device is not a
    /// status change on the other, so nothing about it looks like a transition — but the user's
    /// aggregate did change, and a friends list that misses it leaves someone marked Do Not Disturb on
    /// a device that is no longer running.</para>
    ///
    /// <para>"Device" here means a second session — see <see cref="SecondSessionOfAsync"/> for why the
    /// two share a machine id. Everything presence does is keyed on the sid, so the distinction does
    /// not reach the code under test.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_two_device_friend_reads_as_the_aggregate_of_both_devices(CancellationToken ct = default)
    {
        var bobMachine = Guid.CreateVersion7();
        var alice      = await CreateSessionAsync(ct);
        var bob        = await CreateSessionOnMachineAsync(bobMachine, ct);

        await BefriendAsync(alice, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var bobPhone    = await RealtimeClient.ConnectAsync(bob, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

        var bobDesktopSession = await SecondSessionOfAsync(bob, bobMachine, ct);
        await using var bobDesktop = await RealtimeClient.ConnectAsync(bobDesktopSession, ct);

        var beforeDnd = aliceClient.Mark();
        await bobDesktop.Heartbeat(UserStatus.DoNotDisturb, ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(10), beforeDnd, ct);

        Assert.That(await probe.AggregatedStatusAsync(bob.UserId, ct), Is.EqualTo(UserStatus.DoNotDisturb),
            "one device on Do Not Disturb has to outrank the other device's Online");

        var beforeSignOut = aliceClient.Mark();
        await bobDesktop.GoOffline(ct);

        await aliceClient.WaitForAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeSignOut, ct);

        Assert.That(await probe.AggregatedStatusAsync(bob.UserId, ct), Is.EqualTo(UserStatus.Online),
            "the Do Not Disturb device is gone; what is left is an Online one");
    }

    /// <summary>
    /// A client whose connection died and came back learns its friends' statuses again on the new
    /// connection.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract this now guards (defect S14, fixed).</b> The friends seed belongs to a
    /// connection, not to a session. <c>UserGrain.PushFriendPresenceAsync</c> used to be reached from
    /// exactly one place — <c>UserSessionGrain.EnsureSessionStartedAsync</c> — which returns
    /// immediately once <c>SessionStarted</c> is set, so a reconnect inside the grace re-attached to
    /// the same grain, took the early return, and the new transport learned nothing about anybody. It
    /// is now called from <c>AttachConnectionAsync</c>, so every hub connect gets it and a cold start
    /// is unchanged. Not from the heartbeat path: this is one friend-id query and one batched Redis
    /// read, the same order <c>AppHub.OnConnectedAsync</c> already pays per connection.</para>
    ///
    /// <para>Why nothing else could cover for it, which is what makes this worth a test of its own:
    /// the friends fan-out only fires on a real transition, so a friend who does not move is never
    /// mentioned again, and <c>Resume</c> replays only what was published during the gap — in this
    /// scenario, nothing. Without the seed on re-attach, the state a client has after a dropped
    /// socket is strictly worse than after a cold start, which is the opposite of what a reconnect is
    /// for.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_client_that_reconnects_inside_the_grace_is_told_its_friends_statuses_again(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using var bobClient = await RealtimeClient.ConnectAsync(bob, ct);

        Assert.That(await probe.WaitForAggregatedStatusAsync(bob.UserId, UserStatus.Online, TimeSpan.FromSeconds(10), ct),
            Is.EqualTo(UserStatus.Online));

        await using (var firstConnection = await RealtimeClient.ConnectAsync(alice, ct))
        {
            // The cold start works — that is the control, and it is what makes the failure below a
            // statement about the reconnect rather than about the push in general.
            await firstConnection.WaitForAsync<UserChangedStatus>(
                e => e.userId == bob.UserId && e.status == UserStatus.Online, TimeSpan.FromSeconds(10), ct: ct);

            await firstConnection.AbortAsync(ct: ct);
        }

        await using var reconnected = await RealtimeClient.ConnectAsync(alice, ct);

        Assert.That(reconnected.SessionId, Is.EqualTo(alice.SessionId),
            "the reconnect did not reuse the session, so this is not the scenario under test");

        var relearned = await reconnected.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status != UserStatus.Offline,
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.That(relearned, Is.Not.Null,
            "a client that reconnected inside the grace window was told nothing about a friend who is online, " +
            $"so its friends list stays at the client's default of offline. {reconnected.Dump()}");
    }

    /// <summary>
    /// A status change reaches every online friend, not just one of them.
    /// </summary>
    /// <remarks>
    /// <para>The fan-out contract, with the two friends deliberately sharing no space so that the
    /// friends path is the only route the event can take. <c>UserGrain.BroadcastStatusToFriendsAsync</c>
    /// collects the sessions of <em>every</em> friend into one list and hands it to
    /// <c>IUserSessionNotifier.NotifySessionsAsync</c>; that method has to deliver to every user the
    /// list names, and this pins it with more than one of them online at once.</para>
    ///
    /// <para>Defect S13, now fixed in <c>UserStreamNotifier.NotifySessionsAsync</c>
    /// (<c>src/Argon.Core/Features/Logic/IUserSessionDiscoveryService.cs</c>), which used to read
    /// <c>sessions[0].UserId</c> and call <c>AppHubServer.ForUser</c> exactly once. Every friend
    /// after the first was silently dropped — which one won was whatever order the friends query
    /// returned — and since <c>ForUser</c> also writes the replay entry only for the user it sends
    /// to, the skipped friends could not recover the event through <c>Resume()</c> either, so a
    /// friends list stayed wrong until that friend next moved and the lottery was redrawn. The shape
    /// of the call is what hid it: every other caller passes one user's own sessions, where the first
    /// element is the only answer there is. Two friends is the smallest case that can tell the
    /// difference, hence Alice and Carol both watching the same transition of Bob's.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_status_change_reaches_every_online_friend(CancellationToken ct = default)
    {
        var bob   = await CreateSessionAsync(ct);
        var alice = await CreateSessionAsync(ct);
        var carol = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);
        await BefriendAsync(carol, bob, ct);

        await using var aliceClient = await RealtimeClient.ConnectAsync(alice, ct);
        await using var carolClient = await RealtimeClient.ConnectAsync(carol, ct);
        await using var bobClient   = await RealtimeClient.ConnectAsync(bob, ct);

        Assert.That(await probe.WaitForAggregatedStatusAsync(bob.UserId, UserStatus.Online, TimeSpan.FromSeconds(10), ct),
            Is.EqualTo(UserStatus.Online));

        var beforeChange = (alice: aliceClient.Mark(), carol: carolClient.Mark());

        await bobClient.Heartbeat(UserStatus.DoNotDisturb, ct);

        Assert.That(await probe.WaitForAggregatedStatusAsync(bob.UserId, UserStatus.DoNotDisturb, TimeSpan.FromSeconds(10), ct),
            Is.EqualTo(UserStatus.DoNotDisturb), "the friend's status never changed, so there was nothing to fan out");

        var toAliceTask = aliceClient.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(10), beforeChange.alice, ct);
        var toCarolTask = carolClient.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == bob.UserId && e.status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(10), beforeChange.carol, ct);

        var toAlice = await toAliceTask;
        var toCarol = await toCarolTask;

        Assert.Multiple(() =>
        {
            Assert.That(toAlice, Is.Not.Null,
                $"the first friend never heard about the status change. {aliceClient.Dump(beforeChange.alice)}");
            Assert.That(toCarol, Is.Not.Null,
                $"the second friend never heard about the status change. {carolClient.Dump(beforeChange.carol)}");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers. Everything below is a convenience over the shared harness, not a substitute for it.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes two users friends and returns once both directions of the friendship exist.
    /// </summary>
    private static async Task BefriendAsync(TestUserSession requester, TestUserSession target, CancellationToken ct)
    {
        var status = await requester.Friends.SendFriendRequest(target.Credentials.username, ct);

        Assert.That(status, Is.EqualTo(SendFriendStatus.SuccessSent).Or.EqualTo(SendFriendStatus.AutoAccepted),
            $"could not send a friend request: {status}");

        if (status == SendFriendStatus.SuccessSent)
            await target.Friends.AcceptFriendRequest(requester.UserId, ct);

        var friendships = await requester.Friends.GetMyFriendships(50, 0, ct);

        Assert.That(friendships.Values.Select(x => x.friendId), Does.Contain(target.UserId),
            "the friendship was not established, so nothing below is about friends presence");
    }

    /// <summary>
    /// A second live session of an account that is already signed in: the same bearer token behind a
    /// fresh <see cref="DefaultHeaderInterceptor"/>, so the server keys a second session grain from
    /// the new sid while the first one keeps running.
    /// </summary>
    /// <remarks>
    /// <para>The suite has no "sign in again" helper — <see cref="TestBase.CreateSessionAsync"/>
    /// always registers a new account — and multi-device presence is meaningless without one. The
    /// session a request belongs to is decided by the headers (<c>HttpContextExtensions.GetSessionId</c>
    /// reads the cookie, then <c>X-Ctt</c>, then <c>Sec-Ref</c>) rather than by anything inside the
    /// token, so a second interceptor is all it takes to be a second session.</para>
    ///
    /// <para>The machine id has to be carried over, and that is not a shortcut: an access token is
    /// device-bound (<c>TokenAuthorization.AuthorizeByToken</c> hands the caller's <c>Sec-Carry</c> to
    /// <c>ClassicJwtFlow.ValidateAccessTokenDevice</c>, which throws
    /// <c>MachineIdNotMatchedException</c> on a mismatch), so a genuinely different machine would need
    /// its own sign-in and there is no password flow in this suite to drive one. Presence keys on the
    /// sid throughout — the session grain is <c>"{userId}:{sid}"</c>, and every Redis key under
    /// <c>presence:</c> and <c>status:</c> names the sid — so two sids on one machine id are two
    /// sessions to every part of the system under test here.</para>
    ///
    /// <para>The Ion client makes unary calls only — <c>GetMe</c>, <c>PickTicket</c>, the friends
    /// service — because <see cref="RealtimeClient"/> builds its own transport off the test server.
    /// A WebSocket factory that throws is therefore the honest one: if something ever does try to
    /// open an Ion stream on this client it should say so rather than silently take another path.</para>
    /// </remarks>
    private async Task<TestUserSession> SecondSessionOfAsync(TestUserSession primary, Guid machineId, CancellationToken ct)
    {
        var interceptor = new DefaultHeaderInterceptor(machineId);
        var client      = IonClient.Create(HttpClient, NoWebSockets);

        client.WithInterceptor(interceptor);
        interceptor.SetToken(primary.Token);

        var device = new TestUserSession(client, FactoryAsp.Services, primary.Credentials, primary.Token, interceptor.SessionId)
        {
            UserId = primary.UserId
        };

        var me = await device.Users.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(me.userId, Is.EqualTo(primary.UserId),
                "the second session signed in as somebody else");
            Assert.That(device.SessionId, Is.Not.EqualTo(primary.SessionId),
                "the second client claims the first one's sid, so it is not a second session at all");
        });

        return device;
    }

    private static Task<WebSocket> NoWebSockets(Uri uri, CancellationToken ct, string[]? protocols)
        => throw new NotSupportedException("a second-device client in this fixture only makes unary calls");

    /// <summary>
    /// Fails if any recorded delivery matching <paramref name="predicate"/> turns up inside
    /// <paramref name="window"/>.
    /// </summary>
    /// <remarks>
    /// The harness's own <c>AssertNoneWithinAsync</c> predicates on the decoded event alone, and two
    /// of the properties here are about which stream carried it. Spends the whole window on purpose —
    /// an absence has no edge to poll for — so callers keep it short.
    /// </remarks>
    private static async Task AssertNoRecordWithinAsync(
        RealtimeClient client, Func<RecordedEvent, bool> predicate, TimeSpan window, string because,
        int from, CancellationToken ct)
    {
        await Task.Delay(window, ct);

        var offender = client.Records(from).FirstOrDefault(predicate);

        if (offender is not null)
            Assert.Fail($"{because} — but one arrived inside the {window.TotalSeconds:F0}s window: {offender}");
    }

    /// <summary>
    /// Returns a mark taken at a moment when <paramref name="client"/> has been silent for a short
    /// while, so a window opened at it starts after the connect fan-out rather than inside it.
    /// </summary>
    /// <remarks>
    /// <para>A session start is not reliably one broadcast. <c>UserGrain</c> is a
    /// <c>[StatelessWorker]</c> and <c>UserPresenceService.MarkBroadcastIfChangedAsync</c> is a
    /// non-atomic read-compare-write, so two activations racing on one connect can both decide the
    /// status changed and fan the same Online out twice, tens of milliseconds apart. That defect has
    /// its own test — <c>PresenceAggregationTests.MarkBroadcastIfChanged_UnderConcurrency_AnnouncesOnce</c>,
    /// which is red and marked as a known bug — and it is not what a test about later transitions is
    /// measuring. Marking on the first Online lets the duplicate land on the wrong side of the mark
    /// whenever the second copy arrives inside the 25 ms poll interval, which under a loaded host
    /// (four fixtures share one Argon host) it intermittently does.</para>
    ///
    /// <para>This waits the connect fan-out out instead of relaxing anything: the sequence asserted
    /// afterwards is still exact and still ordered, and a duplicated or reordered <em>transition</em>
    /// still fails. The quiet period is deliberately far longer than the gap between two racing copies
    /// of one broadcast (tens of milliseconds) and far shorter than the grain's 15 s refresh tick, so
    /// it cannot swallow a real event: nothing else is due on this connection in between.</para>
    /// </remarks>
    private static async Task<int> MarkAfterConnectStormSettlesAsync(
        RealtimeClient client, CancellationToken ct)
    {
        var quiet    = TimeSpan.FromMilliseconds(500);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            var mark = client.Mark();

            await Task.Delay(quiet, ct);

            // Nothing at all arrived while we waited, so `mark` is a boundary with no event straddling it.
            if (client.Mark() == mark || DateTimeOffset.UtcNow >= deadline)
                return client.Mark();
        }
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Friends presence", string.Empty), ct);

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
