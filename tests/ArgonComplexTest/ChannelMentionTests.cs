namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
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

    [Test, CancelAfter(120_000)]
    public async Task A_reply_mentions_the_author_it_answers_but_never_the_one_replying(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "replies", ChannelType.Text, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var question = await guest.Channels.SendMessage(spaceId, channelId, "anyone?", Entities(), NextRandomId(), null, ct);
        var own      = await owner.Channels.SendMessage(spaceId, channelId, "a note", Entities(), NextRandomId(), null, ct);

        await owner.Channels.SendMessage(spaceId, channelId, "replying to myself", Entities(), NextRandomId(), own, ct);
        await owner.Channels.SendMessage(spaceId, channelId, "yes", Entities(), NextRandomId(), question, ct);

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
            NextRandomId(), null, ct);

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

        await owner.Channels.SendMessage(spaceId, channelId, "@everyone hello", Entities(Everyone()), NextRandomId(), null, ct);

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
        var pinged     = await archetypes.CreateArchetype(spaceId, "pinged", ct);

        foreach (var member in new[] { holder, mutedOne })
            Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, member.UserId, ct), pinged.id, true, ct),
                Is.True);

        await mutedOne.Users.MuteTarget(spaceId, MuteTargetKind.Space, MuteLevelType.All, false, null, ct);

        await using var observer = await RealtimeClient.ConnectAsync(holder, ct);

        await owner.Channels.SendMessage(spaceId, channelId, "@pinged", Entities(Role(pinged.id)), NextRandomId(), null, ct);

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

        var messageId = await member.Channels.SendMessage(spaceId, channelId, "@everyone look", Entities(Everyone()), NextRandomId(), null, ct);
        await member.Channels.SendMessage(spaceId, channelId, "@listener", Entities(Mention(listener.UserId)), NextRandomId(), null, ct);

        Assert.That(await WaitForMentionsAsync(listener, channelId, 1, ct), Is.EqualTo(1), "premise: the direct mention lands");

        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(e => e.channelId == channelId, TimeSpan.FromSeconds(2),
            "a member without MentionEveryone announced an @everyone to the space", mark, ct);

        Assert.That(await MentionsAsync(listener, channelId, ct), Is.EqualTo(1),
            "a member without MentionEveryone still counted as mentioning everyone");

        var delivered = (await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values.Single(m => m.messageId == messageId);
        Assert.That(delivered.entities.Values.OfType<MessageEntityMentionEveryone>(), Is.Not.Empty,
            "the message itself goes out as written; only the ping is withheld");
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
        var raiders    = await archetypes.CreateArchetype(spaceId, "raiders", ct);
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, holder.UserId, ct), raiders.id, true, ct),
            Is.True);

        await using var observer = await RealtimeClient.ConnectAsync(holder, ct);

        bool IsRolePing(BatchMentionOccurred e) => e.channelId == channelId && e.mentionType == MentionTargetType.Role;

        await sender.Channels.SendMessage(spaceId, channelId, "@raiders", Entities(Role(raiders.id)), NextRandomId(), null, ct);

        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(IsRolePing, TimeSpan.FromSeconds(3),
            "a role that is not mentionable was pinged by a member who may not mention everyone", ct: ct);

        await archetypes.UpdateArchetype(spaceId, raiders with { isMentionable = true }, ct);
        await sender.Channels.SendMessage(spaceId, channelId, "@raiders", Entities(Role(raiders.id)), NextRandomId(), null, ct);

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

        var foreign = await ArchetypesOf(otherOwner).CreateArchetype(otherSpace, "foreign", ct);
        Assert.That(await ArchetypesOf(otherOwner).SetArchetypeToMember(otherSpace,
            await MemberIdOfAsync(otherOwner, otherSpace, outsider.UserId, ct), foreign.id, true, ct), Is.True);

        var archetypes = ArchetypesOf(owner);
        var fence      = await archetypes.CreateArchetype(spaceId, "fence", ct);
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, holder.UserId, ct), fence.id, true, ct),
            Is.True);
        var everyone = await EveryoneAsync(owner, spaceId, ct);

        await using var observer = await RealtimeClient.ConnectAsync(holder, ct);

        bool IsRolePing(BatchMentionOccurred e) => e.channelId == channelId && e.mentionType == MentionTargetType.Role;

        // Every role that pings announces itself once, so one announcement means the other two were dropped.
        await owner.Channels.SendMessage(spaceId, channelId, "@foreign @everyone @fence",
            Entities(Role(foreign.id), Role(everyone.id), Role(fence.id)), NextRandomId(), null, ct);

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
}
