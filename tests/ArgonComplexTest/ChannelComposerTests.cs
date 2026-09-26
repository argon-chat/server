namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Runtime;
using static ChannelTestKit;
using ReportActionKind = ConsoleContracts.ReportActionKind;

/// <summary>
/// Author tools: posts scheduled for later and the server-side draft.
/// </summary>
/// <remarks>
/// A post cannot be scheduled less than a minute ahead, so "due" is reached by backdating its
/// <c>PublishAt</c> in the table and delivering the reminder tick straight to the grain, as the
/// reminder service would. The report base is for the operator console a ban is decided in.
/// </remarks>
[TestFixture]
public class ChannelComposerTests : ReportTestBase
{
    private const string ReminderName = "scheduled-posts";

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static IonArray<IMessageEntity> Entities(params IMessageEntity[] entities) => new(entities);

    private static MessageEntityMention Mention(Guid userId) => new(EntityType.Mention, 0, 6, 1, userId);

    private static MessageEntityBold Bold(int offset, int length) => new(EntityType.Bold, offset, length, 1);

    private static MessageEntityAttachment Attachment()
        => new(EntityType.Attachment, 0, 0, 1, Guid.NewGuid(), "photo.png", 1024, "image/png", 64, 64, null, null);

    private static IChannelComposerInteraction ComposerOf(TestUserSession session)
        => session.Client.ForService<IChannelComposerInteraction>(Services);

    private static DateTimeOffset In(TimeSpan span) => DateTimeOffset.UtcNow + span;

    private static GrainId ComposerGrain(Guid channelId) => Grains.GetGrain<IChannelComposerGrain>(channelId).GetGrainId();

    private static Task TickAsync(Guid channelId)
    {
        var now = DateTime.UtcNow;
        return Grains.GetGrain<IRemindable>(ComposerGrain(channelId))
           .ReceiveReminder(ReminderName, new TickStatus(now, TimeSpan.FromMinutes(1), now));
    }

    private static Task<ReminderEntry?> ReminderAsync(Guid channelId)
        => Services.GetRequiredService<IReminderTable>().ReadRow(ComposerGrain(channelId), ReminderName)!;

    private static async Task BackdateAsync(Guid postId, TimeSpan ago, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        var when = DateTimeOffset.UtcNow - ago;
        await db.ScheduledPosts.Where(p => p.Id == postId)
           .ExecuteUpdateAsync(s => s.SetProperty(p => p.PublishAt, when), ct);
    }

    private static async Task<ScheduledPostEntity> StoredPostAsync(Guid postId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.ScheduledPosts.AsNoTracking().SingleAsync(p => p.Id == postId, ct);
    }

    private static async Task<MessageDraftEntity?> StoredDraftAsync(Guid userId, Guid channelId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.MessageDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.UserId == userId && d.ChannelId == channelId, ct);
    }

    private static async Task DeactivateAsync(GrainId grain)
        => await Grains.GetGrain<IGrainManagementExtension>(grain).DeactivateOnIdle();

    private static GrainId DraftsGrain(Guid userId) => Grains.GetGrain<IMessageDraftsGrain>(userId).GetGrainId();

    /// <summary>Written straight into the row, as ReportRulesTests does.</summary>
    private static async Task LockDownAsync(Guid userId, LockdownReason reason, DateTimeOffset until, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
           .SetProperty(u => u.LockdownReason, reason)
           .SetProperty(u => u.LockDownExpiration, (DateTimeOffset?)until), ct);
    }

    private static ScheduledPost Scheduled(ISchedulePostResult result)
    {
        Assert.That(result, Is.InstanceOf<SuccessSchedulePost>(), $"refused: {(result as FailedSchedulePost)?.error}");
        return ((SuccessSchedulePost)result).post;
    }

    private static SchedulePostError? ErrorOf(ISchedulePostResult result) => (result as FailedSchedulePost)?.error;

    private async Task<(TestUserSession Owner, TestUserSession Guest, Guid SpaceId, Guid ChannelId)> TextChannelAsync(
        ChannelType kind, CancellationToken ct)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "composer", kind, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        return (owner, guest, spaceId, channelId);
    }

    private static async Task<Guid> RoleWithPostingAsync(TestUserSession owner, Guid spaceId, Guid channelId, Guid memberUserId,
        CancellationToken ct)
    {
        var archetypes = ArchetypesOf(owner);
        var role       = await archetypes.CreateArchetype(spaceId, "herald", ct).Ok();

        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, memberUserId, ct), role.id, true, ct),
            Is.True);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, role.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.SendMessages, ct);

        return role.id;
    }

    // ── Scheduling ──────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Scheduling_checks_the_time_window_the_content_and_who_may_post(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, textId) = await TextChannelAsync(ChannelType.Text, ct);
        var newsId  = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        var voiceId = await CreateChannelAsync(owner, spaceId, "lobby", ChannelType.Voice, ct);
        var filesId = await CreateChannelAsync(owner, spaceId, "no-files", ChannelType.Text, ct);
        await DenyOnChannelAsync(owner, spaceId, filesId, ArgonEntitlement.AttachFiles, ct);

        var composer = ComposerOf(guest);
        var later    = In(TimeSpan.FromMinutes(5));

        var tooSoon    = await composer.SchedulePost(spaceId, textId, "hi", Entities(), In(TimeSpan.FromSeconds(30)), ct);
        var tooLate    = await composer.SchedulePost(spaceId, textId, "hi", Entities(), In(TimeSpan.FromDays(31)), ct);
        var empty      = await composer.SchedulePost(spaceId, textId, "   ", Entities(), later, ct);
        var tooLong    = await composer.SchedulePost(spaceId, textId, new string('a', 4097), Entities(), later, ct);
        var tooMany    = await composer.SchedulePost(spaceId, textId, "album",
            Entities(Enumerable.Range(0, 11).Select(_ => (IMessageEntity)Attachment()).ToArray()), later, ct);
        var news       = await composer.SchedulePost(spaceId, newsId, "me too", Entities(), later, ct);
        var noFiles    = await composer.SchedulePost(spaceId, filesId, "photo", Entities(Attachment()), later, ct);
        var voice      = await composer.SchedulePost(spaceId, voiceId, "hi", Entities(), later, ct);
        var wrongSpace = await composer.SchedulePost(Guid.NewGuid(), textId, "hi", Entities(), later, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(tooSoon), Is.EqualTo(SchedulePostError.PUBLISH_TOO_SOON));
            Assert.That(ErrorOf(tooLate), Is.EqualTo(SchedulePostError.PUBLISH_TOO_LATE));
            Assert.That(ErrorOf(empty), Is.EqualTo(SchedulePostError.EMPTY_MESSAGE));
            Assert.That(ErrorOf(tooLong), Is.EqualTo(SchedulePostError.MESSAGE_TOO_LONG));
            Assert.That(ErrorOf(tooMany), Is.EqualTo(SchedulePostError.TOO_MANY_ATTACHMENTS));
            Assert.That(ErrorOf(news), Is.EqualTo(SchedulePostError.INSUFFICIENT_PERMISSIONS), "a reader scheduled an announcement");
            Assert.That(ErrorOf(noFiles), Is.EqualTo(SchedulePostError.INSUFFICIENT_PERMISSIONS), "a file went past a denied AttachFiles");
            Assert.That(ErrorOf(voice), Is.EqualTo(SchedulePostError.NOT_A_TEXT_CHANNEL));
            Assert.That(ErrorOf(wrongSpace), Is.EqualTo(SchedulePostError.CHANNEL_NOT_FOUND));
        });

        var post = Scheduled(await composer.SchedulePost(spaceId, textId, "see you", Entities(), later, ct));

        Assert.Multiple(() =>
        {
            Assert.That(post.status, Is.EqualTo(ScheduledPostStatus.PENDING));
            Assert.That(post.authorId, Is.EqualTo(guest.UserId));
            Assert.That((post.publishAt - later).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
            Assert.That(post.messageId, Is.Null);
        });

        Assert.That(await ReminderAsync(textId), Is.Not.Null, "scheduling a post armed no durable reminder");

        // The owner may post in the announcement channel, so a scheduled announcement is fine for them.
        Scheduled(await ComposerOf(owner).SchedulePost(spaceId, newsId, "patch notes", Entities(), later, ct));
    }

    [Test, CancelAfter(180_000)]
    public async Task An_author_holds_at_most_10_pending_posts_in_a_channel(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        var posts = new List<ScheduledPost>();
        for (var i = 0; i < 10; i++)
            posts.Add(Scheduled(await ComposerOf(owner).SchedulePost(spaceId, channelId, $"post {i}", Entities(), In(TimeSpan.FromHours(1 + i)), ct)));

        var byOwner = await ComposerOf(owner).SchedulePost(spaceId, channelId, "one more", Entities(), In(TimeSpan.FromDays(2)), ct);
        var byGuest = await ComposerOf(guest).SchedulePost(spaceId, channelId, "mine", Entities(), In(TimeSpan.FromDays(2)), ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(byOwner), Is.EqualTo(SchedulePostError.TOO_MANY_SCHEDULED));
            Assert.That(byGuest, Is.InstanceOf<SuccessSchedulePost>(), "one member's queue blocked everybody else's");
        });

        Assert.That(await ComposerOf(owner).CancelScheduledPost(spaceId, channelId, posts[0].postId, ct), Is.True);
        Scheduled(await ComposerOf(owner).SchedulePost(spaceId, channelId, "one more", Entities(), In(TimeSpan.FromDays(2)), ct));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_channel_holds_at_most_100_pending_posts(CancellationToken ct = default)
    {
        var (_, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        // 99 posts by ten other authors, seeded before the channel's composer first activates.
        await using (var db = await DbAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 99; i++)
                db.ScheduledPosts.Add(new ScheduledPostEntity
                {
                    Id = Guid.CreateVersion7(), SpaceId = spaceId, ChannelId = channelId, AuthorId = new Guid(i / 10 + 1, 0, 0, new byte[8]),
                    Text = $"seeded {i}", PublishAt = now.AddHours(1 + i), Status = ScheduledPostStatus.PENDING,
                    RandomId = i + 1, CreatedAt = now, UpdatedAt = now
                });
            await db.SaveChangesAsync(ct);
        }

        var last = await ComposerOf(guest).SchedulePost(spaceId, channelId, "the 100th", Entities(), In(TimeSpan.FromHours(1)), ct);
        var full = await ComposerOf(guest).SchedulePost(spaceId, channelId, "no room", Entities(), In(TimeSpan.FromHours(1)), ct);

        Assert.Multiple(() =>
        {
            Assert.That(last, Is.InstanceOf<SuccessSchedulePost>(), $"refused below the channel limit: {ErrorOf(last)}");
            Assert.That(ErrorOf(full), Is.EqualTo(SchedulePostError.TOO_MANY_SCHEDULED));
        });
    }

    // ── Publishing ──────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_due_post_goes_out_through_the_normal_send_as_its_author(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        await using var author = await RealtimeClient.ConnectAsync(guest, ct);

        var post = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "@owner look",
            Entities(Mention(owner.UserId)), In(TimeSpan.FromMinutes(10)), ct));

        await BackdateAsync(post.postId, TimeSpan.FromSeconds(1), ct);
        await TickAsync(channelId);

        var stored = await StoredPostAsync(post.postId, ct);
        var read   = (await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values;

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(ScheduledPostStatus.PUBLISHED));
            Assert.That(stored.MessageId, Is.Not.Null);
            Assert.That(read.Select(m => (m.messageId, m.sender, m.text)),
                Is.EqualTo(new[] { (stored.MessageId!.Value, guest.UserId, "@owner look") }),
                "the post did not land as the author's message");
        });

        // The mention went through the send's own fan-out.
        Assert.That(await PollAsync(() => MentionsAsync(owner, channelId, ct), n => n >= 1, Window, ct), Is.EqualTo(1),
            "the scheduled post did not ping the member it mentions");

        var told = await author.WaitForAsync<ScheduledPostUpdated>(
            e => e.post.postId == post.postId && e.post.status == ScheduledPostStatus.PUBLISHED, Window, ct: ct);
        Assert.That(told.post.messageId, Is.EqualTo(stored.MessageId));

        // Nothing pending is left, so nothing is armed, and a second tick sends nothing twice.
        Assert.That(await ReminderAsync(channelId), Is.Null, "the reminder outlived the last pending post");
        await TickAsync(channelId);
        Assert.That((await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values, Has.Count.EqualTo(1));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_post_outlives_the_activation_that_scheduled_it(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        var post = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "from the past", Entities(), In(TimeSpan.FromMinutes(3)), ct));

        await Grains.GetGrain<IGrainManagementExtension>(ComposerGrain(channelId)).DeactivateOnIdle();
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

        Assert.That(await ReminderAsync(channelId), Is.Not.Null, "the reminder did not survive the activation");

        await BackdateAsync(post.postId, TimeSpan.FromSeconds(1), ct);
        await TickAsync(channelId);

        var read = (await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values;
        Assert.That(read.Select(m => m.text), Is.EqualTo(new[] { "from the past" }));
        Assert.That((await StoredPostAsync(post.postId, ct)).Status, Is.EqualTo(ScheduledPostStatus.PUBLISHED));
    }

    [Test, CancelAfter(120_000)]
    public async Task An_author_who_lost_the_right_to_post_gets_a_failed_post_and_can_retry_it(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Announcement, ct);
        var roleId = await RoleWithPostingAsync(owner, spaceId, channelId, guest.UserId, ct);

        await using var author = await RealtimeClient.ConnectAsync(guest, ct);

        var post = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "breaking", Entities(), In(TimeSpan.FromMinutes(10)), ct));

        var memberId = await MemberIdOfAsync(owner, spaceId, guest.UserId, ct);
        Assert.That(await ArchetypesOf(owner).SetArchetypeToMember(spaceId, memberId, roleId, false, ct), Is.True);

        await BackdateAsync(post.postId, TimeSpan.FromSeconds(1), ct);
        await TickAsync(channelId);

        var stored = await StoredPostAsync(post.postId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(ScheduledPostStatus.FAILED));
            Assert.That(stored.Failure, Is.EqualTo(ScheduledPostFailure.INSUFFICIENT_PERMISSIONS));
            Assert.That(stored.MessageId, Is.Null);
        });
        Assert.That((await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values, Is.Empty,
            "a post went out after its author lost SendMessages");

        await author.WaitForAsync<ScheduledPostUpdated>(
            e => e.post.postId == post.postId && e.post.status == ScheduledPostStatus.FAILED, Window, ct: ct);

        var listed = (await ComposerOf(guest).GetScheduledPosts(spaceId, channelId, ct)).Values;
        Assert.That(listed.Select(p => (p.postId, p.status, p.failure)),
            Is.EqualTo(new[] { (post.postId, ScheduledPostStatus.FAILED, ScheduledPostFailure.INSUFFICIENT_PERMISSIONS) }),
            "the author cannot see why the post did not go out");

        var stillRefused = await ComposerOf(guest).ReschedulePost(spaceId, channelId, post.postId, In(TimeSpan.FromMinutes(5)), ct);
        Assert.That(ErrorOf(stillRefused), Is.EqualTo(SchedulePostError.INSUFFICIENT_PERMISSIONS));

        Assert.That(await ArchetypesOf(owner).SetArchetypeToMember(spaceId, memberId, roleId, true, ct), Is.True);
        var retried = Scheduled(await ComposerOf(guest).ReschedulePost(spaceId, channelId, post.postId, In(TimeSpan.FromMinutes(5)), ct));
        Assert.Multiple(() =>
        {
            Assert.That(retried.status, Is.EqualTo(ScheduledPostStatus.PENDING));
            Assert.That(retried.failure, Is.EqualTo(ScheduledPostFailure.NONE));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_post_for_a_deleted_channel_fails(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        var post = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "hello?", Entities(), In(TimeSpan.FromMinutes(10)), ct));

        await owner.Channels.DeleteChannel(spaceId, channelId, ct).Ok();
        await BackdateAsync(post.postId, TimeSpan.FromSeconds(1), ct);
        await TickAsync(channelId);

        var stored = await StoredPostAsync(post.postId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(ScheduledPostStatus.FAILED));
            Assert.That(stored.Failure, Is.EqualTo(ScheduledPostFailure.CHANNEL_NOT_FOUND));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Slow_mode_holds_a_due_post_and_fails_it_once_the_retry_window_is_over(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);
        Assert.That(await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 5, null, ct), Is.InstanceOf<SuccessUpdateChannel>());

        await guest.Channels.SendMessage(spaceId, channelId, "live", Entities(), NextRandomId(), null, ct).Ok();

        var held  = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "held", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var stale = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "stale", Entities(), In(TimeSpan.FromMinutes(10)), ct));

        await BackdateAsync(held.postId, TimeSpan.FromSeconds(1), ct);
        await BackdateAsync(stale.postId, TimeSpan.FromMinutes(16), ct);
        await TickAsync(channelId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await StoredPostAsync(held.postId, ct)).Status, Is.EqualTo(ScheduledPostStatus.PENDING),
                "a post inside the author's cooldown was not held");
            var failed = await StoredPostAsync(stale.postId, ct);
            Assert.That(failed.Status, Is.EqualTo(ScheduledPostStatus.FAILED));
            Assert.That(failed.Failure, Is.EqualTo(ScheduledPostFailure.SLOW_MODE));
        });

        await Task.Delay(TimeSpan.FromSeconds(5.5), ct);
        await TickAsync(channelId);

        Assert.That((await StoredPostAsync(held.postId, ct)).Status, Is.EqualTo(ScheduledPostStatus.PUBLISHED),
            "the held post did not go out once the cooldown was over");
        var texts = (await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values.Select(m => m.text);
        Assert.That(texts, Is.EquivalentTo(new[] { "live", "held" }));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_held_post_does_not_rewrite_the_reminder_on_every_tick(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);
        Assert.That(await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 60, null, ct), Is.InstanceOf<SuccessUpdateChannel>());

        await guest.Channels.SendMessage(spaceId, channelId, "live", Entities(), NextRandomId(), null, ct).Ok();

        var held = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "held", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        await BackdateAsync(held.postId, TimeSpan.FromSeconds(1), ct);

        await TickAsync(channelId);
        var first = await ReminderAsync(channelId);
        await TickAsync(channelId);
        var second = await ReminderAsync(channelId);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null, "a held post was left without a reminder");
            Assert.That((second?.StartAt, second?.ETag), Is.EqualTo((first?.StartAt, first?.ETag)),
                "the reminder was written again for a post the periodic tick already retries");
        });
        Assert.That((await StoredPostAsync(held.postId, ct)).Status, Is.EqualTo(ScheduledPostStatus.PENDING), "premise: the post is held");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_new_activation_does_not_rewrite_a_reminder_already_armed_for_its_target(CancellationToken ct = default)
    {
        var (_, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "first", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var armed = await ReminderAsync(channelId);

        await DeactivateAsync(ComposerGrain(channelId));
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

        // Later than the first, so the earliest post and the reminder's target are unchanged.
        Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "later", Entities(), In(TimeSpan.FromMinutes(20)), ct));
        var after = await ReminderAsync(channelId);

        Assert.That(armed, Is.Not.Null);
        Assert.That((after?.StartAt, after?.ETag), Is.EqualTo((armed?.StartAt, armed?.ETag)),
            "a fresh activation wrote the reminder again for the target it already had");
    }

    // ── Platform lockdown ───────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_post_whose_author_is_under_a_critical_lockdown_fails_as_account_restricted(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        await using var author = await RealtimeClient.ConnectAsync(guest, ct);

        var banned = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "from a banned account", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var lapsed = Scheduled(await ComposerOf(owner).SchedulePost(spaceId, channelId, "from a lapsed ban", Entities(), In(TimeSpan.FromMinutes(10)), ct));

        // Straight into the rows, past the hook that cancels on a lockdown: this is the check at publish time.
        await LockDownAsync(guest.UserId, LockdownReason.TOS_VIOLATION, DateTimeOffset.UtcNow.AddDays(1), ct);
        await LockDownAsync(owner.UserId, LockdownReason.TOS_VIOLATION, DateTimeOffset.UtcNow.AddMinutes(-1), ct);

        await BackdateAsync(banned.postId, TimeSpan.FromSeconds(1), ct);
        await BackdateAsync(lapsed.postId, TimeSpan.FromSeconds(1), ct);
        await TickAsync(channelId);

        var stored = await StoredPostAsync(banned.postId, ct);
        var texts  = (await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values.Select(m => m.text);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(ScheduledPostStatus.FAILED));
            Assert.That(stored.Failure, Is.EqualTo(ScheduledPostFailure.ACCOUNT_RESTRICTED));
            Assert.That(stored.MessageId, Is.Null);
            Assert.That(texts, Is.EqualTo(new[] { "from a lapsed ban" }), "a banned author's post went out, or a lapsed ban held one back");
        });

        await author.WaitForAsync<ScheduledPostUpdated>(
            e => e.post.postId == banned.postId && e.post.failure == ScheduledPostFailure.ACCOUNT_RESTRICTED, Window, ct: ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_critical_lockdown_cancels_the_authors_pending_posts_in_every_channel(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);
        var otherId = await CreateChannelAsync(owner, spaceId, "elsewhere", ChannelType.Text, ct);

        var here  = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "here", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var there = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, otherId, "there", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var kept  = Scheduled(await ComposerOf(owner).SchedulePost(spaceId, channelId, "the owner's", Entities(), In(TimeSpan.FromMinutes(20)), ct));

        Assert.That((await ComposerOf(owner).GetScheduledPosts(spaceId, channelId, ct)).Values, Has.Count.EqualTo(2), "premise");

        var admin = Grains.GetGrain<IAdminUsersGrain>(Guid.Empty);

        // A mute is not a ban.
        Assert.That((await admin.SetLockdownAsync(guest.UserId, LockdownReason.INCITING_MOMENT, In(TimeSpan.FromDays(1)), true, ct)).success, Is.True);
        Assert.That((await StoredPostAsync(here.postId, ct)).Status, Is.EqualTo(ScheduledPostStatus.PENDING), "a mute cancelled a post");

        Assert.That((await admin.SetLockdownAsync(guest.UserId, LockdownReason.TOS_VIOLATION, In(TimeSpan.FromDays(1)), false, ct)).success, Is.True);

        var gone = await PollAsync(
            async () => (await StoredPostAsync(here.postId, ct), await StoredPostAsync(there.postId, ct)),
            p => p.Item1.Status == ScheduledPostStatus.CANCELLED && p.Item2.Status == ScheduledPostStatus.CANCELLED, Window, ct);

        Assert.Multiple(() =>
        {
            Assert.That((gone.Item1.Status, gone.Item2.Status), Is.EqualTo((ScheduledPostStatus.CANCELLED, ScheduledPostStatus.CANCELLED)),
                "the ban left the author's posts queued");
            Assert.That(gone.Item1.ExpireAt, Is.Not.Null);
            Assert.That(gone.Item2.ExpireAt, Is.Not.Null);
        });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await StoredPostAsync(kept.postId, ct)).Status, Is.EqualTo(ScheduledPostStatus.PENDING), "somebody else's post went with it");
            Assert.That((await ComposerOf(owner).GetScheduledPosts(spaceId, channelId, ct)).Values.Select(p => p.postId),
                Is.EqualTo(new[] { kept.postId }), "the channel still lists the cancelled post");
            Assert.That(await PollAsync(() => ReminderAsync(otherId), r => r is null, Window, ct), Is.Null,
                "the reminder of a channel with nothing left pending stayed armed");
        });
    }

    [Test, CancelAfter(240_000)]
    public async Task A_ban_decided_on_a_report_cancels_the_authors_pending_posts(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);

        var post = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "before the ban", Entities(), In(TimeSpan.FromMinutes(10)), ct));

        await FileAsync(owner, Report(UserTarget(guest.UserId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case  = await FindCaseAsync(admin, guest.UserId, ct);
        var banned = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.BAN_USER, null, ct);
        Assert.That(banned.success, Is.True, banned.error);

        var status = await PollAsync(async () => (await StoredPostAsync(post.postId, ct)).Status,
            s => s == ScheduledPostStatus.CANCELLED, Window, ct);
        Assert.That(status, Is.EqualTo(ScheduledPostStatus.CANCELLED), "a ban decided on a report left the author's post queued");
    }

    // ── Retention ───────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Finished_posts_carry_an_expiry_and_live_ones_do_not(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Announcement, ct);
        var roleId = await RoleWithPostingAsync(owner, spaceId, channelId, guest.UserId, ct);

        var composer  = ComposerOf(guest);
        var published = Scheduled(await composer.SchedulePost(spaceId, channelId, "goes out", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var cancelled = Scheduled(await composer.SchedulePost(spaceId, channelId, "called off", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var failed    = Scheduled(await composer.SchedulePost(spaceId, channelId, "refused", Entities(), In(TimeSpan.FromMinutes(20)), ct));
        var pending   = Scheduled(await composer.SchedulePost(spaceId, channelId, "later", Entities(), In(TimeSpan.FromMinutes(30)), ct));

        Assert.That(await composer.CancelScheduledPost(spaceId, channelId, cancelled.postId, ct), Is.True);

        await BackdateAsync(published.postId, TimeSpan.FromSeconds(1), ct);
        await TickAsync(channelId);

        var memberId = await MemberIdOfAsync(owner, spaceId, guest.UserId, ct);
        Assert.That(await ArchetypesOf(owner).SetArchetypeToMember(spaceId, memberId, roleId, false, ct), Is.True);
        await BackdateAsync(failed.postId, TimeSpan.FromSeconds(1), ct);
        await TickAsync(channelId);

        var rows = new Dictionary<string, ScheduledPostEntity>
        {
            ["published"] = await StoredPostAsync(published.postId, ct),
            ["cancelled"] = await StoredPostAsync(cancelled.postId, ct),
            ["failed"]    = await StoredPostAsync(failed.postId, ct),
            ["pending"]   = await StoredPostAsync(pending.postId, ct)
        };

        Assert.Multiple(() =>
        {
            Assert.That(rows.ToDictionary(r => r.Key, r => r.Value.Status), Is.EqualTo(new Dictionary<string, ScheduledPostStatus>
            {
                ["published"] = ScheduledPostStatus.PUBLISHED,
                ["cancelled"] = ScheduledPostStatus.CANCELLED,
                ["failed"]    = ScheduledPostStatus.FAILED,
                ["pending"]   = ScheduledPostStatus.PENDING
            }), "premise");

            // The context stamps UpdatedAt itself on save, a moment after the grain's clock.
            var second = TimeSpan.FromSeconds(1);
            Assert.That(rows["published"].ExpireAt, Is.EqualTo(rows["published"].UpdatedAt).Within(second), "a published post is kept");
            Assert.That(rows["cancelled"].ExpireAt, Is.EqualTo(rows["cancelled"].UpdatedAt).Within(second), "a cancelled post is kept");
            Assert.That(rows["failed"].ExpireAt, Is.EqualTo(rows["failed"].UpdatedAt + TimeSpan.FromDays(7)).Within(second),
                "a failed post does not stay listed for its author for a week");
            Assert.That(rows["pending"].ExpireAt, Is.Null, "a pending post would expire before it is published");
        });

        // A week on, the failed post drops out of its author's list.
        await using (var db = await DbAsync(ct))
            await db.ScheduledPosts.Where(p => p.Id == failed.postId)
               .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpireAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)), ct);
        await DeactivateAsync(ComposerGrain(channelId));
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

        Assert.That((await composer.GetScheduledPosts(spaceId, channelId, ct)).Values.Select(p => p.postId), Is.EqualTo(new[] { pending.postId }));

        // Rescheduling a failed post makes it live again, without an expiry.
        Assert.That(await ArchetypesOf(owner).SetArchetypeToMember(spaceId, memberId, roleId, true, ct), Is.True);
        Scheduled(await composer.ReschedulePost(spaceId, channelId, failed.postId, In(TimeSpan.FromMinutes(40)), ct));
        Assert.That((await StoredPostAsync(failed.postId, ct)).ExpireAt, Is.Null);
    }

    // ── Cancel and reschedule ───────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task The_author_reschedules_and_cancels_and_a_moderator_only_cancels(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);
        var other = await CreateSessionAsync(ct);
        await JoinAsync(owner, other, spaceId, ct);

        var first  = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "first", Entities(), In(TimeSpan.FromMinutes(10)), ct));
        var second = Scheduled(await ComposerOf(guest).SchedulePost(spaceId, channelId, "second", Entities(), In(TimeSpan.FromMinutes(20)), ct));

        var otherCancel     = await ComposerOf(other).CancelScheduledPost(spaceId, channelId, first.postId, ct);
        var otherReschedule = await ComposerOf(other).ReschedulePost(spaceId, channelId, first.postId, In(TimeSpan.FromHours(1)), ct);
        var otherList       = (await ComposerOf(other).GetScheduledPosts(spaceId, channelId, ct)).Values;
        var ownerList       = (await ComposerOf(owner).GetScheduledPosts(spaceId, channelId, ct)).Values;
        var ownerReschedule = await ComposerOf(owner).ReschedulePost(spaceId, channelId, first.postId, In(TimeSpan.FromHours(1)), ct);
        var unknown         = await ComposerOf(guest).ReschedulePost(spaceId, channelId, Guid.NewGuid(), In(TimeSpan.FromHours(1)), ct);

        Assert.Multiple(() =>
        {
            Assert.That(otherCancel, Is.False, "a member cancelled somebody else's post");
            Assert.That(ErrorOf(otherReschedule), Is.EqualTo(SchedulePostError.NOT_AUTHOR));
            Assert.That(otherList, Is.Empty, "a member without ManageMessages saw other people's posts");
            Assert.That(ownerList.Select(p => p.postId), Is.EqualTo(new[] { first.postId, second.postId }),
                "ManageMessages does not see the channel's pending posts");
            Assert.That(ErrorOf(ownerReschedule), Is.EqualTo(SchedulePostError.NOT_AUTHOR), "a moderator moved somebody else's post");
            Assert.That(ErrorOf(unknown), Is.EqualTo(SchedulePostError.POST_NOT_FOUND));
        });

        var later = In(TimeSpan.FromHours(3));
        var moved = Scheduled(await ComposerOf(guest).ReschedulePost(spaceId, channelId, second.postId, later, ct));
        Assert.That((moved.publishAt - later).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
        Assert.That(ErrorOf(await ComposerOf(guest).ReschedulePost(spaceId, channelId, second.postId, In(TimeSpan.FromSeconds(10)), ct)),
            Is.EqualTo(SchedulePostError.PUBLISH_TOO_SOON));

        Assert.That(await ComposerOf(owner).CancelScheduledPost(spaceId, channelId, first.postId, ct), Is.True,
            "ManageMessages could not cancel a pending post");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await StoredPostAsync(first.postId, ct)).Status, Is.EqualTo(ScheduledPostStatus.CANCELLED));
            Assert.That(await ComposerOf(guest).CancelScheduledPost(spaceId, channelId, first.postId, ct), Is.False);
            Assert.That(ErrorOf(await ComposerOf(guest).ReschedulePost(spaceId, channelId, first.postId, In(TimeSpan.FromHours(1)), ct)),
                Is.EqualTo(SchedulePostError.NOT_PENDING));
            Assert.That((await ComposerOf(guest).GetScheduledPosts(spaceId, channelId, ct)).Values.Select(p => p.postId),
                Is.EqualTo(new[] { second.postId }));
        });

        Assert.That(await ComposerOf(guest).CancelScheduledPost(spaceId, channelId, second.postId, ct), Is.True);
        Assert.That((await ComposerOf(guest).GetScheduledPosts(spaceId, channelId, ct)).Values, Is.Empty);
        Assert.That(await ReminderAsync(channelId), Is.Null, "cancelling the last pending post left the reminder armed");
    }

    // ── Drafts ──────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_draft_is_kept_per_user_and_channel_and_empty_text_clears_it(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);
        var otherId  = await CreateChannelAsync(owner, spaceId, "other", ChannelType.Text, ct);
        var hiddenId = await CreateChannelAsync(owner, spaceId, "hidden", ChannelType.Text, ct);
        await DenyOnChannelAsync(owner, spaceId, hiddenId, ArgonEntitlement.ViewChannel, ct);

        var composer = ComposerOf(guest);

        Assert.That(await composer.GetDraft(spaceId, channelId, ct), Is.Null);

        await composer.SaveDraft(spaceId, channelId, "hello world", Entities(Bold(6, 5)), ct);
        var saved = await composer.GetDraft(spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(saved?.text, Is.EqualTo("hello world"));
            Assert.That(saved?.entities.Values.OfType<MessageEntityBold>().Select(b => (b.offset, b.length)),
                Is.EqualTo(new[] { (6, 5) }));
            Assert.That(saved?.channelId, Is.EqualTo(channelId));
        });

        await composer.SaveDraft(spaceId, channelId, "second thoughts", Entities(), ct);

        await Assert.MultipleAsync(async () =>
        {
            var latest = await composer.GetDraft(spaceId, channelId, ct);
            Assert.That(latest?.text, Is.EqualTo("second thoughts"), "the last write did not win");
            Assert.That(latest?.entities.Values, Is.Empty);
            Assert.That(await composer.GetDraft(spaceId, otherId, ct), Is.Null, "a draft leaked into another channel");
            Assert.That(await ComposerOf(owner).GetDraft(spaceId, channelId, ct), Is.Null, "a draft leaked to another user");
        });

        Assert.That(async () => await composer.SaveDraft(spaceId, channelId, new string('a', 4097), Entities(), ct), Throws.Exception);
        Assert.That(async () => await composer.SaveDraft(spaceId, hiddenId, "psst", Entities(), ct), Throws.Exception,
            "a draft was kept for a channel the user cannot see");
        Assert.That((await composer.GetDraft(spaceId, channelId, ct))?.text, Is.EqualTo("second thoughts"));

        await composer.SaveDraft(spaceId, channelId, "", Entities(), ct);
        Assert.That(await composer.GetDraft(spaceId, channelId, ct), Is.Null, "empty text did not delete the draft");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_draft_is_served_from_memory_and_written_behind(CancellationToken ct = default)
    {
        var (_, guest, spaceId, channelId) = await TextChannelAsync(ChannelType.Text, ct);
        var composer = ComposerOf(guest);

        // The first save activates the grain, so its first flush is a whole period (10 s) away.
        for (var i = 0; i < 20; i++)
            await composer.SaveDraft(spaceId, channelId, $"typing {i}", Entities(), ct);

        Assert.That(await StoredDraftAsync(guest.UserId, channelId, ct), Is.Null, "a keystroke save went straight to the table");
        Assert.That((await composer.GetDraft(spaceId, channelId, ct))?.text, Is.EqualTo("typing 19"));

        var flushed = await PollAsync(() => StoredDraftAsync(guest.UserId, channelId, ct), d => d is not null, TimeSpan.FromSeconds(10) + Window, ct);

        Assert.Multiple(() =>
        {
            Assert.That(flushed?.Text, Is.EqualTo("typing 19"), "the timer did not write the draft");
            Assert.That(flushed?.ExpireAt - flushed?.UpdatedAt, Is.EqualTo(TimeSpan.FromDays(30)), "the draft does not expire a month after its last save");
        });

        // Changed underneath the activation, which keeps serving what it holds.
        await using (var db = await DbAsync(ct))
            await db.MessageDrafts.Where(d => d.UserId == guest.UserId && d.ChannelId == channelId)
               .ExecuteUpdateAsync(s => s.SetProperty(d => d.Text, "changed underneath"), ct);
        Assert.That((await composer.GetDraft(spaceId, channelId, ct))?.text, Is.EqualTo("typing 19"), "the draft was read from the table again");

        // Just after a flush, so only the deactivation can write this one within the window below.
        await composer.SaveDraft(spaceId, channelId, "last words", Entities(Bold(5, 5)), ct);
        await DeactivateAsync(DraftsGrain(guest.UserId));

        var written = await PollAsync(() => StoredDraftAsync(guest.UserId, channelId, ct), d => d?.Text == "last words", TimeSpan.FromSeconds(3), ct);
        Assert.That(written?.Text, Is.EqualTo("last words"), "the deactivation did not write the unsaved draft");

        var reloaded = await composer.GetDraft(spaceId, channelId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded?.text, Is.EqualTo("last words"), "the draft did not survive a new activation");
            Assert.That(reloaded?.entities.Values.OfType<MessageEntityBold>().Select(b => (b.offset, b.length)), Is.EqualTo(new[] { (5, 5) }));
        });

        // Clearing is written behind as well.
        await composer.SaveDraft(spaceId, channelId, "", Entities(), ct);
        await DeactivateAsync(DraftsGrain(guest.UserId));
        Assert.That(await PollAsync(() => StoredDraftAsync(guest.UserId, channelId, ct), d => d is null, TimeSpan.FromSeconds(3), ct), Is.Null,
            "a cleared draft stayed in the table");
    }
}
