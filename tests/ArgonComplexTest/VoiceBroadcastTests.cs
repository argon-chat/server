namespace ArgonComplexTest.Tests;

using System.Text;
using System.Text.Json;
using Argon.Grains;
using Argon.Grains.Interfaces;
using Argon.Sfu;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ArgonComplexTest.Tests.ChannelTestKit;
using LkParticipant = Livekit.Server.Sdk.Dotnet.ParticipantInfo;

/// <summary>
/// The live half of a broadcast ("radio") channel: who gets a radio link, what the link is, how the
/// radio participant is forwarded into the targets, and everything that revokes it — leaving,
/// kicks, server mute, target edits, the mode going off, the channel or the membership going away,
/// and the sweeper that catches what no hook saw.
/// </summary>
/// <remarks>
/// No SFU runs in the suite; <see cref="FakeLiveKit"/> answers LiveKit's API and records what the
/// server asked of it. The sweeper reads the radio room off the canned <see cref="FakeLiveKit.Participants"/>.
/// </remarks>
[TestFixture]
public class VoiceBroadcastTests : TestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    private TimeSpan originalSweepPeriod;
    private TimeSpan originalTransmitGrace;

    [OneTimeSetUp]
    public void ShortenTheSweeper()
    {
        originalSweepPeriod   = VoiceBroadcastGrain.SweepPeriod;
        originalTransmitGrace = VoiceBroadcastGrain.TransmitGrace;

        VoiceBroadcastGrain.SweepPeriod   = TimeSpan.FromSeconds(1);
        VoiceBroadcastGrain.TransmitGrace = TimeSpan.FromSeconds(1);
    }

    [OneTimeTearDown]
    public void RestoreTheSweeper()
    {
        VoiceBroadcastGrain.SweepPeriod   = originalSweepPeriod;
        VoiceBroadcastGrain.TransmitGrace = originalTransmitGrace;
    }

    // ── links refused ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Links_need_the_member_in_HQ_and_HQ_to_broadcast(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);

        var outsideVoice = await member.Channels.GetBroadcastLinks(spaceId, hq, ct);

        await JoinVoiceAsync(member, spaceId, party, ct);
        var elsewhere    = await member.Channels.GetBroadcastLinks(spaceId, hq, ct);
        var ordinaryRoom = await member.Channels.GetBroadcastLinks(spaceId, party, ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(outsideVoice), Is.EqualTo(BroadcastLinksError.NOT_IN_CHANNEL), "not in voice at all");
            Assert.That(Error(elsewhere), Is.EqualTo(BroadcastLinksError.NOT_IN_CHANNEL), "in another channel of the space");
            Assert.That(Error(ordinaryRoom), Is.EqualTo(BroadcastLinksError.NOT_A_BROADCAST_CHANNEL));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Links_need_Broadcast_and_no_server_restriction(CancellationToken ct = default)
    {
        var (owner, denied, spaceId) = await SpaceWithMemberAsync(ct);
        var muted = await CreateSessionAsync(ct);
        await JoinAsync(owner, muted, spaceId, ct);

        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);

        // Decided before the mode goes on, so enabling leaves the deny alone.
        await DenyOnChannelAsync(owner, spaceId, hq, ArgonEntitlement.Broadcast, ct);
        await EnableBroadcastAsync(owner, spaceId, hq, Settings(party), ct);

        await JoinVoiceAsync(denied, spaceId, hq, ct);
        var noRight = await denied.Channels.GetBroadcastLinks(spaceId, hq, ct);

        await EveryoneMayBroadcastAsync(owner, spaceId, hq, ct);
        await owner.Servers.SetMemberVoiceModeration(spaceId, muted.UserId, true, null, ct);
        await JoinVoiceAsync(muted, spaceId, hq, ct);
        var restricted = await muted.Channels.GetBroadcastLinks(spaceId, hq, ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(noRight), Is.EqualTo(BroadcastLinksError.INSUFFICIENT_PERMISSIONS));
            Assert.That(Error(restricted), Is.EqualTo(BroadcastLinksError.SERVER_RESTRICTED));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Links_need_an_SFU_that_forwards(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        await JoinVoiceAsync(member, spaceId, hq, ct);

        // The bound options instance the grain reads on every call; only the broadcast grain looks at it.
        var sfu = Services.GetRequiredService<IOptions<CallKitOptions>>().Value.Sfu;
        Assert.That(sfu.Capabilities.Remove(SfuInstanceCfg.ForwardCapability), Is.True, "the test host declares the capability");
        IBroadcastLinksResult withoutFork;
        try
        {
            withoutFork = await member.Channels.GetBroadcastLinks(spaceId, hq, ct);
        }
        finally
        {
            sfu.Capabilities.Add(SfuInstanceCfg.ForwardCapability);
        }

        var withFork = await member.Channels.GetBroadcastLinks(spaceId, hq, ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(withoutFork), Is.EqualTo(BroadcastLinksError.SFU_UNAVAILABLE));
            Assert.That(withFork, Is.InstanceOf<SuccessBroadcastLinks>(), $"{Error(withFork)}");
        });
    }

    // ── links issued ────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Links_carry_a_publish_only_radio_token_and_are_reused_for_a_minute(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        await JoinVoiceAsync(member, spaceId, hq, ct);

        var first  = await member.Channels.GetBroadcastLinks(spaceId, hq, ct) as SuccessBroadcastLinks;
        var second = await member.Channels.GetBroadcastLinks(spaceId, hq, ct) as SuccessBroadcastLinks;

        Assert.That(first, Is.Not.Null, "the first request was refused");
        var claims = Payload(first!.token);
        var grants = claims.GetProperty("video");
        var ttl    = claims.GetProperty("exp").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Multiple(() =>
        {
            Assert.That(first.identity, Is.EqualTo($"bc:{member.UserId}"));
            Assert.That(first.room, Is.EqualTo(Radio(spaceId, hq)));
            Assert.That(first.settings.targets.Values, Is.EqualTo(new[] { party }));
            Assert.That(first.rtc.endpoint, Is.Not.Empty);

            Assert.That(claims.GetProperty("sub").GetString(), Is.EqualTo($"bc:{member.UserId}"));
            Assert.That(grants.GetProperty("room").GetString(), Is.EqualTo(Radio(spaceId, hq)));
            Assert.That(grants.GetProperty("canPublish").GetBoolean(), Is.True);
            Assert.That(grants.GetProperty("canSubscribe").GetBoolean(), Is.False);
            Assert.That(grants.GetProperty("canPublishSources").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(new[] { "microphone" }));
            Assert.That(ttl, Is.InRange(14 * 60, 15 * 60), "fifteen minutes, only to connect");

            Assert.That(second?.token, Is.EqualTo(first.token), "a second request inside a minute gets the same token");
        });
    }

    // ── confirm ─────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Confirming_forwards_the_radio_into_every_target_that_still_qualifies(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var alpha   = await CreateChannelAsync(owner, spaceId, "alpha", ChannelType.Voice, ct);
        var bravo   = await CreateChannelAsync(owner, spaceId, "bravo", ChannelType.Voice, ct);
        var charlie = await CreateChannelAsync(owner, spaceId, "charlie", ChannelType.Voice, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, Settings(alpha, bravo, charlie), ct);

        // Valid when saved, a broadcast channel by the time anyone confirms: decision 8 says it does not hear HQ.
        Assert.That(await owner.Channels.SetBroadcastMode(spaceId, charlie, true, ct), Is.InstanceOf<SuccessSetBroadcastSettings>());

        await JoinVoiceAsync(member, spaceId, hq, ct);
        var links     = await member.Channels.GetBroadcastLinks(spaceId, hq, ct);
        var confirmed = await member.Channels.ConfirmBroadcastLinks(spaceId, hq, ct);

        var forwards = GetFakeLiveKit().ForwardParticipantCalls
           .Where(f => f.Room == Radio(spaceId, hq) && f.Identity == $"bc:{member.UserId}")
           .Select(f => f.DestinationRoom)
           .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(links, Is.InstanceOf<SuccessBroadcastLinks>(), $"{Error(links)}");
            Assert.That((confirmed as SuccessConfirmBroadcastLinks)?.forwardedTargets, Is.EqualTo(2), $"{(confirmed as FailedConfirmBroadcastLinks)?.error}");
            Assert.That(forwards, Is.EquivalentTo(new[] { $"{spaceId}/{alpha}", $"{spaceId}/{bravo}" }));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Confirming_without_links_is_refused(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        await JoinVoiceAsync(member, spaceId, hq, ct);

        var confirmed = await member.Channels.ConfirmBroadcastLinks(spaceId, hq, ct);

        Assert.That((confirmed as FailedConfirmBroadcastLinks)?.error, Is.EqualTo(BroadcastLinksError.NOT_IN_CHANNEL),
            "no link was issued to this activation; the client refetches");
    }

    // ── revocation ──────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Leaving_HQ_or_being_kicked_removes_the_radio_participant(CancellationToken ct = default)
    {
        var (owner, leaver, spaceId) = await SpaceWithMemberAsync(ct);
        var kicked = await CreateSessionAsync(ct);
        await JoinAsync(owner, kicked, spaceId, ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        var radio = Radio(spaceId, hq);

        await OnAirAsync(leaver, spaceId, hq, ct);
        await OnAirAsync(kicked, spaceId, hq, ct);

        await leaver.Channels.DisconnectFromVoiceChannel(spaceId, hq, ct);
        var wasKicked = await owner.Channels.KickMemberFromChannel(spaceId, hq, kicked.UserId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(wasKicked, Is.True);
            Assert.That(await RemovedAsync(radio, $"bc:{leaver.UserId}", ct), Is.True, "leaving");
            Assert.That(await RemovedAsync(radio, $"bc:{kicked.UserId}", ct), Is.True, "kicked");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_server_mute_removes_the_radio_participant(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        await OnAirAsync(member, spaceId, hq, ct);

        await owner.Servers.SetMemberVoiceModeration(spaceId, member.UserId, true, null, ct);

        Assert.That(await RemovedAsync(Radio(spaceId, hq), $"bc:{member.UserId}", ct), Is.True);
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Editing_the_targets_forwards_into_the_new_ones_and_leaves_the_dropped_ones(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var alpha = await CreateChannelAsync(owner, spaceId, "alpha", ChannelType.Voice, ct);
        var bravo = await CreateChannelAsync(owner, spaceId, "bravo", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(alpha), ct);
        await OnAirAsync(member, spaceId, hq, ct);

        Assert.That(await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(bravo), ct), Is.InstanceOf<SuccessSetBroadcastSettings>());

        var live     = GetFakeLiveKit();
        var identity = $"bc:{member.UserId}";

        Assert.Multiple(async () =>
        {
            Assert.That(live.ForwardParticipantCalls, Has.Some.Matches<ForwardParticipantRequest>(f =>
                f.Room == Radio(spaceId, hq) && f.Identity == identity && f.DestinationRoom == $"{spaceId}/{bravo}"), "the added target");
            Assert.That(await RemovedAsync($"{spaceId}/{alpha}", identity, ct), Is.True, "the dropped target");
            Assert.That(live.RemoveParticipantCalls, Has.None.Matches<RoomParticipantIdentity>(c =>
                c.Room == Radio(spaceId, hq) && c.Identity == identity), "the radio itself stays");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Switching_the_mode_off_removes_every_radio_participant(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        await OnAirAsync(member, spaceId, hq, ct);

        Assert.That(await owner.Channels.SetBroadcastMode(spaceId, hq, false, ct), Is.InstanceOf<SuccessSetBroadcastSettings>());

        var refetched = await member.Channels.GetBroadcastLinks(spaceId, hq, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await RemovedAsync(Radio(spaceId, hq), $"bc:{member.UserId}", ct), Is.True);
            Assert.That(Error(refetched), Is.EqualTo(BroadcastLinksError.NOT_A_BROADCAST_CHANNEL));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Deleting_HQ_empties_the_room_and_the_radio(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        await OnAirAsync(member, spaceId, hq, ct);

        await owner.Channels.DeleteChannel(spaceId, hq, ct);

        var slot = await Poll.ForValueAsync(
            () => GetGrainFactory().GetGrain<ISpaceGrain>(spaceId).GetUserVoiceSlotAsync(member.UserId),
            s => s is null, Settle, ct: ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await RemovedAsync($"{spaceId}/{hq}", member.UserId.ToString(), ct), Is.True, "kicked from the room");
            Assert.That(await RemovedAsync(Radio(spaceId, hq), $"bc:{member.UserId}", ct), Is.True, "the radio participant");
            Assert.That(slot, Is.Null, "the voice slot");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_member_removed_from_the_space_leaves_voice_and_the_radio(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        await OnAirAsync(member, spaceId, hq, ct);

        var space = GetGrainFactory().GetGrain<ISpaceGrain>(spaceId);
        await space.RemoveMemberAsync(member.UserId);

        var occupants = await Poll.ForValueAsync(() => OccupantIdsAsync(owner, spaceId, hq, ct),
            users => !users.Contains(member.UserId), Settle, ct: ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await space.GetUserVoiceSlotAsync(member.UserId), Is.Null);
            Assert.That(occupants, Does.Not.Contain(member.UserId));
            Assert.That(await RemovedAsync($"{spaceId}/{hq}", member.UserId.ToString(), ct), Is.True, "kicked from the room");
            Assert.That(await RemovedAsync(Radio(spaceId, hq), $"bc:{member.UserId}", ct), Is.True, "the radio participant");
        });
    }

    // ── sweeper ─────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_sweeper_removes_a_radio_participant_nobody_was_issued(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        var radio = Radio(spaceId, hq);
        await OnAirAsync(member, spaceId, hq, ct);

        var stranger = Guid.NewGuid();
        GetFakeLiveKit().Participants[radio] = Room(RadioParticipant(member.UserId, muted: true), RadioParticipant(stranger, muted: true));

        Assert.Multiple(async () =>
        {
            Assert.That(await RemovedAsync(radio, $"bc:{stranger}", ct), Is.True);
            Assert.That(GetFakeLiveKit().RemoveParticipantCalls, Has.None.Matches<RoomParticipantIdentity>(c =>
                c.Room == radio && c.Identity == $"bc:{member.UserId}"), "the legitimate broadcaster stays");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_sweeper_mutes_a_key_held_past_the_cap(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party) with { maxTransmitSeconds = 10 }, ct);
        var radio = Radio(spaceId, hq);
        await OnAirAsync(member, spaceId, hq, ct);

        GetFakeLiveKit().Participants[radio] = Room(RadioParticipant(member.UserId, muted: false, trackSid: "TR_key"));

        // Ten seconds of cap plus the shortened grace, then the next sweep.
        var mute = await Poll.ForValueAsync(
            () => Task.FromResult(GetFakeLiveKit().MutePublishedTrackCalls.FirstOrDefault(m => m.Room == radio && m.Identity == $"bc:{member.UserId}")),
            m => m is not null, TimeSpan.FromSeconds(40), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(mute, Is.Not.Null);
            Assert.That(mute!.TrackSid, Is.EqualTo("TR_key"));
            Assert.That(mute.Muted, Is.True);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_sweeper_removes_a_broadcaster_who_lost_Broadcast(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var hq    = await BroadcastChannelAsync(owner, spaceId, Settings(party), ct);
        var radio = Radio(spaceId, hq);
        await OnAirAsync(member, spaceId, hq, ct);
        GetFakeLiveKit().Participants[radio] = Room(RadioParticipant(member.UserId, muted: true));

        // Two sweeps with nothing to do first, so a removal cannot be blamed on anything but the deny.
        await Task.Delay(VoiceBroadcastGrain.SweepPeriod * 2, ct);
        Assert.That(GetFakeLiveKit().RemoveParticipantCalls, Has.None.Matches<RoomParticipantIdentity>(c => c.Room == radio));

        await DenyOnChannelAsync(owner, spaceId, hq, ArgonEntitlement.Broadcast, ct);

        Assert.That(await RemovedAsync(radio, $"bc:{member.UserId}", ct), Is.True);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string Radio(Guid spaceId, Guid channelId)
        => $"radio/{spaceId}/{channelId}";

    private static BroadcastLinksError? Error(IBroadcastLinksResult result)
        => (result as FailedBroadcastLinks)?.error;

    private static BroadcastSettings Settings(params Guid[] targets)
        => new(new IonArray<Guid>(targets.ToList()), BroadcastOverlap.MIX, -8, 120, false);

    private async Task<(TestUserSession Owner, TestUserSession Member, Guid SpaceId)> SpaceWithMemberAsync(CancellationToken ct)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        await JoinAsync(owner, member, spaceId, ct);
        return (owner, member, spaceId);
    }

    private static async Task<Guid> BroadcastChannelAsync(TestUserSession owner, Guid spaceId, BroadcastSettings settings, CancellationToken ct)
    {
        var hq = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);
        await EnableBroadcastAsync(owner, spaceId, hq, settings, ct);
        return hq;
    }

    /// <summary>Mode on, then every field of <paramref name="settings"/> written.</summary>
    private static async Task EnableBroadcastAsync(TestUserSession owner, Guid spaceId, Guid hq, BroadcastSettings settings, CancellationToken ct)
    {
        var enabled = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        Assert.That(enabled, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{(enabled as FailedSetBroadcastSettings)?.error}");

        var patched = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Patch(settings), ct);
        Assert.That(patched, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{(patched as FailedSetBroadcastSettings)?.error}");
    }

    private static IonPartial<BroadcastSettings> Targets(params Guid[] targets)
        => new IonPartial<BroadcastSettings>().Modify(x => x.targets, new IonArray<Guid>(targets.ToList()));

    private static IonPartial<BroadcastSettings> Patch(BroadcastSettings s)
        => new IonPartial<BroadcastSettings>()
           .Modify(x => x.targets, s.targets)
           .Modify(x => x.overlap, s.overlap)
           .Modify(x => x.duckingDb, s.duckingDb)
           .Modify(x => x.maxTransmitSeconds, s.maxTransmitSeconds)
           .Modify(x => x.chirp, s.chirp);

    /// <summary>Replaces the everyone overwrite on the channel with a plain Broadcast allow.</summary>
    private static async Task EveryoneMayBroadcastAsync(TestUserSession owner, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var everyone = await EveryoneAsync(owner, spaceId, ct);
        await ArchetypesOf(owner).UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.Broadcast, ct);
    }

    private static async Task JoinVoiceAsync(TestUserSession user, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var result = await user.Channels.Interlink(spaceId, channelId, ct);
        Assert.That(result, Is.InstanceOf<SuccessJoinVoice>(), $"Interlink refused the voice join: {(result as FailedJoinVoice)?.error}");
    }

    /// <summary>In HQ with a confirmed radio link, the way a client that pressed nothing yet is.</summary>
    private static async Task OnAirAsync(TestUserSession user, Guid spaceId, Guid hq, CancellationToken ct)
    {
        await JoinVoiceAsync(user, spaceId, hq, ct);
        var links     = await user.Channels.GetBroadcastLinks(spaceId, hq, ct);
        var confirmed = await user.Channels.ConfirmBroadcastLinks(spaceId, hq, ct);

        Assert.That(links, Is.InstanceOf<SuccessBroadcastLinks>(), $"links refused: {Error(links)}");
        Assert.That(confirmed, Is.InstanceOf<SuccessConfirmBroadcastLinks>(), $"confirm refused: {(confirmed as FailedConfirmBroadcastLinks)?.error}");
    }

    private Task<bool> RemovedAsync(string room, string identity, CancellationToken ct)
        => Poll.ForValueAsync(
            () => Task.FromResult(GetFakeLiveKit().RemoveParticipantCalls.Any(c => c.Room == room && c.Identity == identity)),
            removed => removed, Settle, ct: ct);

    private static async Task<List<Guid>> OccupantIdsAsync(TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var channels = await reader.Servers.GetChannels(spaceId, ct);
        return channels.Values.First(c => c.channel.channelId == channelId).users.Values.Select(u => u.userId).ToList();
    }

    private static ListParticipantsResponse Room(params LkParticipant[] participants)
        => new() { Participants = { participants } };

    private static LkParticipant RadioParticipant(Guid userId, bool muted, string trackSid = "TR_mic")
        => new()
        {
            Identity = $"bc:{userId}",
            Sid      = $"PA_{userId:N}",
            Tracks   = { new TrackInfo { Sid = trackSid, Type = TrackType.Audio, Source = TrackSource.Microphone, Muted = muted } }
        };

    /// <summary>The claims of a LiveKit token.</summary>
    private static JsonElement Payload(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.Clone();
    }
}
