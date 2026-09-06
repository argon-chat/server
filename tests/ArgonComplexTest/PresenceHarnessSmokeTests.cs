namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;

/// <summary>
/// Proves the realtime/presence harness itself, end to end against the real server and the real
/// Redis behind it.
/// </summary>
/// <remarks>
/// <para>Every other fixture in this campaign asserts something about presence <em>through</em>
/// <see cref="RealtimeClient"/> and <see cref="PresenceProbe"/>. That makes the harness a shared
/// premise: if the hub connection silently fails to join a space group, or the ticket resolves to
/// the wrong sid, or a decoded event never reaches the recorded list, then every one of those
/// fixtures goes red — or worse, green — for a reason that has nothing to do with the product. This
/// fixture is where that premise is checked, so a failure here is read as "the harness is broken"
/// and a failure anywhere else is read as "the product is".</para>
///
/// <para>The scenarios are deliberately the least controversial presence behaviours in the system:
/// a member of a space coming online is seen by the other members, a heartbeat carrying a new status
/// propagates, an explicit <c>GoOffline</c> is immediate, an ungraceful drop is not, and a
/// connecting client is told which of its friends are already online. Nothing here probes a
/// suspected defect. If one of these goes red it is either the harness or a bug so central that the
/// rest of the campaign is moot, and both are worth knowing before the other fixtures run.</para>
///
/// <para>The two members are made members <em>before</em> anyone connects. Joining a space is itself
/// a status broadcast in this product, and mixing that in would leave the fixture unable to say
/// whether the Online it saw came from the connection or from the join.</para>
/// </remarks>
[TestFixture]
public class PresenceHarnessSmokeTests : TestBase
{
    private PresenceProbe probe = null!;

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    /// <summary>
    /// One observer watches a fellow member connect, change status and leave: Online on connect,
    /// DoNotDisturb on the heartbeat that carries it, Offline the moment the client says so — and
    /// the space snapshot agrees with the stream throughout.
    /// </summary>
    /// <remarks>
    /// This is the whole client-visible presence contract in one pass, and it exercises every part of
    /// the harness that the rest of the campaign leans on: the ticket resolving to the right sid, the
    /// hub putting the connection into the space group, the CBOR decode, the wait-with-deadline, and
    /// the probe reading Redis for the same user the events named.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task An_observer_sees_a_member_come_online_go_dnd_and_go_offline(CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var joiner   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Harness Smoke", ct);
        await JoinAsync(observer, joiner, spaceId, ct);

        // The observer connects first and alone, so everything it records after this point is about
        // the joiner rather than about its own arrival.
        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        Assert.That(watcher.IsConnected, Is.True, "the observer's hub connection did not come up");
        Assert.That(watcher.ConnectionId, Is.Not.Null.And.Not.Empty);

        var beforeJoinerConnects = watcher.Mark();

        await using var member = await RealtimeClient.ConnectAsync(joiner, ct);

        var online = await watcher.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(10), beforeJoinerConnects, ct);

        Assert.Multiple(() =>
        {
            Assert.That(online.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace),
                "a member's status has to reach the space group, not only the user's own stream");
            Assert.That(online.SpaceId, Is.EqualTo(spaceId),
                "the status arrived addressed to a different space than the one both users are in");
        });

        // A heartbeat carrying a new status is how the desktop client changes it; there is no other
        // path from the client to the aggregate.
        var beforeDnd = watcher.Mark();
        await member.Heartbeat(UserStatus.DoNotDisturb, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(10), beforeDnd, ct);

        // The snapshot a client loads a space with must say the same thing the stream did. Polled
        // rather than read once because SpaceReadGrain.GetPresence is cached for a second.
        var snapshot = await Poll.ForValueAsync(
            async () => (await observer.Servers.GetMemberPresence(spaceId, ct))
               .Values.FirstOrDefault(m => m.userId == joiner.UserId)?.status,
            status => status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.That(snapshot, Is.EqualTo(UserStatus.DoNotDisturb),
            "GetMemberPresence disagrees with the status the space was just told over the stream");

        Assert.That(await probe.AggregatedStatusAsync(joiner.UserId, ct), Is.EqualTo(UserStatus.DoNotDisturb),
            "the stored aggregate disagrees with what was broadcast");

        // Deliberate offline: the client said it is leaving, so there is no grace to serve.
        var beforeOffline = watcher.Mark();
        await member.GoOffline(ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(5), beforeOffline, ct);

        var stillOnline = await Poll.ForValueAsync(
            () => probe.IsUserOnlineAsync(joiner.UserId, ct),
            value => !value,
            TimeSpan.FromSeconds(5), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(stillOnline, Is.False,
                "the presence keys survived an explicit GoOffline, so the user reads online while gone");
            Assert.That(watcher.DecodeFailures, Is.Empty,
                "an event on this connection could not be decoded, so the recorded stream is incomplete");
        });
    }

    /// <summary>
    /// A connection that dies without a close frame does not take its user offline: no Offline
    /// reaches the space inside the grace window, and the session's presence key is left to drain
    /// rather than being refreshed or deleted.
    /// </summary>
    /// <remarks>
    /// <para>This is the case the whole disconnect grace exists for — a lid closing, a train tunnel —
    /// and it is the one the harness has to be able to produce, because a graceful stop takes a
    /// different path through <c>UserSessionGrain</c> entirely. Aborting the WebSocket without a
    /// close frame is what makes the difference, so this test is also the proof that
    /// <see cref="RealtimeClient.AbortAsync"/> does what it claims.</para>
    ///
    /// <para>The five-second wait is a fixed one on purpose: the assertion is that something does
    /// <em>not</em> happen, and an absence has no edge to poll for. The TTL is then sampled twice
    /// three seconds apart — after the detach nothing can refresh it, so a strictly falling TTL is
    /// the evidence that the session is draining towards the grace rather than being kept alive.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task An_ungraceful_drop_is_not_broadcast_as_offline_and_leaves_the_presence_key_draining(
        CancellationToken ct = default)
    {
        var observer = await CreateSessionAsync(ct);
        var joiner   = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, "Harness Grace", ct);
        await JoinAsync(observer, joiner, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(observer, ct);

        var beforeJoinerConnects = watcher.Mark();
        await using var member = await RealtimeClient.ConnectAsync(joiner, ct);

        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(10), beforeJoinerConnects, ct);

        var presenceKey = PresenceProbe.PresenceSessionKey(joiner.UserId, joiner.SessionId);

        Assert.That(await probe.Exists(presenceKey), Is.True,
            $"no presence key for the session that just connected ({presenceKey}) — the harness has the wrong sid. " +
            $"index=[{string.Join(", ", await probe.SessionIndexAsync(joiner.UserId))}] " +
            $"active=[{string.Join(", ", await probe.ActiveSessionIdsAsync(joiner.UserId, ct))}] " +
            $"viaCache={await probe.Cache.KeyExistsAsync(presenceKey, ct)} " +
            $"aggregatedKey={await probe.Exists(PresenceProbe.AggregatedStatusKey(joiner.UserId))}");

        var beforeDrop = watcher.Mark();

        await member.AbortAsync(ct: ct);
        Assert.That(member.IsConnected, Is.False, "the aborted connection still reports itself connected");

        await watcher.AssertNoneWithinAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(5),
            "a transport drop must ride out the disconnect grace instead of announcing the user offline",
            beforeDrop, ct);

        var ttlFirst = await probe.TtlOf(presenceKey);

        // Nothing refreshes a detached session's presence key, so three seconds of wall clock is
        // three seconds off the TTL. A TTL that held still would mean the session still believes it
        // has a live connection.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var ttlSecond = await probe.TtlOf(presenceKey);

        Assert.Multiple(() =>
        {
            Assert.That(ttlFirst, Is.Not.Null,
                "the presence key was deleted outright, so the drop was treated as a deliberate offline");
            Assert.That(ttlSecond, Is.Not.Null,
                "the presence key vanished inside the grace window");
            Assert.That(ttlSecond, Is.LessThan(ttlFirst),
                $"the presence TTL is not draining ({ttlFirst} then {ttlSecond}), so something is still refreshing " +
                "a session whose only connection is gone");
        });
    }

    /// <summary>
    /// The personal <c>forSelf</c> stream works: a client that connects while a friend is already
    /// online is told about that friend, on its own stream and with no space attached.
    /// </summary>
    /// <remarks>
    /// The two users deliberately share no space, so the only route this event can take is
    /// <c>UserGrain.PushFriendPresenceAsync</c> → <c>AppHubServer.ForUser</c>. That makes it the one
    /// scenario in this fixture that proves the <c>forSelf</c> handler and its
    /// <c>spaceId == Guid.Empty</c> convention, which the friends-presence fixtures depend on.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_connecting_client_is_pushed_its_online_friends_on_its_own_stream(CancellationToken ct = default)
    {
        var alreadyOnline = await CreateSessionAsync(ct);
        var latecomer     = await CreateSessionAsync(ct);

        await alreadyOnline.Friends.SendFriendRequest(latecomer.Credentials.username, ct);
        await latecomer.Friends.AcceptFriendRequest(alreadyOnline.UserId, ct);

        await using var friend = await RealtimeClient.ConnectAsync(alreadyOnline, ct);

        // The push only carries friends whose aggregate is not Offline, so the friend has to be
        // established as online before the latecomer connects — otherwise a green run would prove
        // nothing about the stream.
        var friendStatus = await probe.WaitForAggregatedStatusAsync(
            alreadyOnline.UserId, UserStatus.Online, TimeSpan.FromSeconds(10), ct);

        Assert.That(friendStatus, Is.EqualTo(UserStatus.Online),
            "the already-connected friend never reached Online, so there is nothing to push");

        await using var arriving = await RealtimeClient.ConnectAsync(latecomer, ct);

        var pushed = await arriving.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == alreadyOnline.UserId && e.spaceId == Guid.Empty && e.status != UserStatus.Offline,
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(pushed.Stream, Is.EqualTo(RealtimeStream.ForSelf),
                "the friend's presence arrived on a broadcast stream rather than the client's own");
            Assert.That(((UserChangedStatus)pushed.Event).status, Is.EqualTo(UserStatus.Online),
                "the pushed status is not the one the friend actually has");
            Assert.That(arriving.DecodeFailures, Is.Empty);
        });
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Presence harness", string.Empty), ct);

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
