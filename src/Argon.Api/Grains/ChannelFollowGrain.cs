namespace Argon.Grains;

using System.Linq.Expressions;
using Argon.Core.Entities.Data;
using Argon.Features.Clustering.Regions;
using Argon.Features.EF;
using Core.Services;
using Microsoft.EntityFrameworkCore;

public class ChannelFollowGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker entitlementChecker,
    ILogger<ChannelFollowGrain> logger) : Grain, IChannelFollowGrain
{
    public async Task<IFollowChannelResult> FollowAsync(Guid callerId, Guid sourceSpaceId, Guid sourceChannelId, Guid targetSpaceId)
    {
        var targetChannelId = this.GetPrimaryKey();

        if (sourceChannelId == targetChannelId)
            return new FailedFollowChannel(FollowChannelError.SAME_CHANNEL);

        await using var ctx = await context.CreateDbContextAsync();

        var source = await ctx.Channels
           .AsNoTracking()
           .Where(c => c.Id == sourceChannelId && c.SpaceId == sourceSpaceId)
           .Select(c => new { c.ChannelType })
           .FirstOrDefaultAsync();

        if (source is null)
            return new FailedFollowChannel(FollowChannelError.SOURCE_NOT_FOUND);

        if (!await entitlementChecker.HasChannelAccessAsync(sourceSpaceId, sourceChannelId, callerId, ArgonEntitlement.ViewChannel)
         || !await entitlementChecker.HasChannelAccessAsync(sourceSpaceId, sourceChannelId, callerId, ArgonEntitlement.ReadHistory))
            return new FailedFollowChannel(FollowChannelError.NO_ACCESS_TO_SOURCE);

        if (source.ChannelType != ChannelType.Announcement)
            return new FailedFollowChannel(FollowChannelError.NOT_AN_ANNOUNCEMENT_CHANNEL);

        var target = await ctx.Channels
           .AsNoTracking()
           .Where(c => c.Id == targetChannelId && c.SpaceId == targetSpaceId)
           .Select(c => new { c.ChannelType })
           .FirstOrDefaultAsync();

        if (target is null)
            return new FailedFollowChannel(FollowChannelError.TARGET_NOT_FOUND);

        if (!await entitlementChecker.HasChannelAccessAsync(targetSpaceId, targetChannelId, callerId, ArgonEntitlement.ManageChannels))
            return new FailedFollowChannel(FollowChannelError.INSUFFICIENT_PERMISSIONS);

        if (target.ChannelType is not (ChannelType.Text or ChannelType.Announcement))
            return new FailedFollowChannel(FollowChannelError.TARGET_NOT_TEXT);

        if (await ctx.ChannelFollows.AnyAsync(f => f.SourceChannelId == sourceChannelId && f.TargetChannelId == targetChannelId))
            return new FailedFollowChannel(FollowChannelError.ALREADY_FOLLOWING);

        // Only live sources count; a row left by a deleted one frees its slot. This activation takes
        // one follow of its channel at a time, so the count and the insert cannot interleave.
        var sources = await (
                from f in ctx.ChannelFollows
                join c in ctx.Channels on f.SourceChannelId equals c.Id
                where f.TargetChannelId == targetChannelId
                select f.Id)
           .CountAsync();

        if (sources >= ChannelFollowEntity.MaxSourcesPerTarget)
            return new FailedFollowChannel(FollowChannelError.TOO_MANY_FOLLOWS);

        var follow = new ChannelFollowEntity
        {
            Id              = ArgonId.New(),
            SourceSpaceId   = sourceSpaceId,
            SourceChannelId = sourceChannelId,
            TargetSpaceId   = targetSpaceId,
            TargetChannelId = targetChannelId,
            CreatorId       = callerId,
            CreatedAt       = DateTimeOffset.UtcNow
        };

        ctx.ChannelFollows.Add(follow);

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            return new FailedFollowChannel(FollowChannelError.ALREADY_FOLLOWING);
        }

        logger.LogInformation("Channel {TargetChannelId} now follows {SourceChannelId} (by {CallerId})",
            targetChannelId, sourceChannelId, callerId);

        var link = (await LinksAsync(ctx, f => f.Id == follow.Id)).FirstOrDefault();

        return link is null
            ? new FailedFollowChannel(FollowChannelError.TARGET_NOT_FOUND)
            : new SuccessFollowChannel(link);
    }

    public async Task<List<ChannelFollowLink>> GetFollowersAsync(Guid callerId, Guid spaceId)
    {
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, callerId, ArgonEntitlement.ManageChannels))
            return [];

        await using var ctx = await context.CreateDbContextAsync();
        return await LinksAsync(ctx, f => f.SourceChannelId == channelId && f.SourceSpaceId == spaceId);
    }

    public async Task<List<ChannelFollowLink>> GetFollowedSourcesAsync(Guid callerId, Guid spaceId)
    {
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, callerId, ArgonEntitlement.ViewChannel))
            return [];

        await using var ctx = await context.CreateDbContextAsync();
        return await LinksAsync(ctx, f => f.TargetChannelId == channelId && f.TargetSpaceId == spaceId);
    }

    public async Task<IRemoveFollowResult> RemoveAsync(Guid callerId, Guid spaceId, Guid followId)
    {
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, callerId, ArgonEntitlement.ManageChannels))
            return new FailedRemoveFollow(RemoveFollowError.INSUFFICIENT_PERMISSIONS);

        await using var ctx = await context.CreateDbContextAsync();

        var removed = await ctx.ChannelFollows
           .Where(f => f.Id == followId
                    && (f.SourceChannelId == channelId && f.SourceSpaceId == spaceId
                     || f.TargetChannelId == channelId && f.TargetSpaceId == spaceId))
           .ExecuteDeleteAsync();

        if (removed == 0)
            return new FailedRemoveFollow(RemoveFollowError.FOLLOW_NOT_FOUND);

        logger.LogInformation("Follow {FollowId} removed from channel {ChannelId} (by {CallerId})", followId, channelId, callerId);

        return new SuccessRemoveFollow();
    }

    /// <summary>The links matching <paramref name="which"/> whose channels and spaces both still exist.</summary>
    private static async Task<List<ChannelFollowLink>> LinksAsync(ApplicationDbContext ctx, Expression<Func<ChannelFollowEntity, bool>> which)
    {
        var rows = await (
                from f in ctx.ChannelFollows.Where(which)
                join sc in ctx.Channels on f.SourceChannelId equals sc.Id
                join tc in ctx.Channels on f.TargetChannelId equals tc.Id
                join ss in ctx.Spaces on sc.SpaceId equals ss.Id
                join ts in ctx.Spaces on tc.SpaceId equals ts.Id
                orderby f.CreatedAt
                select new
                {
                    f.Id,
                    SourceSpaceId     = ss.Id,
                    SourceChannelId   = sc.Id,
                    SourceSpaceName   = ss.Name,
                    SourceChannelName = sc.Name,
                    SourceAvatar      = ss.AvatarFileId,
                    TargetSpaceId     = ts.Id,
                    TargetChannelId   = tc.Id,
                    TargetSpaceName   = ts.Name,
                    TargetChannelName = tc.Name,
                    TargetAvatar      = ts.AvatarFileId,
                    f.CreatedAt,
                    f.CreatorId
                })
           .AsNoTracking()
           .ToListAsync();

        return rows.Select(r => new ChannelFollowLink(
                r.Id,
                r.SourceSpaceId, r.SourceChannelId, r.SourceSpaceName, r.SourceChannelName,
                r.TargetSpaceId, r.TargetChannelId, r.TargetSpaceName, r.TargetChannelName,
                r.CreatedAt.UtcDateTime, r.CreatorId,
                string.IsNullOrEmpty(r.SourceAvatar) ? null : r.SourceAvatar,
                string.IsNullOrEmpty(r.TargetAvatar) ? null : r.TargetAvatar))
           .ToList();
    }
}
