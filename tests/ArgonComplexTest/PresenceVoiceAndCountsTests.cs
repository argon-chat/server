namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;

/// <summary>
/// What a voice room and an online counter say about a user whose session has ended.
/// </summary>
/// <remarks>
/// <para>Two things in this product claim to know who is present, and neither of them owns the
/// answer. A voice channel keeps its occupants in <c>ChannelGrain</c> state, written when somebody
/// joins and erased only when somebody leaves — by their own client, by a moderator, or by the
/// LiveKit webhook. The online counters on the space header and on the invite sheet
/// (<c>GetSpaceStats().onlineCount</c>, <c>PreviewInvite(...).onlineCount</c>) count aggregated
/// statuses in Redis. Presence itself lives in a third place: the session grain and the TTL'd keys
/// behind it. When a session ends, all three have to agree, and nothing makes them.</para>
///
/// <para>The failure a user sees is a ghost in a call: a name sitting in a voice room with nobody
/// behind it, for as long as the channel activation lives, which is a day at a time. Everyone in the
/// space sees it, nobody can click it away, and the person it belongs to is not even signed in. The
/// counter failure is quieter and more corrosive: the number on an invite is the one thing a
/// stranger uses to decide whether a space is alive, and a member who is present but miscounted
/// makes that number lie in the direction that costs the space its guests.</para>
///
/// <para>So the fixture drives the ends of a session — a deliberate <c>GoOffline</c>, a revocation
/// from another device, a dead network that rides out the grace — and asks the two claimants what
/// they now believe, at the event level an observer actually sees rather than through Redis. The
/// control cases matter as much as the failures: a user with a second device still signed in has
/// not left the call, and a member who reconnects inside the grace has not gone anywhere either.</para>
///
/// <para>Voice is entered through the ion <c>Interlink</c> RPC, which is what the desktop client
/// calls. It reaches <c>ChannelGrain.Join</c>, which registers the occupant and fires
/// <c>JoinedToChannelUser</c> before it mints an SFU token; token minting is a local JWT signature,
/// so no SFU has to be reachable for any of this. <see cref="JoinVoiceAsync"/> falls back to the
/// grain directly if the RPC ever stops working in-process, and says so in the failure message
/// rather than skipping.</para>
/// </remarks>
[TestFixture]
public class PresenceVoiceAndCountsTests : TestBase
{
    private PresenceProbe probe = null!;

    /// <summary>How long an intended reaction to a session ending is given before it counts as absent.</summary>
    private static readonly TimeSpan ReactionWindow = TimeSpan.FromSeconds(5);

    [OneTimeSetUp]
    public async Task OpenProbeAsync()
        => probe = await PresenceProbe.CreateAsync();

    // ── H23: voice membership vs the session ────────────────────────────────────────────────────

    /// <summary>
    /// Joining a voice channel puts the user in it, visibly: the space is told
    /// <c>JoinedToChannelUser</c> and the channel list every client renders from now names them.
    /// </summary>
    /// <remarks>
    /// The premise of every other voice test here. If this one fails, the ones below are not saying
    /// anything about cleanup — they are saying the join never happened.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Joining_a_voice_channel_announces_the_user_and_lists_them(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var speaker = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, "Voice Join", ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "join-room", ct);
        await JoinSpaceAsync(owner, speaker, spaceId, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        await using var voice   = await RealtimeClient.ConnectAsync(speaker, ct);

        var beforeJoin = watcher.Mark();
        await JoinVoiceAsync(speaker, spaceId, channelId, ct);

        var joined = await watcher.WaitForRecordAsync<JoinedToChannelUser>(
            e => e.channelId == channelId && e.userId == speaker.UserId,
            TimeSpan.FromSeconds(10), beforeJoin, ct);

        var occupants = await Poll.ForValueAsync(
            () => VoiceOccupantsAsync(owner, spaceId, channelId, ct),
            users => users.Contains(speaker.UserId),
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(joined.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace),
                "a voice arrival has to reach the whole space, not only the joiner");
            Assert.That(joined.SpaceId, Is.EqualTo(spaceId));
            Assert.That(occupants, Does.Contain(speaker.UserId),
                $"the channel list does not show the user the space was just told had joined (joined via {voiceJoinPath})");
            Assert.That(watcher.DecodeFailures, Is.Empty);
        });
    }

    /// <summary>
    /// A user whose only session says goodbye leaves the call with it: the space is told
    /// <c>LeavedFromChannelUser</c> and the channel stops listing them.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract this now guards (defect S15, fixed).</b> The last session of a user
    /// ending takes them out of every voice channel they are in, with the same
    /// <c>LeavedFromChannelUser</c> a deliberate hang-up produces.
    /// <c>UserSessionGrain.FinalizeOfflineAsync</c> already computes <c>stillOnline</c> to decide what
    /// aggregate to broadcast; when that is false it now also calls
    /// <c>IUserGrain.LeaveAllVoiceAsync()</c>, which walks the user's spaces, asks each
    /// <c>SpaceGrain.GetUserVoiceSlotAsync</c> — the reverse index <c>ChannelGrain.Join</c> already
    /// maintains, so this is O(1) per space and not a scan — and calls <c>IChannelGrain.Leave</c> for
    /// any hit. Going through <c>Leave</c> rather than editing state is what fires the event, settles
    /// the voice XP and releases the day-long <c>DelayDeactivation</c>.</para>
    ///
    /// <para>Before it, <c>ChannelGrain.Users</c> was emptied only by the client's own
    /// <c>DisconnectFromVoiceChannel</c>, a moderator kick or the LiveKit participant-left webhook, so
    /// a client that quit, crashed or was signed out left an occupant behind — and the channel pinned
    /// its own activation for a day while any occupant remained, so the ghost outlived everything
    /// that could have cleaned it up. The gate on <c>stillOnline</c> is what keeps
    /// <see cref="One_of_two_sessions_going_offline_leaves_the_call_alone"/> true: a second device
    /// signing out must not hang up the call the first one is in.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_last_session_going_offline_takes_the_user_out_of_voice(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var speaker = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, "Voice GoOffline", ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "goodbye-room", ct);
        await JoinSpaceAsync(owner, speaker, spaceId, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        await using var voice   = await RealtimeClient.ConnectAsync(speaker, ct);

        await JoinVoiceAsync(speaker, spaceId, channelId, ct);
        await watcher.WaitForAsync<JoinedToChannelUser>(
            e => e.channelId == channelId && e.userId == speaker.UserId, TimeSpan.FromSeconds(10), ct: ct);

        var beforeOffline = watcher.Mark();
        await voice.GoOffline(ct);

        // The status broadcast is the proof that the session really ended; the voice departure is
        // what this test is about, and it is looked for over the same window.
        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == speaker.UserId && e.status == UserStatus.Offline,
            ReactionWindow, beforeOffline, ct);

        var left = await watcher.FirstWithinAsync<LeavedFromChannelUser>(
            e => e.channelId == channelId && e.userId == speaker.UserId, ReactionWindow, beforeOffline, ct);

        var occupants = await VoiceOccupantsAsync(owner, spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(left, Is.Not.Null,
                "the space was told the user went offline but never that they left the call, so every " +
                $"client keeps them in the room. {watcher.Dump(beforeOffline)}");
            Assert.That(occupants, Does.Not.Contain(speaker.UserId),
                $"the voice channel still lists a user with no live session at all (joined via {voiceJoinPath})");
        });
    }

    /// <summary>
    /// Ending a session from another device takes that device out of the call it was in.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract this now guards (defects S5 and S15, both fixed).</b> This is the
    /// devices screen: somebody sees a session they do not recognise and revokes it, and the case
    /// where a leftover seat matters most, because the whole point of the button is to remove
    /// someone's access. Two failures met here. S5: <c>SecurityGrain.EndSessionAsync</c> wrote the
    /// tombstone with <c>SetAddAsync</c> and then called <c>cache.KeyExpireAsync</c> on the same key
    /// — <c>GETEX</c>, a string command, which answers <c>WRONGTYPE</c> against a set — so the
    /// exception unwound before <c>GoOfflineAsync</c> and the presence removal ever ran and the
    /// caller was told <c>INTERNAL_ERROR</c> while the device stayed signed in, online and in the
    /// call. S15: even once the sign-out ran, <c>FinalizeOfflineAsync</c> never looked at voice.</para>
    ///
    /// <para>Now <c>EndSessionAsync</c> uses the type-agnostic <c>UpdateStringExpirationAsync</c> and
    /// reaches <c>GoOfflineAsync</c>, which lands in the same <c>FinalizeOfflineAsync</c> as the two
    /// tests above — and that calls <c>IUserGrain.LeaveAllVoiceAsync()</c> when no live session
    /// remains. The revoking device here is an ion client only: it signs in for a second sid and
    /// never opens a hub connection, so the account's only presence is the session being revoked and
    /// the expected outcome is unambiguous — no session, no seat.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Revoking_the_session_in_voice_takes_it_out_of_the_call(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var speaker = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, "Voice Revoke", ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "revoked-room", ct);
        await JoinSpaceAsync(owner, speaker, spaceId, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        await using var voice   = await RealtimeClient.ConnectAsync(speaker, ct);

        await JoinVoiceAsync(speaker, spaceId, channelId, ct);
        await watcher.WaitForAsync<JoinedToChannelUser>(
            e => e.channelId == channelId && e.userId == speaker.UserId, TimeSpan.FromSeconds(10), ct: ct);

        var phone = await SignInAgainAsync(speaker, ct);

        // RevokeSession only accepts a sid discovery can still see, so the session in the call has to
        // be listed before the revocation means anything.
        var listed = await Poll.ForValueAsync(
            async () => (await phone.Security.GetSessions(ct)).Values.Any(s => s.sessionId == speaker.SessionId),
            found => found, TimeSpan.FromSeconds(10), ct: ct);

        Assert.That(listed, Is.True,
            "the session that is in the voice channel is not on the devices screen, so there is nothing to revoke");

        var beforeRevoke = watcher.Mark();

        var revoked = await phone.Security.RevokeSession(speaker.SessionId, ct);

        // Whatever the call answered, the intended end state is the same — no session, no seat — so
        // the result is asserted alongside the outcome rather than before it. An early Assert.That
        // here would stop the test at the RPC and hide what actually happened to the presence and to
        // the room.
        var wentOffline = await watcher.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == speaker.UserId && e.status == UserStatus.Offline, ReactionWindow, beforeRevoke, ct);

        var left = await watcher.FirstWithinAsync<LeavedFromChannelUser>(
            e => e.channelId == channelId && e.userId == speaker.UserId, ReactionWindow, beforeRevoke, ct);

        var occupants   = await VoiceOccupantsAsync(owner, spaceId, channelId, ct);
        var stillOnline = await probe.IsUserOnlineAsync(speaker.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
                $"signing a live device of one's own account out failed: {(revoked as FailedRevokeSession)?.error}");
            Assert.That(wentOffline, Is.Not.Null,
                $"the space was never told the revoked session's user went offline. {watcher.Dump(beforeRevoke)}");
            Assert.That(stillOnline, Is.False,
                "the revoked session still has a live presence key, so the account still reads online");
            Assert.That(left, Is.Not.Null,
                "a revoked session was never announced as leaving the voice channel it was sitting in. " +
                watcher.Dump(beforeRevoke));
            Assert.That(occupants, Does.Not.Contain(speaker.UserId),
                "the voice channel still lists a session that was explicitly revoked");
        });
    }

    /// <summary>
    /// A dead network eventually takes the user out of the call too — once the disconnect grace has
    /// given up on them.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract this now guards (defect S15, fixed), down the third and last road into
    /// <c>FinalizeOfflineAsync</c>:</b> the <c>presence-grace</c> reminder in
    /// <c>UserSessionGrain.ReceiveReminder</c> firing after the presence key has lapsed. The voice
    /// teardown hangs off <c>FinalizeOfflineAsync</c> rather than off any one caller precisely so
    /// that all three roads out — sign-out, revocation and this one — behave the same. This is the
    /// common case in production, where laptops close and phones lose signal and nobody presses
    /// sign-out, so it is the road most ghosts used to arrive by.</para>
    ///
    /// <para>Slow by construction. Orleans will not schedule a reminder sooner than a minute, and
    /// nothing in the harness can pull one forward, so the grace costs its full minute of wall clock.
    /// What can be accelerated is the other half of the condition: the presence key is force-expired
    /// once the transport is gone (nothing refreshes it after the detach), so the first reminder tick
    /// finalizes instead of the third. The finalize is observed through the <c>Offline</c> the space
    /// receives, which is the event that says the session is over.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 4), Category("Slow")]
    public async Task An_ungraceful_drop_takes_the_user_out_of_voice_once_the_grace_finalizes(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var speaker = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, "Voice Grace", ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "dropped-room", ct);
        await JoinSpaceAsync(owner, speaker, spaceId, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        await using var voice   = await RealtimeClient.ConnectAsync(speaker, ct);

        await JoinVoiceAsync(speaker, spaceId, channelId, ct);
        await watcher.WaitForAsync<JoinedToChannelUser>(
            e => e.channelId == channelId && e.userId == speaker.UserId, TimeSpan.FromSeconds(10), ct: ct);

        var beforeDrop = watcher.Mark();
        await voice.AbortAsync(ct: ct);

        // Fixed, and only as a precondition: there is no hook that observes the server finishing
        // OnDisconnectedAsync (see harnessGaps), and force-expiring the presence key before the
        // detach lands would race the 15 s refresh tick that is still armed until it does.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var presenceKey = PresenceProbe.PresenceSessionKey(speaker.UserId, speaker.SessionId);
        await probe.ForceExpire(presenceKey, TimeSpan.FromSeconds(1));

        var lapsed = await Poll.UntilAsync(async () => !await probe.Exists(presenceKey),
            TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(lapsed, Is.True,
            "the presence key of a session with no transport is still being refreshed, so the grace can never finalize");

        // The grace reminder cannot fire before its first minute; the Offline broadcast is the
        // finalize itself, so this waits on the event rather than on the clock.
        await watcher.WaitForAsync<UserChangedStatus>(
            e => e.userId == speaker.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(150), beforeDrop, ct);

        var afterFinalize = watcher.Mark();

        var left = await watcher.FirstWithinAsync<LeavedFromChannelUser>(
            e => e.channelId == channelId && e.userId == speaker.UserId, ReactionWindow, beforeDrop, ct);

        var occupants = await VoiceOccupantsAsync(owner, spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(left, Is.Not.Null,
                "the disconnect grace gave up on the session and the space was told the user is offline, " +
                $"but the voice channel was never told anything. {watcher.Dump(beforeDrop)}");
            Assert.That(occupants, Does.Not.Contain(speaker.UserId),
                "a user the server itself considers offline is still an occupant of the voice channel");
            Assert.That(afterFinalize, Is.GreaterThan(beforeDrop),
                "no events at all reached the observer after the drop");
        });
    }

    /// <summary>
    /// Signing out on the phone does not hang up on the desktop: one session of two going offline
    /// leaves the user in the call.
    /// </summary>
    /// <remarks>
    /// The control for the three tests above, and the reason none of them can be fixed by hanging up
    /// on every disconnect. Voice occupancy is per user, not per session, so it may only end when the
    /// user has no live session left — a second device signing out has to be invisible to the room.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task One_of_two_sessions_going_offline_leaves_the_call_alone(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var desktop = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, "Voice Two Devices", ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "two-device-room", ct);
        await JoinSpaceAsync(owner, desktop, spaceId, ct);

        var phone = await SignInAgainAsync(desktop, ct);

        await using var watcher      = await WatchSpaceAsync(owner, spaceId, ct);
        await using var desktopVoice = await RealtimeClient.ConnectAsync(desktop, ct);
        await using var phoneClient  = await RealtimeClient.ConnectAsync(phone, ct);

        await JoinVoiceAsync(desktop, spaceId, channelId, ct);
        await watcher.WaitForAsync<JoinedToChannelUser>(
            e => e.channelId == channelId && e.userId == desktop.UserId, TimeSpan.FromSeconds(10), ct: ct);

        var beforePhoneLeaves = watcher.Mark();
        await phoneClient.GoOffline(ct);

        // An absence, so the window is fixed and short: five seconds is the same budget the tests
        // above give the departure they expect to see.
        await watcher.AssertNoneWithinAsync<LeavedFromChannelUser>(
            e => e.channelId == channelId && e.userId == desktop.UserId, ReactionWindow,
            "one device of two signing out must not hang up the call the other device is in",
            beforePhoneLeaves, ct);

        var occupants = await VoiceOccupantsAsync(owner, spaceId, channelId, ct);
        var aggregate = await probe.AggregatedStatusAsync(desktop.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(occupants, Does.Contain(desktop.UserId),
                "the user was dropped from the call because their other device signed out");
            Assert.That(aggregate, Is.Not.EqualTo(UserStatus.Offline),
                "the account reads offline while one of its two sessions is still connected");
        });
    }

    // ── H24: online counts ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two online counters a user ever sees — the space header and the invite sheet — agree with
    /// each other and with the presence snapshot the client renders the member list from.
    /// </summary>
    /// <remarks>
    /// Four members, three of them connected with three different non-offline statuses and one who
    /// only ever joined through the API. The number has to be three in all three places: a header
    /// that says four and a member list that shows three online is the shape of every "who is
    /// actually here" complaint.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_online_count_is_the_members_who_are_not_offline(CancellationToken ct = default)
    {
        await using var scene = await BuildCountedSpaceAsync(ct);

        var stats = await Poll.ForValueAsync(
            () => OnlineCountAsync(scene.Owner, scene.SpaceId, ct),
            count => count == 3, TimeSpan.FromSeconds(15), ct: ct);

        var preview    = await PreviewOnlineCountAsync(scene.Owner, scene.Code, ct);
        var notOffline = await NotOfflineCountAsync(scene.Owner, scene.SpaceId, ct);
        var described  = await DescribePresenceAsync(scene, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stats, Is.EqualTo(3),
                "GetSpaceStats counted a different number of people than the three connected members " +
                $"(Online, Away, DoNotDisturb) it has; the fourth never connected. {described}");
            Assert.That(preview, Is.EqualTo(3),
                "the invite sheet advertises a different online count than the space header");
            Assert.That(notOffline, Is.EqualTo(3),
                "GetMemberPresence disagrees with the counters about how many members are not offline");
        });
    }

    /// <summary>
    /// A member who sets themselves to Touch Grass is still one of the people who are here.
    /// </summary>
    /// <remarks>
    /// <para>The contract (defect S1, fixed; H3 surfacing in H24).
    /// <c>UserPresenceService.RecalculateAggregatedStatusAsync</c>
    /// (src/Argon.Core/Features/Logic/IUserPresenceService.cs) used to fold the session statuses with a
    /// ladder that recognised only <c>DoNotDisturb</c>, <c>Online</c> and <c>Away</c>; a session whose
    /// only status was <c>TouchGrass</c> (or <c>InGame</c>, or <c>Listen</c>) contributed nothing, so
    /// the fold ended at <c>Offline</c> and wrote it. Both counters read that aggregate
    /// (<c>SpaceGrain.GetSpaceStats</c> and <c>GetInvitePreview</c> count
    /// <c>BatchGetAggregatedStatusAsync</c> values that are not <c>Offline</c>), so a connected,
    /// heartbeating member dropped out of the count — and out of the member list, since
    /// <c>SpaceReadGrain.GetPresence</c> reads the same key. The fold is now total and carries the
    /// winning session's status verbatim.</para>
    ///
    /// <para>The rule the counters need: any status a client can set that is not <c>Offline</c> keeps
    /// the member in the count. The desktop client persists <c>TouchGrass</c> as a preferred status and
    /// has a label and a colour for it, so this is a status users really do sit in for hours.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_member_on_touch_grass_is_still_counted_as_here(CancellationToken ct = default)
    {
        await using var scene = await BuildCountedSpaceAsync(ct);

        await Poll.UntilAsync(async () => await OnlineCountAsync(scene.Owner, scene.SpaceId, ct) == 3,
            TimeSpan.FromSeconds(15), ct: ct);

        await scene.AwayClient.Heartbeat(UserStatus.TouchGrass, ct);

        // Polled towards the intended value so a correct implementation answers as soon as it has
        // converged; the assertion below reports whatever it actually settled on.
        var stats = await Poll.ForValueAsync(
            () => OnlineCountAsync(scene.Owner, scene.SpaceId, ct),
            count => count == 3, TimeSpan.FromSeconds(10), ct: ct);

        var preview    = await PreviewOnlineCountAsync(scene.Owner, scene.Code, ct);
        var notOffline = await NotOfflineCountAsync(scene.Owner, scene.SpaceId, ct);
        var aggregate  = await probe.AggregatedStatusAsync(scene.Away.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate, Is.EqualTo(UserStatus.TouchGrass),
                "a connected member whose only session heartbeats TouchGrass aggregates to something else");
            Assert.That(stats, Is.EqualTo(3),
                "a member switching to TouchGrass changed how many people the space says are online");
            Assert.That(preview, Is.EqualTo(3),
                "the invite sheet lost a member to a status change that did not disconnect anybody");
            Assert.That(notOffline, Is.EqualTo(3),
                "GetMemberPresence now shows a connected member as offline");
        });
    }

    /// <summary>
    /// A member who signs out leaves the count immediately — no grace, because they said so.
    /// </summary>
    /// <remarks>
    /// The counterpart to the test above: the count has to move when presence genuinely ends, and it
    /// has to move at once. A deliberate <c>GoOffline</c> skips the disconnect grace entirely, so
    /// three seconds is generous.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_member_signing_out_drops_out_of_the_count_at_once(CancellationToken ct = default)
    {
        await using var scene = await BuildCountedSpaceAsync(ct);

        await Poll.UntilAsync(async () => await OnlineCountAsync(scene.Owner, scene.SpaceId, ct) == 3,
            TimeSpan.FromSeconds(15), ct: ct);

        await scene.DndClient.GoOffline(ct);

        var stats = await Poll.ForValueAsync(
            () => OnlineCountAsync(scene.Owner, scene.SpaceId, ct),
            count => count == 2, TimeSpan.FromSeconds(3), ct: ct);

        var notOffline = await Poll.ForValueAsync(
            () => NotOfflineCountAsync(scene.Owner, scene.SpaceId, ct),
            count => count == 2, TimeSpan.FromSeconds(5), ct: ct);

        var preview = await PreviewOnlineCountAsync(scene.Owner, scene.Code, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stats, Is.EqualTo(2),
                "a member who signed out is still counted on the space header");
            Assert.That(preview, Is.EqualTo(2),
                "the invite sheet still counts a member who signed out");
            Assert.That(notOffline, Is.EqualTo(2),
                "GetMemberPresence still shows a member who signed out as not offline");
        });
    }

    /// <summary>
    /// Joining a space through the API without ever connecting makes you a member, not a presence:
    /// the count does not move and the space is not told you came online.
    /// </summary>
    /// <remarks>
    /// <para>Pins the counters and the event stream to the same answer about the same member. The
    /// counters were always the honest half — <c>GetSpaceStats</c> and <c>PreviewInvite</c> read the
    /// aggregate, which for someone who has never connected is <c>Offline</c> — so the test is really
    /// about the half that used to disagree with them.</para>
    ///
    /// <para>Defect S4 (ghost leg), now fixed: <c>SpaceGrain.UserJoined</c>
    /// (src/Argon.Api/Grains/SpaceGrain.cs), which runs on every <c>AddMemberAsync</c>, reads the
    /// aggregate and announces nothing when it is Offline, instead of calling
    /// <c>SetUserStatus(userId, UserStatus.Online)</c> unconditionally. That flat Online announced a
    /// user with no session at all to the whole space, and because <c>status:user:{u}:lastbroadcast</c>
    /// was never written for them no later transition could correct it — the header said one online
    /// while the roster showed two green dots, until the client reloaded its snapshot. The roster
    /// itself, not the roster event, is the gate for "the join landed", so the silence asserted below
    /// cannot be confused with a join that has not arrived yet.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_member_who_never_connected_is_neither_counted_nor_announced(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var ghost = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Counts Ghost", ct);
        var code    = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);

        var beforeGhostJoins = watcher.Mark();

        var joined = await ghost.Users.JoinToSpace(code, ct);
        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"the ghost could not join: {(joined as FailedJoin)?.error}");

        // The gate is the roster itself rather than the roster event, so that "nothing was announced"
        // below cannot be confused with "the join has not landed yet" — and so this test does not
        // depend on the very broadcast it is judging.
        var inRoster = await Poll.UntilAsync(
            async () => (await owner.Servers.GetMemberPresence(spaceId, ct)).Values.Any(m => m.userId == ghost.UserId),
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.That(inRoster, Is.True, "the new member never appeared in the space roster, so the join did not land");

        // Spends its whole window when nothing matches, which is the intended outcome; when the
        // phantom does arrive it returns at once, and the roster event fired immediately before it is
        // already in the log by then.
        var announced = await watcher.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == ghost.UserId, ReactionWindow, beforeGhostJoins, ct);

        var rosterEvent = watcher.EventsOfType<JoinToServerUser>(beforeGhostJoins)
           .FirstOrDefault(e => e.spaceId == spaceId && e.userId == ghost.UserId);

        var stats      = await OnlineCountAsync(owner, spaceId, ct);
        var preview    = await PreviewOnlineCountAsync(owner, code, ct);
        var notOffline = await NotOfflineCountAsync(owner, spaceId, ct);
        var aggregate  = await probe.AggregatedStatusAsync(ghost.UserId, ct);
        var alive      = await probe.IsUserOnlineAsync(ghost.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(rosterEvent, Is.Not.Null,
                "the members of the space were never told a new member joined");
            Assert.That(alive, Is.False, "a user who never connected has a live presence key");
            Assert.That(aggregate, Is.EqualTo(UserStatus.Offline),
                "a user who never connected has a non-offline aggregate");
            Assert.That(announced, Is.Null,
                "the space was told a member with no session at all changed status to " +
                $"'{(announced?.Event as UserChangedStatus)?.status}' when they joined — every client now " +
                "shows them present, and nothing will correct it because the hysteresis record was never written");
            Assert.That(stats, Is.EqualTo(1),
                "the space header counts a member who has never connected");
            Assert.That(preview, Is.EqualTo(1),
                "the invite sheet counts a member who has never connected");
            Assert.That(notOffline, Is.EqualTo(1),
                "GetMemberPresence disagrees with the counters about the member who never connected");
        });
    }

    /// <summary>
    /// A member whose connection dropped and came back inside the grace is still one of the people
    /// who are here, two minutes later.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract this now guards (defect S2, fixed), seen through the counters:</b> a
    /// session that dropped and reconnected renews exactly like one that never dropped, so it goes on
    /// being counted. <c>UserSessionGrain.DetachConnectionAsync</c> still disposes
    /// <c>refreshTimer</c> when the last connection goes — that is what lets the TTL lapse so the
    /// grace can finalize — but <c>EnsureRefreshTimer()</c> is now called from
    /// <c>AttachConnectionAsync</c> and every <c>HeartBeatAsync</c>, not only from
    /// <c>OnActivateAsync</c> and <c>EnsureSessionStartedAsync</c> (which returns immediately once
    /// <c>SessionStarted</c> is set, and was why a reconnect left the timer dead).</para>
    ///
    /// <para>The tick is the sole writer of <c>RefreshSessionStatusTtlAsync</c>, so without it
    /// <c>status:user:{u}:session</c> and <c>status:user:{u}:aggregated</c> lapsed 120 s after the
    /// reconnect while the heartbeat kept renewing the <em>presence</em> key — <c>IsUserOnline</c>
    /// said yes, and both counters, the member list and the friends push all read the member as
    /// Offline while they sat there connected. Slow: the failure is a TTL lapsing, so it cannot
    /// appear sooner than 120 s after the last refresh, and the test heartbeats every 10 s throughout
    /// exactly as the desktop client does — a session that stopped heartbeating would deactivate and
    /// self-heal on the next activation, which is not the case under test.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 5), Category("Slow")]
    public async Task A_member_who_reconnected_inside_the_grace_is_still_counted(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Counts Reconnect", ct);
        await JoinSpaceAsync(owner, member, spaceId, ct);
        var code = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        await using var client  = await RealtimeClient.ConnectAsync(member, ct);

        var settled = await Poll.ForValueAsync(
            () => OnlineCountAsync(owner, spaceId, ct), count => count == 2, TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(settled, Is.EqualTo(2), "the two connected members were never both counted");

        await client.AbortAsync(ct: ct);

        // Fixed, and only as a precondition: nothing observes the server finishing
        // OnDisconnectedAsync, and reconnecting before the detach lands would leave the session with a
        // connection the whole time — which is the case this test is NOT about.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        await client.RestartAsync(ct);
        Assert.That(client.IsConnected, Is.True, "the reconnect inside the grace did not come back up");

        using var beating  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var       heartbeat = HeartbeatEvery(client, TimeSpan.FromSeconds(10), beating.Token);

        try
        {
            // The status keys carry a 120 s TTL and were last renewed at most one tick before the
            // drop, so a lapse can show up from about 105 s in. Polling for the count to break means
            // an implementation that renews correctly is not made to wait for the whole window before
            // it is asserted on — it simply reads 2 the entire time.
            var count = await Poll.ForValueAsync(
                () => OnlineCountAsync(owner, spaceId, ct),
                value => value != 2, TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(2), ct);

            var preview    = await PreviewOnlineCountAsync(owner, code, ct);
            var notOffline = await NotOfflineCountAsync(owner, spaceId, ct);
            var aggregate  = await probe.AggregatedStatusAsync(member.UserId, ct);
            var alive      = await probe.IsUserOnlineAsync(member.UserId, ct);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.True, "the reconnected client dropped again during the wait");
                Assert.That(count, Is.EqualTo(2),
                    "a connected, heartbeating member stopped being counted after a reconnect inside the grace " +
                    $"(presence key alive: {alive}, aggregate: {aggregate})");
                Assert.That(preview, Is.EqualTo(2),
                    "the invite sheet stopped counting a member who is still connected");
                Assert.That(notOffline, Is.EqualTo(2),
                    "GetMemberPresence shows a connected, heartbeating member as offline");
                Assert.That(aggregate, Is.Not.EqualTo(UserStatus.Offline),
                    "the stored aggregate of a connected member lapsed to Offline");
            });
        }
        finally
        {
            await beating.CancelAsync();
            await heartbeat;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Which ion path the voice join actually took, reported when a test fails.</summary>
    private string voiceJoinPath = "not attempted";

    /// <summary>
    /// Enters a voice channel the way the desktop client does — the <c>Interlink</c> RPC — falling
    /// back to <c>IChannelGrain.Join</c> if the RPC cannot be driven in-process, so a broken SFU
    /// stub cannot silently turn a voice test into a no-op.
    /// </summary>
    private async Task JoinVoiceAsync(TestUserSession user, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        try
        {
            var result = await user.Channels.Interlink(spaceId, channelId, ct);

            if (result is SuccessJoinVoice)
            {
                voiceJoinPath = "ion Interlink";
                return;
            }

            Assert.Fail($"Interlink refused the voice join: {(result as FailedJoinVoice)?.error}");
        }
        catch (Exception e) when (e is not AssertionException)
        {
            // The token mint is a local JWT signature and the RTC endpoint read is configuration, so
            // this should not happen; if it does, the grain call exercises the same ChannelGrain.Join
            // and the report says which road the test took.
            voiceJoinPath = $"IChannelGrain.Join (Interlink threw: {e.GetType().Name}: {e.Message})";

            Orleans.Runtime.RequestContext.Set("$caller_user_id", user.UserId);
            try
            {
                var joined = await GetGrainFactory().GetGrain<IChannelGrain>(channelId).Join();
                Assert.That(joined.IsSuccess, Is.True, $"ChannelGrain.Join refused: {joined.Error}");
            }
            finally
            {
                Orleans.Runtime.RequestContext.Clear();
            }
        }
    }

    /// <summary>
    /// An observer connected and provably inside the space's broadcast group.
    /// </summary>
    /// <remarks>
    /// <c>ConnectAsync</c> returns once the hub has accepted the connection, which is not the instant
    /// <c>AppHub.OnConnectedAsync</c> finishes putting it into <c>spaces/{id}</c>. A test that
    /// triggers a broadcast a millisecond later can miss the first event and read it as "the server
    /// never sent it" — which is exactly how this fixture first mistook a lost <c>JoinToServerUser</c>
    /// for a product defect. <c>SubscribeToSpace</c> is a hub method the client awaits and joining a
    /// group is idempotent, so this closes the window without changing what is observed.
    /// </remarks>
    private static async Task<RealtimeClient> WatchSpaceAsync(TestUserSession observer, Guid spaceId, CancellationToken ct)
    {
        var client = await RealtimeClient.ConnectAsync(observer, ct);
        await client.SubscribeToSpace(spaceId, ct);
        return client;
    }

    /// <summary>The users a client would render inside the given voice channel, read live.</summary>
    private static async Task<List<Guid>> VoiceOccupantsAsync(TestUserSession reader, Guid spaceId, Guid channelId,
        CancellationToken ct)
    {
        var channels = await reader.Servers.GetChannels(spaceId, ct);
        var channel  = channels.Values.FirstOrDefault(c => c.channel.channelId == channelId);

        return channel is null ? [] : channel.users.Values.Select(u => u.userId).ToList();
    }

    private static async Task<int> OnlineCountAsync(TestUserSession reader, Guid spaceId, CancellationToken ct)
        => (await reader.Servers.GetSpaceStats(spaceId, ct)).onlineCount;

    private static async Task<int> PreviewOnlineCountAsync(TestUserSession reader, InviteCode code, CancellationToken ct)
    {
        var result = await reader.Users.PreviewInvite(code, ct);

        if (result is SuccessPreview success)
            return success.preview.onlineCount;

        Assert.Fail($"PreviewInvite failed: {(result as FailedPreview)?.error}");
        return -1;
    }

    private static async Task<int> NotOfflineCountAsync(TestUserSession reader, Guid spaceId, CancellationToken ct)
        => (await reader.Servers.GetMemberPresence(spaceId, ct)).Values.Count(m => m.status != UserStatus.Offline);

    /// <summary>Heartbeats on a cadence, the way the realtime worker does, until told to stop.</summary>
    private static Task HeartbeatEvery(RealtimeClient client, TimeSpan period, CancellationToken stop)
        => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, stop);
                    await client.Heartbeat(UserStatus.Online, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // A heartbeat that cannot be delivered is itself part of what the test asserts on
                    // (client.IsConnected), so it is not swallowed silently there.
                }
            }
        }, CancellationToken.None);

    private async Task<string> DescribePresenceAsync(CountedSpace scene, CancellationToken ct)
    {
        var presence = await scene.Owner.Servers.GetMemberPresence(scene.SpaceId, ct);

        var rows = presence.Values.Select(m => $"{Name(m.userId)}={m.status}");
        return $"presence: [{string.Join(", ", rows)}]";

        string Name(Guid id)
            => id == scene.Owner.UserId ? "owner"
             : id == scene.Away.UserId  ? "away"
             : id == scene.Dnd.UserId   ? "dnd"
             : id == scene.Ghost.UserId ? "ghost"
             : id.ToString();
    }

    /// <summary>
    /// A space with four members: the connected owner, a connected Away member, a connected
    /// DoNotDisturb member, and one who joined through the API and never opened a connection.
    /// </summary>
    private async Task<CountedSpace> BuildCountedSpaceAsync(CancellationToken ct)
    {
        var owner = await CreateSessionAsync(ct);
        var away  = await CreateSessionAsync(ct);
        var dnd   = await CreateSessionAsync(ct);
        var ghost = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Presence Counts", ct);
        var code    = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);

        foreach (var guest in new[] { away, dnd, ghost })
        {
            var joined = await guest.Users.JoinToSpace(code, ct);
            Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
                $"a member could not join the space: {(joined as FailedJoin)?.error}");
        }

        var ownerClient = await WatchSpaceAsync(owner, spaceId, ct);
        var awayClient  = await RealtimeClient.ConnectAsync(away, ct);
        var dndClient   = await RealtimeClient.ConnectAsync(dnd, ct);

        await awayClient.Heartbeat(UserStatus.Away, ct);
        await dndClient.Heartbeat(UserStatus.DoNotDisturb, ct);

        var awayStatus = await probe.WaitForAggregatedStatusAsync(away.UserId, UserStatus.Away, TimeSpan.FromSeconds(15), ct);
        var dndStatus  = await probe.WaitForAggregatedStatusAsync(dnd.UserId, UserStatus.DoNotDisturb, TimeSpan.FromSeconds(15), ct);
        var ownerStatus = await probe.WaitForAggregatedStatusAsync(owner.UserId, UserStatus.Online, TimeSpan.FromSeconds(15), ct);

        Assert.Multiple(() =>
        {
            Assert.That(ownerStatus, Is.EqualTo(UserStatus.Online), "the owner never reached Online");
            Assert.That(awayStatus, Is.EqualTo(UserStatus.Away), "the Away member never reached Away");
            Assert.That(dndStatus, Is.EqualTo(UserStatus.DoNotDisturb), "the DND member never reached DoNotDisturb");
        });

        return new CountedSpace(owner, away, dnd, ghost, spaceId, code, ownerClient, awayClient, dndClient);
    }

    private sealed record CountedSpace(
        TestUserSession Owner,
        TestUserSession Away,
        TestUserSession Dnd,
        TestUserSession Ghost,
        Guid            SpaceId,
        InviteCode      Code,
        RealtimeClient  OwnerClient,
        RealtimeClient  AwayClient,
        RealtimeClient  DndClient) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await OwnerClient.DisposeAsync();
            await AwayClient.DisposeAsync();
            await DndClient.DisposeAsync();
        }
    }

    /// <summary>
    /// A second device of an account that is already registered: a fresh client with its own sid,
    /// signed in with the same credentials.
    /// </summary>
    /// <remarks>
    /// <see cref="TestBase.CreateSessionAsync"/> always registers a new user, so it cannot express
    /// two devices of one account. The sid a session is keyed on is minted by the interceptor rather
    /// than by the token, so signing in again on a new interceptor is exactly what a second device
    /// looks like to the server.
    /// </remarks>
    private async Task<TestUserSession> SignInAgainAsync(TestUserSession account, CancellationToken ct)
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WsFactory);

        client.WithInterceptor(interceptor);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Authorize(
            new UserCredentialsInput(account.Credentials.email, null, account.Credentials.username,
                account.Credentials.password, null, null),
            ct);

        if (result is not SuccessAuthorize authorized)
        {
            Assert.Fail($"the second device could not sign in: {(result as FailedAuthorize)?.error}");
            return null!;
        }

        interceptor.SetToken(authorized.token);

        var session = new TestUserSession(client, FactoryAsp.Services, account.Credentials, authorized.token,
            interceptor.SessionId)
        {
            UserId = account.UserId
        };

        Assert.That(session.SessionId, Is.Not.EqualTo(account.SessionId),
            "the second device claims the same sid as the first, so it is not a second session at all");

        return session;
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = FactoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Presence voice and counts", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private static async Task<Guid> CreateVoiceChannelAsync(TestUserSession owner, Guid spaceId, string name,
        CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Voice, "Presence voice channel", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created voice channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    private static async Task JoinSpaceAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Guest could not join the space: {(joined as FailedJoin)?.error}");
    }
}
