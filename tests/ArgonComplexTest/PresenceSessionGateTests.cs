namespace ArgonComplexTest.Tests;

using System.Collections.Concurrent;
using Argon.Features.Auth;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;

/// <summary>
/// The session grain's gates and its less travelled doors: the sids it refuses to start, an
/// activation that ends underneath a live client, the correction a re-attach owes the room, and the
/// typing path of the Ion <c>Dispatch</c> RPC.
/// </summary>
/// <remarks>
/// <para>The state machine itself — attach, detach, grace, heartbeats, the stale sweep — is
/// <c>PresenceSessionGrainTests</c>'. What is here is what that fixture never reaches: a grain key
/// whose sid will not parse, a revocation still written in the pre-set key shape, an activation
/// deactivated by the silo rather than finalized by the session, and a re-attach after a lapse that
/// moved the user's aggregate.</para>
///
/// <para>Waits are ratios of the host's bound presence clocks (<see cref="PresenceWaits"/>), like every
/// other presence fixture, and sessions are addressed by a synthetic sid and finalized in
/// <see cref="ReleaseSessionsAsync"/> so a red test leaves nothing ticking.</para>
/// </remarks>
[TestFixture]
public class PresenceSessionGateTests : TestBase
{
    private PresenceProbe probe = null!;

    private readonly ConcurrentBag<(Guid UserId, string Sid)> started = [];

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    [TearDown]
    public async Task ReleaseSessionsAsync()
    {
        foreach (var (userId, sid) in started)
        {
            try
            {
                await probe.SessionGrain(userId, sid).GoOfflineAsync();
            }
            catch
            {
                // Best effort, as in PresenceSessionGrainTests: cleanup must not become the failure.
            }
        }

        started.Clear();
    }

    private IUserSessionGrain OwnedSession(Guid userId, string sid)
    {
        started.Add((userId, sid));
        return probe.SessionGrain(userId, sid);
    }

    private static string NewSid() => Guid.CreateVersion7().ToString();

    // ── gates ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A session whose sid is not a guid is refused outright, by attach and by heartbeat alike, and so
    /// is a key with no user in it.
    /// </summary>
    /// <remarks>
    /// Every revocation lookup is keyed on the sid as a guid, so a sid that will not parse is one no
    /// sign-out could ever name. The gate reads that as "revoked" rather than "not revoked": an
    /// identity nothing can end must not be allowed to hold presence.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_session_nobody_could_sign_out_is_never_started(CancellationToken ct = default)
    {
        var user     = await CreateSessionAsync(ct);
        var sid      = $"not-a-guid-{Guid.NewGuid():N}";
        var grain    = OwnedSession(user.UserId, sid);
        var bareKey  = GetGrainFactory().GetGrain<IUserSessionGrain>($"bare-{Guid.NewGuid():N}");

        var attached   = await grain.AttachConnectionAsync("transport-1");
        var heartbeat  = await grain.HeartBeatAsync("transport-1", UserStatus.Online);
        var bare       = await bareKey.AttachConnectionAsync("transport-1");

        var alive = await probe.IsSessionAliveAsync(user.UserId, sid, ct);
        var index = await probe.SessionIndexAsync(user.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(attached, Is.False, "a sid no revocation can name was attached");
            Assert.That(heartbeat, Is.False, "a heartbeat started the session the attach refused");
            Assert.That(bare, Is.False, "a key with no user in it started a session");
            Assert.That(alive, Is.False, "the refused session holds a presence key");
            Assert.That(index, Does.Not.Contain(sid), "the refused session is on the devices screen");
        });
    }

    /// <summary>
    /// A sign-out written in the pre-set key shape still keeps its session from starting.
    /// </summary>
    /// <remarks>
    /// Revocations used to be one key per session (<see cref="SessionRevocation.LegacyRevokedKey"/>)
    /// before they were a set, and a revocation written then must not be forgotten by the deploy that
    /// stopped writing them. A fresh sid of the same user beside it is the control: the refusal is
    /// about the tombstone, not the account.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_revocation_in_the_legacy_key_shape_still_refuses_its_session(CancellationToken ct = default)
    {
        var user    = await CreateSessionAsync(ct);
        var revoked = Guid.CreateVersion7();
        var control = NewSid();

        await probe.Cache.StringSetAsync(SessionRevocation.LegacyRevokedKey(user.UserId, revoked), "1", TimeSpan.FromHours(1), ct);

        var refused  = await OwnedSession(user.UserId, revoked.ToString()).AttachConnectionAsync("transport-1");
        var accepted = await OwnedSession(user.UserId, control).AttachConnectionAsync("transport-1");

        var revokedAlive = await probe.IsSessionAliveAsync(user.UserId, revoked, ct);
        var controlAlive = await probe.IsSessionAliveAsync(user.UserId, control, ct);

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.False, "a session signed out under the old key shape came back");
            Assert.That(revokedAlive, Is.False);
            Assert.That(accepted, Is.True, "control: an unrevoked session of the same user is refused too");
            Assert.That(controlAlive, Is.True);
        });
    }

    // ── an activation that ends under a live client ─────────────────────────────────────────────

    /// <summary>
    /// A session activation deactivated underneath a live client leaves the user's presence alone, and
    /// the client's next heartbeat brings the session back renewing its keys.
    /// </summary>
    /// <remarks>
    /// <para>The silo ends activations for its own reasons — a drain, memory pressure, a shutdown — and
    /// none of those is the session ending. The deactivation settles only the activation's own
    /// accounting; the presence keys belong to the grace and the TTL, so a user whose activation was
    /// collected must still read as online, and must not be announced offline for it.</para>
    ///
    /// <para>What brings it back is the client carrying on as before: its next heartbeat lands on a
    /// fresh activation, which starts the session again and re-arms the tick. The TTL two ticks later
    /// is the proof the tick is running, not merely that a key was written once.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_session_deactivated_under_a_live_client_keeps_presence_and_restarts_on_its_next_heartbeat(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var sid   = NewSid();
        var grain = OwnedSession(user.UserId, sid);

        await grain.AttachConnectionAsync("transport-1");
        await grain.HeartBeatAsync("transport-1", UserStatus.DoNotDisturb);

        Assert.That(await probe.WaitForAggregatedStatusAsync(user.UserId, UserStatus.DoNotDisturb, PresenceWaits.Settle, ct),
            Is.EqualTo(UserStatus.DoNotDisturb), "setup: the session never came up");

        Assert.That(await UserStateGrainSupport.DeactivateAndWaitAsync(grain, PresenceWaits.Settle, ct), Is.True,
            "the session activation never deactivated");

        var aliveAfterDeactivation  = await probe.IsSessionAliveAsync(user.UserId, sid, ct);
        var statusAfterDeactivation = await probe.AggregatedStatusAsync(user.UserId, ct);

        var resumed = await grain.HeartBeatAsync("transport-1", UserStatus.DoNotDisturb);

        await Task.Delay(PresenceWaits.TwoTicks, ct);

        var ttl = await probe.TtlOf(PresenceProbe.PresenceSessionKey(user.UserId, sid));

        Assert.Multiple(() =>
        {
            Assert.That(aliveAfterDeactivation, Is.True, "the deactivation removed the session's presence");
            Assert.That(statusAfterDeactivation, Is.EqualTo(UserStatus.DoNotDisturb), "the deactivation moved the user's status");
            Assert.That(resumed, Is.True, "the client's heartbeat was refused by the fresh activation");
            Assert.That(ttl, Is.Not.Null.And.GreaterThanOrEqualTo(PresenceWaits.FreshTtlFloor),
                $"two ticks after the heartbeat the presence key is at {ttl}: the fresh activation is not renewing it");
        });

        await grain.GoOfflineAsync();

        Assert.That(await probe.WaitUntilOfflineAsync(user.UserId, PresenceWaits.Immediately, ct), Is.True,
            "the restarted session could not be signed out");
    }

    /// <summary>
    /// A grace tick that lands after the client came back does not end the session, and a reminder the
    /// session does not own is ignored.
    /// </summary>
    /// <remarks>
    /// <para>The grace is a durable reminder, and reminders are delivered at least once: a tick already
    /// on its way when the client re-attached still arrives, after the attach has unregistered it. The
    /// session must read the connection that is now there and stand the grace down, not finalize a
    /// session somebody is using. The tick is delivered by hand because that is the only way to make
    /// "in flight across the attach" happen on purpose rather than one run in a thousand.</para>
    ///
    /// <para>A reminder under another name — one an earlier build registered, say — reaches the same
    /// callback and must do nothing at all.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_grace_tick_that_lands_after_the_client_came_back_does_not_end_the_session(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var sid   = NewSid();
        var grain = OwnedSession(user.UserId, sid);

        await grain.AttachConnectionAsync("transport-1");
        await grain.HeartBeatAsync("transport-1", UserStatus.Online);

        // The drop arms the grace; the return stands it down.
        await grain.DetachConnectionAsync("transport-1");
        await grain.AttachConnectionAsync("transport-2");

        var callback = GetGrainFactory().GetGrain<IRemindable>(grain.GetGrainId());
        var now      = DateTime.UtcNow;

        await callback.ReceiveReminder("presence-grace", new TickStatus(now, PresenceWaits.Timings.GracePeriod, now));
        await callback.ReceiveReminder("a-reminder-from-another-build", new TickStatus(now, PresenceWaits.Timings.GracePeriod, now));

        var alive     = await probe.IsSessionAliveAsync(user.UserId, sid, ct);
        var aggregate = await probe.AggregatedStatusAsync(user.UserId, ct);
        var started   = await grain.TouchAsync(UserStatus.Online);

        Assert.Multiple(() =>
        {
            Assert.That(alive, Is.True, "a grace tick finalized a session with a connection attached");
            Assert.That(aggregate, Is.EqualTo(UserStatus.Online));
            Assert.That(started, Is.True, "the session no longer counts itself started");
        });
    }

    // ── the correction a re-attach owes ─────────────────────────────────────────────────────────

    /// <summary>
    /// A re-attach after the user's status lapsed re-announces it to the room; a re-attach with
    /// nothing lapsed announces nothing.
    /// </summary>
    /// <remarks>
    /// <para>A lapse is silent — the keys go and nobody is told — so an observer who read the roster
    /// during it holds Offline. The re-attach writes the status back, and the room has to hear about it,
    /// past the hysteresis that would otherwise drop the broadcast as a repeat of the last one it
    /// recorded (<c>UserPresenceGrain.ReassertSessionStatusAsync</c>).</para>
    ///
    /// <para>The lapse is produced by deleting the two keys rather than by waiting out their TTL: the
    /// lapse is the event under test, and it is the same event whichever of the two caused it. The
    /// second window attaching first is the control that makes the correction specific to a lapse.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_reattach_after_the_status_lapsed_reannounces_it_to_the_room(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, ct);
        await JoinAsync(owner, member, spaceId, ct);

        await using var room = await IonRealtimeClient.ConnectAsync(owner, ct);
        await room.BarrierAsync(ct);

        var sid   = NewSid();
        var grain = OwnedSession(member.UserId, sid);

        await grain.AttachConnectionAsync("window-1");
        await grain.HeartBeatAsync("window-1", UserStatus.DoNotDisturb);

        await room.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == member.UserId && e.status == UserStatus.DoNotDisturb, PresenceWaits.Settle, ct: ct);

        // Control: a second window, nothing lapsed. Nothing to correct, so nothing is said.
        var quiet = room.Mark();

        await grain.AttachConnectionAsync("window-2");

        var repeated = await room.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == member.UserId, PresenceWaits.NegativeWindow, quiet, ct);

        // The lapse.
        await probe.Redis.KeyDeleteAsync(PresenceProbe.SessionStatusKey(member.UserId, sid));
        await probe.Redis.KeyDeleteAsync(PresenceProbe.AggregatedStatusKey(member.UserId));

        var duringLapse = await probe.AggregatedStatusAsync(member.UserId, ct);
        var corrected   = room.Mark();

        await grain.AttachConnectionAsync("window-3");

        var reannounced = await room.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == member.UserId && e.status == UserStatus.DoNotDisturb, PresenceWaits.Settle, corrected, ct);

        var sessionStatus = await probe.SessionStatusAsync(member.UserId, sid);
        var aggregate     = await probe.AggregatedStatusAsync(member.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(repeated, Is.Null, "a re-attach with nothing lapsed re-announced the status to the room");
            Assert.That(duringLapse, Is.EqualTo(UserStatus.Offline), "premise: the lapse reads as Offline");
            Assert.That(reannounced, Is.Not.Null,
                $"the status came back but the room was never told, so it still holds the lapse. {room.Dump(corrected)}");
            Assert.That(sessionStatus, Is.EqualTo(UserStatus.DoNotDisturb), "the re-attach did not write the status back");
            Assert.That(aggregate, Is.EqualTo(UserStatus.DoNotDisturb));
        });
    }

    // ── the Ion dispatch path ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Typing and stopping, sent over the Ion <c>Dispatch</c> RPC, reach the people watching the channel.
    /// </summary>
    /// <remarks>
    /// The legacy client-event path: <c>EventBusImpl.DispatchTree</c> hands typing to the caller's
    /// session grain, which passes it to the channel. No transport is needed on the typist's side,
    /// which is the point of the RPC.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Typing_sent_over_the_Ion_dispatch_reaches_the_channel(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var typist = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, ct);
        await JoinAsync(owner, typist, spaceId, ct);
        var channelId = await CreateTextChannelAsync(owner, spaceId, ct);

        await using var viewer = await IonRealtimeClient.ConnectAsync(owner, ct);

        await viewer.SubscribeToChannel(channelId);
        await viewer.BarrierAsync(ct);

        var mark = viewer.Mark();

        await typist.Bus.Dispatch(new IAmTypingEvent(channelId), ct);

        var typing = await viewer.WaitForRecordAsync<UserTypingEvent>(
            e => e.userId == typist.UserId, PresenceWaits.Settle, mark, ct);

        await typist.Bus.Dispatch(new IAmStopTypingEvent(channelId), ct);

        var stopped = await viewer.WaitForRecordAsync<UserStopTypingEvent>(
            e => e.userId == typist.UserId, PresenceWaits.Settle, mark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(typing.ChannelId, Is.EqualTo(channelId));
            Assert.That(((UserTypingEvent)typing.Event).spaceId, Is.EqualTo(spaceId));
            Assert.That(stopped.ChannelId, Is.EqualTo(channelId));
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest("Session gates", "PresenceSessionGateTests", string.Empty), ct);

        Assert.That(result, Is.InstanceOf<SuccessCreateSpace>(), $"could not create the space: {(result as FailedCreateSpace)?.error}");

        return ((SuccessCreateSpace)result).space.spaceId;
    }

    private static async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(), $"the guest could not join: {(joined as FailedJoin)?.error}");
    }

    private static async Task<Guid> CreateTextChannelAsync(TestUserSession owner, Guid spaceId, CancellationToken ct)
    {
        const string name = "session-gates";

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Text, "PresenceSessionGateTests", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);

        return channels.Values.Single(c => c.channel.name == name).channel.channelId;
    }
}
