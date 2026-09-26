namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using static ChannelTestKit;

/// <summary>
/// Announcement channels: everyone reads and reacts, only the roles the owner lets through post, bots
/// do not post at all, an author edits their own post, and @everyone pings a limited number of times
/// an hour.
/// </summary>
[TestFixture]
public class AnnouncementChannelTests : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static IonArray<IMessageEntity> Entities(params IMessageEntity[] entities) => new(entities);

    private static MessageEntityMentionEveryone Everyone() => new(EntityType.MentionEveryone, 0, 9, 1);

    private static MessageEntityMention Mention(Guid userId) => new(EntityType.Mention, 0, 5, 1, userId);

    private static MessageEntityBold Bold(int offset, int length) => new(EntityType.Bold, offset, length, 1);

    private static MessageEntityAttachment Attachment(Guid fileId)
        => new(EntityType.Attachment, 0, 0, 1, fileId, "photo.png", 1024, "image/png", 64, 64, null, null);

    private async Task<(TestUserSession Owner, TestUserSession Guest, Guid SpaceId, Guid ChannelId)> NewsAsync(CancellationToken ct)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        return (owner, guest, spaceId, channelId);
    }

    private static async Task<Guid> RoleWithPostingAsync(TestUserSession owner, Guid spaceId, Guid channelId, Guid memberUserId,
        string name, CancellationToken ct)
    {
        var archetypes = ArchetypesOf(owner);
        var role       = await archetypes.CreateArchetype(spaceId, name, ct).Ok();

        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, memberUserId, ct), role.id, true, ct),
            Is.True);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, role.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.SendMessages, ct);

        return role.id;
    }

    [Test, CancelAfter(120_000)]
    public async Task Everyone_reads_and_reacts_but_only_the_owner_posts_in_a_new_announcement_channel(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        var everyone   = await EveryoneAsync(owner, spaceId, ct);
        var overwrites = await ArchetypesOf(owner).GetChannelEntitlementOverwrites(spaceId, channelId, ct);
        var forAll     = overwrites.Values.SingleOrDefault(o => o.archetypeId == everyone.id);

        Assert.That(forAll, Is.Not.Null, "a new announcement channel came without an overwrite for everyone");
        Assert.Multiple(() =>
        {
            Assert.That(forAll!.deny, Is.EqualTo(ArgonEntitlement.SendMessages), "everyone lost more than posting");
            Assert.That(forAll.allow, Is.EqualTo(ArgonEntitlement.None));
        });

        var posted = await owner.Channels.SendMessage(spaceId, channelId, "Raid tonight", Entities(), NextRandomId(), null, ct).Ok();

        Assert.That(await guest.Channels.SendMessage(spaceId, channelId, "me too", Entities(), NextRandomId(), null, ct),
            Is.EqualTo(new FailedSendMessage(SendMessageError.NO_PERMISSION)), "a plain member posted in an announcement channel");

        var read = await guest.Channels.QueryMessages(spaceId, channelId, null, 10, ct);
        Assert.That(read.Values.Select(m => m.messageId), Is.EqualTo(new[] { posted }), "a member could not read the announcement");

        Assert.That(await guest.Channels.AddReaction(spaceId, channelId, posted, "👍", ct), Is.InstanceOf<SuccessAddReaction>(),
            "a reader could not react to an announcement");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_text_channel_gets_no_overwrite_and_stays_open(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var overwrites = await ArchetypesOf(owner).GetChannelEntitlementOverwrites(spaceId, channelId, ct);

        Assert.That(overwrites.Values, Is.Empty);
        Assert.That(await guest.Channels.SendMessage(spaceId, channelId, "hi", Entities(), NextRandomId(), null, ct),
            Is.InstanceOf<SuccessSendMessage>());
    }

    [Test, CancelAfter(120_000)]
    public async Task A_role_the_owner_lets_through_posts(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        await RoleWithPostingAsync(owner, spaceId, channelId, guest.UserId, "herald", ct);

        Assert.That(await guest.Channels.SendMessage(spaceId, channelId, "patch notes", Entities(), NextRandomId(), null, ct),
            Is.InstanceOf<SuccessSendMessage>(), "a role allowed to post on the channel was still refused");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_bot_does_not_post_even_with_a_role_that_may(CancellationToken ct = default)
    {
        var (owner, _, spaceId, channelId) = await NewsAsync(ct);
        var textId = await CreateChannelAsync(owner, spaceId, "bots", ChannelType.Text, ct);

        var bot = await ChannelTestBot.SeedAsync(owner.UserId, ct);
        await bot.JoinAsync(spaceId);
        await RoleWithPostingAsync(owner, spaceId, channelId, bot.UserId, "bot-role", ct);

        // Premise: the same bot posts fine where posting is open.
        await bot.SendAsync(spaceId, textId, "hello", ct: ct);

        using var refused = await bot.CallAsync(HttpMethod.Post, "/IMessages/v1/Send", new
        {
            spaceId,
            channelId,
            text     = "breaking news",
            randomId = Random.Shared.NextInt64(1, long.MaxValue)
        }, ct);

        Assert.That(refused.IsSuccessStatusCode, Is.False, "a bot posted in an announcement channel");

        var read = await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct);
        Assert.That(read.Values, Is.Empty);
    }

    [Test, CancelAfter(120_000)]
    public async Task The_author_edits_an_announcement_and_readers_get_the_new_text(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        await using var observer = await RealtimeClient.ConnectAsync(guest, ct);
        await observer.SubscribeToChannel(channelId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "Raid at 20:00", Entities(), NextRandomId(), null, ct).Ok();

        var result = await owner.Channels.EditMessage(spaceId, channelId, messageId, "Raid at 21:00", Entities(Bold(8, 5)), ct);

        Assert.That(result, Is.InstanceOf<SuccessEditMessage>(), $"the edit was refused: {(result as FailedEditMessage)?.error}");
        var edited = ((SuccessEditMessage)result).message;

        var update = await observer.WaitForAsync<MessageUpdated>(
            e => e.message.messageId == messageId && e.message.text == "Raid at 21:00", Window, ct: ct);
        var stored = await StoredMessageAsync(spaceId, channelId, messageId, ct);
        var read   = (await guest.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values.Single(m => m.messageId == messageId);

        Assert.Multiple(() =>
        {
            Assert.That(edited.editedAt, Is.Not.Null, "the edited message came back without editedAt");
            Assert.That(update.message.editedAt, Is.Not.Null, "readers were not told the message was edited");
            Assert.That(update.message.entities.Values.OfType<MessageEntityBold>().Count(), Is.EqualTo(1));
            Assert.That(stored!.Text, Is.EqualTo("Raid at 21:00"));
            Assert.That(stored.EditedAt, Is.Not.Null);
            Assert.That(read.text, Is.EqualTo("Raid at 21:00"));
            Assert.That(read.editedAt, Is.Not.Null, "history lost the edited mark");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_the_author_edits_and_an_edit_needs_text_within_the_limit(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var guestMessage = await guest.Channels.SendMessage(spaceId, channelId, "mine", Entities(), NextRandomId(), null, ct).Ok();
        var deleted      = await guest.Channels.SendMessage(spaceId, channelId, "gone", Entities(), NextRandomId(), null, ct).Ok();
        await guest.Channels.DeleteMessage(spaceId, channelId, deleted, ct);

        var byOwner  = await owner.Channels.EditMessage(spaceId, channelId, guestMessage, "not yours", Entities(), ct);
        var empty    = await guest.Channels.EditMessage(spaceId, channelId, guestMessage, "  ", Entities(), ct);
        var tooLong  = await guest.Channels.EditMessage(spaceId, channelId, guestMessage, new string('a', 4097), Entities(), ct);
        var unknown  = await guest.Channels.EditMessage(spaceId, channelId, guestMessage + 1_000_000, "x", Entities(), ct);
        var ofDelete = await guest.Channels.EditMessage(spaceId, channelId, deleted, "back", Entities(), ct);

        Assert.Multiple(() =>
        {
            Assert.That((byOwner as FailedEditMessage)?.error, Is.EqualTo(EditMessageError.NOT_AUTHOR), "the space owner rewrote a member's message");
            Assert.That((empty as FailedEditMessage)?.error, Is.EqualTo(EditMessageError.EMPTY_MESSAGE));
            Assert.That((tooLong as FailedEditMessage)?.error, Is.EqualTo(EditMessageError.MESSAGE_TOO_LONG));
            Assert.That((unknown as FailedEditMessage)?.error, Is.EqualTo(EditMessageError.MESSAGE_NOT_FOUND));
            Assert.That((ofDelete as FailedEditMessage)?.error, Is.EqualTo(EditMessageError.MESSAGE_NOT_FOUND), "a deleted message was edited");
        });

        Assert.That((await StoredMessageAsync(spaceId, channelId, guestMessage, ct))!.Text, Is.EqualTo("mine"));
    }

    [Test, CancelAfter(120_000)]
    public async Task An_edit_keeps_the_attachments_and_takes_none_from_the_client(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "files", ChannelType.Text, ct);

        var original  = Guid.NewGuid();
        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "photo", Entities(Attachment(original)), NextRandomId(), null, ct).Ok();

        var result = await owner.Channels.EditMessage(spaceId, channelId, messageId, "",
            Entities(Attachment(Guid.NewGuid())), ct);

        Assert.That(result, Is.InstanceOf<SuccessEditMessage>(),
            $"clearing the caption of a message with a file was refused: {(result as FailedEditMessage)?.error}");

        var stored = await StoredMessageAsync(spaceId, channelId, messageId, ct);
        Assert.That(stored!.Entities.OfType<MessageEntityAttachment>().Select(a => a.fileId), Is.EqualTo(new[] { original }),
            "the edit swapped or added an attachment");
    }

    [Test, CancelAfter(120_000)]
    public async Task An_edit_that_adds_everyone_pings_nobody(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "hello", Entities(), NextRandomId(), null, ct).Ok();
        Assert.That(await owner.Channels.EditMessage(spaceId, channelId, messageId, "@everyone hello", Entities(Everyone()), ct),
            Is.InstanceOf<SuccessEditMessage>());

        // The edit fans nothing out, so one direct mention after it must leave the count at exactly one.
        await owner.Channels.SendMessage(spaceId, channelId, "@guest", Entities(Mention(guest.UserId)), NextRandomId(), null, ct).Ok();

        Assert.That(await PollAsync(() => MentionsAsync(guest, channelId, ct), n => n >= 1, Window, ct), Is.EqualTo(1),
            "editing @everyone into a message pinged the space");
    }

    /// <summary>
    /// The limit is per channel and counts deleted posts, so neither several authors nor deleting a
    /// ping buys another one. Every post still goes out; only the ping is held back.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Everyone_pings_readers_of_an_announcement_channel_at_most_three_times_an_hour(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        await using var observer = await RealtimeClient.ConnectAsync(guest, ct);

        bool IsPing(BatchMentionOccurred e) => e.channelId == channelId && e.mentionType == MentionTargetType.Everyone;

        var sent = new List<long>();
        for (var i = 1; i <= 4; i++)
            sent.Add(await owner.Channels.SendMessage(spaceId, channelId, $"@everyone {i}", Entities(Everyone()), NextRandomId(), null, ct).Ok());

        await PollAsync(() => Task.FromResult(observer.EventsOfType<BatchMentionOccurred>().Count(IsPing)), n => n >= 3, Window, ct);
        var mark = observer.Mark();

        await owner.Channels.DeleteMessage(spaceId, channelId, sent[0], ct);
        await owner.Channels.SendMessage(spaceId, channelId, "@everyone 5", Entities(Everyone()), NextRandomId(), null, ct).Ok();

        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(IsPing, TimeSpan.FromSeconds(3),
            "@everyone past the hourly limit pinged the channel's readers", from: mark, ct: ct);

        var read = await guest.Channels.QueryMessages(spaceId, channelId, null, 10, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(observer.EventsOfType<BatchMentionOccurred>().Count(IsPing), Is.EqualTo(3));
            Assert.That(await MentionsAsync(guest, channelId, ct), Is.EqualTo(3));
            Assert.That(read.Values.Count, Is.EqualTo(4), "a post past the limit was not delivered");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_text_channel_becomes_an_announcement_channel_and_back_keeping_its_history(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        Assert.That(await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 30, null, ct), Is.InstanceOf<SuccessUpdateChannel>());
        await guest.Channels.SendMessage(spaceId, channelId, "before", Entities(), NextRandomId(), null, ct).Ok();

        await using var observer = await RealtimeClient.ConnectAsync(guest, ct);

        var toNews = await owner.Channels.SetChannelType(spaceId, channelId, ChannelType.Announcement, ct);
        Assert.That(toNews, Is.InstanceOf<SuccessUpdateChannel>(), $"refused: {(toNews as FailedUpdateChannel)?.error}");

        var news     = ((SuccessUpdateChannel)toNews).channel;
        var everyone = await EveryoneAsync(owner, spaceId, ct);
        var forAll   = (await ArchetypesOf(owner).GetChannelEntitlementOverwrites(spaceId, channelId, ct)).Values
           .SingleOrDefault(o => o.archetypeId == everyone.id);

        Assert.Multiple(() =>
        {
            Assert.That(news.type, Is.EqualTo(ChannelType.Announcement));
            Assert.That(news.slowModeSeconds, Is.Null, "slow mode stayed on an announcement channel");
            Assert.That(forAll?.deny, Is.EqualTo(ArgonEntitlement.SendMessages));
        });

        await observer.WaitForAsync<EntitlementsChanged>(e => e.spaceId == spaceId, Window, ct: ct);

        // The guest posted a moment ago, so a refusal now also proves the cached permission was dropped.
        Assert.That(await guest.Channels.SendMessage(spaceId, channelId, "during", Entities(), NextRandomId(), null, ct),
            Is.EqualTo(new FailedSendMessage(SendMessageError.NO_PERMISSION)), "a plain member still posted after the channel became an announcement channel");

        var toText = await owner.Channels.SetChannelType(spaceId, channelId, ChannelType.Text, ct);
        Assert.That(toText, Is.InstanceOf<SuccessUpdateChannel>(), $"refused: {(toText as FailedUpdateChannel)?.error}");
        Assert.That(((SuccessUpdateChannel)toText).channel.type, Is.EqualTo(ChannelType.Text));

        Assert.That((await ArchetypesOf(owner).GetChannelEntitlementOverwrites(spaceId, channelId, ct)).Values, Is.Empty,
            "the deny for everyone outlived the announcement channel");
        Assert.That(await guest.Channels.SendMessage(spaceId, channelId, "after", Entities(), NextRandomId(), null, ct),
            Is.InstanceOf<SuccessSendMessage>());

        var history = await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct);
        Assert.That(history.Values.Select(m => m.text), Is.EquivalentTo(new[] { "before", "after" }));
    }

    [Test, CancelAfter(120_000)]
    public async Task Converting_touches_only_SendMessages_on_the_everyone_overwrite(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.AttachFiles, ct);

        var everyone = await EveryoneAsync(owner, spaceId, ct);
        async Task<ArgonEntitlement?> DenyAsync()
            => (await ArchetypesOf(owner).GetChannelEntitlementOverwrites(spaceId, channelId, ct)).Values
               .SingleOrDefault(o => o.archetypeId == everyone.id)?.deny;

        await owner.Channels.SetChannelType(spaceId, channelId, ChannelType.Announcement, ct);
        Assert.That(await DenyAsync(), Is.EqualTo(ArgonEntitlement.AttachFiles | ArgonEntitlement.SendMessages));

        await owner.Channels.SetChannelType(spaceId, channelId, ChannelType.Text, ct);
        Assert.That(await DenyAsync(), Is.EqualTo(ArgonEntitlement.AttachFiles), "converting back dropped a deny it never added");
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_text_and_announcement_channels_convert_and_only_for_who_may_edit_overwrites(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var mod   = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var textId    = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        var voiceId   = await CreateChannelAsync(owner, spaceId, "lobby", ChannelType.Voice, ct);
        await JoinAsync(owner, mod, spaceId, ct);

        var archetypes = ArchetypesOf(owner);
        var channels   = await archetypes.CreateArchetype(spaceId, "channel-keepers", ct).Ok();
        channels = await archetypes.UpdateArchetype(spaceId, channels with
        {
            entitlement = ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory | ArgonEntitlement.SendMessages | ArgonEntitlement.ManageChannels
        }, ct).Ok();
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, mod.UserId, ct), channels.id, true, ct),
            Is.True);

        var fromVoice = await owner.Channels.SetChannelType(spaceId, voiceId, ChannelType.Text, ct);
        var toVoice   = await owner.Channels.SetChannelType(spaceId, textId, ChannelType.Voice, ct);
        var byMod     = await mod.Channels.SetChannelType(spaceId, textId, ChannelType.Announcement, ct);

        Assert.Multiple(() =>
        {
            Assert.That((fromVoice as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.TYPE_NOT_CONVERTIBLE));
            Assert.That((toVoice as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.TYPE_NOT_CONVERTIBLE));
            Assert.That((byMod as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.INSUFFICIENT_PERMISSIONS),
                "ManageChannels alone rewrote who may post");
        });

        await archetypes.UpdateArchetype(spaceId, channels with { entitlement = channels.entitlement | ArgonEntitlement.ManageArchetype }, ct).Ok();

        Assert.That(await mod.Channels.SetChannelType(spaceId, textId, ChannelType.Announcement, ct), Is.InstanceOf<SuccessUpdateChannel>());
    }
}
