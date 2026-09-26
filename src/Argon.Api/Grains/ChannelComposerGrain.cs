namespace Argon.Grains;

using Argon.Core.Features.Transport;
using Argon.Core.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A channel's scheduled posts: validation, the author's list, and publishing when they come due.
/// </summary>
/// <remarks>
/// <para><b>Publishing.</b> One reminder per channel, armed for the earliest pending post and re-armed
/// after every change and every tick (unregistered when nothing is pending). The posts live in
/// <c>ScheduledPosts</c> and the reminder in the reminder table, so neither a deactivation nor a
/// silo restart loses one; accuracy is the reminder's, about a minute.</para>
///
/// <para><b>The send.</b> A due post is re-checked (channel, membership, SendMessages, AttachFiles)
/// and then sent through <see cref="IChannelGrain.SendMessage"/> as its author, so pings, the
/// mass-mention budget and the length rule apply as they do to a live send. Its stored randomId
/// deduplicates a publish repeated after a crash.</para>
///
/// <para><b>Slow mode applies.</b> A post the author's cooldown refuses is held and retried on the
/// next tick, and fails as SLOW_MODE once it is <see cref="RetryWindow"/> late. Other transient
/// refusals are held the same way and fail as SEND_FAILED.</para>
///
/// <para><b>Tests</b> backdate <c>PublishAt</c> in the table and deliver the tick through
/// <see cref="IRemindable.ReceiveReminder"/>, as TtlSweepTests do, rather than wait for the minute.</para>
/// </remarks>
public class ChannelComposerGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker entitlementChecker,
    AppHubServer appHubServer,
    IOptions<MessagesOptions> messageOptions,
    IOptions<Orleans.Hosting.ReminderOptions> reminderOptions,
    ILogger<ChannelComposerGrain> logger) : Grain, IChannelComposerGrain, IRemindable
{
    public const string ReminderName = "scheduled-posts";

    public const int MaxPendingPerChannel = 25;
    public const int MaxAttachments       = 10;

    public static readonly TimeSpan MinLead     = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLead     = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(15);

    // A far post is reached in steps: the tick finds nothing due and arms again.
    private static readonly TimeSpan MaxArmAhead = TimeSpan.FromDays(1);

    private DateTimeOffset? armedFor;

    private Guid ChannelId => this.GetPrimaryKey();

    private TimeSpan TickPeriod
    {
        get
        {
            var floor = reminderOptions.Value.MinimumReminderPeriod;
            return floor > TimeSpan.FromMinutes(1) ? floor : TimeSpan.FromMinutes(1);
        }
    }

    private sealed record ChannelInfo(Guid SpaceId, ChannelType Type);

    public async Task<ISchedulePostResult> SchedulePostAsync(Guid spaceId, string text, List<IMessageEntity> entities, DateTimeOffset publishAt)
    {
        var authorId = this.GetUserId();
        text     ??= "";
        entities ??= [];

        await using var ctx = await context.CreateDbContextAsync();

        var channel = await ChannelAsync(ctx);
        if (channel is null || channel.SpaceId != spaceId)
            return Failed(SchedulePostError.CHANNEL_NOT_FOUND);
        if (channel.Type is not (ChannelType.Text or ChannelType.Announcement))
            return Failed(SchedulePostError.NOT_A_TEXT_CHANNEL);

        var error = ValidateContent(text, entities);
        if (error != SchedulePostError.NONE)
            return Failed(error);

        if (this.IsBotCaller() || !await MaySendAsync(spaceId, authorId, entities))
            return Failed(SchedulePostError.INSUFFICIENT_PERMISSIONS);

        error = ValidateTime(publishAt);
        if (error != SchedulePostError.NONE)
            return Failed(error);

        if (await PendingCountAsync(ctx) >= MaxPendingPerChannel)
            return Failed(SchedulePostError.TOO_MANY_SCHEDULED);

        var now = DateTimeOffset.UtcNow;
        var post = new ScheduledPostEntity
        {
            Id        = ArgonId.New(),
            SpaceId   = spaceId,
            ChannelId = ChannelId,
            AuthorId  = authorId,
            Text      = text,
            Entities  = Sanitize(entities),
            PublishAt = publishAt.ToUniversalTime(),
            Status    = ScheduledPostStatus.PENDING,
            Failure   = ScheduledPostFailure.NONE,
            RandomId  = NewRandomId(),
            CreatedAt = now,
            UpdatedAt = now
        };

        ctx.ScheduledPosts.Add(post);
        await ctx.SaveChangesAsync();

        await ArmAsync(ctx);
        await NotifyAsync(post);

        return new SuccessSchedulePost(post.ToDto());
    }

    public async Task<ISchedulePostResult> ReschedulePostAsync(Guid spaceId, Guid postId, DateTimeOffset publishAt)
    {
        var callerId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync();

        var channel = await ChannelAsync(ctx);
        if (channel is null || channel.SpaceId != spaceId)
            return Failed(SchedulePostError.CHANNEL_NOT_FOUND);

        var post = await ctx.ScheduledPosts.FirstOrDefaultAsync(p => p.Id == postId && p.ChannelId == ChannelId);
        if (post is null)
            return Failed(SchedulePostError.POST_NOT_FOUND);
        if (post.AuthorId != callerId)
            return Failed(SchedulePostError.NOT_AUTHOR);
        if (post.Status is not (ScheduledPostStatus.PENDING or ScheduledPostStatus.FAILED))
            return Failed(SchedulePostError.NOT_PENDING);

        if (!await MaySendAsync(spaceId, callerId, post.Entities))
            return Failed(SchedulePostError.INSUFFICIENT_PERMISSIONS);

        var error = ValidateTime(publishAt);
        if (error != SchedulePostError.NONE)
            return Failed(error);

        if (post.Status == ScheduledPostStatus.FAILED && await PendingCountAsync(ctx) >= MaxPendingPerChannel)
            return Failed(SchedulePostError.TOO_MANY_SCHEDULED);

        post.PublishAt = publishAt.ToUniversalTime();
        post.Status    = ScheduledPostStatus.PENDING;
        post.Failure   = ScheduledPostFailure.NONE;
        post.RandomId  = NewRandomId();
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();

        await ArmAsync(ctx);
        await NotifyAsync(post);

        return new SuccessSchedulePost(post.ToDto());
    }

    public async Task<bool> CancelScheduledPostAsync(Guid spaceId, Guid postId)
    {
        var callerId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync();

        var post = await ctx.ScheduledPosts.FirstOrDefaultAsync(p => p.Id == postId && p.ChannelId == ChannelId && p.SpaceId == spaceId);
        if (post is null)
            return false;

        var allowed = post.AuthorId == callerId
            ? post.Status is ScheduledPostStatus.PENDING or ScheduledPostStatus.FAILED
            : post.Status == ScheduledPostStatus.PENDING
           && await entitlementChecker.HasChannelAccessAsync(spaceId, ChannelId, callerId, ArgonEntitlement.ManageMessages);

        if (!allowed)
            return false;

        post.Status    = ScheduledPostStatus.CANCELLED;
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();

        await ArmAsync(ctx);
        await NotifyAsync(post);

        return true;
    }

    public async Task<List<ScheduledPost>> GetScheduledPostsAsync(Guid spaceId)
    {
        var callerId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync();

        var channel = await ChannelAsync(ctx);
        if (channel is null || channel.SpaceId != spaceId)
            return [];

        var moderator = await entitlementChecker.HasChannelAccessAsync(spaceId, ChannelId, callerId, ArgonEntitlement.ManageMessages);

        var posts = await ctx.ScheduledPosts.AsNoTracking()
           .Where(p => p.ChannelId == ChannelId)
           .Where(p => (p.AuthorId == callerId && (p.Status == ScheduledPostStatus.PENDING || p.Status == ScheduledPostStatus.FAILED))
                    || (moderator && p.AuthorId != callerId && p.Status == ScheduledPostStatus.PENDING))
           .OrderBy(p => p.PublishAt)
           .Take(200)
           .ToListAsync();

        return posts.Select(p => p.ToDto()).ToList();
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        try
        {
            await PublishDueAsync();
        }
        catch (Exception e)
        {
            // Not rethrown: the period brings the next tick, and the rows are still pending.
            logger.LogError(e, "Scheduled posts of channel {ChannelId} were not published", ChannelId);
        }
    }

    private async Task PublishDueAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var due = await ctx.ScheduledPosts
           .Where(p => p.ChannelId == ChannelId && p.Status == ScheduledPostStatus.PENDING && p.PublishAt <= now)
           .OrderBy(p => p.PublishAt)
           .ThenBy(p => p.CreatedAt)
           .ToListAsync();

        if (due.Count > 0)
        {
            var channel = await ChannelAsync(ctx);

            foreach (var post in due)
            {
                if (!await PublishAsync(post, channel))
                    continue;

                post.UpdatedAt = DateTimeOffset.UtcNow;
                await ctx.SaveChangesAsync();
                await NotifyAsync(post);
            }
        }

        await ArmAsync(ctx);
    }

    /// <summary>Publishes or fails one due post. False when it is held for the next tick.</summary>
    private async Task<bool> PublishAsync(ScheduledPostEntity post, ChannelInfo? channel)
    {
        if (channel is null || channel.SpaceId != post.SpaceId || channel.Type is not (ChannelType.Text or ChannelType.Announcement))
            return Fail(post, ScheduledPostFailure.CHANNEL_NOT_FOUND);

        if (!await MaySendAsync(post.SpaceId, post.AuthorId, post.Entities))
            return Fail(post, ScheduledPostFailure.INSUFFICIENT_PERMISSIONS);

        try
        {
            post.MessageId = await SendAsAuthorAsync(post);
            post.Status    = ScheduledPostStatus.PUBLISHED;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(post, ScheduledPostFailure.INSUFFICIENT_PERMISSIONS);
        }
        catch (Exception e)
        {
            var slowMode = e is SlowModeException;

            if (DateTimeOffset.UtcNow - post.PublishAt < RetryWindow)
            {
                logger.LogInformation("Scheduled post {PostId} held ({Reason}); retried on the next tick",
                    post.Id, slowMode ? "slow mode" : e.GetType().Name);
                return false;
            }

            logger.LogWarning(e, "Scheduled post {PostId} gave up after the retry window", post.Id);
            return Fail(post, slowMode ? ScheduledPostFailure.SLOW_MODE : ScheduledPostFailure.SEND_FAILED);
        }
    }

    private static bool Fail(ScheduledPostEntity post, ScheduledPostFailure failure)
    {
        post.Status  = ScheduledPostStatus.FAILED;
        post.Failure = failure;
        return true;
    }

    private async Task<long> SendAsAuthorAsync(ScheduledPostEntity post)
    {
        // The author is the caller, carried the way the Ion layer carries one.
        RequestContext.Clear();
        RequestContext.Set("$caller_user_id", post.AuthorId);
        try
        {
            return await GrainFactory.GetGrain<IChannelGrain>(ChannelId)
               .SendMessage(post.Text, post.Entities.ToList(), post.RandomId, null);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    private async Task ArmAsync(ApplicationDbContext ctx)
    {
        try
        {
            var next = await ctx.ScheduledPosts.AsNoTracking()
               .Where(p => p.ChannelId == ChannelId && p.Status == ScheduledPostStatus.PENDING)
               .MinAsync(p => (DateTimeOffset?)p.PublishAt);

            if (next is null)
            {
                if (await this.GetReminder(ReminderName) is { } reminder)
                    await this.UnregisterReminder(reminder);
                armedFor = null;
                return;
            }

            var now = DateTimeOffset.UtcNow;
            // A post already due is being held: it waits for the next period rather than spinning.
            var target = next.Value > now ? next.Value : now + TickPeriod;
            if (target - now > MaxArmAhead)
                target = now + MaxArmAhead;

            if (armedFor == target)
                return;

            await this.RegisterOrUpdateReminder(ReminderName, target - now, TickPeriod);
            armedFor = target;
        }
        catch (Exception e)
        {
            // The next change or tick arms it again.
            logger.LogError(e, "Could not arm the scheduled-post reminder of channel {ChannelId}", ChannelId);
        }
    }

    private async Task NotifyAsync(ScheduledPostEntity post)
    {
        try
        {
            await appHubServer.ForUser(new ScheduledPostUpdated(post.SpaceId, post.ChannelId, post.ToDto()), post.AuthorId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "ScheduledPostUpdated for post {PostId} was not delivered", post.Id);
        }
    }

    private async Task<ChannelInfo?> ChannelAsync(ApplicationDbContext ctx)
        => await ctx.Channels.AsNoTracking()
           .Where(c => c.Id == ChannelId)
           .Select(c => new ChannelInfo(c.SpaceId, c.ChannelType))
           .FirstOrDefaultAsync();

    private async Task<int> PendingCountAsync(ApplicationDbContext ctx)
        => await ctx.ScheduledPosts.CountAsync(p => p.ChannelId == ChannelId && p.Status == ScheduledPostStatus.PENDING);

    private async Task<bool> MaySendAsync(Guid spaceId, Guid userId, List<IMessageEntity> entities)
    {
        if (!await entitlementChecker.HasChannelAccessAsync(spaceId, ChannelId, userId, ArgonEntitlement.SendMessages))
            return false;

        return !HasFiles(entities)
            || await entitlementChecker.HasChannelAccessAsync(spaceId, ChannelId, userId, ArgonEntitlement.AttachFiles);
    }

    private SchedulePostError ValidateContent(string text, List<IMessageEntity> entities)
    {
        if (text.Length > messageOptions.Value.MaxTextLength)
            return SchedulePostError.MESSAGE_TOO_LONG;
        if (entities.Count(IsFile) > MaxAttachments)
            return SchedulePostError.TOO_MANY_ATTACHMENTS;
        if (string.IsNullOrWhiteSpace(text) && !HasFiles(entities))
            return SchedulePostError.EMPTY_MESSAGE;
        return SchedulePostError.NONE;
    }

    private static SchedulePostError ValidateTime(DateTimeOffset publishAt)
    {
        var now = DateTimeOffset.UtcNow;
        if (publishAt < now + MinLead)
            return SchedulePostError.PUBLISH_TOO_SOON;
        if (publishAt > now + MaxLead)
            return SchedulePostError.PUBLISH_TOO_LATE;
        return SchedulePostError.NONE;
    }

    private static bool IsFile(IMessageEntity e) => e is MessageEntityAttachment or MessageEntityGif;

    private static bool HasFiles(List<IMessageEntity>? entities) => entities?.Any(IsFile) == true;

    // As SendMessage does: download and preview URLs are resolved by the server, never kept from the client.
    private static List<IMessageEntity> Sanitize(List<IMessageEntity> entities)
        => entities.Select(e => e switch
        {
            MessageEntityAttachment { downloadUrl: not null } a => a with { downloadUrl = null },
            MessageEntityGif { previewUrl: not null } g         => g with { previewUrl = null },
            _                                                   => e
        }).ToList();

    private static long NewRandomId() => Random.Shared.NextInt64(1, long.MaxValue);

    private static ISchedulePostResult Failed(SchedulePostError error) => new FailedSchedulePost(error);
}
