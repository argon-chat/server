namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using static ChannelTestKit;

/// <summary>
/// Following announcement channels: who may link a source to a target, the per-target limit,
/// publishing a post into every follower without pinging anyone there, and the links going away with
/// either channel.
/// </summary>
[TestFixture]
public class ChannelFollowTests : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static IonArray<IMessageEntity> Entities(params IMessageEntity[] entities) => new(entities);

    private static IChannelFollowInteraction FollowsOf(TestUserSession session)
        => session.Client.ForService<IChannelFollowInteraction>(Services);

    private static FollowChannelError? ErrorOf(IFollowChannelResult result) => (result as FailedFollowChannel)?.error;

    private static PublishMessageError? ErrorOf(IPublishMessageResult result) => (result as FailedPublishMessage)?.error;

    private static RemoveFollowError? ErrorOf(IRemoveFollowResult result) => (result as FailedRemoveFollow)?.error;

    private sealed record Pair(
        TestUserSession Publisher, Guid SourceSpaceId, Guid SourceChannelId,
        TestUserSession Admin, Guid TargetSpaceId, Guid TargetChannelId);

    /// <summary>
    /// A publisher's space with an announcement channel and an admin's space with a text channel. The
    /// admin is a plain member of the publisher's space, which is what reading the source takes.
    /// </summary>
    private async Task<Pair> PairAsync(CancellationToken ct)
    {
        var publisher = await CreateSessionAsync(ct);
        var admin     = await CreateSessionAsync(ct);

        var sourceSpace = await CreateSpaceAsync(publisher, ct);
        var news        = await CreateChannelAsync(publisher, sourceSpace, "news", ChannelType.Announcement, ct);

        var targetSpace = await CreateSpaceAsync(admin, ct);
        var general     = await CreateChannelAsync(admin, targetSpace, "general", ChannelType.Text, ct);

        await JoinAsync(publisher, admin, sourceSpace, ct);

        return new Pair(publisher, sourceSpace, news, admin, targetSpace, general);
    }

    private static async Task<ChannelFollowLink> FollowAsync(TestUserSession caller, Guid sourceSpaceId, Guid sourceChannelId,
        Guid targetSpaceId, Guid targetChannelId, CancellationToken ct)
    {
        var result = await FollowsOf(caller).FollowChannel(sourceSpaceId, sourceChannelId, targetSpaceId, targetChannelId, ct);

        Assert.That(result, Is.InstanceOf<SuccessFollowChannel>(), $"the follow was refused: {ErrorOf(result)}");
        return ((SuccessFollowChannel)result).link;
    }

    private static Task<ChannelFollowLink> FollowAsync(Pair p, CancellationToken ct)
        => FollowAsync(p.Admin, p.SourceSpaceId, p.SourceChannelId, p.TargetSpaceId, p.TargetChannelId, ct);

    private static async Task<SuccessPublishMessage> PublishAsync(TestUserSession caller, Guid spaceId, Guid channelId, long messageId,
        CancellationToken ct)
    {
        var result = await FollowsOf(caller).PublishMessage(spaceId, channelId, messageId, ct);

        Assert.That(result, Is.InstanceOf<SuccessPublishMessage>(), $"the publish was refused: {ErrorOf(result)}");
        return (SuccessPublishMessage)result;
    }

    private static async Task<List<ArgonMessage>> CopiesAsync(TestUserSession reader, Guid spaceId, Guid channelId, int expected,
        CancellationToken ct)
    {
        var history = await PollAsync(() => reader.Channels.QueryMessages(spaceId, channelId, null, 50, ct),
            h => h.Values.Count(m => m.crosspost is not null) >= expected, Window, ct);

        return history.Values.Where(m => m.crosspost is not null).ToList();
    }

    private static async Task<int> FollowRowsAsync(Guid channelId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.ChannelFollows.CountAsync(f => f.SourceChannelId == channelId || f.TargetChannelId == channelId, ct);
    }

    private static MessageEntityAttachment Attachment(Guid fileId)
        => new(EntityType.Attachment, 0, 0, 1, fileId, "photo.png", 1024, "image/png", 64, 64, null, null);

    [Test, CancelAfter(180_000)]
    public async Task Following_takes_read_access_to_the_source_and_ManageChannels_in_the_target(CancellationToken ct = default)
    {
        var publisher = await CreateSessionAsync(ct);
        var admin     = await CreateSessionAsync(ct);
        var member    = await CreateSessionAsync(ct);

        var sourceSpace = await CreateSpaceAsync(publisher, ct);
        var news        = await CreateChannelAsync(publisher, sourceSpace, "news", ChannelType.Announcement, ct);
        var secret      = await CreateChannelAsync(publisher, sourceSpace, "secret", ChannelType.Announcement, ct);
        var chatter     = await CreateChannelAsync(publisher, sourceSpace, "chatter", ChannelType.Text, ct);

        var targetSpace = await CreateSpaceAsync(admin, ct);
        var general     = await CreateChannelAsync(admin, targetSpace, "general", ChannelType.Text, ct);
        var lobby       = await CreateChannelAsync(admin, targetSpace, "lobby", ChannelType.Voice, ct);
        await JoinAsync(admin, member, targetSpace, ct);

        var outsider = await FollowsOf(admin).FollowChannel(sourceSpace, news, targetSpace, general, ct);
        Assert.That(ErrorOf(outsider), Is.EqualTo(FollowChannelError.NO_ACCESS_TO_SOURCE), "a stranger to the source space followed it");

        await JoinAsync(publisher, admin, sourceSpace, ct);
        await JoinAsync(publisher, member, sourceSpace, ct);
        await DenyOnChannelAsync(publisher, sourceSpace, secret, ArgonEntitlement.ReadHistory, ct);

        var hidden     = await FollowsOf(admin).FollowChannel(sourceSpace, secret, targetSpace, general, ct);
        var byMember   = await FollowsOf(member).FollowChannel(sourceSpace, news, targetSpace, general, ct);
        var ofText     = await FollowsOf(admin).FollowChannel(sourceSpace, chatter, targetSpace, general, ct);
        var intoVoice  = await FollowsOf(admin).FollowChannel(sourceSpace, news, targetSpace, lobby, ct);
        var wrongSpace = await FollowsOf(admin).FollowChannel(sourceSpace, news, sourceSpace, general, ct);
        var unknown    = await FollowsOf(admin).FollowChannel(sourceSpace, Guid.NewGuid(), targetSpace, general, ct);
        var itself     = await FollowsOf(publisher).FollowChannel(sourceSpace, news, sourceSpace, news, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(hidden), Is.EqualTo(FollowChannelError.NO_ACCESS_TO_SOURCE), "a channel the caller cannot read was followed");
            Assert.That(ErrorOf(byMember), Is.EqualTo(FollowChannelError.INSUFFICIENT_PERMISSIONS), "a member without ManageChannels linked a target");
            Assert.That(ErrorOf(ofText), Is.EqualTo(FollowChannelError.NOT_AN_ANNOUNCEMENT_CHANNEL));
            Assert.That(ErrorOf(intoVoice), Is.EqualTo(FollowChannelError.TARGET_NOT_TEXT));
            Assert.That(ErrorOf(wrongSpace), Is.EqualTo(FollowChannelError.TARGET_NOT_FOUND));
            Assert.That(ErrorOf(unknown), Is.EqualTo(FollowChannelError.SOURCE_NOT_FOUND));
            Assert.That(ErrorOf(itself), Is.EqualTo(FollowChannelError.SAME_CHANNEL));
        });

        var link = await FollowAsync(admin, sourceSpace, news, targetSpace, general, ct);
        var again = await FollowsOf(admin).FollowChannel(sourceSpace, news, targetSpace, general, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(again), Is.EqualTo(FollowChannelError.ALREADY_FOLLOWING));
            Assert.That(link.sourceChannelId, Is.EqualTo(news));
            Assert.That(link.sourceSpaceId, Is.EqualTo(sourceSpace));
            Assert.That(link.targetChannelId, Is.EqualTo(general));
            Assert.That(link.targetSpaceId, Is.EqualTo(targetSpace));
            Assert.That(link.sourceChannelName, Is.EqualTo("news"));
            Assert.That(link.targetChannelName, Is.EqualTo("general"));
            Assert.That(link.sourceSpaceName, Is.Not.Empty);
            Assert.That(link.creatorId, Is.EqualTo(admin.UserId));
        });

        // Within one space as well.
        var local = await FollowAsync(publisher, sourceSpace, news, sourceSpace, chatter, ct);
        Assert.That(local.targetSpaceId, Is.EqualTo(sourceSpace));
    }

    [Test, CancelAfter(300_000)]
    public async Task A_channel_follows_at_most_twenty_sources(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var digest  = await CreateChannelAsync(owner, spaceId, "digest", ChannelType.Text, ct);

        var sources = new List<Guid>();
        for (var i = 0; i < 21; i++)
            sources.Add(await CreateChannelAsync(owner, spaceId, $"feed-{i}", ChannelType.Announcement, ct));

        foreach (var source in sources.Take(20))
            await FollowAsync(owner, spaceId, source, spaceId, digest, ct);

        var over = await FollowsOf(owner).FollowChannel(spaceId, sources[20], spaceId, digest, ct);
        Assert.That(ErrorOf(over), Is.EqualTo(FollowChannelError.TOO_MANY_FOLLOWS));

        // A deleted source frees its slot.
        await owner.Channels.DeleteChannel(spaceId, sources[0], ct).Ok();
        await FollowAsync(owner, spaceId, sources[20], spaceId, digest, ct);

        var followed = await FollowsOf(owner).GetFollowedSources(spaceId, digest, ct);
        Assert.That(followed.Values.Select(l => l.sourceChannelId), Is.EquivalentTo(sources.Skip(1)));
    }

    [Test, CancelAfter(180_000)]
    public async Task Publishing_copies_the_post_into_every_follower_and_pings_nobody_there(CancellationToken ct = default)
    {
        var p      = await PairAsync(ct);
        var reader = await CreateSessionAsync(ct);
        await JoinAsync(p.Admin, reader, p.TargetSpaceId, ct);

        var bulletin = await CreateChannelAsync(p.Admin, p.TargetSpaceId, "bulletin", ChannelType.Announcement, ct);
        var lounge   = await CreateChannelAsync(p.Publisher, p.SourceSpaceId, "lounge", ChannelType.Text, ct);

        await FollowAsync(p, ct);
        await FollowAsync(p.Admin, p.SourceSpaceId, p.SourceChannelId, p.TargetSpaceId, bulletin, ct);
        await FollowAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, p.SourceSpaceId, lounge, ct);

        // Ids that mean something in the TARGET space: the author could name them, and they must not ping.
        var archetypes = ArchetypesOf(p.Admin);
        var raiders    = await archetypes.CreateArchetype(p.TargetSpaceId, "raiders", ct).Ok();
        raiders = await archetypes.UpdateArchetype(p.TargetSpaceId, raiders with { isMentionable = true }, ct).Ok();
        Assert.That(await archetypes.SetArchetypeToMember(p.TargetSpaceId, await MemberIdOfAsync(p.Admin, p.TargetSpaceId, reader.UserId, ct),
            raiders.id, true, ct), Is.True);

        await using var observer = await RealtimeClient.ConnectAsync(reader, ct);
        await observer.SubscribeToChannel(p.TargetChannelId, ct);

        const string text = "@everyone @reader @raiders big news";
        var file = Guid.NewGuid();

        var messageId = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, text, Entities(
            new MessageEntityMentionEveryone(EntityType.MentionEveryone, 0, 9, 1),
            new MessageEntityMention(EntityType.Mention, 10, 7, 1, reader.UserId),
            new MessageEntityMentionRole(EntityType.MentionRole, 18, 8, 1, raiders.id),
            new MessageEntityBold(EntityType.Bold, 27, 3, 1),
            Attachment(file)), NextRandomId(), null, ct).Ok();

        var published = await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, messageId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(published.targetCount, Is.EqualTo(3));
            Assert.That(published.deliveredCount, Is.EqualTo(3), "a follower did not receive the post");
        });

        var sent = await observer.WaitForAsync<MessageSent>(e => e.message.channelId == p.TargetChannelId && e.message.crosspost is not null,
            Window, ct: ct);
        var copy = sent.message;

        Assert.Multiple(() =>
        {
            Assert.That(copy.sender, Is.EqualTo(p.Publisher.UserId), "the copy is not attributed to the author");
            Assert.That(copy.text, Is.EqualTo(text));
            Assert.That(copy.crosspost!.sourceMessageId, Is.EqualTo(messageId));
            Assert.That(copy.crosspost.sourceChannelId, Is.EqualTo(p.SourceChannelId));
            Assert.That(copy.crosspost.sourceSpaceId, Is.EqualTo(p.SourceSpaceId));
            Assert.That(copy.crosspost.sourceChannelName, Is.EqualTo("news"));
            Assert.That(copy.crosspost.sourceSpaceName, Is.Not.Empty);
            Assert.That(copy.entities.Values.Where(e => e is MessageEntityMention or MessageEntityMentionEveryone or MessageEntityMentionRole),
                Is.Empty, "a mention survived into the copy");
            Assert.That(copy.entities.Values.OfType<MessageEntityBold>().Count(), Is.EqualTo(1), "formatting was lost");
            Assert.That(copy.entities.Values.OfType<MessageEntityAttachment>().Single().fileId, Is.EqualTo(file));
            Assert.That(copy.entities.Values.OfType<MessageEntityAttachment>().Single().downloadUrl, Is.Not.Null,
                "the attachment does not resolve in the target");
        });

        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(e => e.channelId == p.TargetChannelId || e.channelId == bulletin,
            TimeSpan.FromSeconds(3), "a crosspost pinged the target channel", ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MentionsAsync(reader, p.TargetChannelId, ct), Is.Zero, "the reader was pinged by a copy");
            Assert.That(await MentionsAsync(reader, bulletin, ct), Is.Zero, "the reader was pinged by a copy");
            Assert.That(await CopiesAsync(p.Admin, p.TargetSpaceId, p.TargetChannelId, 1, ct), Has.Count.EqualTo(1));
            Assert.That(await CopiesAsync(p.Admin, p.TargetSpaceId, bulletin, 1, ct), Has.Count.EqualTo(1));
            Assert.That(await CopiesAsync(p.Publisher, p.SourceSpaceId, lounge, 1, ct), Has.Count.EqualTo(1));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task The_author_or_a_moderator_publishes_a_post_and_only_once(CancellationToken ct = default)
    {
        var p      = await PairAsync(ct);
        var herald = await CreateSessionAsync(ct);
        var reader = await CreateSessionAsync(ct);
        await JoinAsync(p.Publisher, herald, p.SourceSpaceId, ct);
        await JoinAsync(p.Publisher, reader, p.SourceSpaceId, ct);

        var archetypes = ArchetypesOf(p.Publisher);
        var role       = await archetypes.CreateArchetype(p.SourceSpaceId, "herald", ct).Ok();
        Assert.That(await archetypes.SetArchetypeToMember(p.SourceSpaceId,
            await MemberIdOfAsync(p.Publisher, p.SourceSpaceId, herald.UserId, ct), role.id, true, ct), Is.True);
        await archetypes.UpsertArchetypeEntitlementForChannel(p.SourceSpaceId, p.SourceChannelId, role.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.SendMessages, ct);

        await FollowAsync(p, ct);

        await using var observer = await RealtimeClient.ConnectAsync(reader, ct);
        await observer.SubscribeToChannel(p.SourceChannelId, ct);

        var first  = await herald.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "patch notes", Entities(), NextRandomId(), null, ct).Ok();
        var second = await herald.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "hotfix", Entities(), NextRandomId(), null, ct).Ok();
        var rules  = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "rules", Entities(), NextRandomId(), null, ct).Ok();

        var byReader      = await FollowsOf(reader).PublishMessage(p.SourceSpaceId, p.SourceChannelId, first, ct);
        var ofSomeoneElse = await FollowsOf(herald).PublishMessage(p.SourceSpaceId, p.SourceChannelId, rules, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(byReader), Is.EqualTo(PublishMessageError.INSUFFICIENT_PERMISSIONS), "a reader published a post");
            Assert.That(ErrorOf(ofSomeoneElse), Is.EqualTo(PublishMessageError.INSUFFICIENT_PERMISSIONS),
                "an author without ManageMessages published someone else's post");
        });

        var own       = await PublishAsync(herald, p.SourceSpaceId, p.SourceChannelId, first, ct);
        var twice     = await FollowsOf(p.Publisher).PublishMessage(p.SourceSpaceId, p.SourceChannelId, first, ct);
        var moderated = await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, second, ct);
        var unknown   = await FollowsOf(p.Publisher).PublishMessage(p.SourceSpaceId, p.SourceChannelId, rules + 1_000_000, ct);

        var announced = await observer.WaitForAsync<MessagePublished>(e => e.messageId == first, Window, ct: ct);
        var history   = (await reader.Channels.QueryMessages(p.SourceSpaceId, p.SourceChannelId, null, 10, ct)).Values;

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(twice), Is.EqualTo(PublishMessageError.ALREADY_PUBLISHED));
            Assert.That(ErrorOf(unknown), Is.EqualTo(PublishMessageError.MESSAGE_NOT_FOUND));
            Assert.That(own.deliveredCount, Is.EqualTo(1));
            Assert.That(moderated.deliveredCount, Is.EqualTo(1));
            Assert.That(announced.channelId, Is.EqualTo(p.SourceChannelId));
            Assert.That(announced.publishedAt, Is.EqualTo(own.publishedAt).Within(TimeSpan.FromSeconds(1)));
            Assert.That(history.Single(m => m.messageId == first).publishedAt, Is.Not.Null, "history lost the published mark");
            Assert.That(history.Single(m => m.messageId == second).publishedAt, Is.Not.Null);
            Assert.That(history.Single(m => m.messageId == rules).publishedAt, Is.Null, "an unpublished post carries a published mark");
        });

        // Once each, however often it was asked.
        Assert.That(await CopiesAsync(p.Admin, p.TargetSpaceId, p.TargetChannelId, 2, ct), Has.Count.EqualTo(2));
    }

    [Test, CancelAfter(180_000)]
    public async Task Only_original_posts_of_an_announcement_channel_are_published(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var news    = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        var relay   = await CreateChannelAsync(owner, spaceId, "relay", ChannelType.Announcement, ct);
        var general = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);

        await FollowAsync(owner, spaceId, news, spaceId, relay, ct);

        var chat = await owner.Channels.SendMessage(spaceId, general, "hi", Entities(), NextRandomId(), null, ct).Ok();
        var post = await owner.Channels.SendMessage(spaceId, news, "news", Entities(), NextRandomId(), null, ct).Ok();
        var gone = await owner.Channels.SendMessage(spaceId, news, "oops", Entities(), NextRandomId(), null, ct).Ok();
        await owner.Channels.DeleteMessage(spaceId, news, gone, ct);

        await PublishAsync(owner, spaceId, news, post, ct);
        var copy = (await CopiesAsync(owner, spaceId, relay, 1, ct)).Single();

        var ofText    = await FollowsOf(owner).PublishMessage(spaceId, general, chat, ct);
        var ofCopy    = await FollowsOf(owner).PublishMessage(spaceId, relay, copy.messageId, ct);
        var ofDeleted = await FollowsOf(owner).PublishMessage(spaceId, news, gone, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(ofText), Is.EqualTo(PublishMessageError.NOT_AN_ANNOUNCEMENT_CHANNEL));
            Assert.That(ErrorOf(ofCopy), Is.EqualTo(PublishMessageError.NOT_PUBLISHABLE), "a crosspost was published on");
            Assert.That(ErrorOf(ofDeleted), Is.EqualTo(PublishMessageError.MESSAGE_NOT_FOUND));
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task A_channel_publishes_at_most_ten_posts_an_hour(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var news    = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);

        for (var i = 0; i < 10; i++)
        {
            var post      = await owner.Channels.SendMessage(spaceId, news, $"post {i}", Entities(), NextRandomId(), null, ct).Ok();
            var published = await PublishAsync(owner, spaceId, news, post, ct);
            Assert.That(published.targetCount, Is.Zero, "a channel nobody follows has targets");
        }

        var eleventh = await owner.Channels.SendMessage(spaceId, news, "one too many", Entities(), NextRandomId(), null, ct).Ok();
        var refused  = await FollowsOf(owner).PublishMessage(spaceId, news, eleventh, ct);

        Assert.That(ErrorOf(refused), Is.EqualTo(PublishMessageError.PUBLISH_RATE_LIMITED));
        Assert.That((await StoredMessageAsync(spaceId, news, eleventh, ct))!.PublishedAt, Is.Null, "a refused publish marked the post");
    }

    [Test, CancelAfter(240_000)]
    public async Task Deleting_either_channel_or_turning_the_source_into_text_ends_its_follows(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);

        var a  = await CreateChannelAsync(owner, spaceId, "news-a", ChannelType.Announcement, ct);
        var b  = await CreateChannelAsync(owner, spaceId, "news-b", ChannelType.Announcement, ct);
        var c  = await CreateChannelAsync(owner, spaceId, "news-c", ChannelType.Announcement, ct);
        var ta = await CreateChannelAsync(owner, spaceId, "into-a", ChannelType.Text, ct);
        var tb = await CreateChannelAsync(owner, spaceId, "into-b", ChannelType.Text, ct);
        var tc = await CreateChannelAsync(owner, spaceId, "into-c", ChannelType.Text, ct);

        await FollowAsync(owner, spaceId, a, spaceId, ta, ct);
        await FollowAsync(owner, spaceId, b, spaceId, tb, ct);
        await FollowAsync(owner, spaceId, c, spaceId, tc, ct);

        await owner.Channels.DeleteChannel(spaceId, a, ct).Ok();
        await owner.Channels.DeleteChannel(spaceId, tb, ct).Ok();
        Assert.That(await owner.Channels.SetChannelType(spaceId, c, ChannelType.Text, ct), Is.InstanceOf<SuccessUpdateChannel>());

        var post      = await owner.Channels.SendMessage(spaceId, b, "anyone there?", Entities(), NextRandomId(), null, ct).Ok();
        var published = await PublishAsync(owner, spaceId, b, post, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await FollowRowsAsync(a, ct), Is.Zero, "a deleted source kept its follows");
            Assert.That(await FollowRowsAsync(tb, ct), Is.Zero, "a deleted target kept its follows");
            Assert.That(await FollowRowsAsync(c, ct), Is.Zero, "a source turned into text kept its followers");
            Assert.That((await FollowsOf(owner).GetFollowedSources(spaceId, ta, ct)).Values, Is.Empty);
            Assert.That((await FollowsOf(owner).GetFollowers(spaceId, b, ct)).Values, Is.Empty);
            Assert.That((await FollowsOf(owner).GetFollowedSources(spaceId, tc, ct)).Values, Is.Empty);
            Assert.That(published.targetCount, Is.Zero, "a deleted target was still delivered to");
        });

        // Turning it back does not bring the followers back.
        Assert.That(await owner.Channels.SetChannelType(spaceId, c, ChannelType.Announcement, ct), Is.InstanceOf<SuccessUpdateChannel>());
        Assert.That((await FollowsOf(owner).GetFollowers(spaceId, c, ct)).Values, Is.Empty);
    }

    [Test, CancelAfter(180_000)]
    public async Task Deleting_a_space_ends_the_follows_of_its_channels(CancellationToken ct = default)
    {
        var p = await PairAsync(ct);
        await FollowAsync(p, ct);

        await Grains.GetGrain<ISpaceGrain>(p.SourceSpaceId).DeleteSpace();

        Assert.That(await FollowRowsAsync(p.TargetChannelId, ct), Is.Zero, "the follows of a deleted space survived it");
        Assert.That((await FollowsOf(p.Admin).GetFollowedSources(p.TargetSpaceId, p.TargetChannelId, ct)).Values, Is.Empty);
    }

    [Test, CancelAfter(180_000)]
    public async Task Either_side_unfollows_with_ManageChannels_on_its_own_channel(CancellationToken ct = default)
    {
        var p      = await PairAsync(ct);
        var member = await CreateSessionAsync(ct);
        await JoinAsync(p.Admin, member, p.TargetSpaceId, ct);
        var other = await CreateChannelAsync(p.Admin, p.TargetSpaceId, "other", ChannelType.Text, ct);

        var link = await FollowAsync(p, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await FollowsOf(p.Publisher).GetFollowers(p.SourceSpaceId, p.SourceChannelId, ct)).Values.Select(l => l.followId),
                Is.EqualTo(new[] { link.followId }));
            Assert.That((await FollowsOf(p.Admin).GetFollowers(p.SourceSpaceId, p.SourceChannelId, ct)).Values, Is.Empty,
                "a member without ManageChannels listed the followers");
            Assert.That((await FollowsOf(member).GetFollowedSources(p.TargetSpaceId, p.TargetChannelId, ct)).Values.Select(l => l.followId),
                Is.EqualTo(new[] { link.followId }));
        });

        var byMember  = await FollowsOf(member).RemoveFollow(p.TargetSpaceId, p.TargetChannelId, link.followId, ct);
        var elsewhere = await FollowsOf(p.Admin).RemoveFollow(p.TargetSpaceId, other, link.followId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(byMember), Is.EqualTo(RemoveFollowError.INSUFFICIENT_PERMISSIONS));
            Assert.That(ErrorOf(elsewhere), Is.EqualTo(RemoveFollowError.FOLLOW_NOT_FOUND), "a follow was removed through a channel it does not involve");
        });

        // The source side cuts it.
        Assert.That(await FollowsOf(p.Publisher).RemoveFollow(p.SourceSpaceId, p.SourceChannelId, link.followId, ct),
            Is.InstanceOf<SuccessRemoveFollow>());
        Assert.That((await FollowsOf(p.Admin).GetFollowedSources(p.TargetSpaceId, p.TargetChannelId, ct)).Values, Is.Empty);

        // The target side cuts the next one.
        var again = await FollowAsync(p, ct);
        Assert.That(await FollowsOf(p.Admin).RemoveFollow(p.TargetSpaceId, p.TargetChannelId, again.followId, ct),
            Is.InstanceOf<SuccessRemoveFollow>());

        var gone = await FollowsOf(p.Admin).RemoveFollow(p.TargetSpaceId, p.TargetChannelId, again.followId, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(ErrorOf(gone), Is.EqualTo(RemoveFollowError.FOLLOW_NOT_FOUND));
            Assert.That((await FollowsOf(p.Publisher).GetFollowers(p.SourceSpaceId, p.SourceChannelId, ct)).Values, Is.Empty);
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task A_copy_lives_in_the_target_as_its_own_message(CancellationToken ct = default)
    {
        var p      = await PairAsync(ct);
        var reader = await CreateSessionAsync(ct);
        await JoinAsync(p.Admin, reader, p.TargetSpaceId, ct);

        await FollowAsync(p, ct);

        var post = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "Raid at 20:00", Entities(), NextRandomId(), null, ct).Ok();
        await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);

        var copy = (await CopiesAsync(reader, p.TargetSpaceId, p.TargetChannelId, 1, ct)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(copy.sender, Is.EqualTo(p.Publisher.UserId));
            Assert.That(copy.text, Is.EqualTo("Raid at 20:00"));
            Assert.That(copy.crosspost!.sourceMessageId, Is.EqualTo(post));
            Assert.That(copy.crosspost.sourceChannelName, Is.EqualTo("news"));
            Assert.That(copy.publishedAt, Is.Null, "the copy claims to be published itself");
        });

        // Unread for someone who has read nothing, like any other message.
        var unread = await PollAsync(async () => (await reader.Users.GetGlobalBadges(ct)).spaces.Values.FirstOrDefault(s => s.spaceId == p.TargetSpaceId),
            b => b is { unreadChannelCount: > 0 }, TimeSpan.FromSeconds(30), ct);
        Assert.That(unread?.unreadChannelCount, Is.EqualTo(1), "the copy did not make the target channel unread");

        // Reactions and replies work on it.
        Assert.That(await reader.Channels.AddReaction(p.TargetSpaceId, p.TargetChannelId, copy.messageId, "👍", ct),
            Is.InstanceOf<SuccessAddReaction>());
        var reply = await reader.Channels.SendMessage(p.TargetSpaceId, p.TargetChannelId, "see you there", Entities(), NextRandomId(),
            copy.messageId, ct).Ok();

        // A reply to the copy does not reach its author, who is not in this space; a mention sent after it
        // shows the reply's fan-out has run.
        await reader.Channels.SendMessage(p.TargetSpaceId, p.TargetChannelId, "@admin", Entities(
            new MessageEntityMention(EntityType.Mention, 0, 6, 1, p.Admin.UserId)), NextRandomId(), null, ct).Ok();
        Assert.That(await PollAsync(() => MentionsAsync(p.Admin, p.TargetChannelId, ct), n => n >= 1, Window, ct), Is.EqualTo(1));
        await using (var db = await DbAsync(ct))
        {
            var authorMentions = await db.ChannelReadStates
               .Where(r => r.UserId == p.Publisher.UserId && r.ChannelId == p.TargetChannelId)
               .SumAsync(r => r.MentionCount, ct);
            Assert.That(authorMentions, Is.Zero, "a reply to a copy pinged the source's author");
        }

        // What happens to the source afterwards stays there.
        Assert.That(await p.Publisher.Channels.EditMessage(p.SourceSpaceId, p.SourceChannelId, post, "Raid cancelled", Entities(), ct),
            Is.InstanceOf<SuccessEditMessage>());
        await p.Publisher.Channels.DeleteMessage(p.SourceSpaceId, p.SourceChannelId, post, ct);

        // The copy is the target's: its author does not edit or take it down there, the target's moderators do.
        var editCopy   = await p.Publisher.Channels.EditMessage(p.TargetSpaceId, p.TargetChannelId, copy.messageId, "rewritten", Entities(), ct);
        var deleteCopy = await p.Publisher.Channels.DeleteMessage(p.TargetSpaceId, p.TargetChannelId, copy.messageId, ct);

        var history = (await reader.Channels.QueryMessages(p.TargetSpaceId, p.TargetChannelId, null, 10, ct)).Values;

        Assert.Multiple(() =>
        {
            Assert.That((editCopy as FailedEditMessage)?.error, Is.EqualTo(EditMessageError.NOT_AUTHOR), "the author rewrote a copy");
            Assert.That((deleteCopy as FailedDeleteMessage)?.error, Is.EqualTo(DeleteMessageError.INSUFFICIENT_PERMISSIONS),
                "the author took a copy down in a space they are not in");
            Assert.That(history.Single(m => m.messageId == copy.messageId).text, Is.EqualTo("Raid at 20:00"), "an edit of the source reached the copy");
            Assert.That(history.Single(m => m.messageId == reply).replyId, Is.EqualTo(copy.messageId));
        });

        Assert.That(await p.Admin.Channels.DeleteMessage(p.TargetSpaceId, p.TargetChannelId, copy.messageId, ct), Is.InstanceOf<SuccessDeleteMessage>());
    }

    [Test, CancelAfter(180_000)]
    public async Task A_bot_in_the_target_space_sees_where_a_crosspost_came_from(CancellationToken ct = default)
    {
        var p = await PairAsync(ct);
        await FollowAsync(p, ct);

        var bot = await ChannelTestBot.SeedAsync(p.Admin.UserId, ct);
        await bot.JoinAsync(p.TargetSpaceId);

        await using var events = await bot.OpenEventsAsync(ct);

        var plain = await p.Admin.Channels.SendMessage(p.TargetSpaceId, p.TargetChannelId, "hello", Entities(), NextRandomId(), null, ct).Ok();
        var post  = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "Patch 1.2 is out", Entities(), NextRandomId(), null, ct).Ok();
        await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);

        var copyFrame   = await events.WaitForAsync("messageCreate", d => d["message"]?["crosspost"] is JObject, Window, ct);
        var normalFrame = await events.WaitForAsync("messageCreate", d => (long?)d["message"]?["messageId"] == plain, Window, ct);

        var crosspost = (JObject)copyFrame["message"]!["crosspost"]!;

        Assert.Multiple(() =>
        {
            Assert.That((string?)copyFrame["channelId"], Is.EqualTo(p.TargetChannelId.ToString()));
            Assert.That((string?)copyFrame["message"]!["text"], Is.EqualTo("Patch 1.2 is out"));
            Assert.That((string?)crosspost["sourceSpaceId"], Is.EqualTo(p.SourceSpaceId.ToString()));
            Assert.That((string?)crosspost["sourceChannelId"], Is.EqualTo(p.SourceChannelId.ToString()));
            Assert.That((long?)crosspost["sourceMessageId"], Is.EqualTo(post));
            Assert.That((string?)crosspost["sourceChannelName"], Is.EqualTo("news"));
            Assert.That((string?)crosspost["sourceSpaceName"], Is.Not.Empty);
            Assert.That(normalFrame["message"]?["crosspost"] is null or { Type: JTokenType.Null }, Is.True,
                "an ordinary message carried a crosspost source");
        });
    }
}
