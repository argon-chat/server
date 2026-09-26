namespace Argon.Grains;

using Core.Entities.Data;

public partial class ChannelGrain
{
    public const int PinLimit = 50;

    private bool HoldsMessages => _self.ChannelType is ChannelType.Text or ChannelType.Announcement;

    // Pins of live messages, newest first. This grain is their only writer, so once read the list is
    // kept up to date here instead of being read again.
    private List<ChannelPinEntity>? pins;

    // The pinned messages as last read. An entry goes when its message changes and is read again on
    // the next listing; the version tells a read in flight that one went meanwhile.
    private readonly Dictionary<long, ArgonMessageEntity> pinnedMessages = new();
    private int pinnedMessagesVersion;

    public async Task<IPinMessageResult> PinMessage(long messageId, CancellationToken ct = default)
    {
        if (!HoldsMessages)
            return new FailedPinMessage(PinMessageError.NOT_A_TEXT_CHANNEL);

        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await MayManagePinsAsync(callerId, ct))
            return new FailedPinMessage(PinMessageError.INSUFFICIENT_PERMISSIONS);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var message = await ctx.Messages
           .AsNoTracking()
           .FirstOrDefaultAsync(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted, ct);

        if (message is null)
            return new FailedPinMessage(PinMessageError.MESSAGE_NOT_FOUND);

        var current = await PinsAsync(ct);

        if (current.Find(p => p.MessageId == messageId) is { } existing)
            return new SuccessPinMessage(ToPinnedMessage(existing, message));

        if (current.Count >= PinLimit)
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

        current.Insert(0, pin);
        FillEntityUrls(message);
        pinnedMessages[messageId] = message;

        await FireChannel(new MessagePinned(SpaceId, channelId, messageId, callerId), ct);

        return new SuccessPinMessage(ToPinnedMessage(pin, message));
    }

    public async Task<IUnpinMessageResult> UnpinMessage(long messageId, CancellationToken ct = default)
    {
        if (!HoldsMessages)
            return new FailedUnpinMessage(PinMessageError.NOT_A_TEXT_CHANNEL);

        var callerId = this.GetUserId();

        if (!await MayManagePinsAsync(callerId, ct))
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
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory, ct))
            return [];

        var current = await PinsAsync(ct);
        var missing = current.Where(p => !pinnedMessages.ContainsKey(p.MessageId)).Select(p => p.MessageId).ToList();
        var read    = missing.Count > 0 ? await ReadPinnedMessagesAsync(missing, ct) : null;

        var result = new List<PinnedMessage>(current.Count);
        foreach (var pin in current)
            if (pinnedMessages.TryGetValue(pin.MessageId, out var message) || read?.TryGetValue(pin.MessageId, out message) == true)
                result.Add(ToPinnedMessage(pin, message!));

        return result;
    }

    /// <summary>Pinning is moderation that answers with the message, so it takes ReadHistory as well.</summary>
    private async Task<bool> MayManagePinsAsync(Guid callerId, CancellationToken ct)
        => await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), callerId, ArgonEntitlement.ManageMessages, ct)
        && await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), callerId, ArgonEntitlement.ReadHistory, ct);

    /// <summary>The pins, read once per activation together with their messages.</summary>
    private async Task<List<ChannelPinEntity>> PinsAsync(CancellationToken ct)
    {
        if (pins is not null)
            return pins;

        var channelId = this.GetPrimaryKey();
        var version   = pinnedMessagesVersion;

        await using var ctx = await context.CreateDbContextAsync(ct);

        // A pin row can outlive its message; only live messages take a slot.
        var rows = await ctx.ChannelPins
           .AsNoTracking()
           .Where(p => p.ChannelId == channelId)
           .Join(ctx.Messages.AsNoTracking().Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && !m.IsDeleted),
                p => p.MessageId, m => m.MessageId, (p, m) => new { Pin = p, Message = m })
           .ToListAsync(ct);

        if (version == pinnedMessagesVersion)
            foreach (var row in rows)
            {
                FillEntityUrls(row.Message);
                pinnedMessages[row.Message.MessageId] = row.Message;
            }

        return pins ??= rows
           .Select(r => r.Pin)
           .OrderByDescending(p => p.PinnedAt)
           .ThenByDescending(p => p.MessageId)
           .ToList();
    }

    /// <summary>Reads pinned messages whose copy was dropped; a pin whose message is gone goes too.</summary>
    private async Task<Dictionary<long, ArgonMessageEntity>> ReadPinnedMessagesAsync(List<long> messageIds, CancellationToken ct)
    {
        var channelId = this.GetPrimaryKey();
        var version   = pinnedMessagesVersion;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var read = await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && messageIds.Contains(m.MessageId) && !m.IsDeleted)
           .ToDictionaryAsync(m => m.MessageId, ct);

        foreach (var message in read.Values)
            FillEntityUrls(message);

        if (version == pinnedMessagesVersion)
            foreach (var (id, message) in read)
                pinnedMessages[id] = message;

        pins?.RemoveAll(p => messageIds.Contains(p.MessageId) && !read.ContainsKey(p.MessageId));

        return read;
    }

    /// <summary>
    /// Every change to a message is announced to the channel, so a pinned copy is dropped on the same
    /// announcement.
    /// </summary>
    private void ForgetChangedPin<T>(T ev) where T : IArgonEvent
    {
        var messageId = ev switch
        {
            MessageUpdated e   => e.message.messageId,
            MessageEdited e    => e.messageId,
            MessagePublished e => e.messageId,
            _                  => (long?)null
        };

        if (messageId is not { } id)
            return;

        pinnedMessagesVersion++;
        pinnedMessages.Remove(id);
    }

    /// <summary>Removes the message's pin, if it has one, and tells the channel.</summary>
    private async Task DropPinAsync(ApplicationDbContext ctx, long messageId, Guid byUserId, CancellationToken ct)
    {
        var current = await PinsAsync(ct);
        if (!current.Exists(p => p.MessageId == messageId))
            return;

        var channelId = this.GetPrimaryKey();

        await ctx.ChannelPins
           .Where(p => p.ChannelId == channelId && p.MessageId == messageId)
           .ExecuteDeleteAsync(ct);

        current.RemoveAll(p => p.MessageId == messageId);
        pinnedMessages.Remove(messageId);

        await FireChannel(new MessageUnpinned(SpaceId, channelId, messageId, byUserId), ct);
    }

    private PinnedMessage ToPinnedMessage(ChannelPinEntity pin, ArgonMessageEntity message)
    {
        // Reactions are buffered here and reach the row on the next flush.
        if (_reactionCache.TryGetValue(message.MessageId, out var reactions))
            message = message with { Reactions = reactions };

        FillEntityUrls(message);

        return new PinnedMessage(message.ToDto(), pin.PinnedBy, pin.PinnedAt.UtcDateTime);
    }
}
