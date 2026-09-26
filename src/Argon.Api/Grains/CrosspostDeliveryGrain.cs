namespace Argon.Grains;

using System.Globalization;
using Argon.Features.Clustering.Regions;
using Core.Services;
using Microsoft.EntityFrameworkCore;
using Orleans.Providers;
using Persistence.States;
using Services.L1L2;

/// <summary>
/// Delivers one published post to the channels following its source, in pages of <see cref="PageSize"/>
/// follows taken in id order.
/// </summary>
/// <remarks>
/// <para>The cursor is persisted after every page and the reminder armed at start resumes from it after
/// a deactivation or restart; a redone page lands nothing twice, since each target claims the post.</para>
/// <para>A follow is delivered only while its creator passes <see cref="FollowAccess.CheckSourceAsync"/>
/// and holds ManageChannels in the target; otherwise it is deleted. A failing target is logged and skipped.</para>
/// <para>A reminder tick runs every remaining page before it returns, which is what tests rely on.</para>
/// </remarks>
public class CrosspostDeliveryGrain(
    [PersistentState("crosspost-delivery", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<CrosspostDeliveryState> state,
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker entitlementChecker,
    IPermissionCache permissionCache,
    IArgonRegionRegistry regions,
    IOptions<Orleans.Hosting.ReminderOptions> reminderOptions,
    ILogger<CrosspostDeliveryGrain> logger) : Grain, ICrosspostDeliveryGrain, IRemindable
{
    public const string ReminderName = "crosspost-delivery";
    public const int    PageSize     = 500;

    // Targets written to at once within a page.
    private const int Concurrency = 16;

    // A job still unfinished this long after its publish is given up.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private IGrainTimer? nextPage;

    private sealed record Target(Guid FollowId, Guid SpaceId, Guid ChannelId, Guid CreatorId);

    private Guid SourceChannelId => this.GetPrimaryKey(out _);

    private long SourceMessageId
    {
        get
        {
            this.GetPrimaryKey(out var ext);
            return long.Parse(ext!, CultureInfo.InvariantCulture);
        }
    }

    private TimeSpan TickPeriod
    {
        get
        {
            var floor = reminderOptions.Value.MinimumReminderPeriod;
            return floor > TimeSpan.FromMinutes(1) ? floor : TimeSpan.FromMinutes(1);
        }
    }

    public async Task StartAsync(Guid sourceSpaceId)
    {
        // A repeat only re-arms, so a start that failed half way can be retried.
        if (state.State.StartedAt is null)
        {
            state.State.SourceSpaceId = sourceSpaceId;
            state.State.StartedAt     = DateTimeOffset.UtcNow;
            await state.WriteStateAsync();
        }

        await this.RegisterOrUpdateReminder(ReminderName, TickPeriod, TickPeriod);
        ScheduleNextPage();
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName == ReminderName)
            await RunAsync(untilDone: true);
    }

    /// <summary>Each page in a turn of its own, so a deactivation lands between pages.</summary>
    private void ScheduleNextPage()
    {
        nextPage?.Dispose();
        nextPage = this.RegisterGrainTimer(_ => RunAsync(untilDone: false),
            new GrainTimerCreationOptions(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
    }

    private async Task RunAsync(bool untilDone)
    {
        try
        {
            if (state.State.StartedAt is not { } startedAt)
            {
                // A tick left over from a finished job.
                await FinishAsync();
                return;
            }

            if (DateTimeOffset.UtcNow - startedAt > MaxAge)
            {
                logger.LogWarning("Crosspost of message {MessageId} from channel {ChannelId} given up after {Delivered} deliveries",
                    SourceMessageId, SourceChannelId, state.State.Delivered);
                await FinishAsync();
                return;
            }

            await using var ctx = await context.CreateDbContextAsync();

            var draft = await DraftAsync(ctx);
            if (draft is null)
            {
                await FinishAsync();
                return;
            }

            var isPublic = await FollowAccess.IsPublicAsync(ctx, permissionCache, state.State.SourceSpaceId, SourceChannelId);

            do
            {
                if (!await DeliverPageAsync(ctx, draft, isPublic))
                {
                    await FinishAsync();
                    return;
                }
            } while (untilDone);

            ScheduleNextPage();
        }
        catch (Exception e)
        {
            // Not rethrown: the reminder resumes from the last persisted page.
            logger.LogError(e, "Crosspost of message {MessageId} from channel {ChannelId} stopped; the reminder resumes it",
                SourceMessageId, SourceChannelId);
        }
    }

    /// <summary>Delivers the page after the cursor and moves the cursor past it. False when no page follows.</summary>
    private async Task<bool> DeliverPageAsync(ApplicationDbContext ctx, CrosspostDraft draft, bool isPublic)
    {
        var sourceSpaceId   = state.State.SourceSpaceId;
        var sourceChannelId = SourceChannelId;

        var follows = ctx.ChannelFollows.AsNoTracking().Where(f => f.SourceChannelId == sourceChannelId);
        if (state.State.Cursor is { } after)
            follows = follows.Where(f => f.Id.CompareTo(after) > 0);

        // Deleted channels and spaces drop out through their query filters.
        var page = await (
                from f in follows
                join c in ctx.Channels on f.TargetChannelId equals c.Id
                join s in ctx.Spaces on c.SpaceId equals s.Id
                where c.ChannelType != ChannelType.Voice
                orderby f.Id
                select new Target(f.Id, f.TargetSpaceId, f.TargetChannelId, f.CreatorId))
           .Take(PageSize)
           .ToListAsync();

        if (page.Count == 0)
            return false;

        var delivered = 0;
        var dropped   = new ConcurrentBag<Guid>();

        // On this activation's scheduler: the body reaches grain services.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Concurrency, TaskScheduler = TaskScheduler.Current };

        await Parallel.ForEachAsync(page, options, async (target, _) =>
        {
            try
            {
                if (await FollowAccess.CheckSourceAsync(entitlementChecker, sourceSpaceId, sourceChannelId, target.CreatorId, isPublic)
                        != FollowChannelError.NONE
                 || !await entitlementChecker.HasChannelAccessAsync(target.SpaceId, target.ChannelId, target.CreatorId,
                        ArgonEntitlement.ManageChannels))
                {
                    dropped.Add(target.FollowId);
                    return;
                }

                if (await ChannelOf(target.ChannelId).ReceiveCrosspostAsync(draft) is not null)
                    Interlocked.Increment(ref delivered);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Crosspost of message {MessageId} from channel {SourceChannelId} did not reach channel {TargetChannelId}",
                    SourceMessageId, sourceChannelId, target.ChannelId);
            }
        });

        if (!dropped.IsEmpty)
        {
            var ids = dropped.ToList();
            await ctx.ChannelFollows.Where(f => ids.Contains(f.Id)).ExecuteDeleteAsync();

            logger.LogInformation("Dropped {Count} follows of channel {ChannelId} whose creators lost access", ids.Count, sourceChannelId);
        }

        state.State.Cursor     =  page[^1].FollowId;
        state.State.Delivered  += delivered;
        state.State.Dropped    += dropped.Count;
        await state.WriteStateAsync();

        return page.Count == PageSize;
    }

    /// <summary>The copy of the post as its channel shows it now, or null when there is nothing left to deliver.</summary>
    private async Task<CrosspostDraft?> DraftAsync(ApplicationDbContext ctx)
    {
        var sourceSpaceId   = state.State.SourceSpaceId;
        var sourceChannelId = SourceChannelId;
        var messageId       = SourceMessageId;

        var row = await (
                from m in ctx.Messages
                join c in ctx.Channels on m.ChannelId equals c.Id
                join s in ctx.Spaces on c.SpaceId equals s.Id
                where m.SpaceId == sourceSpaceId && m.ChannelId == sourceChannelId && m.MessageId == messageId
                   && !m.IsDeleted && m.PublishedAt != null && c.ChannelType == ChannelType.Announcement
                select new
                {
                    m.CreatorId,
                    m.Text,
                    m.Entities,
                    ChannelName = c.Name,
                    c.Announcement,
                    SpaceName = s.Name,
                    s.AvatarFileId
                })
           .AsNoTracking()
           .FirstOrDefaultAsync();

        if (row is null)
            return null;

        var settings   = row.Announcement ?? ChannelAnnouncement.Default;
        var hideAuthor = settings.PostAsSpace && !settings.ShowAuthor;

        return new CrosspostDraft(
            hideAuthor ? UserEntity.SystemUser : row.CreatorId,
            row.Text,
            ChannelGrain.CrosspostEntities(row.Entities),
            new MessageCrosspost
            {
                SourceSpaceId           = sourceSpaceId,
                SourceChannelId         = sourceChannelId,
                SourceMessageId         = messageId,
                SourceSpaceName         = row.SpaceName,
                SourceChannelName       = row.ChannelName,
                SourceSpaceAvatarFileId = string.IsNullOrEmpty(row.AvatarFileId) ? null : row.AvatarFileId,
                HideAuthor              = hideAuthor
            });
    }

    /// <summary>The target's channel grain, in whichever region owns it, as the Ion layer resolves one.</summary>
    private IChannelGrain ChannelOf(Guid channelId)
    {
        if (!ForeignRegionCalls.IsForeign(channelId))
            return GrainFactory.GetGrain<IChannelGrain>(channelId);

        if (regions.TryGetClientFor(channelId, out var owner))
        {
            ForeignRegionCalls.Routed(channelId, nameof(IChannelGrain));
            return owner.GetGrain<IChannelGrain>(channelId);
        }

        ForeignRegionCalls.Unroutable(channelId, nameof(IChannelGrain), logger);
        return regions.GetClient(regions.RegionOf(channelId)).GetGrain<IChannelGrain>(channelId);
    }

    private async Task FinishAsync()
    {
        nextPage?.Dispose();
        nextPage = null;

        if (state.State.StartedAt is not null)
        {
            logger.LogInformation("Crosspost of message {MessageId} from channel {ChannelId} done: {Delivered} delivered, {Dropped} follows dropped",
                SourceMessageId, SourceChannelId, state.State.Delivered, state.State.Dropped);
            await state.ClearStateAsync();
            state.State = new CrosspostDeliveryState();
        }

        if (await this.GetReminder(ReminderName) is { } reminder)
            await this.UnregisterReminder(reminder);

        this.DeactivateOnIdle();
    }
}
