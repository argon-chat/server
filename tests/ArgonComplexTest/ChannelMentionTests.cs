namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using static ChannelTestKit;

/// <summary>
/// Who a message counts as a mention for: a reply, a direct mention, @everyone and a role — and who
/// each of those deliberately leaves out.
/// </summary>
/// <remarks>
/// The fan-out runs after the send has returned, so a count is polled rather than read once. The
/// batch mentions announce themselves with <c>BatchMentionOccurred</c> only after the counts are
/// written, which is what lets a zero read after that event stand as an answer.
/// </remarks>
[TestFixture]
public class ChannelMentionTests : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static IonArray<IMessageEntity> Entities(params IMessageEntity[] entities) => new(entities);

    private static MessageEntityMention Mention(Guid userId) => new(EntityType.Mention, 0, 5, 1, userId);

    private static MessageEntityMentionEveryone Everyone() => new(EntityType.MentionEveryone, 0, 9, 1);

    private static MessageEntityMentionRole Role(Guid archetypeId) => new(EntityType.MentionRole, 0, 6, 1, archetypeId);

    private static Task<int> WaitForMentionsAsync(TestUserSession member, Guid channelId, int expected, CancellationToken ct)
        => PollAsync(() => MentionsAsync(member, channelId, ct), n => n >= expected, Window, ct);

    /// <summary>The stored mention count, for users the badge endpoint would not answer for.</summary>
    private static async Task<int> StoredMentionsAsync(Guid userId, Guid channelId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.ChannelReadStates.AsNoTracking()
           .Where(r => r.UserId == userId && r.ChannelId == channelId)
           .Select(r => r.MentionCount)
           .FirstOrDefaultAsync(ct);
    }

    private static IonArray<IMessageEntity> Mentions(IEnumerable<Guid> userIds)
        => new(userIds.Select(IMessageEntity (u) => Mention(u)).ToArray());

    [Test, CancelAfter(120_000)]
    public async Task A_reply_mentions_the_author_it_answers_but_never_the_one_replying(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "replies", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var question = await guest.Channels.SendMessage(spaceId, channelId, "anyone?", Entities(), NextRandomId(), null, ct).Ok();
        var own      = await owner.Channels.SendMessage(spaceId, channelId, "a note", Entities(), NextRandomId(), null, ct).Ok();

        await owner.Channels.SendMessage(spaceId, channelId, "replying to myself", Entities(), NextRandomId(), own, ct).Ok();
        await owner.Channels.SendMessage(spaceId, channelId, "yes", Entities(), NextRandomId(), question, ct).Ok();

        Assert.That(await WaitForMentionsAsync(guest, channelId, 1, ct), Is.EqualTo(1),
            "a reply did not count as a mention for the author of the message it answers");

        // The owner's own reply was sent first, so by now its fan-out has had every chance.
        Assert.That(await MentionsAsync(owner, channelId, ct), Is.Zero, "replying to your own message mentioned you");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_direct_mention_counts_for_the_member_named_and_not_for_the_sender(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "mentions", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await owner.Channels.SendMessage(spaceId, channelId, "@guest @me", Entities(Mention(guest.UserId), Mention(owner.UserId)),
            NextRandomId(), null, ct).Ok();

        Assert.That(await WaitForMentionsAsync(guest, channelId, 1, ct), Is.EqualTo(1));
        Assert.That(await MentionsAsync(owner, channelId, ct), Is.Zero, "mentioning yourself counted as a mention");
    }

    /// <summary>
    /// @everyone is the one mention a member can opt out of without muting the room, and muting the
    /// room opts out of it too.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Everyone_reaches_each_member_except_the_muted_and_those_who_suppress_it(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        var listener   = await CreateSessionAsync(ct);
        var muted      = await CreateSessionAsync(ct);
        var suppressor = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "announcements", ChannelType.Text, ct);

        foreach (var member in new[] { listener, muted, suppressor })
            await JoinAsync(owner, member, spaceId, ct);

        await muted.Users.MuteTarget(channelId, MuteTargetKind.Channel, MuteLevelType.All, false, null, ct);
        await suppressor.Users.MuteTarget(spaceId, MuteTargetKind.Space, MuteLevelType.None, true, null, ct);

        await using var observer = await RealtimeClient.ConnectAsync(listener, ct);

        await owner.Channels.SendMessage(spaceId, channelId, "@everyone hello", Entities(Everyone()), NextRandomId(), null, ct).Ok();

        await observer.WaitForAsync<BatchMentionOccurred>(
            e => e.channelId == channelId && e.mentionType == MentionTargetType.Everyone, Window, ct: ct);

        // The event is fired once the counts are written, so from here a zero is an answer, not a race.
        Assert.That(await WaitForMentionsAsync(listener, channelId, 1, ct), Is.EqualTo(1), "@everyone did not reach a member");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MentionsAsync(muted, channelId, ct), Is.Zero, "a member who muted the channel was counted for @everyone");
            Assert.That(await MentionsAsync(suppressor, channelId, ct), Is.Zero,
                "a member who suppressed @everyone in the space was counted for it");
            Assert.That(await MentionsAsync(owner, channelId, ct), Is.Zero, "@everyone counted for its own sender");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_role_mention_reaches_the_members_holding_it_except_the_muted(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var holder   = await CreateSessionAsync(ct);
        var mutedOne = await CreateSessionAsync(ct);
        var outsider = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "roles", ChannelType.Text, ct);

        foreach (var member in new[] { holder, mutedOne, outsider })
            await JoinAsync(owner, member, spaceId, ct);

        var archetypes = ArchetypesOf(owner);
        var pinged     = await archetypes.CreateArchetype(spaceId, "pinged", ct).Ok();

        foreach (var member in new[] { holder, mutedOne })
            Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, member.UserId, ct), pinged.id, true, ct),
                Is.True);

        await mutedOne.Users.MuteTarget(spaceId, MuteTargetKind.Space, MuteLevelType.All, false, null, ct);

        await using var observer = await RealtimeClient.ConnectAsync(holder, ct);

        await owner.Channels.SendMessage(spaceId, channelId, "@pinged", Entities(Role(pinged.id)), NextRandomId(), null, ct).Ok();

        await observer.WaitForAsync<BatchMentionOccurred>(
            e => e.channelId == channelId && e.mentionType == MentionTargetType.Role, Window, ct: ct);

        Assert.That(await WaitForMentionsAsync(holder, channelId, 1, ct), Is.EqualTo(1), "a role mention missed a member holding the role");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MentionsAsync(mutedOne, channelId, ct), Is.Zero, "a member who muted the space was counted for a role mention");
            Assert.That(await MentionsAsync(outsider, channelId, ct), Is.Zero, "a role mention counted for a member who does not hold the role");
        });
    }

    /// <summary>
    /// <c>MentionEveryone</c> is seeded on "everyone" and is the entitlement a space takes away to stop
    /// ordinary members pinging the whole room. Taking it away has to reach the server, not only the
    /// client's mention picker.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Without_MentionEveryone_an_everyone_mention_is_delivered_but_pings_nobody(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var member   = await CreateSessionAsync(ct);
        var listener = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "quiet-please", ChannelType.Text, ct);
        await JoinAsync(owner, member, spaceId, ct);
        await JoinAsync(owner, listener, spaceId, ct);

        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.MentionEveryone, ct);

        await using var observer = await RealtimeClient.ConnectAsync(listener, ct);
        var mark = observer.Mark();

        var messageId = await member.Channels.SendMessage(spaceId, channelId, "@everyone look", Entities(Everyone()), NextRandomId(), null, ct).Ok();
        await member.Channels.SendMessage(spaceId, channelId, "@listener", Entities(Mention(listener.UserId)), NextRandomId(), null, ct).Ok();

        Assert.That(await WaitForMentionsAsync(listener, channelId, 1, ct), Is.EqualTo(1), "premise: the direct mention lands");

        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(e => e.channelId == channelId, TimeSpan.FromSeconds(2),
            "a member without MentionEveryone announced an @everyone to the space", mark, ct);

        Assert.That(await MentionsAsync(listener, channelId, ct), Is.EqualTo(1),
            "a member without MentionEveryone still counted as mentioning everyone");

        var delivered = (await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values.Single(m => m.messageId == messageId);
        Assert.That(delivered.text, Is.EqualTo("@everyone look"), "the message itself goes out as written");
        Assert.That(delivered.entities.Values.OfType<MessageEntityMentionEveryone>(), Is.Empty,
            "the message came with an @everyone highlight its sender may not use");
    }

    /// <summary>
    /// A role pings when it is marked mentionable, or when the sender may mention everyone anyway.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task A_member_without_MentionEveryone_pings_a_role_only_once_it_is_mentionable(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var holder = await CreateSessionAsync(ct);
        var sender = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "roles", ChannelType.Text, ct);

        foreach (var member in new[] { holder, sender })
            await JoinAsync(owner, member, spaceId, ct);

        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.MentionEveryone, ct);

        var archetypes = ArchetypesOf(owner);
        var raiders    = await archetypes.CreateArchetype(spaceId, "raiders", ct).Ok();
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, holder.UserId, ct), raiders.id, true, ct),
            Is.True);

        await using var observer = await RealtimeClient.ConnectAsync(holder, ct);

        bool IsRolePing(BatchMentionOccurred e) => e.channelId == channelId && e.mentionType == MentionTargetType.Role;

        await sender.Channels.SendMessage(spaceId, channelId, "@raiders", Entities(Role(raiders.id)), NextRandomId(), null, ct).Ok();

        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(IsRolePing, TimeSpan.FromSeconds(3),
            "a role that is not mentionable was pinged by a member who may not mention everyone", ct: ct);

        await archetypes.UpdateArchetype(spaceId, raiders with { isMentionable = true }, ct).Ok();
        await sender.Channels.SendMessage(spaceId, channelId, "@raiders", Entities(Role(raiders.id)), NextRandomId(), null, ct).Ok();

        await observer.WaitForAsync<BatchMentionOccurred>(IsRolePing, Window, ct: ct);
        Assert.That(await WaitForMentionsAsync(holder, channelId, 1, ct), Is.EqualTo(1));
    }

    /// <summary>
    /// A role id is only a guid on the wire: one from another space, or the everyone role itself, must
    /// not turn into pings here.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task A_role_of_another_space_and_the_everyone_role_ping_nobody(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        var holder     = await CreateSessionAsync(ct);
        var bystander  = await CreateSessionAsync(ct);
        var otherOwner = await CreateSessionAsync(ct);
        var outsider   = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "roles", ChannelType.Text, ct);

        foreach (var member in new[] { holder, bystander })
            await JoinAsync(owner, member, spaceId, ct);

        var otherSpace = await CreateSpaceAsync(otherOwner, ct);
        await JoinAsync(otherOwner, outsider, otherSpace, ct);

        var foreign = await ArchetypesOf(otherOwner).CreateArchetype(otherSpace, "foreign", ct).Ok();
        Assert.That(await ArchetypesOf(otherOwner).SetArchetypeToMember(otherSpace,
            await MemberIdOfAsync(otherOwner, otherSpace, outsider.UserId, ct), foreign.id, true, ct), Is.True);

        var archetypes = ArchetypesOf(owner);
        var fence      = await archetypes.CreateArchetype(spaceId, "fence", ct).Ok();
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, holder.UserId, ct), fence.id, true, ct),
            Is.True);
        var everyone = await EveryoneAsync(owner, spaceId, ct);

        await using var observer = await RealtimeClient.ConnectAsync(holder, ct);

        bool IsRolePing(BatchMentionOccurred e) => e.channelId == channelId && e.mentionType == MentionTargetType.Role;

        // Every role that pings announces itself once, so one announcement means the other two were dropped.
        await owner.Channels.SendMessage(spaceId, channelId, "@foreign @everyone @fence",
            Entities(Role(foreign.id), Role(everyone.id), Role(fence.id)), NextRandomId(), null, ct).Ok();

        await observer.WaitForAsync<BatchMentionOccurred>(IsRolePing, Window, ct: ct);
        var mark = observer.Mark();
        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(IsRolePing, TimeSpan.FromSeconds(2),
            "more than one of the mentioned roles pinged", from: mark, ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(observer.EventsOfType<BatchMentionOccurred>().Count(IsRolePing), Is.EqualTo(1));
            Assert.That(await WaitForMentionsAsync(holder, channelId, 1, ct), Is.EqualTo(1));
            Assert.That(await MentionsAsync(outsider, channelId, ct), Is.Zero, "a role of another space was pinged into this channel");
            Assert.That(await MentionsAsync(bystander, channelId, ct), Is.Zero, "mentioning the everyone role pinged everybody");
        });
    }

    /// <summary>
    /// A highlight is a claim that the message pinged, so a mention the sender may not ping is stored
    /// as plain text, on a send and on an edit alike.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Mentions_the_sender_may_not_ping_are_kept_as_plain_text(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "roles", ChannelType.Text, ct);
        await JoinAsync(owner, member, spaceId, ct);
        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.MentionEveryone, ct);

        var archetypes = ArchetypesOf(owner);
        var open       = await archetypes.CreateArchetype(spaceId, "open", ct).Ok();
        open = await archetypes.UpdateArchetype(spaceId, open with { isMentionable = true }, ct).Ok();
        var closed   = await archetypes.CreateArchetype(spaceId, "closed", ct).Ok();
        var everyone = await EveryoneAsync(owner, spaceId, ct);

        IonArray<IMessageEntity> All() => Entities(Everyone(), Role(open.id), Role(closed.id), Role(everyone.id));

        static (bool Everyone, Guid[] Roles) Highlights(IEnumerable<IMessageEntity> entities)
        {
            var list = entities.ToList();
            return (list.OfType<MessageEntityMentionEveryone>().Any(), list.OfType<MessageEntityMentionRole>().Select(r => r.archetypeId).ToArray());
        }

        var sent    = await member.Channels.SendMessage(spaceId, channelId, "@all of them", All(), NextRandomId(), null, ct).Ok();
        var draft   = await member.Channels.SendMessage(spaceId, channelId, "hi", Entities(), NextRandomId(), null, ct).Ok();
        var edit    = await member.Channels.EditMessage(spaceId, channelId, draft, "@all of them", All(), ct);
        var byOwner = await owner.Channels.SendMessage(spaceId, channelId, "@all of them", All(), NextRandomId(), null, ct).Ok();

        Assert.That(edit, Is.InstanceOf<SuccessEditMessage>(), $"the edit was refused: {(edit as FailedEditMessage)?.error}");

        var ofSent    = Highlights((await StoredMessageAsync(spaceId, channelId, sent, ct))!.Entities);
        var ofEdit    = Highlights((await StoredMessageAsync(spaceId, channelId, draft, ct))!.Entities);
        var ofOwner   = Highlights((await StoredMessageAsync(spaceId, channelId, byOwner, ct))!.Entities);
        var editReply = Highlights(((SuccessEditMessage)edit).message.entities.Values);

        Assert.Multiple(() =>
        {
            Assert.That(ofSent.Everyone, Is.False, "@everyone stayed highlighted for a sender who may not use it");
            Assert.That(ofSent.Roles, Is.EqualTo(new[] { open.id }), "only the mentionable role may stay highlighted");
            Assert.That(ofEdit.Everyone, Is.False, "an edit brought back an @everyone highlight");
            Assert.That(ofEdit.Roles, Is.EqualTo(new[] { open.id }));
            Assert.That(editReply.Everyone, Is.False, "the edit answered with an @everyone highlight it did not store");
            Assert.That(editReply.Roles, Is.EqualTo(new[] { open.id }), "the edit answered with other roles than it stored");
            Assert.That(ofOwner.Everyone, Is.True, "the owner may mention everyone");
            Assert.That(ofOwner.Roles, Is.EquivalentTo(new[] { open.id, closed.id }), "the everyone role is never a role mention");
        });
    }

    /// <summary>
    /// Direct mentions count once per member, only for members of the space, and only for the first
    /// fifty named in a message.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task A_direct_mention_counts_once_for_members_only_and_for_at_most_fifty_of_them(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var guest    = await CreateSessionAsync(ct);
        var leaver   = await CreateSessionAsync(ct);
        var outsider = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "mentions", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);
        await JoinAsync(owner, leaver, spaceId, ct);
        await Grains.GetGrain<ISpaceGrain>(spaceId).RemoveMemberAsync(leaver.UserId);

        await owner.Channels.SendMessage(spaceId, channelId, "@guest @guest @guest @leaver @outsider",
            Mentions([guest.UserId, guest.UserId, guest.UserId, leaver.UserId, outsider.UserId]), NextRandomId(), null, ct).Ok();

        // Fifty names first, so the guest named last is past the cap.
        var strangers = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid());
        await owner.Channels.SendMessage(spaceId, channelId, "a crowd", Mentions(strangers.Append(guest.UserId)), NextRandomId(), null, ct).Ok();

        await owner.Channels.SendMessage(spaceId, channelId, "@guest", Mentions([guest.UserId]), NextRandomId(), null, ct).Ok();

        Assert.That(await WaitForMentionsAsync(guest, channelId, 2, ct), Is.GreaterThanOrEqualTo(2), "premise: the mentions land");
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MentionsAsync(guest, channelId, ct), Is.EqualTo(2), "a member named after the first fifty was pinged");
            Assert.That(await StoredMentionsAsync(leaver.UserId, channelId, ct), Is.Zero, "a member who left was pinged");
            Assert.That(await StoredMentionsAsync(outsider.UserId, channelId, ct), Is.Zero, "someone outside the space was pinged");
        });
    }

    /// <summary>
    /// Naming more than ten members in an announcement is a mass ping like @everyone and spends the
    /// same hourly budget; ten, or one member named many times, does not.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task Naming_more_than_ten_members_in_an_announcement_spends_the_mass_ping_budget(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);

        var members = new List<TestUserSession>();
        for (var i = 0; i < 11; i++)
        {
            var member = await CreateSessionAsync(ct);
            await JoinAsync(owner, member, spaceId, ct);
            members.Add(member);
        }

        var first = members[0];
        var last  = members[^1];
        var all   = members.Select(m => m.UserId).ToList();

        await using var observer = await RealtimeClient.ConnectAsync(first, ct);

        bool IsEveryonePing(BatchMentionOccurred e) => e.channelId == channelId && e.mentionType == MentionTargetType.Everyone;

        await owner.Channels.SendMessage(spaceId, channelId, "all eleven", Mentions(all), NextRandomId(), null, ct).Ok();
        Assert.That(await WaitForMentionsAsync(last, channelId, 1, ct), Is.EqualTo(1), "eleven names pinged nobody while the budget lasted");

        for (var i = 0; i < 3; i++)
            await owner.Channels.SendMessage(spaceId, channelId, "@everyone", Entities(Everyone()), NextRandomId(), null, ct).Ok();

        await PollAsync(() => Task.FromResult(observer.EventsOfType<BatchMentionOccurred>().Count(IsEveryonePing)), n => n >= 2, Window, ct);
        var mark = observer.Mark();
        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(IsEveryonePing, TimeSpan.FromSeconds(3),
            "a third @everyone pinged although eleven names had spent a slot of the hour", from: mark, ct: ct);

        // The budget is spent: ten names and one name eleven times still ping, eleven names do not.
        await owner.Channels.SendMessage(spaceId, channelId, "ten of you", Mentions(all.Take(10)), NextRandomId(), null, ct).Ok();
        await owner.Channels.SendMessage(spaceId, channelId, "you, eleven times", Mentions(Enumerable.Repeat(last.UserId, 11)), NextRandomId(), null, ct).Ok();
        await owner.Channels.SendMessage(spaceId, channelId, "all eleven again", Mentions(all), NextRandomId(), null, ct).Ok();

        Assert.That(await WaitForMentionsAsync(first, channelId, 4, ct), Is.GreaterThanOrEqualTo(4), "ten names did not ping");
        Assert.That(await WaitForMentionsAsync(last, channelId, 4, ct), Is.GreaterThanOrEqualTo(4), "one member named eleven times did not ping");
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(observer.EventsOfType<BatchMentionOccurred>().Count(IsEveryonePing), Is.EqualTo(2));
            Assert.That(await MentionsAsync(first, channelId, ct), Is.EqualTo(4), "eleven names pinged past the hourly budget");
            Assert.That(await MentionsAsync(last, channelId, ct), Is.EqualTo(4), "eleven names pinged past the hourly budget");
        });
    }

    /// <summary>
    /// Only a ping that went out spends the announcement budget: an @everyone its sender may not use
    /// costs nothing. What was spent is remembered across a new activation of the channel.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Only_pings_that_went_out_spend_the_announcement_budget_across_activations(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var herald = await CreateSessionAsync(ct);
        var reader = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, herald, spaceId, ct);
        await JoinAsync(owner, reader, spaceId, ct);

        // The herald posts, but may not mention everyone.
        var archetypes = ArchetypesOf(owner);
        var heralds    = await archetypes.CreateArchetype(spaceId, "herald", ct).Ok();
        await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, herald.UserId, ct), heralds.id, true, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, heralds.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.SendMessages, ct);
        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.SendMessages | ArgonEntitlement.MentionEveryone, ct);

        await using var observer = await RealtimeClient.ConnectAsync(reader, ct);

        bool IsEveryonePing(BatchMentionOccurred e) => e.channelId == channelId && e.mentionType == MentionTargetType.Everyone;
        int Pings() => observer.EventsOfType<BatchMentionOccurred>().Count(IsEveryonePing);

        var posted = await PollAsync(async () =>
            await herald.Channels.SendMessage(spaceId, channelId, "@everyone 1", Entities(Everyone()), NextRandomId(), null, ct) is SuccessSendMessage ok
                ? ok.readback.messageId
                : 0L, id => id != 0, Window, ct);
        Assert.That(posted, Is.Not.Zero, "premise: the herald posts");

        for (var i = 2; i <= 3; i++)
            await herald.Channels.SendMessage(spaceId, channelId, $"@everyone {i}", Entities(Everyone()), NextRandomId(), null, ct).Ok();

        for (var i = 0; i < 2; i++)
            await owner.Channels.SendMessage(spaceId, channelId, "@everyone", Entities(Everyone()), NextRandomId(), null, ct).Ok();

        Assert.That(await PollAsync(() => Task.FromResult(Pings()), n => n >= 2, Window, ct), Is.EqualTo(2),
            "@everyone that could not ping used up the hour");

        // A new activation reads what was spent back from the stored messages.
        await Grains.GetGrain<IChannelGrain>(channelId).ClearChannel();
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        for (var i = 0; i < 2; i++)
            await owner.Channels.SendMessage(spaceId, channelId, "@everyone", Entities(Everyone()), NextRandomId(), null, ct).Ok();

        await PollAsync(() => Task.FromResult(Pings()), n => n >= 3, Window, ct);
        var mark = observer.Mark();
        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(IsEveryonePing, TimeSpan.FromSeconds(3),
            "the new activation forgot the pings already sent this hour", from: mark, ct: ct);

        Assert.That(Pings(), Is.EqualTo(3));
    }
}
