namespace Argon.Grains;

using Argon.Core.Features.Transport;
using Argon.Core.Services;
using Argon.Features.Moderation;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A channel's scheduled posts: validation, the author's list, and publishing when they come due.
/// </summary>
/// <remarks>
/// <para><b>Publishing.</b> One reminder per channel, armed for the earliest pending post and re-armed
/// after every change and every tick (unregistered when nothing is pending). The posts live in
/// <c>ScheduledPosts</c> and the reminder in the reminder table, so neither a deactivation nor a
/// silo restart loses one; accuracy is the reminder's, about a minute. The armed time is read back
/// from the reminder table once per activation, so an unchanged target is not written again.</para>
///
/// <para><b>The send.</b> A due post is re-checked (channel, the author's lockdown, membership,
/// SendMessages, AttachFiles) and then sent through <see cref="IChannelGrain.SendMessage"/> as its
/// author, so pings, the mass-mention budget and the length rule apply as they do to a live send. Its
/// stored randomId deduplicates a publish repeated after a crash.</para>
///
/// <para><b>Slow mode applies.</b> A post the author's cooldown refuses is held and retried on the
/// next tick, and fails as SLOW_MODE once it is <see cref="RetryWindow"/> late. Other transient
/// refusals are held the same way and fail as SEND_FAILED.</para>
///
/// <para><b>The activation caches</b> the channel's space and type and its live posts (pending, and
/// failed ones not yet expired). It is their only writer; the tick still takes due rows from the
/// table, so a row an erasure deleted is never published.</para>
///
/// <para><b>Tests</b> backdate <c>PublishAt</c> in the table and deliver the tick through
/// <see cref="IRemindable.ReceiveReminder"/>, as TtlSweepTests do, rather than wait for the minute.</para>
/// </remarks>
public class ChannelComposerGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker entitlementChecker,
    AppHubServer appHubServer,
    IReminderTable reminderTable,
    IOptions<MessagesOptions> messageOptions,
    IOptions<Orleans.Hosting.ReminderOptions> reminderOptions,
    ILogger<ChannelComposerGrain> logger) : Grain, IChannelComposerGrain, IRemindable
{
    public const string ReminderName = "scheduled-posts";

    public const int MaxPendingPerAuthor  = 10;
    public const int MaxPendingPerChannel = 100;
    public const int MaxAttachments       = 10;

    public static readonly TimeSpan MinLead         = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLead         = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryWindow     = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FailedRetention = TimeSpan.FromDays(7);

    // A far post is reached in steps: the tick finds nothing due and arms again.
    private static readonly TimeSpan MaxArmAhead = TimeSpan.FromDays(1);

    // The reminder service stamps a registration with its own clock.
    private static readonly TimeSpan ArmTolerance = TimeSpan.FromSeconds(5);

    private ChannelInfo?                           channelInfo;
    private Dictionary<Guid, ScheduledPostEntity>? live;

    // The armed reminder's first tick (in the past once it is ticking); null when none is armed.
    private DateTimeOffset? armedFor;
    private bool            armKnown;

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

        var channel = await ChannelAsync();
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

        if (!await HasRoomAsync(authorId))
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

        await using (var ctx = await context.CreateDbContextAsync())
        {
            ctx.ScheduledPosts.Add(post);
            await ctx.SaveChangesAsync();
        }

        Keep(post);
        await ArmAsync();
        await NotifyAsync(post);

        return new SuccessSchedulePost(post.ToDto());
    }

    public async Task<ISchedulePostResult> ReschedulePostAsync(Guid spaceId, Guid postId, DateTimeOffset publishAt)
    {
        var callerId = this.GetUserId();

        var channel = await ChannelAsync();
        if (channel is null || channel.SpaceId != spaceId)
            return Failed(SchedulePostError.CHANNEL_NOT_FOUND);

        await using var ctx = await context.CreateDbContextAsync();

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

        if (post.Status == ScheduledPostStatus.FAILED && !await HasRoomAsync(callerId))
            return Failed(SchedulePostError.TOO_MANY_SCHEDULED);

        post.PublishAt = publishAt.ToUniversalTime();
        post.Status    = ScheduledPostStatus.PENDING;
        post.Failure   = ScheduledPostFailure.NONE;
        post.RandomId  = NewRandomId();
        post.UpdatedAt = DateTimeOffset.UtcNow;
        post.ExpireAt  = null;
        await ctx.SaveChangesAsync();

        Keep(post);
        await ArmAsync();
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

        Finish(post, ScheduledPostStatus.CANCELLED, DateTimeOffset.UtcNow);
        await ctx.SaveChangesAsync();

        Keep(post);
        await ArmAsync();
        await NotifyAsync(post);

        return true;
    }

    public async Task<List<ScheduledPost>> GetScheduledPostsAsync(Guid spaceId)
    {
        var callerId = this.GetUserId();

        var channel = await ChannelAsync();
        if (channel is null || channel.SpaceId != spaceId)
            return [];

        var moderator = await entitlementChecker.HasChannelAccessAsync(spaceId, ChannelId, callerId, ArgonEntitlement.ManageMessages);
        var now       = DateTimeOffset.UtcNow;

        return (await LiveAsync()).Values
           .Where(p => IsLive(p, now))
           .Where(p => p.AuthorId == callerId || (moderator && p.Status == ScheduledPostStatus.PENDING))
           .OrderBy(p => p.PublishAt)
           .Take(200)
           .Select(p => p.ToDto())
           .ToList();
    }

    public async Task CancelPendingOfAuthorAsync(Guid authorId)
    {
        var cancelled = (await LiveAsync()).Values
           .Where(p => p.AuthorId == authorId && p.Status == ScheduledPostStatus.PENDING)
           .ToList();

        var now = DateTimeOffset.UtcNow;

        await using (var ctx = await context.CreateDbContextAsync())
        {
            await ctx.ScheduledPosts
               .Where(p => p.ChannelId == ChannelId && p.AuthorId == authorId && p.Status == ScheduledPostStatus.PENDING)
               .ExecuteUpdateAsync(s => s
                   .SetProperty(p => p.Status, ScheduledPostStatus.CANCELLED)
                   .SetProperty(p => p.UpdatedAt, now)
                   .SetProperty(p => p.ExpireAt, now));
        }

        foreach (var post in cancelled)
        {
            Finish(post, ScheduledPostStatus.CANCELLED, now);
            Keep(post);
        }

        await ArmAsync();

        foreach (var post in cancelled)
            await NotifyAsync(post);

        logger.LogInformation("Cancelled {Count} scheduled post(s) of {AuthorId} in channel {ChannelId}", cancelled.Count, authorId, ChannelId);
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
        var posts = await LiveAsync();

        await using var ctx = await context.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var due = await ctx.ScheduledPosts
           .Where(p => p.ChannelId == ChannelId && p.Status == ScheduledPostStatus.PENDING && p.PublishAt <= now)
           .OrderBy(p => p.PublishAt)
           .ThenBy(p => p.CreatedAt)
           .ToListAsync();

        // A due post the table no longer holds as pending was removed outside this grain.
        foreach (var gone in posts.Values.Where(p => p.Status == ScheduledPostStatus.PENDING && p.PublishAt <= now && due.All(d => d.Id != p.Id)).ToList())
            posts.Remove(gone.Id);

        if (due.Count > 0)
        {
            var channel    = channelInfo = await ReadChannelAsync(ctx);
            var restricted = new Dictionary<Guid, bool>();

            foreach (var post in due)
            {
                if (!await PublishAsync(post, channel, restricted))
                {
                    Keep(post);
                    continue;
                }

                Finish(post, post.Status, DateTimeOffset.UtcNow);
                await ctx.SaveChangesAsync();
                Keep(post);
                await NotifyAsync(post);
            }
        }

        await ArmAsync();
    }

    /// <summary>Publishes or fails one due post. False when it is held for the next tick.</summary>
    private async Task<bool> PublishAsync(ScheduledPostEntity post, ChannelInfo? channel, Dictionary<Guid, bool> restricted)
    {
        if (channel is null || channel.SpaceId != post.SpaceId || channel.Type is not (ChannelType.Text or ChannelType.Announcement))
            return Fail(post, ScheduledPostFailure.CHANNEL_NOT_FOUND);

        if (!restricted.TryGetValue(post.AuthorId, out var locked))
            restricted[post.AuthorId] = locked = await IsRestrictedAsync(post.AuthorId);
        if (locked)
            return Fail(post, ScheduledPostFailure.ACCOUNT_RESTRICTED);

        if (!await MaySendAsync(post.SpaceId, post.AuthorId, post.Entities))
            return Fail(post, ScheduledPostFailure.INSUFFICIENT_PERMISSIONS);

        try
        {
            var (error, messageId) = await SendAsAuthorAsync(post);

            switch (error)
            {
                case SendMessageError.NONE:
                    post.MessageId = messageId;
                    post.Status    = ScheduledPostStatus.PUBLISHED;
                    return true;
                case SendMessageError.NO_PERMISSION or SendMessageError.BOTS_NOT_ALLOWED or SendMessageError.NO_ATTACH_PERMISSION:
                    return Fail(post, ScheduledPostFailure.INSUFFICIENT_PERMISSIONS);
                default:
                    return Hold(post, error.ToString(), error is SendMessageError.SLOW_MODE);
            }
        }
        catch (Exception e)
        {
            return Hold(post, e.GetType().Name, false, e);
        }
    }

    /// <summary>False while the post is inside its retry window, then fails it.</summary>
    private bool Hold(ScheduledPostEntity post, string reason, bool slowMode, Exception? e = null)
    {
        if (DateTimeOffset.UtcNow - post.PublishAt < RetryWindow)
        {
            logger.LogInformation("Scheduled post {PostId} held ({Reason}); retried on the next tick", post.Id, reason);
            return false;
        }

        logger.LogWarning(e, "Scheduled post {PostId} gave up after the retry window", post.Id);
        return Fail(post, slowMode ? ScheduledPostFailure.SLOW_MODE : ScheduledPostFailure.SEND_FAILED);
    }

    private static bool Fail(ScheduledPostEntity post, ScheduledPostFailure failure)
    {
        post.Status  = ScheduledPostStatus.FAILED;
        post.Failure = failure;
        return true;
    }

    // A failed post stays listed for its author for a week; published and cancelled ones are done.
    private static void Finish(ScheduledPostEntity post, ScheduledPostStatus status, DateTimeOffset now)
    {
        post.Status    = status;
        post.UpdatedAt = now;
        post.ExpireAt  = status == ScheduledPostStatus.FAILED ? now + FailedRetention : now;
    }

    private async Task<bool> IsRestrictedAsync(Guid authorId)
    {
        var lockdown = await GrainFactory.GetGrain<IIdentityDirectoryGrain>(Guid.Empty).GetLockdownAsync(authorId);
        return ScheduledPostsOfAuthor.IsRestricted(lockdown.Reason, lockdown.ExpiresAt);
    }

    private async Task<(SendMessageError error, long messageId)> SendAsAuthorAsync(ScheduledPostEntity post)
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

    private async Task ArmAsync()
    {
        try
        {
            var next = (await LiveAsync()).Values
               .Where(p => p.Status == ScheduledPostStatus.PENDING)
               .Min(p => (DateTimeOffset?)p.PublishAt);

            if (!armKnown)
            {
                armedFor = await ArmedInTableAsync();
                armKnown = true;
            }

            if (next is null)
            {
                if (armedFor is null)
                    return;
                if (await this.GetReminder(ReminderName) is { } reminder)
                    await this.UnregisterReminder(reminder);
                armedFor = null;
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // A held post waits for the next period of the reminder that is already ticking.
            if (next <= now && armedFor <= now + TickPeriod)
                return;

            // A step towards a far post that is still on its way is not moved.
            if (next - now > MaxArmAhead && armedFor > now && armedFor <= next)
                return;

            // A post already due is being held: it waits for the next period rather than spinning.
            var target = next.Value > now ? next.Value : now + TickPeriod;
            if (target - now > MaxArmAhead)
                target = now + MaxArmAhead;

            if (armedFor is { } armed && (armed - target).Duration() < ArmTolerance)
                return;

            await this.RegisterOrUpdateReminder(ReminderName, target - now, TickPeriod);
            armedFor = target;
        }
        catch (Exception e)
        {
            // The next change or tick arms it again.
            armKnown = false;
            logger.LogError(e, "Could not arm the scheduled-post reminder of channel {ChannelId}", ChannelId);
        }
    }

    private async Task<DateTimeOffset?> ArmedInTableAsync()
    {
        var entry = await reminderTable.ReadRow(this.GetGrainId(), ReminderName);
        return entry is null ? null : new DateTimeOffset(DateTime.SpecifyKind(entry.StartAt, DateTimeKind.Utc));
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

    private async Task<ChannelInfo?> ChannelAsync()
    {
        if (channelInfo is not null)
            return channelInfo;

        await using var ctx = await context.CreateDbContextAsync();
        return channelInfo = await ReadChannelAsync(ctx);
    }

    private async Task<ChannelInfo?> ReadChannelAsync(ApplicationDbContext ctx)
        => await ctx.Channels.AsNoTracking()
           .Where(c => c.Id == ChannelId)
           .Select(c => new ChannelInfo(c.SpaceId, c.ChannelType))
           .FirstOrDefaultAsync();

    private async Task<Dictionary<Guid, ScheduledPostEntity>> LiveAsync()
    {
        if (live is not null)
            return live;

        await using var ctx = await context.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var rows = await ctx.ScheduledPosts.AsNoTracking()
           .Where(p => p.ChannelId == ChannelId
                    && (p.Status == ScheduledPostStatus.PENDING || p.Status == ScheduledPostStatus.FAILED)
                    && (p.ExpireAt == null || p.ExpireAt > now))
           .ToListAsync();

        return live = rows.ToDictionary(p => p.Id);
    }

    private static bool IsLive(ScheduledPostEntity post, DateTimeOffset now)
        => post.Status is ScheduledPostStatus.PENDING or ScheduledPostStatus.FAILED && (post.ExpireAt is null || post.ExpireAt > now);

    /// <summary>Brings the cached copy in line with a row just written.</summary>
    private void Keep(ScheduledPostEntity post)
    {
        if (live is null)
            return;

        if (IsLive(post, DateTimeOffset.UtcNow))
            live[post.Id] = post;
        else
            live.Remove(post.Id);
    }

    private async Task<bool> HasRoomAsync(Guid authorId)
    {
        var pending = (await LiveAsync()).Values.Where(p => p.Status == ScheduledPostStatus.PENDING).ToList();
        return pending.Count < MaxPendingPerChannel && pending.Count(p => p.AuthorId == authorId) < MaxPendingPerAuthor;
    }

    private async Task<bool> MaySendAsync(Guid spaceId, Guid userId, List<IMessageEntity> entities)
    {
        var needed = HasFiles(entities) ? ArgonEntitlement.SendMessages | ArgonEntitlement.AttachFiles : ArgonEntitlement.SendMessages;
        return await entitlementChecker.HasChannelAccessAsync(spaceId, ChannelId, userId, needed);
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

/// <summary>An author's scheduled posts across channels, for platform moderation and account erasure.</summary>
public static class ScheduledPostsOfAuthor
{
    /// <summary>A Critical lockdown that has not lapsed: what the Ion gate refuses a send for.</summary>
    public static bool IsRestricted(LockdownReason reason, DateTimeOffset? expiresAt)
        => !(expiresAt is { } lapse && lapse <= DateTimeOffset.UtcNow)
        && ReportActionPlanner.SeverityOf(reason) == LockdownSeverity.Critical;

    /// <summary>Cancels the author's pending posts in every channel, through each channel's composer.</summary>
    public static async Task CancelPendingAsync(ApplicationDbContext ctx, IGrainFactory grains, Guid authorId, CancellationToken ct = default)
    {
        var channels = await ctx.ScheduledPosts.AsNoTracking()
           .Where(p => p.AuthorId == authorId && p.Status == ScheduledPostStatus.PENDING)
           .Select(p => p.ChannelId)
           .Distinct()
           .ToListAsync(ct);

        foreach (var channelId in channels)
            await grains.GetGrain<IChannelComposerGrain>(channelId).CancelPendingOfAuthorAsync(authorId);
    }

    /// <summary>The lockdown hook: when the user now stands under a Critical lockdown, their pending posts are cancelled.</summary>
    public static async Task CancelIfRestrictedAsync(ApplicationDbContext ctx, IGrainFactory grains, Guid userId, CancellationToken ct = default)
    {
        var lockdown = await ctx.Users.AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new { u.LockdownReason, u.LockDownExpiration })
           .FirstOrDefaultAsync(ct);

        if (lockdown is not null && IsRestricted(lockdown.LockdownReason, lockdown.LockDownExpiration))
            await CancelPendingAsync(ctx, grains, userId, ct);
    }
}
