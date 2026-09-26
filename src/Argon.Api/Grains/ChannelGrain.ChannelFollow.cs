namespace Argon.Grains;

using System.Security.Cryptography;
using Instruments;
using Microsoft.EntityFrameworkCore;

// Following announcement channels: publishing a post here, and taking a published copy in.
public partial class ChannelGrain
{
    public const int PublishesPerHour = 10;

    public async Task<Either<CrosspostBatch, PublishMessageError>> PublishMessage(long messageId)
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

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel)
         || message.CreatorId != callerId
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

        await FireChannel(new MessagePublished(SpaceId, channelId, messageId, now.UtcDateTime));

        var space = await ctx.Spaces
           .AsNoTracking()
           .Where(s => s.Id == SpaceId)
           .Select(s => new { s.Name, s.AvatarFileId })
           .FirstOrDefaultAsync();

        // Deleted channels and spaces drop out through their query filters.
        var targets = await (
                from f in ctx.ChannelFollows
                join c in ctx.Channels on f.TargetChannelId equals c.Id
                join s in ctx.Spaces on c.SpaceId equals s.Id
                where f.SourceChannelId == channelId && c.ChannelType != ChannelType.Voice
                select f.TargetChannelId)
           .ToListAsync();

        var source = new MessageCrosspost
        {
            SourceSpaceId           = SpaceId,
            SourceChannelId         = channelId,
            SourceMessageId         = messageId,
            SourceSpaceName         = space?.Name ?? string.Empty,
            SourceChannelName       = _self.Name,
            SourceSpaceAvatarFileId = string.IsNullOrEmpty(space?.AvatarFileId) ? null : space.AvatarFileId
        };

        return new CrosspostBatch(
            new CrosspostDraft(message.CreatorId, message.Text, CrosspostEntities(message.Entities), source),
            targets.Distinct().ToList(),
            now);
    }

    public async Task<long?> ReceiveCrosspostAsync(CrosspostDraft draft)
    {
        if (_self.ChannelType is not (ChannelType.Text or ChannelType.Announcement))
            return null;

        var channelId = this.GetPrimaryKey();
        var now       = DateTimeOffset.UtcNow;

        // Sent as the author, but nothing of the send path applies: no permission of theirs here, no
        // slow mode, and above all no mention processing — the copy pings nobody.
        var message = new ArgonMessageEntity
        {
            SpaceId   = SpaceId,
            ChannelId = channelId,
            CreatorId = draft.AuthorId,
            Entities  = [.. draft.Entities],
            Text      = draft.Text,
            Crosspost = draft.Source,
            CreatedAt = now,
            UpdatedAt = now
        };

        var randomId = CrosspostRandomId(draft.Source);

        if (await DeduplicateAsync(message, randomId) is { } known)
            return known;

        var msgId = await messagesLayout.ExecuteInsertMessage(message, randomId);
        RememberSend(randomId, msgId);
        message.MessageId = msgId;

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
    private static List<IMessageEntity> CrosspostEntities(List<IMessageEntity>? entities)
        => SanitizeEntities((entities ?? [])
           .Where(e => e is not (MessageEntityMention or MessageEntityMentionEveryone or MessageEntityMentionRole))
           .Where(e => !IsSystemEntity(e))
           .Where(e => e is not MessageEntityLinkPreview p
                    || p.title is not null || p.description is not null || p.siteName is not null || p.imageUrl is not null)
           .ToList());

    /// <summary>Stable per source message, so a retried delivery lands once in each target.</summary>
    private static long CrosspostRandomId(MessageCrosspost source)
    {
        Span<byte> key = stackalloc byte[24];
        source.SourceChannelId.TryWriteBytes(key);
        BitConverter.TryWriteBytes(key[16..], source.SourceMessageId);
        return BitConverter.ToInt64(SHA256.HashData(key)) & long.MaxValue;
    }
}
