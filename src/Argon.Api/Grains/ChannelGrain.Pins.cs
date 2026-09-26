namespace Argon.Grains;

using Core.Entities.Data;

public partial class ChannelGrain
{
    public const int PinLimit = 50;

    private bool HoldsMessages => _self.ChannelType is ChannelType.Text or ChannelType.Announcement;

    public async Task<IPinMessageResult> PinMessage(long messageId, CancellationToken ct = default)
    {
        if (!HoldsMessages)
            return new FailedPinMessage(PinMessageError.NOT_A_TEXT_CHANNEL);

        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageMessages, ct))
            return new FailedPinMessage(PinMessageError.INSUFFICIENT_PERMISSIONS);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var message = await ctx.Messages
           .AsNoTracking()
           .FirstOrDefaultAsync(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted, ct);

        if (message is null)
            return new FailedPinMessage(PinMessageError.MESSAGE_NOT_FOUND);

        var existing = await ctx.ChannelPins
           .AsNoTracking()
           .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.MessageId == messageId, ct);

        if (existing is not null)
            return new SuccessPinMessage(ToPinnedMessage(existing, message));

        if (await ctx.ChannelPins.CountAsync(p => p.ChannelId == channelId, ct) >= PinLimit)
            return new FailedPinMessage(PinMessageError.PIN_LIMIT_REACHED);

        var now = DateTimeOffset.UtcNow;

        var pin = new ChannelPinEntity
        {
            SpaceId   = SpaceId,
            ChannelId = channelId,
            MessageId = messageId,
            PinnedBy  = callerId,
            // Stored with microsecond precision; the answer should match what a later read returns.
            PinnedAt  = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond))
        };

        ctx.ChannelPins.Add(pin);
        await ctx.SaveChangesAsync(ct);

        await FireChannel(new MessagePinned(SpaceId, channelId, messageId, callerId), ct);

        return new SuccessPinMessage(ToPinnedMessage(pin, message));
    }

    public async Task<IUnpinMessageResult> UnpinMessage(long messageId, CancellationToken ct = default)
    {
        if (!HoldsMessages)
            return new FailedUnpinMessage(PinMessageError.NOT_A_TEXT_CHANNEL);

        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageMessages, ct))
            return new FailedUnpinMessage(PinMessageError.INSUFFICIENT_PERMISSIONS);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await DropPinAsync(ctx, messageId, callerId, ct);

        return new SuccessUnpinMessage();
    }

    public async Task<List<PinnedMessage>> GetPinnedMessages(CancellationToken ct = default)
    {
        if (!HoldsMessages)
            return [];

        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        // The refusal QueryMessages gives: pins are part of the history.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel, ct)
         || !await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ReadHistory, ct))
            return [];

        await using var ctx = await context.CreateDbContextAsync(ct);

        var rows = await ctx.ChannelPins
           .AsNoTracking()
           .Where(p => p.ChannelId == channelId)
           .Join(ctx.Messages.Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && !m.IsDeleted),
                p => p.MessageId, m => m.MessageId, (p, m) => new { Pin = p, Message = m })
           .OrderByDescending(x => x.Pin.PinnedAt)
           .ThenByDescending(x => x.Pin.MessageId)
           .Take(PinLimit)
           .ToListAsync(ct);

        return rows.Select(x => ToPinnedMessage(x.Pin, x.Message)).ToList();
    }

    /// <summary>Removes the message's pin, if it has one, and tells the channel.</summary>
    private async Task DropPinAsync(ApplicationDbContext ctx, long messageId, Guid byUserId, CancellationToken ct)
    {
        var channelId = this.GetPrimaryKey();

        var removed = await ctx.ChannelPins
           .Where(p => p.ChannelId == channelId && p.MessageId == messageId)
           .ExecuteDeleteAsync(ct);

        if (removed > 0)
            await FireChannel(new MessageUnpinned(SpaceId, channelId, messageId, byUserId), ct);
    }

    private PinnedMessage ToPinnedMessage(ChannelPinEntity pin, ArgonMessageEntity message)
    {
        // Reactions are buffered here and reach the row on the next flush.
        if (_reactionCache.TryGetValue(message.MessageId, out var reactions))
            message.Reactions = reactions;

        FillEntityUrls(message);

        return new PinnedMessage(message.ToDto(), pin.PinnedBy, pin.PinnedAt.UtcDateTime);
    }
}
