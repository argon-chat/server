namespace ArgonComplexTest.Tests;

using Argon.Core.Grains.Interfaces;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>EventBus.Realtime</c>, the Ion stream that replaces the SignalR hub for the web client: the same
/// groups, the same session attach, the same revocation — and the same events, byte for byte and
/// entry id for entry id, as the hub delivers to the clients still on it.
/// </summary>
[TestFixture]
public class IonRealtimeTests : TestBase
{
    private PresenceProbe probe = null!;

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    [Test, CancelAfter(120_000)]
    public async Task A_stream_is_welcomed_carries_heartbeats_and_goes_offline_on_request(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        await using var stream = await IonRealtimeClient.ConnectAsync(session, ct);

        Assert.That(stream.ConnectionId, Is.Not.Null.And.Not.Empty, $"no Welcome: {stream.Dump()}");
        Assert.That(stream.IsConnected, Is.True, stream.Dump());

        await stream.Heartbeat(UserStatus.DoNotDisturb);
        await stream.BarrierAsync(ct);

        var status = await Poll.ForValueAsync(
            () => probe.SessionStatusAsync(session.UserId, session.SessionId),
            s => s == UserStatus.DoNotDisturb, PresenceWaits.Settle, ct: ct);

        Assert.That(status, Is.EqualTo(UserStatus.DoNotDisturb),
            "the heartbeat did not reach the session grain the stream attached to");

        await stream.GoOffline();
        await stream.BarrierAsync(ct);

        var gone = await probe.WaitUntilSessionGoneAsync(session.UserId, session.SessionId.ToString(), PresenceWaits.Converge, ct);

        Assert.Multiple(() =>
        {
            Assert.That(gone, Is.True, "GoOffline on the only connection has to end the session at once");
            Assert.That(stream.IsConnected, Is.True, "GoOffline takes the user offline, not the stream down");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Closing_the_stream_detaches_it_and_the_session_goes_offline_after_the_grace(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var stream = await IonRealtimeClient.ConnectAsync(session, ct);
        await stream.Heartbeat(UserStatus.Online);
        await stream.BarrierAsync(ct);

        Assert.That(await Poll.UntilAsync(() => probe.IsSessionAliveAsync(session.UserId, session.SessionId, ct),
            PresenceWaits.Settle, ct: ct), Is.True, "the stream never brought the session up");

        await stream.DisposeAsync();

        Assert.That(await probe.WaitUntilOfflineAsync(session.UserId, PresenceWaits.OfflineDeadline, ct), Is.True,
            "the session outlived its only connection: the disconnect hook did not detach it");
    }

    /// <summary>
    /// One event, two transports: a window on the hub and a window on the stream of the same session
    /// receive it through the same group, under the same replay cursor.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Space_and_personal_events_carry_the_entry_ids_the_hub_gives_them(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var joiner   = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Ion parity", ct);
        await JoinAsync(owner, joiner, spaceId, ct);

        await using var hub = await RealtimeClient.ConnectAsync(owner, ct);
        await using var ion = await IonRealtimeClient.ConnectAsync(owner, ct);

        var (hubMark, ionMark) = (hub.Mark(), ion.Mark());

        // A member coming online over the stream is broadcast to the space group.
        await using var member = await IonRealtimeClient.ConnectAsync(joiner, ct);

        var onHub = await hub.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online, PresenceWaits.Settle, hubMark, ct);
        var onIon = await ion.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online, PresenceWaits.Settle, ionMark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(onIon.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace));
            Assert.That(onIon.SpaceId, Is.EqualTo(spaceId));
            Assert.That(onIon.EntryId, Is.Not.Null.And.EqualTo(onHub.EntryId),
                "the two transports disagree about the replay cursor of one event");
        });

        (hubMark, ionMark) = (hub.Mark(), ion.Mark());

        Assert.That(await stranger.Friends.SendFriendRequest(owner.Credentials.username, ct),
            Is.Not.EqualTo(SendFriendStatus.TargetNotFound));

        var requestOnHub = await hub.WaitForRecordAsync<FriendRequestReceivedEvent>(
            e => e.requesterId == stranger.UserId, PresenceWaits.Settle, hubMark, ct);
        var requestOnIon = await ion.WaitForRecordAsync<FriendRequestReceivedEvent>(
            e => e.requesterId == stranger.UserId, PresenceWaits.Settle, ionMark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(requestOnIon.Stream, Is.EqualTo(RealtimeStream.ForSelf));
            Assert.That(requestOnIon.EntryId, Is.Not.Null.And.EqualTo(requestOnHub.EntryId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Resume_replays_what_was_missed_before_it_answers(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var joiner = await CreateSessionAsync(ct);
        var first  = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Ion resume", ct);
        await JoinAsync(owner, joiner, spaceId, ct);

        await using var ion = await IonRealtimeClient.ConnectAsync(owner, ct);

        await first.Friends.SendFriendRequest(owner.Credentials.username, ct);
        var older = await ion.WaitForRecordAsync<FriendRequestReceivedEvent>(
            e => e.requesterId == first.UserId, PresenceWaits.Settle, ct: ct);

        await second.Friends.SendFriendRequest(owner.Credentials.username, ct);
        var newer = await ion.WaitForRecordAsync<FriendRequestReceivedEvent>(
            e => e.requesterId == second.UserId, PresenceWaits.Settle, ct: ct);

        await using var member = await IonRealtimeClient.ConnectAsync(joiner, ct);
        var online = await ion.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.Online, PresenceWaits.Settle, ct: ct);

        await member.Heartbeat(UserStatus.DoNotDisturb);
        var dnd = await ion.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == joiner.UserId && e.status == UserStatus.DoNotDisturb, PresenceWaits.Settle, ct: ct);

        var mark = ion.Mark();
        var ack  = await ion.ResumeAsync(older.EntryId, [new RealtimeCursor(spaceId, online.EntryId!)], ct: ct);

        var replayed = ion.Records(mark).Take(ack.EventsBefore - mark).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(((Resumed)ack.Frame).needFullResync, Is.False, ion.Dump(mark));
            Assert.That(replayed.Select(x => x.EntryId), Does.Contain(newer.EntryId).And.Contain(dnd.EntryId),
                "the replay did not come before its Resumed");
            Assert.That(replayed.Select(x => x.EntryId), Does.Not.Contain(older.EntryId).And.Not.Contain(online.EntryId),
                "the replay started at the cursor instead of after it");
            Assert.That(replayed.Single(x => x.EntryId == dnd.EntryId).SpaceId, Is.EqualTo(spaceId));
        });

        var gap = await ion.ResumeAsync("1-0", ct: ct);

        Assert.That(((Resumed)gap.Frame).needFullResync, Is.True,
            "a cursor older than the replay window cannot be continued and has to say so");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_channel_is_joined_only_by_a_member_who_may_view_it(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var joiner   = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Ion channels", ct);
        await JoinAsync(owner, joiner, spaceId, ct);
        var channelId = await CreateTextChannelAsync(owner, spaceId, ct);

        await using var viewer  = await IonRealtimeClient.ConnectAsync(owner, ct);
        await using var outside = await IonRealtimeClient.ConnectAsync(stranger, ct);
        await using var typist  = await IonRealtimeClient.ConnectAsync(joiner, ct);

        await viewer.SubscribeToChannel(channelId);
        await outside.SubscribeToChannel(channelId);
        await viewer.BarrierAsync(ct);
        await outside.BarrierAsync(ct);

        var (viewerMark, outsideMark) = (viewer.Mark(), outside.Mark());

        await typist.Typing(spaceId, channelId);

        var typing = await viewer.WaitForRecordAsync<UserTypingEvent>(
            e => e.userId == joiner.UserId, PresenceWaits.Settle, viewerMark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(typing.Stream, Is.EqualTo(RealtimeStream.BroadcastChannel));
            Assert.That(typing.ChannelId, Is.EqualTo(channelId));
            Assert.That(outside.IsConnected, Is.True, "a refused subscribe must cost the command, not the connection");
        });

        var leaked = await outside.FirstWithinAsync<UserTypingEvent>(_ => true, PresenceWaits.Converge, outsideMark, ct);

        Assert.That(leaked, Is.Null, "a non-member subscribed itself to a channel it cannot view");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_device_signed_out_elsewhere_is_told_and_its_stream_closed(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        await using var stream = await IonRealtimeClient.ConnectAsync(phone, ct);

        Assert.That(stream.ConnectionId, Is.Not.Null, stream.Dump());

        Assert.That(await laptop.Security.RevokeSession(phone.SessionId, ct), Is.InstanceOf<SuccessRevokeSession>());

        var revoked = await stream.WaitForControlAsync<SessionRevoked>(PresenceWaits.Settle, ct: ct);
        var ended   = await stream.WaitForEndAsync(PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(((SessionRevoked)revoked.Frame).reason, Is.EqualTo("session_signed_out"));
            Assert.That(ended, Is.InstanceOf<IonStreamClosedException>(), stream.Dump());
            Assert.That((ended as IonStreamClosedException)?.AllowReconnect, Is.False,
                "a signed-out device must not be invited back");
        });
    }

    /// <summary>
    /// The connect gate reads the tombstones uncached, so it refuses a device the cached gate on the
    /// ticket exchange — warmed by the sign-out call itself a moment earlier — still lets through.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_signed_out_device_is_refused_when_it_connects(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        // Connected first, so the session is on the devices screen to be signed out from.
        await using (var first = await IonRealtimeClient.ConnectAsync(phone, ct))
        {
            Assert.That(await laptop.Security.RevokeSession(phone.SessionId, ct), Is.InstanceOf<SuccessRevokeSession>());
            await first.WaitForEndAsync(PresenceWaits.Settle, ct);
        }

        await using var stream = await IonRealtimeClient.ConnectAsync(phone, ct);
        var ended = await stream.WaitForEndAsync(PresenceWaits.Settle, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stream.ConnectionId, Is.Null, "the refused connection was welcomed");
            Assert.That((ended as IonRequestException)?.Error.code, Is.EqualTo("SESSION_REVOKED"), stream.Dump());
        });

        Assert.That(await probe.IsSessionAliveAsync(phone.UserId, phone.SessionId, ct), Is.False,
            "the refused connection brought the signed-out session back");
    }

    private async Task<TestUserSession> SecondDeviceAsync(TestUserSession account, CancellationToken ct)
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WsFactory);

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
        return session;
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Ion realtime", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private static async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(), $"Guest could not join the space: {(joined as FailedJoin)?.error}");
    }

    private async Task<Guid> CreateTextChannelAsync(TestUserSession owner, Guid spaceId, CancellationToken ct)
    {
        const string name = "ion-realtime";

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Text, "Ion realtime", null), ct).Ok();

        Orleans.Runtime.RequestContext.Set("$caller_user_id", owner.UserId);

        try
        {
            var channels = await GetGrainFactory().GetGrain<ISpaceReadGrain>(spaceId).GetChannels();

            return channels.Single(c => c.channel.name == name).channel.channelId;
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }
    }
}
