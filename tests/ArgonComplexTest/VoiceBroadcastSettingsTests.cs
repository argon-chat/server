namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using static ArgonComplexTest.Tests.ChannelTestKit;

/// <summary>
/// Broadcast ("radio") mode on a voice channel: who may switch it on or edit it, which targets are
/// accepted, what enabling writes, how a sparse patch merges into the stored settings, how the
/// change is announced, and how the space's channel topology keeps the target lists consistent.
/// The live flow (links, forwarding, revocation) is <see cref="VoiceBroadcastTests"/>.
/// </summary>
[TestFixture]
public class VoiceBroadcastSettingsTests : TestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Quiet  = TimeSpan.FromSeconds(2);

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Switching_and_editing_need_ManageChannels(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var hq    = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);
        var party = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);

        var refusedOn    = await member.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        var allowedOn    = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        var refusedPatch = await member.Channels.PatchBroadcastSettings(spaceId, hq, Targets(party), ct);
        var allowedPatch = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(party), ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(refusedOn), Is.EqualTo(SetBroadcastSettingsError.INSUFFICIENT_PERMISSIONS));
            Assert.That(allowedOn, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(allowedOn)}");
            Assert.That(Error(refusedPatch), Is.EqualTo(SetBroadcastSettingsError.INSUFFICIENT_PERMISSIONS));
            Assert.That(allowedPatch, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(allowedPatch)}");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_channel_is_created_under_the_id_the_client_chose(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = Guid.CreateVersion7();

        // The client's create dialog: mint the id, create under it, then address the channel by it at once.
        await owner.Channels.CreateChannel(spaceId, hq, new CreateChannelRequest(spaceId, "hq", ChannelType.Voice, "", null), ct).Ok();
        var enabled = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);

        Assert.That(await owner.Channels.CreateChannel(spaceId, hq,
                new CreateChannelRequest(spaceId, "again", ChannelType.Text, "", null), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)), "the id is taken");
        Assert.That(await owner.Channels.CreateChannel(spaceId, Guid.NewGuid(),
                new CreateChannelRequest(spaceId, "v4", ChannelType.Text, "", null), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)), "a v4 carries no timestamp and so no region");

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(channels.Values.Select(c => c.channel.channelId), Does.Contain(hq));
            Assert.That(enabled, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(enabled)}");
            Assert.That(channels.Values.Count(c => c.channel.name is "again" or "v4"), Is.Zero, "a refused create leaves no row");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_text_channel_cannot_broadcast(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var text    = await CreateChannelAsync(owner, spaceId, "chat", ChannelType.Text, ct);

        var mode  = await owner.Channels.SetBroadcastMode(spaceId, text, true, ct);
        var patch = await owner.Channels.PatchBroadcastSettings(spaceId, text, Ducking(-12), ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(mode), Is.EqualTo(SetBroadcastSettingsError.CHANNEL_IS_NOT_VOICE));
            Assert.That(Error(patch), Is.EqualTo(SetBroadcastSettingsError.CHANNEL_IS_NOT_VOICE));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_patch_needs_the_mode_on(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);

        var result = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Ducking(-12), ct);

        Assert.That(Error(result), Is.EqualTo(SetBroadcastSettingsError.NOT_A_BROADCAST_CHANNEL));
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Targets_are_voice_channels_of_the_space_that_do_not_broadcast_themselves(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        var spaceId    = await CreateSpaceAsync(owner, ct);
        var otherSpace = await CreateSpaceAsync(owner, ct);
        var hq         = await BroadcastChannelAsync(owner, spaceId, "hq", ct);
        var otherHq    = await BroadcastChannelAsync(owner, spaceId, "other-hq", ct);
        var party      = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        var text       = await CreateChannelAsync(owner, spaceId, "text", ChannelType.Text, ct);
        var foreign    = await CreateChannelAsync(owner, otherSpace, "foreign", ChannelType.Voice, ct);

        var textTarget    = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(text), ct);
        var foreignTarget = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(foreign), ct);
        var selfTarget    = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(hq), ct);
        var hqTarget      = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(otherHq), ct);
        var mixed         = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(party, text), ct);
        var partyTarget   = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Targets(party), ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(textTarget), Is.EqualTo(SetBroadcastSettingsError.INVALID_TARGET), "a text channel");
            Assert.That(Error(foreignTarget), Is.EqualTo(SetBroadcastSettingsError.INVALID_TARGET), "a voice channel of another space");
            Assert.That(Error(selfTarget), Is.EqualTo(SetBroadcastSettingsError.INVALID_TARGET), "the channel itself");
            Assert.That(Error(hqTarget), Is.EqualTo(SetBroadcastSettingsError.INVALID_TARGET), "another broadcast channel");
            Assert.That(Error(mixed), Is.EqualTo(SetBroadcastSettingsError.INVALID_TARGET), "one bad target refuses the whole list");
            Assert.That(partyTarget, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(partyTarget)}");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Enabling_writes_the_defaults_and_announces_the_change(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        var result   = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        var modified = await WaitForBroadcastChangeAsync(watcher, hq, mark, ct);
        var stored   = await Poll.ForValueAsync(() => ChannelAsync(owner, spaceId, hq, ct), c => c.broadcast is not null, Settle, ct: ct);

        Assert.Multiple(() =>
        {
            AssertSettings((result as SuccessSetBroadcastSettings)?.channel.broadcast, $"returned ({Error(result)})", []);
            AssertSettings(stored.broadcast, "stored", []);
            AssertSettings(modified.patch.GetField(x => x.broadcast).Value, "announced", []);
            Assert.That(modified.spaceId, Is.EqualTo(spaceId));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_patch_clamps_the_numbers_collapses_duplicate_targets_and_announces_the_change(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, "hq", ct);
        var party   = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        var result = await owner.Channels.PatchBroadcastSettings(spaceId, hq,
            Patch(new BroadcastSettings(Ids(party, party), BroadcastOverlap.LOCK, -50, 5000, true)), ct);

        var modified = await WaitForBroadcastChangeAsync(watcher, hq, mark, ct);
        var stored   = await Poll.ForValueAsync(() => ChannelAsync(owner, spaceId, hq, ct), c => c.broadcast?.chirp == true, Settle, ct: ct);

        Assert.Multiple(() =>
        {
            AssertSettings((result as SuccessSetBroadcastSettings)?.channel.broadcast, $"returned ({Error(result)})",
                [party], BroadcastOverlap.LOCK, -40, 3600, true);
            AssertSettings(stored.broadcast, "stored", [party], BroadcastOverlap.LOCK, -40, 3600, true);
            AssertSettings(modified.patch.GetField(x => x.broadcast).Value, "announced", [party], BroadcastOverlap.LOCK, -40, 3600, true);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_patch_of_one_field_leaves_the_others_as_they_are(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, "hq", ct);
        var party   = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        await PatchAsync(owner, spaceId, hq, Targets(party), ct);

        // An editor holding a copy from before the targets were set changes the ducking alone.
        var result = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Ducking(-12), ct);
        var stored = await Poll.ForValueAsync(() => ChannelAsync(owner, spaceId, hq, ct), c => c.broadcast?.duckingDb == -12, Settle, ct: ct);

        Assert.Multiple(() =>
        {
            AssertSettings((result as SuccessSetBroadcastSettings)?.channel.broadcast, $"returned ({Error(result)})", [party], duckingDb: -12);
            AssertSettings(stored.broadcast, "stored", [party], duckingDb: -12);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Clearing_a_field_returns_it_to_its_default(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, "hq", ct);
        var party   = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        await PatchAsync(owner, spaceId, hq, Patch(new BroadcastSettings(Ids(party), BroadcastOverlap.LOCK, -20, 60, true)), ct);

        var result = await owner.Channels.PatchBroadcastSettings(spaceId, hq, new IonPartial<BroadcastSettings>()
           .Remove(x => x.targets)
           .Remove(x => x.overlap)
           .Remove(x => x.duckingDb)
           .Remove(x => x.maxTransmitSeconds)
           .Remove(x => x.chirp), ct);
        var stored = await Poll.ForValueAsync(() => ChannelAsync(owner, spaceId, hq, ct), c => c.broadcast is { maxTransmitSeconds: null }, Settle, ct: ct);

        Assert.Multiple(() =>
        {
            // A cleared limit is no limit, not the 120 s the mode starts with; cleared targets are none.
            AssertSettings((result as SuccessSetBroadcastSettings)?.channel.broadcast, $"returned ({Error(result)})", [], maxTransmitSeconds: null);
            AssertSettings(stored.broadcast, "stored", [], maxTransmitSeconds: null);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_patch_that_changes_nothing_announces_nothing(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, "hq", ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        var result = await owner.Channels.PatchBroadcastSettings(spaceId, hq, Ducking(-8).Remove(x => x.targets), ct);

        Assert.That(result, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(result)}");
        await watcher.AssertNoneWithinAsync<ChannelModifiedV2>(e => e.channelId == hq, Quiet,
            "a patch that restates the stored settings is not a change", mark, ct);
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Changes_arrive_as_a_ChannelModifiedV2_patch_with_only_the_changed_fields(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, "hq", ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);

        var mark    = watcher.Mark();
        var renamed = await owner.Channels.UpdateChannel(spaceId, hq, "hq-2", null, null, null, ct);
        var rename  = await watcher.WaitForAsync<ChannelModifiedV2>(
            e => e.channelId == hq && e.patch.StateOf(nameof(ArgonChannel.name)) != PartialState.None, Settle, mark, ct);

        mark = watcher.Mark();
        await PatchAsync(owner, spaceId, hq, Ducking(-12), ct);
        var ducked = await WaitForBroadcastChangeAsync(watcher, hq, mark, ct);

        mark = watcher.Mark();
        Assert.That(await owner.Channels.SetBroadcastMode(spaceId, hq, false, ct), Is.InstanceOf<SuccessSetBroadcastSettings>());
        var off = await WaitForBroadcastChangeAsync(watcher, hq, mark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(renamed, Is.InstanceOf<SuccessUpdateChannel>(), $"{(renamed as FailedUpdateChannel)?.error}");
            Assert.That(rename.spaceId, Is.EqualTo(spaceId));
            Assert.That(rename.patch.PresentFields(), Is.EqualTo(new[] { nameof(ArgonChannel.name) }), "a rename carries the name and nothing else");
            Assert.That(rename.patch.GetField(x => x.name).Value, Is.EqualTo("hq-2"));

            Assert.That(ducked.patch.PresentFields(), Is.EqualTo(new[] { nameof(ArgonChannel.broadcast) }), "a settings patch carries the broadcast field and nothing else");
            Assert.That(ducked.patch.GetField(x => x.broadcast).Value?.duckingDb, Is.EqualTo(-12));

            Assert.That(off.patch.PresentFields(), Is.EqualTo(new[] { nameof(ArgonChannel.broadcast) }));
            Assert.That(off.patch.StateOf(nameof(ArgonChannel.broadcast)), Is.EqualTo(PartialState.Removed), "the mode going off clears the field");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Enabling_grants_Broadcast_to_everyone_and_keeps_the_existing_deny(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        var spaceId    = await CreateSpaceAsync(owner, ct);
        var hq         = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);
        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(owner, spaceId, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, hq, everyone.id,
            deny: ArgonEntitlement.Video, allow: ArgonEntitlement.None, ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        var enabled = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        await watcher.WaitForAsync<EntitlementsChanged>(e => e.spaceId == spaceId && e.userId is null, Settle, mark, ct);
        var afterEnable = await OverwritesAsync(archetypes, spaceId, hq, ct);

        var disabled      = await owner.Channels.SetBroadcastMode(spaceId, hq, false, ct);
        var disabledAgain = await owner.Channels.SetBroadcastMode(spaceId, hq, false, ct);
        var afterDisable  = await OverwritesAsync(archetypes, spaceId, hq, ct);
        var channel       = await Poll.ForValueAsync(() => ChannelAsync(owner, spaceId, hq, ct), c => c.broadcast is null, Settle, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(enabled, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(enabled)}");
            Assert.That(afterEnable.Select(o => (o.archetypeId, o.allow, o.deny)),
                Is.EqualTo(new[] { ((Guid?)everyone.id, ArgonEntitlement.Broadcast, ArgonEntitlement.Video) }),
                "merged into the existing overwrite, keeping its deny");
            Assert.That(disabled, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(disabled)}");
            Assert.That((disabled as SuccessSetBroadcastSettings)?.channel.broadcast, Is.Null);
            Assert.That(disabledAgain, Is.InstanceOf<SuccessSetBroadcastSettings>(), "off twice is still off");
            Assert.That(channel.broadcast, Is.Null);
            Assert.That(afterDisable.Select(o => o.allow), Is.EqualTo(new[] { ArgonEntitlement.Broadcast }),
                "switching the mode off leaves the overwrite alone");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Enabling_twice_grants_once_and_changes_nothing_the_second_time(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        var spaceId    = await CreateSpaceAsync(owner, ct);
        var hq         = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);
        var archetypes = ArchetypesOf(owner);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var first = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        await watcher.WaitForAsync<EntitlementsChanged>(e => e.spaceId == spaceId && e.userId is null, Settle, 0, ct);
        await WaitForBroadcastChangeAsync(watcher, hq, 0, ct);

        var mark       = watcher.Mark();
        var second     = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        var overwrites = await OverwritesAsync(archetypes, spaceId, hq, ct);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(first)}");
            Assert.That(second, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(second)}");
            Assert.That((second as SuccessSetBroadcastSettings)?.channel.broadcast, Is.Not.Null);
            Assert.That(overwrites.Select(o => o.allow), Is.EqualTo(new[] { ArgonEntitlement.Broadcast }), "one grant, not two");
        });
        await watcher.AssertNoneWithinAsync<EntitlementsChanged>(e => e.spaceId == spaceId, Quiet, "the mode was already on", mark, ct);
        await watcher.AssertNoneWithinAsync<ChannelModifiedV2>(e => e.channelId == hq, Quiet, "the mode was already on", mark, ct);
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task An_overwrite_that_already_mentions_Broadcast_is_left_alone(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        var spaceId    = await CreateSpaceAsync(owner, ct);
        var hq         = await CreateChannelAsync(owner, spaceId, "hq", ChannelType.Voice, ct);
        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(owner, spaceId, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, hq, everyone.id,
            deny: ArgonEntitlement.Broadcast, allow: ArgonEntitlement.None, ct);

        var enabled    = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        var overwrites = await OverwritesAsync(archetypes, spaceId, hq, ct);

        Assert.Multiple(() =>
        {
            Assert.That(enabled, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(enabled)}");
            Assert.That(overwrites.Select(o => (o.archetypeId, o.allow, o.deny)),
                Is.EqualTo(new[] { ((Guid?)everyone.id, ArgonEntitlement.None, ArgonEntitlement.Broadcast) }),
                "an admin who already decided about Broadcast is not overruled");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Deleting_a_target_drops_it_from_the_broadcast_channel(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, "hq", ct);
        var alpha   = await CreateChannelAsync(owner, spaceId, "alpha", ChannelType.Voice, ct);
        var bravo   = await CreateChannelAsync(owner, spaceId, "bravo", ChannelType.Voice, ct);
        await PatchAsync(owner, spaceId, hq, Targets(alpha, bravo), ct);

        await using var watcher = await WatchSpaceAsync(owner, spaceId, ct);
        var mark = watcher.Mark();

        await owner.Channels.DeleteChannel(spaceId, alpha, ct).Ok();

        // The space grain tells the channel grain one-way, so the change lands after the delete has returned.
        var modified = await WaitForBroadcastChangeAsync(watcher, hq, mark, ct);
        var channel  = await Poll.ForValueAsync(() => ChannelAsync(owner, spaceId, hq, ct),
            c => c.broadcast is { } b && b.targets.Values.Count() == 1, Settle, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(modified.patch.GetField(x => x.broadcast).Value?.targets.Values, Is.EqualTo(new[] { bravo }));
            Assert.That(channel.broadcast?.targets.Values, Is.EqualTo(new[] { bravo }));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Duplicating_a_broadcast_channel_copies_its_settings(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var hq      = await BroadcastChannelAsync(owner, spaceId, "hq", ct);
        var party   = await CreateChannelAsync(owner, spaceId, "party", ChannelType.Voice, ct);
        await PatchAsync(owner, spaceId, hq, Patch(new BroadcastSettings(Ids(party), BroadcastOverlap.LOCK, -12, null, true)), ct);

        var copied = await owner.Channels.DuplicateChannel(spaceId, hq, ct);
        var copy   = (copied as SuccessDuplicateChannel)?.channel;

        Assert.Multiple(() =>
        {
            Assert.That(copy, Is.Not.Null, $"{(copied as FailedDuplicateChannel)?.error}");
            Assert.That(copy?.channelId, Is.Not.EqualTo(hq));
            AssertSettings(copy?.broadcast, "copy", [party], BroadcastOverlap.LOCK, -12, null, true);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static SetBroadcastSettingsError? Error(ISetBroadcastSettingsResult result)
        => (result as FailedSetBroadcastSettings)?.error;

    private static IonArray<Guid> Ids(params Guid[] ids)
        => new(ids.ToList());

    private static IonPartial<BroadcastSettings> Targets(params Guid[] targets)
        => new IonPartial<BroadcastSettings>().Modify(x => x.targets, Ids(targets));

    private static IonPartial<BroadcastSettings> Ducking(int db)
        => new IonPartial<BroadcastSettings>().Modify(x => x.duckingDb, db);

    /// <summary>Every field modified: the patch that behaves like a whole-object write.</summary>
    private static IonPartial<BroadcastSettings> Patch(BroadcastSettings s)
        => new IonPartial<BroadcastSettings>()
           .Modify(x => x.targets, s.targets)
           .Modify(x => x.overlap, s.overlap)
           .Modify(x => x.duckingDb, s.duckingDb)
           .Modify(x => x.maxTransmitSeconds, s.maxTransmitSeconds)
           .Modify(x => x.chirp, s.chirp);

    private static void AssertSettings(BroadcastSettings? actual, string what, Guid[] targets,
        BroadcastOverlap overlap = BroadcastOverlap.MIX, int duckingDb = -8, int? maxTransmitSeconds = 120, bool chirp = false)
    {
        if (actual is null)
        {
            Assert.Fail($"{what}: no broadcast settings");
            return;
        }

        Assert.That(actual.targets.Values, Is.EqualTo(targets), $"{what}: targets");
        Assert.That(actual.overlap, Is.EqualTo(overlap), $"{what}: overlap");
        Assert.That(actual.duckingDb, Is.EqualTo(duckingDb), $"{what}: duckingDb");
        Assert.That(actual.maxTransmitSeconds, Is.EqualTo(maxTransmitSeconds), $"{what}: maxTransmitSeconds");
        Assert.That(actual.chirp, Is.EqualTo(chirp), $"{what}: chirp");
    }

    private async Task<(TestUserSession Owner, TestUserSession Member, Guid SpaceId)> SpaceWithMemberAsync(CancellationToken ct)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        await JoinAsync(owner, member, spaceId, ct);
        return (owner, member, spaceId);
    }

    /// <summary>A voice channel with the mode on and the default settings.</summary>
    private static async Task<Guid> BroadcastChannelAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
    {
        var hq     = await CreateChannelAsync(owner, spaceId, name, ChannelType.Voice, ct);
        var result = await owner.Channels.SetBroadcastMode(spaceId, hq, true, ct);
        Assert.That(result, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(result)}");
        return hq;
    }

    private static async Task PatchAsync(TestUserSession owner, Guid spaceId, Guid hq, IonPartial<BroadcastSettings> patch, CancellationToken ct)
    {
        var result = await owner.Channels.PatchBroadcastSettings(spaceId, hq, patch, ct);
        Assert.That(result, Is.InstanceOf<SuccessSetBroadcastSettings>(), $"{Error(result)}");
    }

    /// <summary>The next <see cref="ChannelModifiedV2"/> for the channel whose patch touches <c>broadcast</c>.</summary>
    private static Task<ChannelModifiedV2> WaitForBroadcastChangeAsync(RealtimeClient watcher, Guid channelId, int mark, CancellationToken ct)
        => watcher.WaitForAsync<ChannelModifiedV2>(
            e => e.channelId == channelId && e.patch.StateOf(nameof(ArgonChannel.broadcast)) != PartialState.None, Settle, mark, ct);

    private static async Task<RealtimeClient> WatchSpaceAsync(TestUserSession observer, Guid spaceId, CancellationToken ct)
    {
        var client = await RealtimeClient.ConnectAsync(observer, ct);
        await client.SubscribeToSpace(spaceId, ct);
        return client;
    }

    private static async Task<ArgonChannel> ChannelAsync(TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
        => (await reader.Servers.GetChannels(spaceId, ct)).Values.First(c => c.channel.channelId == channelId).channel;

    private static async Task<List<ChannelEntitlementOverwrite>> OverwritesAsync(IArchetypeInteraction archetypes, Guid spaceId,
        Guid channelId, CancellationToken ct)
        => (await archetypes.GetChannelEntitlementOverwrites(spaceId, channelId, ct)).Values.ToList();
}
