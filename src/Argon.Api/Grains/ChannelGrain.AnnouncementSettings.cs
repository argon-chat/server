namespace Argon.Grains;

using Instruments;
using ion.runtime;
using Microsoft.EntityFrameworkCore;

public partial class ChannelGrain
{
    public async Task<Either<ChannelEntity, UpdateChannelError>> SetAnnouncementSettings(bool reactions, bool postAsSpace, bool showAuthor,
        CancellationToken ct = default)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageChannels, ct))
            return UpdateChannelError.INSUFFICIENT_PERMISSIONS;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null)
            return UpdateChannelError.CHANNEL_NOT_FOUND;
        if (channel.ChannelType != ChannelType.Announcement)
            return UpdateChannelError.NOT_AN_ANNOUNCEMENT_CHANNEL;

        var next = new ChannelAnnouncement { Reactions = reactions, PostAsSpace = postAsSpace, ShowAuthor = showAuthor };
        if (next == ChannelAnnouncement.Of(channel))
            return await WithStoredMarkAsync(ctx, channel, ct);

        channel.Announcement = next;
        await ctx.SaveChangesAsync(ct);

        // AddReaction reads the setting off the activation's copy.
        _self = channel;

        var patch = new IonPartial<ArgonChannel>();
        patch.Modify(x => x.announcement, ChannelAnnouncement.ToDto(next));

        await readCache.SignalInvalidationAsync(SpaceId, ct: ct);
        await Fire(new ChannelModifiedV2(SpaceId, channelId, patch), ct);

        return await WithStoredMarkAsync(ctx, channel, ct);
    }

    /// <summary>The refusal for a new reaction in an announcement channel with reactions off, else null.</summary>
    private IAddReactionResult? RefuseReactionIfDisabled()
    {
        if (_self.ChannelType != ChannelType.Announcement || ChannelAnnouncement.Of(_self).Reactions)
            return null;

        ChannelGrainInstrument.ReactionsAdded.Add(1,
            new KeyValuePair<string, object?>("result", "reactions_disabled"));
        return new FailedAddReaction(AddReactionError.REACTIONS_DISABLED);
    }

    /// <summary>A type change carries the announcement field too: set on becoming one, cleared on leaving.</summary>
    private static void PatchAnnouncementForType(IonPartial<ArgonChannel> patch, ChannelEntity channel)
    {
        if (ChannelAnnouncement.ToDto(channel) is { } settings)
            patch.Modify(x => x.announcement, settings);
        else
            patch.Remove(x => x.announcement);
    }
}
