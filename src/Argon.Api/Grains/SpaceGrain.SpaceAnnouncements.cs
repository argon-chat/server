namespace Argon.Grains;

public partial class SpaceGrain
{
    public async Task<SetMainAnnouncementChannelError> SetMainAnnouncementChannel(Guid? channelId)
    {
        var spaceId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        if (!await entitlementChecker.HasAccessAsync(ctx, spaceId, this.GetUserId(), ArgonEntitlement.ManageServer))
            return SetMainAnnouncementChannelError.NO_PERMISSION;

        if (channelId is { } id)
        {
            var type = await ctx.Channels
               .AsNoTracking()
               .Where(c => c.Id == id && c.SpaceId == spaceId)
               .Select(c => (ChannelType?)c.ChannelType)
               .FirstOrDefaultAsync();

            if (type is null)
                return SetMainAnnouncementChannelError.CHANNEL_NOT_FOUND;
            if (type != ChannelType.Announcement)
                return SetMainAnnouncementChannelError.NOT_ANNOUNCEMENT_CHANNEL;
        }

        var space = await ctx.Spaces.FirstAsync(x => x.Id == spaceId);
        if (space.MainAnnouncementChannelId == channelId)
            return SetMainAnnouncementChannelError.NONE;

        space.MainAnnouncementChannelId = channelId;
        await ctx.SaveChangesAsync();
        await AnnounceSpaceDetailsAsync(space);

        return SetMainAnnouncementChannelError.NONE;
    }

    public Task ForgetMainAnnouncementChannelAsync(Guid channelId)
        => ForgetMainAnnouncementChannelsAsync([channelId]);

    /// <summary>Clears the main announcement channel when it is one of <paramref name="channels"/>.</summary>
    private async Task ForgetMainAnnouncementChannelsAsync(List<Guid> channels)
    {
        if (channels.Count == 0)
            return;

        var spaceId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        var space = await ctx.Spaces.FirstOrDefaultAsync(x => x.Id == spaceId
         && x.MainAnnouncementChannelId != null && channels.Contains(x.MainAnnouncementChannelId.Value));
        if (space is null)
            return;

        space.MainAnnouncementChannelId = null;
        await ctx.SaveChangesAsync();
        await AnnounceSpaceDetailsAsync(space);
    }

    private async Task AnnounceSpaceDetailsAsync(SpaceEntity space)
    {
        await Invalidate();

        var spaceBase = new ArgonSpaceBase(space.Id, space.Name, space.Description!, space.AvatarFileId, space.TopBannedFileId,
            space.BoostCount, space.BoostLevel, space.IsVerified, space.IsOfficial, space.HideBoostStrip, space.InviteImageFileId,
            space.IsCommunity, space.MainAnnouncementChannelId);
        await Fire(new SpaceDetailsUpdated(space.Id, spaceBase));
    }
}
