namespace ArgonComplexTest.Tests;

using System.Globalization;
using System.Security.Cryptography;
using Argon.Api.Features.Utils;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Clustering.Regions;
using Argon.Grains;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Argon.Grains.Persistence.States;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using static ChannelTestKit;

/// <summary>
/// Following announcement channels: who may link a source to a target, the limits, publishing a post
/// into every follower without pinging anyone there, and the links going away with either channel or
/// with their creator's access.
/// </summary>
/// <remarks>
/// A publish only hands the post to its delivery job, so copies are awaited: by polling the target,
/// or by <see cref="DeliveredAsync"/>, which waits for the job to drop its reminder.
/// </remarks>
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

    private static GrainId DeliveryGrain(Guid channelId, long messageId)
        => Grains.GetGrain<ICrosspostDeliveryGrain>(channelId, messageId.ToString(CultureInfo.InvariantCulture)).GetGrainId();

    private static Task<ReminderEntry?> DeliveryReminderAsync(Guid channelId, long messageId)
        => Services.GetRequiredService<IReminderTable>().ReadRow(DeliveryGrain(channelId, messageId), CrosspostDeliveryGrain.ReminderName)!;

    /// <summary>Waits for the delivery of a published post to finish; its job drops its reminder when it does.</summary>
    private static async Task DeliveredAsync(Guid channelId, long messageId, CancellationToken ct)
        => Assert.That(await PollAsync(() => DeliveryReminderAsync(channelId, messageId), r => r is null, TimeSpan.FromSeconds(90), ct),
            Is.Null, "the delivery did not finish");

    /// <summary>The reminder tick, delivered as the reminder service would; it runs every page left.</summary>
    private static Task TickDeliveryAsync(Guid channelId, long messageId)
    {
        var now = DateTime.UtcNow;
        return Grains.GetGrain<IRemindable>(DeliveryGrain(channelId, messageId))
           .ReceiveReminder(CrosspostDeliveryGrain.ReminderName, new TickStatus(now, TimeSpan.FromMinutes(1), now));
    }

    private static async Task<int> StoredCopiesAsync(IEnumerable<Guid> channels, CancellationToken ct)
    {
        var ids = channels.ToList();
        await using var db = await DbAsync(ct);
        return await db.Messages.CountAsync(m => ids.Contains(m.ChannelId) && m.Crosspost != null && !m.IsDeleted, ct);
    }

    private const string DeliveryStateName = "crosspost-delivery";

    private static IGrainStorage DeliveryStore()
        => Services.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    private static async Task<Dictionary<Guid, int>> CopiesPerChannelAsync(Guid spaceId, List<Guid> channels, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.Messages
           .Where(m => m.SpaceId == spaceId && channels.Contains(m.ChannelId) && m.Crosspost != null)
           .GroupBy(m => m.ChannelId)
           .Select(g => new { g.Key, Count = g.Count() })
           .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    private static async Task<bool> FollowExistsAsync(Guid followId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.ChannelFollows.AnyAsync(f => f.Id == followId, ct);
    }

    /// <summary>Channels written straight into the table, for counts no test should create one call at a time.</summary>
    private static async Task<List<Guid>> SeedChannelsAsync(Guid spaceId, Guid creatorId, ChannelType type, int count, CancellationToken ct)
    {
        var ids = Enumerable.Range(0, count).Select(_ => ArgonId.New()).ToList();
        var now = DateTimeOffset.UtcNow;

        await using var db = await DbAsync(ct);
        db.Channels.AddRange(ids.Select((id, i) => new ChannelEntity
        {
            Id              = id,
            SpaceId         = spaceId,
            CreatorId       = creatorId,
            ChannelType     = type,
            Name            = $"seeded-{i}",
            FractionalIndex = FractionalIndex.Min().Value,
            CreatedAt       = now,
            UpdatedAt       = now
        }));
        await db.SaveChangesAsync(ct);

        return ids;
    }

    private static ChannelFollowEntity Link(Guid sourceSpaceId, Guid sourceChannelId, Guid targetSpaceId, Guid targetChannelId, Guid creatorId,
        DateTimeOffset createdAt)
        => new()
        {
            Id              = ArgonId.New(),
            SourceSpaceId   = sourceSpaceId,
            SourceChannelId = sourceChannelId,
            TargetSpaceId   = targetSpaceId,
            TargetChannelId = targetChannelId,
            CreatorId       = creatorId,
            CreatedAt       = createdAt
        };

    private static async Task SeedFollowsAsync(IEnumerable<ChannelFollowEntity> links, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        db.ChannelFollows.AddRange(links);
        await db.SaveChangesAsync(ct);
    }

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
            Assert.That(ErrorOf(unknown), Is.EqualTo(FollowChannelError.NO_ACCESS_TO_SOURCE),
                "a missing source answered differently from one the caller cannot read");
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
            Assert.That(published.deliveredCount, Is.Zero, "delivery ran inside the publish call");
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
            Assert.That(own.targetCount, Is.EqualTo(1));
            Assert.That(moderated.targetCount, Is.EqualTo(1));
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
            Assert.That(copy.crosspost.hideAuthor, Is.False);
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
            Assert.That((editCopy as FailedEditMessage)?.error, Is.EqualTo(EditMessageError.INSUFFICIENT_PERMISSIONS),
                "the author rewrote a copy in a space they are not in");
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

    [Test, CancelAfter(180_000)]
    public async Task An_author_who_may_no_longer_post_there_does_not_publish(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var general = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        await JoinAsync(owner, member, spaceId, ct);

        var old = await member.Channels.SendMessage(spaceId, general, "from before", Entities(), NextRandomId(), null, ct).Ok();

        // Converting the channel takes posting away from everyone, past authors included.
        Assert.That(await owner.Channels.SetChannelType(spaceId, general, ChannelType.Announcement, ct), Is.InstanceOf<SuccessUpdateChannel>());

        var refused = await FollowsOf(member).PublishMessage(spaceId, general, old, ct);

        Assert.That(ErrorOf(refused), Is.EqualTo(PublishMessageError.INSUFFICIENT_PERMISSIONS), "an author without SendMessages published");
        Assert.That((await StoredMessageAsync(spaceId, general, old, ct))!.PublishedAt, Is.Null, "a refused publish marked the post");

        // A moderator still may.
        await PublishAsync(owner, spaceId, general, old, ct);
    }

    [Test, CancelAfter(240_000)]
    public async Task A_private_source_is_never_followed_and_a_follow_ends_when_its_source_turns_private(CancellationToken ct = default)
    {
        var p        = await PairAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        await JoinAsync(p.Publisher, stranger, p.SourceSpaceId, ct);

        var vault = await CreateChannelAsync(p.Publisher, p.SourceSpaceId, "vault", ChannelType.Announcement, ct);
        var desk  = await CreateChannelAsync(p.Admin, p.TargetSpaceId, "desk", ChannelType.Text, ct);

        // Opt-in: allowing ViewChannel to one role is what hides the channel from "everyone".
        var roles    = ArchetypesOf(p.Publisher);
        var insiders = await roles.CreateArchetype(p.SourceSpaceId, "insiders", ct).Ok();
        await roles.UpsertArchetypeEntitlementForChannel(p.SourceSpaceId, vault, insiders.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.ViewChannel, ct);
        Assert.That(await roles.SetArchetypeToMember(p.SourceSpaceId, await MemberIdOfAsync(p.Publisher, p.SourceSpaceId, p.Admin.UserId, ct),
            insiders.id, true, ct), Is.True);

        var reader  = await FollowsOf(p.Admin).FollowChannel(p.SourceSpaceId, vault, p.TargetSpaceId, desk, ct);
        var outside = await FollowsOf(stranger).FollowChannel(p.SourceSpaceId, vault, p.TargetSpaceId, desk, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(reader), Is.EqualTo(FollowChannelError.SOURCE_PRIVATE), "a reader of a private channel followed it");
            Assert.That(ErrorOf(outside), Is.EqualTo(FollowChannelError.NO_ACCESS_TO_SOURCE));
        });

        // Managing it does not make a private channel followable either.
        await roles.UpsertArchetypeEntitlementForChannel(p.SourceSpaceId, vault, insiders.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.ViewChannel | ArgonEntitlement.ManageChannels, ct);
        var manager = await FollowsOf(p.Admin).FollowChannel(p.SourceSpaceId, vault, p.TargetSpaceId, desk, ct);
        Assert.That(ErrorOf(manager), Is.EqualTo(FollowChannelError.SOURCE_PRIVATE), "a manager of a private channel followed it");

        // A public source takes reading it and nothing more, until it turns private under the follow.
        var link = await FollowAsync(p, ct);
        await roles.UpsertArchetypeEntitlementForChannel(p.SourceSpaceId, p.SourceChannelId, insiders.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.ViewChannel, ct);

        var post      = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "members only now", Entities(), NextRandomId(), null, ct).Ok();
        var published = await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);
        await DeliveredAsync(p.SourceChannelId, post, ct);

        Assert.That(published.targetCount, Is.EqualTo(1));
        Assert.That(await StoredCopiesAsync([p.TargetChannelId], ct), Is.Zero, "a post was copied out of a source that turned private");
        Assert.That(await FollowExistsAsync(link.followId, ct), Is.False, "the follow survived its creator failing the rule");
    }

    [Test, CancelAfter(240_000)]
    public async Task A_follow_whose_creator_lost_access_at_either_end_is_dropped_at_the_next_publish(CancellationToken ct = default)
    {
        var p       = await PairAsync(ct);
        var manager = await CreateSessionAsync(ct);
        await JoinAsync(p.Publisher, manager, p.SourceSpaceId, ct);
        await JoinAsync(p.Admin, manager, p.TargetSpaceId, ct);

        var bulletin = await CreateChannelAsync(p.Admin, p.TargetSpaceId, "bulletin", ChannelType.Text, ct);
        var lounge   = await CreateChannelAsync(p.Publisher, p.SourceSpaceId, "lounge", ChannelType.Text, ct);

        var targetRoles = ArchetypesOf(p.Admin);
        var managers    = await targetRoles.CreateArchetype(p.TargetSpaceId, "managers", ct).Ok();
        managers = await targetRoles.UpdateArchetype(p.TargetSpaceId, managers with { entitlement = ArgonEntitlement.ManageChannels }, ct).Ok();
        var managerId = await MemberIdOfAsync(p.Admin, p.TargetSpaceId, manager.UserId, ct);
        Assert.That(await targetRoles.SetArchetypeToMember(p.TargetSpaceId, managerId, managers.id, true, ct), Is.True);

        var byAdmin   = await FollowAsync(p, ct);
        var byManager = await FollowAsync(manager, p.SourceSpaceId, p.SourceChannelId, p.TargetSpaceId, bulletin, ct);
        var byOwner   = await FollowAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, p.SourceSpaceId, lounge, ct);

        // The admin can no longer read the source; the manager can no longer manage the target.
        var sourceRoles = ArchetypesOf(p.Publisher);
        var outcasts    = await sourceRoles.CreateArchetype(p.SourceSpaceId, "outcasts", ct).Ok();
        await sourceRoles.UpsertArchetypeEntitlementForChannel(p.SourceSpaceId, p.SourceChannelId, outcasts.id,
            deny: ArgonEntitlement.ViewChannel, allow: ArgonEntitlement.None, ct);
        Assert.That(await sourceRoles.SetArchetypeToMember(p.SourceSpaceId, await MemberIdOfAsync(p.Publisher, p.SourceSpaceId, p.Admin.UserId, ct),
            outcasts.id, true, ct), Is.True);
        Assert.That(await targetRoles.SetArchetypeToMember(p.TargetSpaceId, managerId, managers.id, false, ct), Is.True);

        var post      = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "after the reshuffle", Entities(), NextRandomId(), null, ct).Ok();
        var published = await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);
        await DeliveredAsync(p.SourceChannelId, post, ct);

        Assert.That(published.targetCount, Is.EqualTo(3));
        Assert.That(await CopiesAsync(p.Publisher, p.SourceSpaceId, lounge, 1, ct), Has.Count.EqualTo(1), "a follow in good standing lost its copy");
        Assert.That(await StoredCopiesAsync([p.TargetChannelId], ct), Is.Zero, "a follower who cannot read the source still got the post");
        Assert.That(await StoredCopiesAsync([bulletin], ct), Is.Zero, "a follow made by someone who no longer manages the target still delivered");
        Assert.That(await FollowExistsAsync(byAdmin.followId, ct), Is.False);
        Assert.That(await FollowExistsAsync(byManager.followId, ct), Is.False);
        Assert.That(await FollowExistsAsync(byOwner.followId, ct), Is.True);
    }

    [Test, CancelAfter(180_000)]
    public async Task A_follow_whose_creator_is_no_longer_a_member_of_the_source_space_delivers_nothing(CancellationToken ct = default)
    {
        var p      = await PairAsync(ct);
        var lounge = await CreateChannelAsync(p.Publisher, p.SourceSpaceId, "lounge", ChannelType.Text, ct);

        var link = await FollowAsync(p, ct);
        await FollowAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, p.SourceSpaceId, lounge, ct);

        // Removed by a path that does not tidy up after itself, so only the check at delivery stands.
        await using (var db = await DbAsync(ct))
        {
            await db.UsersToServerRelations
               .Where(m => m.SpaceId == p.SourceSpaceId && m.UserId == p.Admin.UserId)
               .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true), ct);
        }
        await Services.GetRequiredService<Argon.Services.L1L2.IPermissionCache>().InvalidateMemberAsync(p.SourceSpaceId, p.Admin.UserId);

        var post = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "members only", Entities(), NextRandomId(), null, ct).Ok();
        await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);
        await DeliveredAsync(p.SourceChannelId, post, ct);

        Assert.That(await CopiesAsync(p.Publisher, p.SourceSpaceId, lounge, 1, ct), Has.Count.EqualTo(1));
        Assert.That(await StoredCopiesAsync([p.TargetChannelId], ct), Is.Zero, "a removed member's follow still delivered");
        Assert.That(await FollowExistsAsync(link.followId, ct), Is.False);
    }

    [Test, CancelAfter(180_000)]
    public async Task Leaving_a_space_ends_the_follows_its_member_made_of_that_space(CancellationToken ct = default)
    {
        var p      = await PairAsync(ct);
        var lounge = await CreateChannelAsync(p.Publisher, p.SourceSpaceId, "lounge", ChannelType.Text, ct);
        var own    = await CreateChannelAsync(p.Admin, p.TargetSpaceId, "own-news", ChannelType.Announcement, ct);
        var desk   = await CreateChannelAsync(p.Admin, p.TargetSpaceId, "desk", ChannelType.Text, ct);

        var leaving   = await FollowAsync(p, ct);
        var someone   = await FollowAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, p.SourceSpaceId, lounge, ct);
        var elsewhere = await FollowAsync(p.Admin, p.TargetSpaceId, own, p.TargetSpaceId, desk, ct);

        await Grains.GetGrain<ISpaceGrain>(p.SourceSpaceId).RemoveMemberAsync(p.Admin.UserId);

        Assert.That(await FollowExistsAsync(leaving.followId, ct), Is.False, "a follow outlived its creator leaving the source space");
        Assert.That(await FollowExistsAsync(someone.followId, ct), Is.True, "another member's follow went too");
        Assert.That(await FollowExistsAsync(elsewhere.followId, ct), Is.True, "a follow of another space's channel went too");

        var post      = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "after they left", Entities(), NextRandomId(), null, ct).Ok();
        var published = await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);
        await DeliveredAsync(p.SourceChannelId, post, ct);

        Assert.That(published.targetCount, Is.EqualTo(1));
        Assert.That(await StoredCopiesAsync([p.TargetChannelId], ct), Is.Zero);
    }

    [Test, CancelAfter(300_000)]
    public async Task Delivery_to_more_targets_than_a_page_lands_once_in_each(CancellationToken ct = default)
    {
        var p       = await PairAsync(ct);
        var targets = await SeedChannelsAsync(p.TargetSpaceId, p.Admin.UserId, ChannelType.Text, CrosspostDeliveryGrain.PageSize + 20, ct);

        var since = DateTimeOffset.UtcNow;
        await SeedFollowsAsync(targets.Select(t => Link(p.SourceSpaceId, p.SourceChannelId, p.TargetSpaceId, t, p.Admin.UserId, since)), ct);

        var post      = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "to everyone", Entities(), NextRandomId(), null, ct).Ok();
        var published = await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);
        await DeliveredAsync(p.SourceChannelId, post, ct);

        var perTarget = await CopiesPerChannelAsync(p.TargetSpaceId, targets, ct);

        Assert.Multiple(() =>
        {
            Assert.That(published.targetCount, Is.EqualTo(targets.Count));
            Assert.That(published.deliveredCount, Is.Zero);
            Assert.That(targets.Select(t => perTarget.GetValueOrDefault(t)), Has.All.EqualTo(1), "a target missed the post or got it twice");
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task A_delivery_resumes_from_its_last_persisted_page_on_a_fresh_activation(CancellationToken ct = default)
    {
        var p       = await PairAsync(ct);
        var targets = await SeedChannelsAsync(p.TargetSpaceId, p.Admin.UserId, ChannelType.Text, CrosspostDeliveryGrain.PageSize + 220, ct);

        var since = DateTimeOffset.UtcNow;
        await SeedFollowsAsync(targets.Select(t => Link(p.SourceSpaceId, p.SourceChannelId, p.TargetSpaceId, t, p.Admin.UserId, since)), ct);

        var post = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "after the crash", Entities(), NextRandomId(), null, ct).Ok();

        List<(Guid Id, Guid Target)> follows;
        await using (var db = await DbAsync(ct))
        {
            await db.Messages
               .Where(m => m.SpaceId == p.SourceSpaceId && m.ChannelId == p.SourceChannelId && m.MessageId == post)
               .ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow), ct);

            follows = (await db.ChannelFollows
                   .Where(f => f.SourceChannelId == p.SourceChannelId)
                   .OrderBy(f => f.Id)
                   .Select(f => new { f.Id, f.TargetChannelId })
                   .ToListAsync(ct))
               .Select(f => (f.Id, f.TargetChannelId))
               .ToList();
        }

        var order  = follows.Select(f => f.Target).ToList();
        var cursor = follows[199].Id;

        // What a silo that died mid-page leaves: 200 follows behind the persisted cursor, and 100 past it
        // already delivered before the cursor could move.
        var draft = new CrosspostDraft(p.Publisher.UserId, "after the crash", [], new MessageCrosspost
        {
            SourceSpaceId   = p.SourceSpaceId,
            SourceChannelId = p.SourceChannelId,
            SourceMessageId = post
        });
        foreach (var target in order.Skip(200).Take(100))
            await Grains.GetGrain<IChannelGrain>(target).ReceiveCrosspostAsync(draft);

        var grainId = DeliveryGrain(p.SourceChannelId, post);
        var stored  = new GrainState<CrosspostDeliveryState>(new CrosspostDeliveryState
        {
            SourceSpaceId = p.SourceSpaceId,
            StartedAt     = DateTimeOffset.UtcNow,
            Cursor        = cursor,
            Delivered     = 200
        });
        await DeliveryStore().WriteStateAsync(DeliveryStateName, grainId, stored);

        await TickDeliveryAsync(p.SourceChannelId, post);

        var perTarget = await CopiesPerChannelAsync(p.TargetSpaceId, targets, ct);
        var left      = new GrainState<CrosspostDeliveryState>(new CrosspostDeliveryState());
        await DeliveryStore().ReadStateAsync(DeliveryStateName, grainId, left);

        Assert.Multiple(() =>
        {
            Assert.That(order.Take(200).Select(t => perTarget.GetValueOrDefault(t)), Has.All.Zero, "the job started over instead of resuming");
            Assert.That(order.Skip(200).Select(t => perTarget.GetValueOrDefault(t)), Has.All.EqualTo(1),
                "a target past the cursor missed the post or got it twice");
            Assert.That(left.State.StartedAt, Is.Null, "the finished job left its state behind");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_member_of_the_target_cannot_stand_in_for_the_copy(CancellationToken ct = default)
    {
        var p      = await PairAsync(ct);
        var reader = await CreateSessionAsync(ct);
        await JoinAsync(p.Admin, reader, p.TargetSpaceId, ct);
        await FollowAsync(p, ct);

        var post = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "the real one", Entities(), NextRandomId(), null, ct).Ok();

        // The randomId a copy used to be deduplicated by: a hash of public ids that anyone could work out.
        var key = new byte[24];
        p.SourceChannelId.TryWriteBytes(key);
        BitConverter.TryWriteBytes(key.AsSpan(16), post);
        var predictable = BitConverter.ToInt64(SHA256.HashData(key)) & long.MaxValue;

        var decoy = await reader.Channels.SendMessage(p.TargetSpaceId, p.TargetChannelId, "a decoy", Entities(), predictable, null, ct).Ok();

        await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);
        await DeliveredAsync(p.SourceChannelId, post, ct);

        var copy = (await CopiesAsync(p.Admin, p.TargetSpaceId, p.TargetChannelId, 1, ct)).SingleOrDefault();

        Assert.That(copy, Is.Not.Null, "a message sent ahead with the copy's id took its place");
        Assert.Multiple(() =>
        {
            Assert.That(copy!.messageId, Is.Not.EqualTo(decoy));
            Assert.That(copy.text, Is.EqualTo("the real one"));
            Assert.That(copy.crosspost!.sourceMessageId, Is.EqualTo(post));
        });

        // A redelivery, as after a crash mid-page, finds the copy instead of making another.
        var draft = new CrosspostDraft(p.Publisher.UserId, "the real one", [], new MessageCrosspost
        {
            SourceSpaceId   = p.SourceSpaceId,
            SourceChannelId = p.SourceChannelId,
            SourceMessageId = post
        });
        var again = await Grains.GetGrain<IChannelGrain>(p.TargetChannelId).ReceiveCrosspostAsync(draft);

        Assert.That(again, Is.EqualTo(copy!.messageId));
        Assert.That(await StoredCopiesAsync([p.TargetChannelId], ct), Is.EqualTo(1));
    }

    [Test, CancelAfter(180_000)]
    public async Task A_copy_of_a_post_that_hides_its_author_names_nobody(CancellationToken ct = default)
    {
        var p = await PairAsync(ct);
        Assert.That(await p.Publisher.Channels.SetAnnouncementSettings(p.SourceSpaceId, p.SourceChannelId, true, true, false, ct),
            Is.InstanceOf<SuccessUpdateChannel>());
        await FollowAsync(p, ct);

        var bot = await ChannelTestBot.SeedAsync(p.Admin.UserId, ct);
        await bot.JoinAsync(p.TargetSpaceId);
        await using var events = await bot.OpenEventsAsync(ct);

        var post = await p.Publisher.Channels.SendMessage(p.SourceSpaceId, p.SourceChannelId, "from the team", Entities(), NextRandomId(), null, ct).Ok();
        await PublishAsync(p.Publisher, p.SourceSpaceId, p.SourceChannelId, post, ct);

        var copy  = (await CopiesAsync(p.Admin, p.TargetSpaceId, p.TargetChannelId, 1, ct)).Single();
        var frame = await events.WaitForAsync("messageCreate", d => d["message"]?["crosspost"] is JObject, Window, ct);

        Assert.Multiple(() =>
        {
            Assert.That(copy.crosspost!.hideAuthor, Is.True);
            Assert.That(copy.sender, Is.EqualTo(UserEntity.SystemUser), "the copy named the author the source hides");
            Assert.That((string?)frame["message"]?["sender"]?["userId"], Is.Not.EqualTo(p.Publisher.UserId.ToString()),
                "a bot in the target learned the hidden author");
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task The_follow_lists_show_the_newest_two_hundred(CancellationToken ct = default)
    {
        var p     = await PairAsync(ct);
        var count = ChannelFollowGrain.MaxListed + 5;
        var start = DateTimeOffset.UtcNow.AddHours(-1);

        var targets = await SeedChannelsAsync(p.TargetSpaceId, p.Admin.UserId, ChannelType.Text, count, ct);
        var sources = await SeedChannelsAsync(p.SourceSpaceId, p.Publisher.UserId, ChannelType.Announcement, count, ct);

        await SeedFollowsAsync(targets.Select((t, i) =>
            Link(p.SourceSpaceId, p.SourceChannelId, p.TargetSpaceId, t, p.Admin.UserId, start.AddSeconds(i))), ct);
        await SeedFollowsAsync(sources.Select((s, i) =>
            Link(p.SourceSpaceId, s, p.TargetSpaceId, p.TargetChannelId, p.Admin.UserId, start.AddSeconds(i))), ct);

        var followers = (await FollowsOf(p.Publisher).GetFollowers(p.SourceSpaceId, p.SourceChannelId, ct)).Values;
        var followed  = (await FollowsOf(p.Admin).GetFollowedSources(p.TargetSpaceId, p.TargetChannelId, ct)).Values;

        Assert.Multiple(() =>
        {
            Assert.That(followers.Select(l => l.targetChannelId), Is.EqualTo(Enumerable.Reverse(targets).Take(ChannelFollowGrain.MaxListed)));
            Assert.That(followed.Select(l => l.sourceChannelId), Is.EqualTo(Enumerable.Reverse(sources).Take(ChannelFollowGrain.MaxListed)));
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task A_source_takes_at_most_five_thousand_followers(CancellationToken ct = default)
    {
        var p     = await PairAsync(ct);
        var start = DateTimeOffset.UtcNow.AddHours(-1);

        // Rows alone: the cap counts links, whatever they point at.
        var seeded = Enumerable.Range(0, ChannelFollowEntity.MaxFollowersPerSource)
           .Select(_ => Link(p.SourceSpaceId, p.SourceChannelId, Guid.NewGuid(), ArgonId.New(), p.Publisher.UserId, start))
           .ToList();
        await SeedFollowsAsync(seeded, ct);

        var over = await FollowsOf(p.Admin).FollowChannel(p.SourceSpaceId, p.SourceChannelId, p.TargetSpaceId, p.TargetChannelId, ct);
        Assert.That(ErrorOf(over), Is.EqualTo(FollowChannelError.SOURCE_FOLLOWER_LIMIT), "a source took a follower past the cap");

        await using (var db = await DbAsync(ct))
            await db.ChannelFollows.Where(f => f.Id == seeded[0].Id).ExecuteDeleteAsync(ct);

        await FollowAsync(p, ct);
    }
}
