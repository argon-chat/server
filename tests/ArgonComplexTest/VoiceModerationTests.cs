namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Core.Grains.Interfaces;
using Argon.Grains.Interfaces;
using Argon.Sfu;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Google.Protobuf;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LkParticipant = Livekit.Server.Sdk.Dotnet.ParticipantInfo;

/// <summary>
/// Voice moderation: kicking and moving a member between voice channels, server mute/deafen, the
/// member's own voice flags, and the rights a voice token carries.
/// </summary>
/// <remarks>
/// No SFU runs in the suite; <see cref="FakeLiveKit"/> answers LiveKit's API and records what the
/// server asked of it, which is what the enforcement half of these tests reads.
/// </remarks>
[TestFixture]
public class VoiceModerationTests : TestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    // ── join ────────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Join_without_Connect_on_the_channel_is_refused(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "locked", ChannelType.Voice, ct);

        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(archetypes, spaceId, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            deny: ArgonEntitlement.Connect, allow: ArgonEntitlement.None, ct);

        var refused = await member.Channels.Interlink(spaceId, channelId, ct);
        var allowed = await owner.Channels.Interlink(spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.InstanceOf<FailedJoinVoice>());
            Assert.That((refused as FailedJoinVoice)?.error, Is.EqualTo(JoinToChannelError.INSUFFICIENT_PERMISSIONS));
            Assert.That(allowed, Is.InstanceOf<SuccessJoinVoice>(), "the owner is not bound by the overwrite");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Join_on_a_text_channel_is_refused(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "chat", ChannelType.Text, ct);

        var result = await owner.Channels.Interlink(spaceId, channelId, ct);

        Assert.That((result as FailedJoinVoice)?.error, Is.EqualTo(JoinToChannelError.CHANNEL_IS_NOT_VOICE));
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_token_carries_only_the_media_the_member_may_publish(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "no-video", ChannelType.Voice, ct);

        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(archetypes, spaceId, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            deny: ArgonEntitlement.Video | ArgonEntitlement.Stream, allow: ArgonEntitlement.None, ct);

        var memberGrants = Grants(await JoinAsync(member, spaceId, channelId, ct));
        var ownerGrants  = Grants(await JoinAsync(owner, spaceId, channelId, ct));

        Assert.Multiple(() =>
        {
            Assert.That(Sources(memberGrants), Is.EqualTo(new[] { "microphone" }));
            Assert.That(memberGrants.GetProperty("canPublish").GetBoolean(), Is.True);
            Assert.That(memberGrants.GetProperty("canSubscribe").GetBoolean(), Is.True);
            Assert.That(Sources(ownerGrants), Is.Empty, "an unrestricted member gets LiveKit's \"any source\"");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Joining_another_voice_channel_of_the_space_leaves_the_first(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var first  = await CreateChannelAsync(owner, spaceId, "first", ChannelType.Voice, ct);
        var second = await CreateChannelAsync(owner, spaceId, "second", ChannelType.Voice, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);

        await JoinAsync(member, spaceId, first, ct);
        var mark = watcher.Mark();
        await JoinAsync(member, spaceId, second, ct);

        await watcher.WaitForAsync<LeavedFromChannelUser>(
            e => e.channelId == first && e.userId == member.UserId, Settle, mark, ct);
        var slot = await GetGrainFactory().GetGrain<ISpaceGrain>(spaceId).GetUserVoiceSlotAsync(member.UserId);

        Assert.Multiple(async () =>
        {
            Assert.That(await OccupantIdsAsync(owner, spaceId, first, ct), Does.Not.Contain(member.UserId));
            Assert.That(await OccupantIdsAsync(owner, spaceId, second, ct), Does.Contain(member.UserId));
            Assert.That(slot?.ChannelId, Is.EqualTo(second));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_late_leave_from_the_previous_room_keeps_the_current_slot(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var first  = await CreateChannelAsync(owner, spaceId, "old", ChannelType.Voice, ct);
        var second = await CreateChannelAsync(owner, spaceId, "new", ChannelType.Voice, ct);

        await JoinAsync(member, spaceId, first, ct);
        await JoinAsync(member, spaceId, second, ct);

        var space = GetGrainFactory().GetGrain<ISpaceGrain>(spaceId);
        await space.OnUserLeftVoiceAsync(member.UserId, first);

        Assert.That((await space.GetUserVoiceSlotAsync(member.UserId))?.ChannelId, Is.EqualTo(second));

        await space.OnUserLeftVoiceAsync(member.UserId, second);
        Assert.That(await space.GetUserVoiceSlotAsync(member.UserId), Is.Null);
    }

    // ── kick ────────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Kick_removes_the_member_from_the_room_and_the_roster(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "kick", ChannelType.Voice, ct);
        await JoinAsync(member, spaceId, channelId, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        var kicked = await owner.Channels.KickMemberFromChannel(spaceId, channelId, member.UserId, ct);

        await watcher.WaitForAsync<LeavedFromChannelUser>(
            e => e.channelId == channelId && e.userId == member.UserId, Settle, mark, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(kicked, Is.True);
            Assert.That(await OccupantIdsAsync(owner, spaceId, channelId, ct), Does.Not.Contain(member.UserId));
            Assert.That(GetFakeLiveKit().RemoveParticipantCalls,
                Has.Some.Matches<RoomParticipantIdentity>(c =>
                    c.Room == $"{spaceId}/{channelId}" && c.Identity == member.UserId.ToString()));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Kick_is_refused_without_KickMember_or_when_the_target_is_not_there(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var voice = await CreateChannelAsync(owner, spaceId, "kick-refused", ChannelType.Voice, ct);
        var text  = await CreateChannelAsync(owner, spaceId, "kick-text", ChannelType.Text, ct);
        await JoinAsync(owner, spaceId, voice, ct);

        var byMember   = await member.Channels.KickMemberFromChannel(spaceId, voice, owner.UserId, ct);
        var notThere   = await owner.Channels.KickMemberFromChannel(spaceId, voice, member.UserId, ct);
        var inTextRoom = await owner.Channels.KickMemberFromChannel(spaceId, text, member.UserId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(byMember, Is.False);
            Assert.That(notThere, Is.False);
            Assert.That(inTextRoom, Is.False);
            Assert.That(await OccupantIdsAsync(owner, spaceId, voice, ct), Does.Contain(owner.UserId));
        });
    }

    // ── move ────────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Move_has_the_SFU_move_the_participant_and_the_rosters_follow(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var from = await CreateChannelAsync(owner, spaceId, "lobby", ChannelType.Voice, ct);
        var to   = await CreateChannelAsync(owner, spaceId, "raid", ChannelType.Voice, ct);
        await JoinAsync(member, spaceId, from, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        var result = await owner.Channels.MoveVoiceMember(spaceId, from, member.UserId, to, ct);

        await watcher.WaitForAsync<LeavedFromChannelUser>(e => e.channelId == from && e.userId == member.UserId, Settle, mark, ct);
        await watcher.WaitForAsync<JoinedToChannelUser>(e => e.channelId == to && e.userId == member.UserId, Settle, mark, ct);

        var sfu  = SfuCallsAbout(member.UserId);
        var slot = await GetGrainFactory().GetGrain<ISpaceGrain>(spaceId).GetUserVoiceSlotAsync(member.UserId);

        Assert.That(result, Is.InstanceOf<SuccessMoveVoiceMember>(), $"{(result as FailedMoveVoiceMember)?.error}");
        Assert.That(sfu.Select(c => c.Method), Is.EqualTo(new[] { "MoveParticipant", "UpdateParticipant" }),
            "moved on its own connection, then given the target's rights; never kicked");

        var moved  = (MoveParticipantRequest)sfu[0].Request!;
        var rights = (UpdateParticipantRequest)sfu[1].Request!;
        Assert.Multiple(async () =>
        {
            Assert.That(moved.Room, Is.EqualTo($"{spaceId}/{from}"));
            Assert.That(moved.DestinationRoom, Is.EqualTo($"{spaceId}/{to}"));
            Assert.That(rights.Room, Is.EqualTo($"{spaceId}/{to}"));
            Assert.That(rights.Permission.CanSubscribe, Is.True);
            Assert.That(rights.Permission.CanPublish, Is.True);
            Assert.That(await OccupantIdsAsync(owner, spaceId, from, ct), Does.Not.Contain(member.UserId));
            Assert.That(await OccupantIdsAsync(owner, spaceId, to, ct), Does.Contain(member.UserId));
            Assert.That(slot?.ChannelId, Is.EqualTo(to));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_webhooks_a_move_fires_find_nothing_left_to_do(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var from = await CreateChannelAsync(owner, spaceId, "here", ChannelType.Voice, ct);
        var to   = await CreateChannelAsync(owner, spaceId, "there", ChannelType.Voice, ct);
        await JoinAsync(member, spaceId, from, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();
        Assert.That(await owner.Channels.MoveVoiceMember(spaceId, from, member.UserId, to, ct), Is.InstanceOf<SuccessMoveVoiceMember>());
        await watcher.WaitForAsync<JoinedToChannelUser>(e => e.channelId == to && e.userId == member.UserId, Settle, mark, ct);
        mark = watcher.Mark();

        // The SFU reports the move as a leave from the source and a join to the destination, in either order.
        await WebhookAsync("participant_joined", spaceId, to, member.UserId, ct);
        await WebhookAsync("participant_left", spaceId, from, member.UserId, ct);

        await watcher.AssertNoneWithinAsync<LeavedFromChannelUser>(e => e.userId == member.UserId, TimeSpan.FromSeconds(2),
            "the source had already let the member go when the move took them", mark, ct);
        await watcher.AssertNoneWithinAsync<JoinedToChannelUser>(e => e.userId == member.UserId, TimeSpan.FromSeconds(1),
            "the target admitted the member before the SFU moved them", mark, ct);
        var slot = await GetGrainFactory().GetGrain<ISpaceGrain>(spaceId).GetUserVoiceSlotAsync(member.UserId);

        Assert.Multiple(async () =>
        {
            Assert.That(await OccupantIdsAsync(owner, spaceId, from, ct), Does.Not.Contain(member.UserId));
            Assert.That(await OccupantIdsAsync(owner, spaceId, to, ct), Does.Contain(member.UserId));
            Assert.That(slot?.ChannelId, Is.EqualTo(to), "a late leave from the source must not clear a slot that points at the target");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_move_the_SFU_refuses_leaves_the_member_out_of_voice(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var from = await CreateChannelAsync(owner, spaceId, "stay", ChannelType.Voice, ct);
        var to   = await CreateChannelAsync(owner, spaceId, "go", ChannelType.Voice, ct);
        await JoinAsync(member, spaceId, from, ct);

        var live = GetFakeLiveKit();
        live.FailingMethods["MoveParticipant"] = true;
        IMoveVoiceMemberResult result;
        try
        {
            result = await owner.Channels.MoveVoiceMember(spaceId, from, member.UserId, to, ct);
        }
        finally
        {
            live.FailingMethods.TryRemove("MoveParticipant", out _);
        }

        // The target is told to let go one-way, so it may still be doing so.
        var target = await Poll.ForValueAsync(() => OccupantIdsAsync(owner, spaceId, to, ct),
            users => !users.Contains(member.UserId), Settle, ct: ct);
        var slot   = await GetGrainFactory().GetGrain<ISpaceGrain>(spaceId).GetUserVoiceSlotAsync(member.UserId);
        var kicked = live.RemoveParticipantCalls.Where(c => c.Identity == member.UserId.ToString()).Select(c => c.Room).ToList();

        Assert.Multiple(async () =>
        {
            Assert.That((result as FailedMoveVoiceMember)?.error, Is.EqualTo(MoveVoiceMemberError.SFU_UNAVAILABLE));
            Assert.That(await OccupantIdsAsync(owner, spaceId, from, ct), Does.Not.Contain(member.UserId));
            Assert.That(target, Does.Not.Contain(member.UserId));
            Assert.That(slot, Is.Null);
            Assert.That(kicked, Is.EquivalentTo(new[] { $"{spaceId}/{from}", $"{spaceId}/{to}" }),
                "removed from both rooms rather than left in whichever one the SFU kept them in");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Move_needs_an_SFU_that_moves(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var from = await CreateChannelAsync(owner, spaceId, "with", ChannelType.Voice, ct);
        var to   = await CreateChannelAsync(owner, spaceId, "without", ChannelType.Voice, ct);
        await JoinAsync(member, spaceId, from, ct);

        // The bound options instance the grain reads on every call.
        var sfu = FactoryAsp.Services.GetRequiredService<IOptions<CallKitOptions>>().Value.Sfu;
        Assert.That(sfu.Capabilities.Remove(SfuInstanceCfg.MoveCapability), Is.True, "the test host declares the capability");
        IMoveVoiceMemberResult withoutFork;
        try
        {
            withoutFork = await owner.Channels.MoveVoiceMember(spaceId, from, member.UserId, to, ct);
        }
        finally
        {
            sfu.Capabilities.Add(SfuInstanceCfg.MoveCapability);
        }

        Assert.Multiple(async () =>
        {
            Assert.That((withoutFork as FailedMoveVoiceMember)?.error, Is.EqualTo(MoveVoiceMemberError.SFU_UNAVAILABLE));
            Assert.That(await OccupantIdsAsync(owner, spaceId, from, ct), Does.Contain(member.UserId), "nothing moved: there is no client-side fallback");
            Assert.That(GetFakeLiveKit().MoveParticipantCalls,
                Has.None.Matches<MoveParticipantRequest>(m => m.Identity == member.UserId.ToString()));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Move_refuses_what_it_cannot_do(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var from   = await CreateChannelAsync(owner, spaceId, "a", ChannelType.Voice, ct);
        var to     = await CreateChannelAsync(owner, spaceId, "b", ChannelType.Voice, ct);
        var locked = await CreateChannelAsync(owner, spaceId, "locked", ChannelType.Voice, ct);
        var text   = await CreateChannelAsync(owner, spaceId, "text", ChannelType.Text, ct);

        var otherSpace = await CreateSpaceAsync(owner, ct);
        var elsewhere  = await CreateChannelAsync(owner, otherSpace, "elsewhere", ChannelType.Voice, ct);

        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(archetypes, spaceId, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, locked, everyone.id,
            deny: ArgonEntitlement.Connect, allow: ArgonEntitlement.None, ct);

        await JoinAsync(member, spaceId, from, ct);

        async Task<MoveVoiceMemberError> Move(TestUserSession by, Guid source, Guid memberId, Guid target)
            => await by.Channels.MoveVoiceMember(spaceId, source, memberId, target, ct) switch
            {
                FailedMoveVoiceMember failed => failed.error,
                _                            => MoveVoiceMemberError.NONE
            };

        Assert.Multiple(async () =>
        {
            Assert.That(await Move(owner, from, member.UserId, from), Is.EqualTo(MoveVoiceMemberError.SAME_CHANNEL));
            Assert.That(await Move(owner, to, member.UserId, from), Is.EqualTo(MoveVoiceMemberError.MEMBER_NOT_IN_CHANNEL));
            Assert.That(await Move(member, from, member.UserId, to), Is.EqualTo(MoveVoiceMemberError.INSUFFICIENT_PERMISSIONS));
            Assert.That(await Move(owner, from, member.UserId, Guid.NewGuid()), Is.EqualTo(MoveVoiceMemberError.TARGET_NOT_FOUND));
            Assert.That(await Move(owner, from, member.UserId, elsewhere), Is.EqualTo(MoveVoiceMemberError.TARGET_NOT_FOUND),
                "a channel of another space is not a target");
            Assert.That(await Move(owner, from, member.UserId, text), Is.EqualTo(MoveVoiceMemberError.TARGET_IS_NOT_VOICE));
            Assert.That(await Move(owner, from, member.UserId, locked), Is.EqualTo(MoveVoiceMemberError.MEMBER_CANNOT_JOIN_TARGET));
        });
    }

    // ── server mute / deafen ────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_server_muted_member_joins_without_a_microphone_and_is_shown_muted(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "muted", ChannelType.Voice, ct);

        var set = await owner.Servers.SetMemberVoiceModeration(spaceId, member.UserId, true, null, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark   = watcher.Mark();
        var grants = Grants(await JoinAsync(member, spaceId, channelId, ct));

        var changed = await watcher.WaitForAsync<VoiceMemberStateChanged>(
            e => e.channelId == channelId && e.userId == member.UserId, Settle, mark, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(set, Is.EqualTo(new SuccessVoiceModeration(true, false)));
            Assert.That(changed.state, Is.EqualTo(ChannelMemberState.MUTED_BY_SERVER));
            Assert.That(await StateOfAsync(owner, spaceId, channelId, member.UserId, ct), Is.EqualTo(ChannelMemberState.MUTED_BY_SERVER));
            Assert.That(Sources(grants), Does.Not.Contain("microphone"));
            Assert.That(Sources(grants), Does.Contain("camera"));
            Assert.That(grants.GetProperty("canSubscribe").GetBoolean(), Is.True);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Muting_and_deafening_a_member_in_voice_updates_the_room_and_everyone(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "live", ChannelType.Voice, ct);
        await JoinAsync(member, spaceId, channelId, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var live = GetFakeLiveKit();

        var mark = watcher.Mark();
        await owner.Servers.SetMemberVoiceModeration(spaceId, member.UserId, true, null, ct);
        var muted = await watcher.WaitForAsync<VoiceMemberStateChanged>(
            e => e.userId == member.UserId && e.state.HasFlag(ChannelMemberState.MUTED_BY_SERVER), Settle, mark, ct);
        var mutedRights = await RightsSentAsync(live, spaceId, channelId, member.UserId,
            p => !p.CanPublishSources.Contains(TrackSource.Microphone), ct);

        mark = watcher.Mark();
        await owner.Servers.SetMemberVoiceModeration(spaceId, member.UserId, false, true, ct);
        var deafened = await watcher.WaitForAsync<VoiceMemberStateChanged>(
            e => e.userId == member.UserId && e.state == ChannelMemberState.MUTED_HEADPHONES_BY_SERVER, Settle, mark, ct);
        var deafRights = await RightsSentAsync(live, spaceId, channelId, member.UserId, p => !p.CanSubscribe, ct);

        mark = watcher.Mark();
        await owner.Servers.SetMemberVoiceModeration(spaceId, member.UserId, null, false, ct);
        var cleared = await watcher.WaitForAsync<VoiceMemberStateChanged>(
            e => e.userId == member.UserId && e.state == ChannelMemberState.NONE, Settle, mark, ct);
        var restored = await RightsSentAsync(live, spaceId, channelId, member.UserId,
            p => p.CanSubscribe && p.CanPublishSources.Contains(TrackSource.Microphone), ct);

        Assert.Multiple(() =>
        {
            Assert.That(muted.state, Is.EqualTo(ChannelMemberState.MUTED_BY_SERVER));
            Assert.That(mutedRights.CanSubscribe, Is.True);
            Assert.That(mutedRights.CanPublishSources, Does.Contain(TrackSource.Camera));
            Assert.That(deafened.state, Is.EqualTo(ChannelMemberState.MUTED_HEADPHONES_BY_SERVER));
            Assert.That(deafRights.CanPublishSources, Does.Not.Contain(TrackSource.Microphone), "a deafened member cannot speak either");
            Assert.That(cleared.state, Is.EqualTo(ChannelMemberState.NONE));
            Assert.That(restored.CanPublish, Is.True);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_server_mute_follows_the_member_into_another_channel(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var first  = await CreateChannelAsync(owner, spaceId, "one", ChannelType.Voice, ct);
        var second = await CreateChannelAsync(owner, spaceId, "two", ChannelType.Voice, ct);

        await JoinAsync(member, spaceId, first, ct);
        await owner.Servers.SetMemberVoiceModeration(spaceId, member.UserId, null, true, ct);
        await Poll.ForValueAsync(() => StateOfAsync(owner, spaceId, first, member.UserId, ct),
            s => s == ChannelMemberState.MUTED_HEADPHONES_BY_SERVER, Settle, ct: ct);

        var grants = Grants(await JoinAsync(member, spaceId, second, ct));

        Assert.Multiple(async () =>
        {
            Assert.That(await StateOfAsync(owner, spaceId, second, member.UserId, ct),
                Is.EqualTo(ChannelMemberState.MUTED_HEADPHONES_BY_SERVER));
            Assert.That(grants.GetProperty("canSubscribe").GetBoolean(), Is.False);
            Assert.That(Sources(grants), Does.Not.Contain("microphone"));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Voice_moderation_refuses_what_it_cannot_do(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var moderator = await CreateSessionAsync(ct);
        await JoinSpaceAsync(owner, moderator, spaceId, ct);
        await GrantAsync(owner, spaceId, moderator, ArgonEntitlement.MuteMember, ct);

        async Task<VoiceModerationError> Moderate(TestUserSession by, Guid memberId, bool? muted, bool? deafened)
            => await by.Servers.SetMemberVoiceModeration(spaceId, memberId, muted, deafened, ct) switch
            {
                FailedVoiceModeration failed => failed.error,
                _                            => VoiceModerationError.NONE
            };

        Assert.Multiple(async () =>
        {
            Assert.That(await Moderate(member, moderator.UserId, true, null), Is.EqualTo(VoiceModerationError.INSUFFICIENT_PERMISSIONS));
            Assert.That(await Moderate(moderator, member.UserId, null, true), Is.EqualTo(VoiceModerationError.INSUFFICIENT_PERMISSIONS),
                "deafen is its own entitlement");
            Assert.That(await Moderate(moderator, Guid.NewGuid(), true, null), Is.EqualTo(VoiceModerationError.MEMBER_NOT_FOUND));
            Assert.That(await Moderate(moderator, owner.UserId, true, null), Is.EqualTo(VoiceModerationError.CANNOT_MODERATE_OWNER));
            Assert.That(await Moderate(moderator, member.UserId, true, null), Is.EqualTo(VoiceModerationError.NONE));
        });

        var current = await member.Servers.SetMemberVoiceModeration(spaceId, member.UserId, null, null, ct);
        Assert.That(current, Is.EqualTo(new SuccessVoiceModeration(true, false)), "asking without changing anything reads the state back");
    }

    // ── the member's own flags ──────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Members_report_their_own_flags_but_cannot_touch_the_server_ones(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "flags", ChannelType.Voice, ct);
        await JoinAsync(member, spaceId, channelId, ct);
        await owner.Servers.SetMemberVoiceModeration(spaceId, member.UserId, true, null, ct);
        await Poll.ForValueAsync(() => StateOfAsync(owner, spaceId, channelId, member.UserId, ct),
            s => s == ChannelMemberState.MUTED_BY_SERVER, Settle, ct: ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        await member.Channels.UpdateVoiceState(spaceId, channelId,
            ChannelMemberState.MUTED | ChannelMemberState.STREAMING | ChannelMemberState.MUTED_HEADPHONES_BY_SERVER, ct);

        var changed = await watcher.WaitForAsync<VoiceMemberStateChanged>(
            e => e.userId == member.UserId && e.state.HasFlag(ChannelMemberState.STREAMING), Settle, mark, ct);

        // Somebody outside the room reporting flags for it changes nothing.
        await owner.Channels.UpdateVoiceState(spaceId, channelId, ChannelMemberState.MUTED, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(changed.state,
                Is.EqualTo(ChannelMemberState.MUTED | ChannelMemberState.STREAMING | ChannelMemberState.MUTED_BY_SERVER));
            Assert.That(await OccupantIdsAsync(owner, spaceId, channelId, ct), Does.Not.Contain(owner.UserId));
        });
    }

    // ── recording ───────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Recording_starts_once_and_stops_once(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "record", ChannelType.Voice, ct);
        await JoinAsync(owner, spaceId, channelId, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        var started      = await owner.Channels.BeginRecord(spaceId, channelId, ct);
        var startedAgain = await owner.Channels.BeginRecord(spaceId, channelId, ct);
        var stopped      = await owner.Channels.StopRecord(spaceId, channelId, ct);
        var stoppedAgain = await owner.Channels.StopRecord(spaceId, channelId, ct);

        await watcher.WaitForAsync<RecordStarted>(e => e.channelId == channelId, Settle, mark, ct);
        await watcher.WaitForAsync<RecordEnded>(e => e.channelId == channelId, Settle, mark, ct);

        var live = GetFakeLiveKit();
        Assert.Multiple(() =>
        {
            Assert.That(started, Is.True);
            Assert.That(startedAgain, Is.False);
            Assert.That(stopped, Is.True);
            Assert.That(stoppedAgain, Is.False);
            Assert.That(live.StartEgressCalls, Has.Some.Matches<RoomCompositeEgressRequest>(r => r.RoomName == $"{spaceId}/{channelId}"));
        });
    }

    // ── LiveKit failures ────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_participant_LiveKit_does_not_know_is_reported_not_thrown(CancellationToken ct = default)
    {
        var live  = GetFakeLiveKit();
        var voice = GetGrainFactory().GetGrain<IVoiceControlGrain>(Guid.Empty);
        var room  = new ArgonRoomId(Guid.NewGuid(), Guid.NewGuid());

        // Only this fixture drives these two calls, and its tests run one at a time.
        live.FailingMethods["UpdateParticipant"] = true;
        live.FailingMethods["RemoveParticipant"] = true;
        try
        {
            var updated = await voice.UpdateParticipantRightsAsync(new ArgonUserId(Guid.NewGuid()), room, SfuMediaRights.All);
            var kicked  = await voice.KickParticipantAsync(new ArgonUserId(Guid.NewGuid()), room);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.False);
                Assert.That(kicked, Is.False);
            });
        }
        finally
        {
            live.FailingMethods.Clear();
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(TestUserSession Owner, TestUserSession Member, Guid SpaceId)> SpaceWithMemberAsync(CancellationToken ct)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        await JoinSpaceAsync(owner, member, spaceId, ct);
        return (owner, member, spaceId);
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest("Voice moderation", "", string.Empty), ct);
        if (result is SuccessCreateSpace success)
            return success.space.spaceId;

        Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)?.error}");
        return Guid.Empty;
    }

    private static async Task<Guid> CreateChannelAsync(TestUserSession owner, Guid spaceId, string name, ChannelType type,
        CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty, new CreateChannelRequest(spaceId, name, type, "", null), ct).Ok();

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        return channels.Values.First(c => c.channel.name == name).channel.channelId;
    }

    private static async Task JoinSpaceAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();
        Assert.That(await guest.Users.JoinToSpace(code, ct), Is.InstanceOf<SuccessJoin>());
    }

    private static async Task<string> JoinAsync(TestUserSession user, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var result = await user.Channels.Interlink(spaceId, channelId, ct);
        if (result is SuccessJoinVoice joined)
            return joined.token;

        Assert.Fail($"Interlink refused the voice join: {(result as FailedJoinVoice)?.error}");
        return "";
    }

    private IArchetypeInteraction ArchetypesOf(TestUserSession session)
        => session.Client.ForService<IArchetypeInteraction>(FactoryAsp.Services);

    private static async Task<Archetype> EveryoneAsync(IArchetypeInteraction archetypes, Guid spaceId, CancellationToken ct)
        => (await archetypes.GetServerArchetypes(spaceId, ct)).Values.First(a => a.isDefault);

    private async Task GrantAsync(TestUserSession owner, Guid spaceId, TestUserSession member, ArgonEntitlement entitlement,
        CancellationToken ct)
    {
        var archetypes = ArchetypesOf(owner);
        var created    = await archetypes.CreateArchetype(spaceId, "moderator", ct).Ok();
        await archetypes.UpdateArchetype(spaceId, created with { entitlement = entitlement }, ct).Ok();

        // The archetype call addresses the membership row, not the user.
        var members    = await owner.Servers.GetMembers(spaceId, ct);
        var membership = members.Values.First(m => m.member.userId == member.UserId).member.memberId;
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, membership, created.id, true, ct), Is.True);
    }

    private static async Task<RealtimeClient> WatchSpaceAsync(TestUserSession observer, Guid spaceId, CancellationToken ct)
    {
        var client = await RealtimeClient.ConnectAsync(observer, ct);
        await client.SubscribeToSpace(spaceId, ct);
        return client;
    }

    private static async Task<RealtimeChannelUser?> OccupantAsync(TestUserSession reader, Guid spaceId, Guid channelId,
        Guid userId, CancellationToken ct)
    {
        var channels = await reader.Servers.GetChannels(spaceId, ct);
        return channels.Values.First(c => c.channel.channelId == channelId).users.Values.FirstOrDefault(u => u.userId == userId);
    }

    private static async Task<List<Guid>> OccupantIdsAsync(TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var channels = await reader.Servers.GetChannels(spaceId, ct);
        return channels.Values.First(c => c.channel.channelId == channelId).users.Values.Select(u => u.userId).ToList();
    }

    private static async Task<ChannelMemberState?> StateOfAsync(TestUserSession reader, Guid spaceId, Guid channelId, Guid userId,
        CancellationToken ct)
        => (await OccupantAsync(reader, spaceId, channelId, userId, ct))?.state;

    /// <summary>The participant-scoped LiveKit calls made about one member, in the order they were made.</summary>
    private List<(string Method, IMessage? Request)> SfuCallsAbout(Guid userId)
        => GetFakeLiveKit().Timeline
           .Where(c => c.Request switch
            {
                MoveParticipantRequest m   => m.Identity == userId.ToString(),
                UpdateParticipantRequest u => u.Identity == userId.ToString(),
                RoomParticipantIdentity r  => r.Identity == userId.ToString(),
                _                          => false
            })
           .ToList();

    /// <summary>What livekit-server posts to <c>/webhook-endpoint</c>, signed the way it signs it.</summary>
    private async Task WebhookAsync(string kind, Guid spaceId, Guid channelId, Guid userId, CancellationToken ct)
    {
        var body = JsonFormatter.Default.Format(new WebhookEvent
        {
            Id          = $"EV_{Guid.NewGuid():N}",
            Event       = kind,
            Room        = new Room { Name = $"{spaceId}/{channelId}" },
            Participant = new LkParticipant { Identity = userId.ToString() }
        });
        var signature = new AccessToken(ArgonServerTargetHost.SfuClientId, ArgonServerTargetHost.SfuSecret)
           .WithSha256(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))))
           .ToJwt();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook-endpoint")
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/webhook+json"))
        };
        request.Headers.TryAddWithoutValidation("Authorization", signature);

        using var response = await HttpClient.SendAsync(request, ct);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{kind} was not accepted");
    }

    private static Task<ParticipantPermission> RightsSentAsync(FakeLiveKit live, Guid spaceId, Guid channelId, Guid userId,
        Func<ParticipantPermission, bool> match, CancellationToken ct)
        => Poll.ForValueAsync(
            () => Task.FromResult(live.UpdateParticipantCalls
               .Where(c => c.Room == $"{spaceId}/{channelId}" && c.Identity == userId.ToString())
               .Select(c => c.Permission)
               .LastOrDefault(p => p is not null && match(p))),
            p => p is not null, Settle, ct: ct)!;

    /// <summary>The <c>video</c> grant of a LiveKit token.</summary>
    private static JsonElement Grants(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.GetProperty("video").Clone();
    }

    private static string[] Sources(JsonElement grants)
        => grants.TryGetProperty("canPublishSources", out var sources)
            ? sources.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [];
}
