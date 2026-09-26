namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using static ChannelTestKit;

/// <summary>
/// Announcement channel settings: reactions on or off, post as space and show author. Who may change
/// them, how the change is announced, what reactions off refuses, and the longer text limit
/// announcement posts get.
/// </summary>
[TestFixture]
public class AnnouncementSettingsTests : TestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Quiet  = TimeSpan.FromSeconds(2);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static IonArray<IMessageEntity> NoEntities => new([]);

    private async Task<(TestUserSession Owner, TestUserSession Guest, Guid SpaceId, Guid ChannelId)> NewsAsync(CancellationToken ct)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        return (owner, guest, spaceId, channelId);
    }

    private static async Task<ArgonChannel> ChannelAsync(TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
        => (await reader.Servers.GetChannels(spaceId, ct)).Values.First(c => c.channel.channelId == channelId).channel;

    private static async Task<ArgonChannel> SetAsync(TestUserSession session, Guid spaceId, Guid channelId, bool reactions, bool postAsSpace,
        bool showAuthor, CancellationToken ct)
    {
        var result = await session.Channels.SetAnnouncementSettings(spaceId, channelId, reactions, postAsSpace, showAuthor, ct);
        Assert.That(result, Is.InstanceOf<SuccessUpdateChannel>(), $"refused: {(result as FailedUpdateChannel)?.error}");
        return ((SuccessUpdateChannel)result).channel;
    }

    private static UpdateChannelError? Error(IUpdateChannelResult result) => (result as FailedUpdateChannel)?.error;

    private static async Task<List<ReactionInfo>> ReactionsOnAsync(TestUserSession session, Guid spaceId, Guid channelId, long messageId,
        CancellationToken ct)
    {
        var entries = await session.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>([messageId]), ct);
        return entries.Values.FirstOrDefault(e => e.messageId == messageId)?.reactions.Values.ToList() ?? [];
    }

    private static async Task<RealtimeClient> WatchSpaceAsync(TestUserSession observer, Guid spaceId, CancellationToken ct)
    {
        var client = await RealtimeClient.ConnectAsync(observer, ct);
        await client.SubscribeToSpace(spaceId, ct);
        return client;
    }

    [Test, CancelAfter(120_000)]
    public async Task An_announcement_channel_carries_the_defaults_and_other_channels_carry_none(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, newsId) = await NewsAsync(ct);
        var textId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);

        var news = await ChannelAsync(guest, spaceId, newsId, ct);
        var text = await ChannelAsync(guest, spaceId, textId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(news.announcement, Is.Not.Null, "an announcement channel came without its settings");
            Assert.That(news.announcement?.reactions, Is.True);
            Assert.That(news.announcement?.postAsSpace, Is.False);
            Assert.That(news.announcement?.showAuthor, Is.True);
            Assert.That(text.announcement, Is.Null, "a text channel carried announcement settings");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Settings_are_stored_and_served_to_members(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        var returned = await SetAsync(owner, spaceId, channelId, reactions: false, postAsSpace: true, showAuthor: false, ct);
        var served   = await ChannelAsync(guest, spaceId, channelId, ct);

        await using var db = await DbAsync(ct);
        var stored = await db.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.Announcement).FirstAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(returned.announcement, Is.EqualTo(new AnnouncementSettings(false, true, false)));
            Assert.That(served.announcement, Is.EqualTo(new AnnouncementSettings(false, true, false)), "a member was served stale settings");
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.Reactions, Is.False);
            Assert.That(stored.PostAsSpace, Is.True);
            Assert.That(stored.ShowAuthor, Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Setting_needs_ManageChannels_in_the_channel_and_an_announcement_channel(CancellationToken ct = default)
    {
        var (owner, mod, spaceId, newsId) = await NewsAsync(ct);
        var textId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);

        var byMember = await mod.Channels.SetAnnouncementSettings(spaceId, newsId, false, false, true, ct);
        var onText   = await owner.Channels.SetAnnouncementSettings(spaceId, textId, false, false, true, ct);

        var archetypes = ArchetypesOf(owner);
        var keepers    = await archetypes.CreateArchetype(spaceId, "channel-keepers", ct);
        keepers = await archetypes.UpdateArchetype(spaceId, keepers with
        {
            entitlement = ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory | ArgonEntitlement.ManageChannels
        }, ct);
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, mod.UserId, ct), keepers.id, true, ct),
            Is.True);

        var byKeeper = await mod.Channels.SetAnnouncementSettings(spaceId, newsId, false, false, true, ct);

        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, newsId, keepers.id,
            deny: ArgonEntitlement.ManageChannels, allow: ArgonEntitlement.None, ct);
        var deniedHere = await mod.Channels.SetAnnouncementSettings(spaceId, newsId, true, true, true, ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(byMember), Is.EqualTo(UpdateChannelError.INSUFFICIENT_PERMISSIONS), "a plain member changed the settings");
            Assert.That(Error(onText), Is.EqualTo(UpdateChannelError.NOT_AN_ANNOUNCEMENT_CHANNEL));
            Assert.That(byKeeper, Is.InstanceOf<SuccessUpdateChannel>(), $"ManageChannels was refused: {Error(byKeeper)}");
            Assert.That(Error(deniedHere), Is.EqualTo(UpdateChannelError.INSUFFICIENT_PERMISSIONS),
                "a channel overwrite denying ManageChannels did not hold");
        });

        Assert.That((await ChannelAsync(owner, spaceId, newsId, ct)).announcement, Is.EqualTo(new AnnouncementSettings(false, false, true)));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_change_arrives_as_a_ChannelModifiedV2_patch_carrying_only_the_announcement_field(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        await using var watcher = await WatchSpaceAsync(guest, spaceId, ct);

        var mark = watcher.Mark();
        await SetAsync(owner, spaceId, channelId, reactions: true, postAsSpace: true, showAuthor: true, ct);
        var change = await watcher.WaitForAsync<ChannelModifiedV2>(
            e => e.channelId == channelId && e.patch.StateOf(nameof(ArgonChannel.announcement)) != PartialState.None, Settle, mark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(change.spaceId, Is.EqualTo(spaceId));
            Assert.That(change.patch.PresentFields(), Is.EqualTo(new[] { nameof(ArgonChannel.announcement) }));
            Assert.That(change.patch.GetField(x => x.announcement).Value, Is.EqualTo(new AnnouncementSettings(true, true, true)));
        });

        mark = watcher.Mark();
        await SetAsync(owner, spaceId, channelId, reactions: true, postAsSpace: true, showAuthor: true, ct);
        await watcher.AssertNoneWithinAsync<ChannelModifiedV2>(e => e.channelId == channelId, Quiet,
            "restating the stored settings is not a change", mark, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task Reactions_off_refuses_a_new_reaction_but_keeps_the_old_ones_and_lets_them_be_taken_back(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "Patch 1.2 is out", NoEntities, NextRandomId(), null, ct);
        Assert.That(await guest.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct), Is.InstanceOf<SuccessAddReaction>());
        Assert.That(await owner.Channels.AddReaction(spaceId, channelId, messageId, "🔥", ct), Is.InstanceOf<SuccessAddReaction>());

        await SetAsync(owner, spaceId, channelId, reactions: false, postAsSpace: false, showAuthor: true, ct);

        var fresh   = await guest.Channels.AddReaction(spaceId, channelId, messageId, "❤️", ct);
        var joined  = await guest.Channels.AddReaction(spaceId, channelId, messageId, "🔥", ct);
        var byOwner = await owner.Channels.AddReaction(spaceId, channelId, messageId, "🎉", ct);
        var visible = await ReactionsOnAsync(guest, spaceId, channelId, messageId, ct);
        var removed = await guest.Channels.RemoveReaction(spaceId, channelId, messageId, "👍", ct);
        var after   = await ReactionsOnAsync(guest, spaceId, channelId, messageId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((fresh as FailedAddReaction)?.error, Is.EqualTo(AddReactionError.REACTIONS_DISABLED));
            Assert.That((joined as FailedAddReaction)?.error, Is.EqualTo(AddReactionError.REACTIONS_DISABLED),
                "joining an existing reaction is still adding one");
            Assert.That((byOwner as FailedAddReaction)?.error, Is.EqualTo(AddReactionError.REACTIONS_DISABLED), "the owner is not exempt");
            Assert.That(visible.Select(r => r.emoji), Is.EquivalentTo(new[] { "👍", "🔥" }), "reactions from before went away");
            Assert.That(removed, Is.InstanceOf<SuccessRemoveReaction>(), "taking back a reaction was refused");
            Assert.That(after.Select(r => r.emoji), Is.EquivalentTo(new[] { "🔥" }));
        });

        await SetAsync(owner, spaceId, channelId, reactions: true, postAsSpace: false, showAuthor: true, ct);
        Assert.That(await guest.Channels.AddReaction(spaceId, channelId, messageId, "❤️", ct), Is.InstanceOf<SuccessAddReaction>(),
            "reactions stayed off after being turned back on");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_bot_is_refused_a_reaction_with_its_own_error_when_reactions_are_off(CancellationToken ct = default)
    {
        var (owner, _, spaceId, channelId) = await NewsAsync(ct);
        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "news", NoEntities, NextRandomId(), null, ct);

        var bot = await ChannelTestBot.SeedAsync(owner.UserId, ct);
        await bot.JoinAsync(spaceId);
        await SetAsync(owner, spaceId, channelId, reactions: false, postAsSpace: false, showAuthor: true, ct);

        using var refused = await bot.CallAsync(HttpMethod.Post, "/IReactions/v1/Add", new { channelId, messageId, emoji = "👍" }, ct);
        var body = await refused.Content.ReadAsStringAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That((int)refused.StatusCode, Is.EqualTo(403), body);
            Assert.That(body, Does.Contain("reactions_disabled"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_settings_count_only_while_the_channel_is_an_announcement_channel(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);
        await SetAsync(owner, spaceId, channelId, reactions: false, postAsSpace: true, showAuthor: false, ct);

        await using var watcher = await WatchSpaceAsync(guest, spaceId, ct);

        var mark    = watcher.Mark();
        var toText  = await owner.Channels.SetChannelType(spaceId, channelId, ChannelType.Text, ct);
        var cleared = await watcher.WaitForAsync<ChannelModifiedV2>(
            e => e.channelId == channelId && e.patch.StateOf(nameof(ArgonChannel.type)) != PartialState.None, Settle, mark, ct);

        var messageId = await guest.Channels.SendMessage(spaceId, channelId, "now we talk", NoEntities, NextRandomId(), null, ct);
        var reacted   = await guest.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct);

        mark = watcher.Mark();
        var toNews   = await owner.Channels.SetChannelType(spaceId, channelId, ChannelType.Announcement, ct);
        var restored = await watcher.WaitForAsync<ChannelModifiedV2>(
            e => e.channelId == channelId && e.patch.StateOf(nameof(ArgonChannel.type)) != PartialState.None, Settle, mark, ct);

        Assert.Multiple(() =>
        {
            Assert.That((toText as SuccessUpdateChannel)?.channel.announcement, Is.Null, "a text channel carried announcement settings");
            Assert.That(cleared.patch.StateOf(nameof(ArgonChannel.announcement)), Is.EqualTo(PartialState.Removed),
                "clients kept the settings of a channel that is no longer an announcement channel");
            Assert.That(reacted, Is.InstanceOf<SuccessAddReaction>(), "reactions off outlived the announcement channel");
            Assert.That((toNews as SuccessUpdateChannel)?.channel.announcement, Is.EqualTo(new AnnouncementSettings(false, true, false)),
                "converting back lost the stored settings");
            Assert.That(restored.patch.GetField(x => x.announcement).Value, Is.EqualTo(new AnnouncementSettings(false, true, false)));
        });
    }
}
