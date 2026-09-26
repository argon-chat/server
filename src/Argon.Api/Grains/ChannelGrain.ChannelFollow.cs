namespace Argon.Grains;

using System.Globalization;
using Argon.Core.Entities.Data;
using Argon.Features.EF;
using Instruments;
using Microsoft.EntityFrameworkCore;

// Following announcement channels: publishing a post here, and taking a published copy in.
public partial class ChannelGrain
{
    public const int PublishesPerHour = 10;

    public async Task<Either<PublishedCrosspost, PublishMessageError>> PublishMessage(long messageId)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (_self.ChannelType != ChannelType.Announcement)
            return PublishMessageError.NOT_AN_ANNOUNCEMENT_CHANNEL;

        await using var ctx = await context.CreateDbContextAsync();

        var message = await ctx.Messages
           .AsNoTracking()
           .FirstOrDefaultAsync(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted);

        if (message is null)
            return PublishMessageError.MESSAGE_NOT_FOUND;

        // An author publishes only while they may still post here.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel)
         || !(message.CreatorId == callerId
               && await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.SendMessages))
         && !await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageMessages))
            return PublishMessageError.INSUFFICIENT_PERMISSIONS;

        if (message.Crosspost is not null || message.CreatorId == UserEntity.SystemUser || (message.Entities ?? []).Any(IsSystemEntity))
            return PublishMessageError.NOT_PUBLISHABLE;

        if (message.PublishedAt is not null)
            return PublishMessageError.ALREADY_PUBLISHED;

        var now   = DateTimeOffset.UtcNow;
        var since = now.AddHours(-1);

        // This activation orders every publish of the channel, so the count cannot race the mark.
        var recent = await ctx.Messages
           .CountAsync(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.PublishedAt != null && m.PublishedAt >= since);
        if (recent >= PublishesPerHour)
            return PublishMessageError.PUBLISH_RATE_LIMITED;

        var marked = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && m.PublishedAt == null && !m.IsDeleted)
           .ExecuteUpdateAsync(s => s
               .SetProperty(m => m.PublishedAt, now)
               .SetProperty(m => m.UpdatedAt, now));

        if (marked == 0)
            return PublishMessageError.ALREADY_PUBLISHED;

        // Deleted channels and spaces drop out through their query filters.
        var targets = await (
                from f in ctx.ChannelFollows
                join c in ctx.Channels on f.TargetChannelId equals c.Id
                join s in ctx.Spaces on c.SpaceId equals s.Id
                where f.SourceChannelId == channelId && c.ChannelType != ChannelType.Voice
                select f.Id)
           .CountAsync();

        if (targets > 0)
        {
            try
            {
                await GrainFactory.GetGrain<ICrosspostDeliveryGrain>(channelId, messageId.ToString(CultureInfo.InvariantCulture))
                   .StartAsync(SpaceId);
            }
            catch
            {
                // Not handed over, so not published: the author can try again.
                await ctx.Messages
                   .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId)
                   .ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAt, (DateTimeOffset?)null));
                throw;
            }
        }

        await FireChannel(new MessagePublished(SpaceId, channelId, messageId, now.UtcDateTime));

        return new PublishedCrosspost(now, targets);
    }

    public async Task<long?> ReceiveCrosspostAsync(CrosspostDraft draft)
    {
        if (_self.ChannelType is not (ChannelType.Text or ChannelType.Announcement))
            return null;

        var channelId = this.GetPrimaryKey();
        var now       = DateTimeOffset.UtcNow;
        var source    = draft.Source;

        await using var ctx = await context.CreateDbContextAsync();

        // Deduplicated by what was published rather than by a randomId, which a member here could send first.
        var claim = new CrosspostDeliveryEntity
        {
            TargetChannelId = channelId,
            SourceChannelId = source.SourceChannelId,
            SourceMessageId = source.SourceMessageId,
            CreatedAt       = now,
            ExpireAt        = now + CrosspostDeliveryEntity.Retention
        };
        ctx.CrosspostDeliveries.Add(claim);

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            return await ctx.CrosspostDeliveries
               .AsNoTracking()
               .Where(d => d.TargetChannelId == channelId && d.SourceChannelId == source.SourceChannelId
                        && d.SourceMessageId == source.SourceMessageId)
               .Select(d => d.MessageId)
               .FirstOrDefaultAsync();
        }

        // Nothing of the send path applies: no permission of the author's here, no slow mode, and above
        // all no mention processing — the copy pings nobody.
        var message = new ArgonMessageEntity
        {
            SpaceId   = SpaceId,
            ChannelId = channelId,
            CreatorId = draft.AuthorId,
            Entities  = [.. draft.Entities],
            Text      = draft.Text,
            Crosspost = source,
            CreatedAt = now,
            UpdatedAt = now
        };

        long msgId;
        try
        {
            msgId = await messagesLayout.ExecuteInsertMessage(message, Random.Shared.NextInt64(1, long.MaxValue));
        }
        catch
        {
            ctx.CrosspostDeliveries.Remove(claim);
            await ctx.SaveChangesAsync();
            throw;
        }

        message.MessageId = msgId;

        try
        {
            claim.MessageId = msgId;
            await ctx.SaveChangesAsync();
        }
        catch (Exception e)
        {
            // The claim still stands, so a redelivery cannot duplicate the copy; it only cannot name it.
            logger.LogWarning(e, "Crosspost {MessageId} in channel {ChannelId} was not recorded on its claim", msgId, channelId);
        }

        await ResolveAttachmentUrls(message);
        FireDetached(new MessageSent(SpaceId, message.ToDto()));

        if (lastMessage.Raise(msgId))
            ChannelGrainInstrument.LastMessageAbsorbed.Add(1);

        PublishLastMessageId(msgId);

        return msgId;
    }

    /// <summary>A channel that stops being an announcement channel loses its followers.</summary>
    private async Task ForgetFollowersAsync(ApplicationDbContext ctx, CancellationToken ct)
    {
        var channelId = this.GetPrimaryKey();
        await ctx.ChannelFollows.Where(f => f.SourceChannelId == channelId).ExecuteDeleteAsync(ct);
    }

    private static bool IsSystemEntity(IMessageEntity entity)
        => entity is MessageEntitySystemCallStarted or MessageEntitySystemCallEnded
            or MessageEntitySystemCallTimeout or MessageEntitySystemUserJoined;

    /// <summary>
    /// What a copy keeps: text formatting, attachments, GIFs and a finished link card. Mentions point at
    /// people and roles of the source space, so they go; files resolve anywhere by id (see
    /// <see cref="FillEntityUrls"/>), so they stay.
    /// </summary>
    internal static List<IMessageEntity> CrosspostEntities(List<IMessageEntity>? entities)
        => SanitizeEntities((entities ?? [])
           .Where(e => e is not (MessageEntityMention or MessageEntityMentionEveryone or MessageEntityMentionRole))
           .Where(e => !IsSystemEntity(e))
           .Where(e => e is not MessageEntityLinkPreview p
                    || p.title is not null || p.description is not null || p.siteName is not null || p.imageUrl is not null)
           .ToList());
}
