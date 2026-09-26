namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using static ChannelTestKit;

/// <summary>
/// Pinned messages: moderators who read the history pin and unpin, everyone who reads the channel
/// sees the pins newest first, a channel holds at most fifty live ones, and a deleted message takes
/// its pin with it.
/// </summary>
[TestFixture]
public class ChannelPinTests : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static IonArray<IMessageEntity> Entities(params IMessageEntity[] entities) => new(entities);

    private static IChannelPinsInteraction PinsOf(TestUserSession session)
        => session.Client.ForService<IChannelPinsInteraction>(Services);

    private async Task<(TestUserSession Owner, TestUserSession Guest, Guid SpaceId, Guid ChannelId)> RoomAsync(ChannelType kind,
        CancellationToken ct)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, kind == ChannelType.Announcement ? "news" : "general", kind, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        return (owner, guest, spaceId, channelId);
    }

    private Task<long> SendAsync(TestUserSession author, Guid spaceId, Guid channelId, string text, CancellationToken ct)
        => author.Channels.SendMessage(spaceId, channelId, text, Entities(), NextRandomId(), null, ct).Ok();

    private static async Task<List<PinnedMessage>> PinsAsync(TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
        => (await PinsOf(reader).GetPinnedMessages(spaceId, channelId, ct)).Values.ToList();

    private static async Task<int> StoredPinsAsync(Guid channelId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.ChannelPins.CountAsync(p => p.ChannelId == channelId, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task Pins_list_newest_first_and_unpin_removes_one(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);

        var first  = await SendAsync(guest, spaceId, channelId, "first", ct);
        var second = await SendAsync(owner, spaceId, channelId, "second", ct);

        var pinnedFirst = await PinsOf(owner).PinMessage(spaceId, channelId, first, ct);
        Assert.That(pinnedFirst, Is.InstanceOf<SuccessPinMessage>(), $"the pin was refused: {(pinnedFirst as FailedPinMessage)?.error}");

        await Task.Delay(20, ct);
        Assert.That(await PinsOf(owner).PinMessage(spaceId, channelId, second, ct), Is.InstanceOf<SuccessPinMessage>());

        var pins = await PinsAsync(guest, spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(pins.Select(p => p.message.messageId), Is.EqualTo(new[] { second, first }), "pins are not newest first");
            Assert.That(pins.Select(p => p.message.text), Is.EqualTo(new[] { "second", "first" }));
            Assert.That(pins.All(p => p.pinnedBy == owner.UserId), Is.True, "pinnedBy is not the pinner");
            Assert.That(((SuccessPinMessage)pinnedFirst).pin.message.messageId, Is.EqualTo(first));
            Assert.That(((SuccessPinMessage)pinnedFirst).pin.pinnedBy, Is.EqualTo(owner.UserId));
        });

        Assert.That(await PinsOf(owner).UnpinMessage(spaceId, channelId, first, ct), Is.InstanceOf<SuccessUnpinMessage>());

        Assert.That((await PinsAsync(guest, spaceId, channelId, ct)).Select(p => p.message.messageId), Is.EqualTo(new[] { second }));
    }

    [Test, CancelAfter(120_000)]
    public async Task Pinning_twice_and_unpinning_what_is_not_pinned_both_succeed_without_side_effects(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var messageId = await SendAsync(owner, spaceId, channelId, "keep", ct);
        var loose     = await SendAsync(owner, spaceId, channelId, "loose", ct);

        await using var observer = await RealtimeClient.ConnectAsync(guest, ct);
        await observer.SubscribeToChannel(channelId, ct);

        var once = await PinsOf(owner).PinMessage(spaceId, channelId, messageId, ct);
        await observer.WaitForAsync<MessagePinned>(e => e.messageId == messageId, Window, ct: ct);
        var mark = observer.Mark();

        var twice = await PinsOf(owner).PinMessage(spaceId, channelId, messageId, ct);
        var unpin = await PinsOf(owner).UnpinMessage(spaceId, channelId, loose, ct);

        Assert.Multiple(() =>
        {
            Assert.That(twice, Is.InstanceOf<SuccessPinMessage>(), "pinning a pinned message failed");
            Assert.That(((SuccessPinMessage)twice).pin.pinnedAt, Is.EqualTo(((SuccessPinMessage)once).pin.pinnedAt),
                "a second pin moved the first one");
            Assert.That(unpin, Is.InstanceOf<SuccessUnpinMessage>());
        });

        await observer.AssertNoneWithinAsync<MessagePinned>(e => e.messageId == messageId, TimeSpan.FromSeconds(1),
            "a repeated pin was announced again", from: mark, ct: ct);
        await observer.AssertNoneWithinAsync<MessageUnpinned>(e => e.channelId == channelId, TimeSpan.FromSeconds(1),
            "unpinning a message that was not pinned was announced", ct: ct);

        Assert.That(await StoredPinsAsync(channelId, ct), Is.EqualTo(1));
    }

    [Test, CancelAfter(120_000)]
    public async Task Pinning_needs_ManageMessages(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var own    = await SendAsync(guest, spaceId, channelId, "my own", ct);
        var pinned = await SendAsync(owner, spaceId, channelId, "rules", ct);
        await PinsOf(owner).PinMessage(spaceId, channelId, pinned, ct);

        var pin   = await PinsOf(guest).PinMessage(spaceId, channelId, own, ct);
        var unpin = await PinsOf(guest).UnpinMessage(spaceId, channelId, pinned, ct);

        Assert.Multiple(() =>
        {
            Assert.That((pin as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.INSUFFICIENT_PERMISSIONS),
                "a plain member pinned their own message");
            Assert.That((unpin as FailedUnpinMessage)?.error, Is.EqualTo(PinMessageError.INSUFFICIENT_PERMISSIONS),
                "a plain member unpinned a message");
        });
        Assert.That((await PinsAsync(owner, spaceId, channelId, ct)).Select(p => p.message.messageId), Is.EqualTo(new[] { pinned }));

        var archetypes = ArchetypesOf(owner);
        var role       = await archetypes.CreateArchetype(spaceId, "pinner", ct).Ok();
        await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, guest.UserId, ct), role.id, true, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, role.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.ManageMessages, ct);

        var granted = await PollAsync(() => PinsOf(guest).PinMessage(spaceId, channelId, own, ct),
            r => r is SuccessPinMessage, TimeSpan.FromSeconds(10), ct);

        Assert.That(granted, Is.InstanceOf<SuccessPinMessage>(), "ManageMessages on the channel did not let the member pin");
        Assert.That(await PinsOf(guest).UnpinMessage(spaceId, channelId, pinned, ct), Is.InstanceOf<SuccessUnpinMessage>());
    }

    [Test, CancelAfter(180_000)]
    public async Task A_channel_holds_at_most_fifty_pins(CancellationToken ct = default)
    {
        var (owner, _, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);

        var ids = new List<long>();
        for (var i = 0; i <= ChannelGrain.PinLimit; i++)
            ids.Add(await SendAsync(owner, spaceId, channelId, $"m{i}", ct));

        foreach (var id in ids.Take(ChannelGrain.PinLimit))
            Assert.That(await PinsOf(owner).PinMessage(spaceId, channelId, id, ct), Is.InstanceOf<SuccessPinMessage>());

        var over   = await PinsOf(owner).PinMessage(spaceId, channelId, ids[^1], ct);
        var repeat = await PinsOf(owner).PinMessage(spaceId, channelId, ids[0], ct);

        Assert.Multiple(() =>
        {
            Assert.That((over as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.PIN_LIMIT_REACHED));
            Assert.That(repeat, Is.InstanceOf<SuccessPinMessage>(), "an already pinned message was refused at the limit");
        });
        Assert.That(await PinsAsync(owner, spaceId, channelId, ct), Has.Count.EqualTo(ChannelGrain.PinLimit));

        await PinsOf(owner).UnpinMessage(spaceId, channelId, ids[0], ct);

        Assert.That(await PinsOf(owner).PinMessage(spaceId, channelId, ids[^1], ct), Is.InstanceOf<SuccessPinMessage>(),
            "a freed slot could not be used");
    }

    [Test, CancelAfter(120_000)]
    public async Task Deleting_a_pinned_message_removes_its_pin(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var byAuthor    = await SendAsync(guest, spaceId, channelId, "oops", ct);
        var byOperator  = await SendAsync(guest, spaceId, channelId, "reported", ct);
        var stays       = await SendAsync(owner, spaceId, channelId, "stays", ct);

        foreach (var id in new[] { byAuthor, byOperator, stays })
            await PinsOf(owner).PinMessage(spaceId, channelId, id, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(channelId, ct);

        Assert.That(await guest.Channels.DeleteMessage(spaceId, channelId, byAuthor, ct), Is.InstanceOf<SuccessDeleteMessage>());
        Assert.That(await Grains.GetGrain<IChannelGrain>(channelId).DeleteMessageByModeration(byOperator, Guid.NewGuid(), ct), Is.True);

        var authorUnpin   = await observer.WaitForAsync<MessageUnpinned>(e => e.messageId == byAuthor, Window, ct: ct);
        var operatorUnpin = await observer.WaitForAsync<MessageUnpinned>(e => e.messageId == byOperator, Window, ct: ct);

        Assert.Multiple(async () =>
        {
            Assert.That(authorUnpin.byUserId, Is.EqualTo(guest.UserId), "the unpin is not attributed to whoever deleted");
            Assert.That(operatorUnpin.byUserId, Is.EqualTo(UserEntity.SystemUser));
            Assert.That((await PinsAsync(owner, spaceId, channelId, ct)).Select(p => p.message.messageId), Is.EqualTo(new[] { stays }));
            Assert.That(await StoredPinsAsync(channelId, ct), Is.EqualTo(1), "the pin rows of deleted messages remain");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Viewers_of_the_channel_are_told_about_pins(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var messageId = await SendAsync(owner, spaceId, channelId, "look", ct);

        await using var observer = await RealtimeClient.ConnectAsync(guest, ct);
        await observer.SubscribeToChannel(channelId, ct);

        await PinsOf(owner).PinMessage(spaceId, channelId, messageId, ct);
        var pinned = await observer.WaitForAsync<MessagePinned>(e => e.messageId == messageId, Window, ct: ct);

        await PinsOf(owner).UnpinMessage(spaceId, channelId, messageId, ct);
        var unpinned = await observer.WaitForAsync<MessageUnpinned>(e => e.messageId == messageId, Window, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(pinned.spaceId, Is.EqualTo(spaceId));
            Assert.That(pinned.channelId, Is.EqualTo(channelId));
            Assert.That(pinned.byUserId, Is.EqualTo(owner.UserId));
            Assert.That(unpinned.channelId, Is.EqualTo(channelId));
            Assert.That(unpinned.byUserId, Is.EqualTo(owner.UserId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Announcement_channels_take_pins(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Announcement, ct);
        var messageId = await SendAsync(owner, spaceId, channelId, "Patch notes", ct);

        var pin      = await PinsOf(owner).PinMessage(spaceId, channelId, messageId, ct);
        var byReader = await PinsOf(guest).UnpinMessage(spaceId, channelId, messageId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(pin, Is.InstanceOf<SuccessPinMessage>(), $"the pin was refused: {(pin as FailedPinMessage)?.error}");
            Assert.That((byReader as FailedUnpinMessage)?.error, Is.EqualTo(PinMessageError.INSUFFICIENT_PERMISSIONS));
            Assert.That((await PinsAsync(guest, spaceId, channelId, ct)).Select(p => p.message.text), Is.EqualTo(new[] { "Patch notes" }),
                "a reader of the announcement channel does not see its pin");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Reading_pins_needs_access_to_the_history(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var messageId = await SendAsync(owner, spaceId, channelId, "secret plan", ct);
        await PinsOf(owner).PinMessage(spaceId, channelId, messageId, ct);

        Assert.That(await PinsAsync(guest, spaceId, channelId, ct), Has.Count.EqualTo(1), "premise: the member reads the pins");

        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.ReadHistory, ct);

        var hidden = await PollAsync(() => PinsAsync(guest, spaceId, channelId, ct), p => p.Count == 0, TimeSpan.FromSeconds(10), ct);
        var outsider = await CreateSessionAsync(ct);

        Assert.Multiple(async () =>
        {
            Assert.That(hidden, Is.Empty, "a member without ReadHistory read the pins");
            Assert.That(await PinsAsync(outsider, spaceId, channelId, ct), Is.Empty, "someone outside the space read the pins");
            Assert.That(await PinsAsync(owner, spaceId, channelId, ct), Has.Count.EqualTo(1), "the owner lost the pins");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_live_messages_of_a_text_channel_are_pinned(CancellationToken ct = default)
    {
        var (owner, _, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var otherId = await CreateChannelAsync(owner, spaceId, "elsewhere", ChannelType.Text, ct);
        var voiceId = await CreateChannelAsync(owner, spaceId, "lounge", ChannelType.Voice, ct);

        var deleted   = await SendAsync(owner, spaceId, channelId, "gone", ct);
        var elsewhere = await SendAsync(owner, spaceId, otherId, "not here", ct);
        await owner.Channels.DeleteMessage(spaceId, channelId, deleted, ct);

        var ofDeleted  = await PinsOf(owner).PinMessage(spaceId, channelId, deleted, ct);
        var ofUnknown  = await PinsOf(owner).PinMessage(spaceId, channelId, deleted + 1_000_000, ct);
        var ofOther    = await PinsOf(owner).PinMessage(spaceId, channelId, elsewhere, ct);
        var inVoice    = await PinsOf(owner).PinMessage(spaceId, voiceId, 1, ct);
        var voiceUnpin = await PinsOf(owner).UnpinMessage(spaceId, voiceId, 1, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((ofDeleted as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.MESSAGE_NOT_FOUND));
            Assert.That((ofUnknown as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.MESSAGE_NOT_FOUND));
            Assert.That((ofOther as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.MESSAGE_NOT_FOUND),
                "a message of another channel was pinned here");
            Assert.That((inVoice as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.NOT_A_TEXT_CHANNEL));
            Assert.That((voiceUnpin as FailedUnpinMessage)?.error, Is.EqualTo(PinMessageError.NOT_A_TEXT_CHANNEL));
            Assert.That(await PinsAsync(owner, spaceId, voiceId, ct), Is.Empty);
            Assert.That(await StoredPinsAsync(channelId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Pinning_and_unpinning_need_ReadHistory_too(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var rules  = await SendAsync(owner, spaceId, channelId, "rules", ct);
        var secret = await SendAsync(owner, spaceId, channelId, "secret", ct);
        await PinsOf(owner).PinMessage(spaceId, channelId, rules, ct);

        var archetypes = ArchetypesOf(owner);
        var role       = await archetypes.CreateArchetype(spaceId, "pinner", ct).Ok();
        await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, guest.UserId, ct), role.id, true, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, role.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.ManageMessages, ct);
        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.ReadHistory, ct);

        var pin   = await PinsOf(guest).PinMessage(spaceId, channelId, secret, ct);
        var unpin = await PinsOf(guest).UnpinMessage(spaceId, channelId, rules, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((pin as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.INSUFFICIENT_PERMISSIONS),
                "a moderator without ReadHistory pinned a message and got its content back");
            Assert.That((unpin as FailedUnpinMessage)?.error, Is.EqualTo(PinMessageError.INSUFFICIENT_PERMISSIONS),
                "a moderator without ReadHistory unpinned a message");
            Assert.That((await PinsAsync(owner, spaceId, channelId, ct)).Select(p => p.message.messageId), Is.EqualTo(new[] { rules }));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task The_pin_limit_counts_only_pins_of_live_messages(CancellationToken ct = default)
    {
        var (owner, _, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);

        var ids = new List<long>();
        for (var i = 0; i <= ChannelGrain.PinLimit + 1; i++)
            ids.Add(await SendAsync(owner, spaceId, channelId, $"m{i}", ct));

        // A pin row left behind by a message deleted before its pin was dropped.
        var dead = ids[0];
        await owner.Channels.DeleteMessage(spaceId, channelId, dead, ct);
        await using (var db = await DbAsync(ct))
        {
            db.ChannelPins.Add(new ChannelPinEntity { SpaceId = spaceId, ChannelId = channelId, MessageId = dead, PinnedBy = owner.UserId });
            await db.SaveChangesAsync(ct);
        }

        foreach (var id in ids.Skip(1).Take(ChannelGrain.PinLimit - 1))
            Assert.That(await PinsOf(owner).PinMessage(spaceId, channelId, id, ct), Is.InstanceOf<SuccessPinMessage>());

        var fiftieth = await PinsOf(owner).PinMessage(spaceId, channelId, ids[ChannelGrain.PinLimit], ct);
        var over     = await PinsOf(owner).PinMessage(spaceId, channelId, ids[^1], ct);

        Assert.Multiple(async () =>
        {
            Assert.That(fiftieth, Is.InstanceOf<SuccessPinMessage>(), "the pin of a deleted message took a slot");
            Assert.That((over as FailedPinMessage)?.error, Is.EqualTo(PinMessageError.PIN_LIMIT_REACHED));
            Assert.That(await PinsAsync(owner, spaceId, channelId, ct), Has.Count.EqualTo(ChannelGrain.PinLimit));
        });
    }

    /// <summary>
    /// The channel is the only writer of its pins, so it keeps them: a listing does not read the pin
    /// rows again, and still shows every change to a pinned message.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Pins_are_kept_by_the_channel_and_follow_changes_to_the_messages(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await RoomAsync(ChannelType.Text, ct);
        var messageId = await SendAsync(owner, spaceId, channelId, "v1", ct);

        await PinsOf(owner).PinMessage(spaceId, channelId, messageId, ct);
        Assert.That((await PinsAsync(guest, spaceId, channelId, ct)).Single().message.text, Is.EqualTo("v1"));

        // Taken away behind the channel's back: a listing that read the rows again would come back empty.
        await using (var db = await DbAsync(ct))
            await db.ChannelPins.Where(p => p.ChannelId == channelId).ExecuteDeleteAsync(ct);

        Assert.That(await PinsAsync(guest, spaceId, channelId, ct), Has.Count.EqualTo(1), "the pins were read from the database again");

        Assert.That(await owner.Channels.EditMessage(spaceId, channelId, messageId, "v2", Entities(), ct), Is.InstanceOf<SuccessEditMessage>());
        Assert.That(await guest.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct), Is.InstanceOf<SuccessAddReaction>());

        var pinned = (await PinsAsync(guest, spaceId, channelId, ct)).Single().message;

        Assert.Multiple(() =>
        {
            Assert.That(pinned.text, Is.EqualTo("v2"), "the pin still shows the text from before the edit");
            Assert.That(pinned.editedAt, Is.Not.Null);
            Assert.That(pinned.reactions.Values.Select(r => (r.emoji, r.count)), Is.EqualTo(new[] { ("👍", 1) }),
                "the pin does not show the new reaction");
        });
    }
}
