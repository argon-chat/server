namespace Argon.Grains;

using Instruments;

public partial class ChannelGrain : IChannelWebhooksGrain
{
    public async Task<IssuedWebhookResult> CreateWebhook(string name, CancellationToken ct = default)
    {
        if (await WebhookAccessAsync(ct) is var denied and not ChannelWebhookError.NONE)
            return new IssuedWebhookResult(null, null, denied);

        if (WebhookNameError(ref name) is var invalid and not ChannelWebhookError.NONE)
            return new IssuedWebhookResult(null, null, invalid);

        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        // One activation per channel, so the count and the insert cannot race another create.
        if (await ctx.ChannelWebhooks.CountAsync(w => w.ChannelId == channelId, ct) >= ChannelWebhookEntity.MaxPerChannel)
            return new IssuedWebhookResult(null, null, ChannelWebhookError.LIMIT_REACHED);

        var token = ChannelWebhookEntity.NewToken();
        var row = new ChannelWebhookEntity
        {
            Id        = ArgonId.NewIn(SpaceId),
            SpaceId   = SpaceId,
            ChannelId = channelId,
            Name      = name,
            TokenHash = ChannelWebhookEntity.HashToken(token),
            CreatorId = this.GetUserId(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        ctx.ChannelWebhooks.Add(row);
        await ctx.SaveChangesAsync(ct);

        return new IssuedWebhookResult(row.ToDto(), token, ChannelWebhookError.NONE);
    }

    public async Task<List<ChannelWebhook>> GetWebhooks(CancellationToken ct = default)
    {
        if (await WebhookAccessAsync(ct) is not ChannelWebhookError.NONE)
            return [];

        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);
        var rows = await ctx.ChannelWebhooks.AsNoTracking()
           .Where(w => w.ChannelId == channelId)
           .OrderBy(w => w.CreatedAt)
           .ToListAsync(ct);

        return rows.Select(w => w.ToDto()).ToList();
    }

    public async Task<IUpdateWebhookResult> RenameWebhook(Guid webhookId, string name, CancellationToken ct = default)
    {
        if (await WebhookAccessAsync(ct) is var denied and not ChannelWebhookError.NONE)
            return new FailedUpdateWebhook(denied);

        if (WebhookNameError(ref name) is var invalid and not ChannelWebhookError.NONE)
            return new FailedUpdateWebhook(invalid);

        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);
        var row = await ctx.ChannelWebhooks.FirstOrDefaultAsync(w => w.Id == webhookId && w.ChannelId == channelId, ct);
        if (row is null)
            return new FailedUpdateWebhook(ChannelWebhookError.WEBHOOK_NOT_FOUND);

        row.Name = name;
        await ctx.SaveChangesAsync(ct);
        await GrainFactory.GetGrain<IIncomingWebhookGrain>(webhookId).ForgetAsync();

        return new SuccessUpdateWebhook(row.ToDto());
    }

    public async Task<IssuedWebhookResult> RegenerateWebhookToken(Guid webhookId, CancellationToken ct = default)
    {
        if (await WebhookAccessAsync(ct) is var denied and not ChannelWebhookError.NONE)
            return new IssuedWebhookResult(null, null, denied);

        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);
        var row = await ctx.ChannelWebhooks.FirstOrDefaultAsync(w => w.Id == webhookId && w.ChannelId == channelId, ct);
        if (row is null)
            return new IssuedWebhookResult(null, null, ChannelWebhookError.WEBHOOK_NOT_FOUND);

        var token = ChannelWebhookEntity.NewToken();
        row.TokenHash = ChannelWebhookEntity.HashToken(token);
        await ctx.SaveChangesAsync(ct);
        await GrainFactory.GetGrain<IIncomingWebhookGrain>(webhookId).ForgetAsync();

        return new IssuedWebhookResult(row.ToDto(), token, ChannelWebhookError.NONE);
    }

    public async Task<bool> DeleteWebhook(Guid webhookId, CancellationToken ct = default)
    {
        if (await WebhookAccessAsync(ct) is not ChannelWebhookError.NONE)
            return false;

        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);
        var deleted = await ctx.ChannelWebhooks
           .Where(w => w.Id == webhookId && w.ChannelId == channelId)
           .ExecuteDeleteAsync(ct);

        if (deleted == 0)
            return false;

        await GrainFactory.GetGrain<IIncomingWebhookGrain>(webhookId).ForgetAsync();
        return true;
    }

    public async Task<WebhookPostOutcome> PostWebhookMessage(MessageWebhookAuthor author, string text)
    {
        if (_self.ChannelType is not (ChannelType.Text or ChannelType.Announcement))
            return WebhookPostOutcome.ChannelGone;

        if (string.IsNullOrWhiteSpace(text) || text.Length > messageOptions.Value.MaxTextLength)
            return WebhookPostOutcome.Invalid;

        try
        {
            EnforceChannelCap();
        }
        catch (InvalidOperationException)
        {
            return WebhookPostOutcome.ChannelBusy;
        }

        var channelId = this.GetPrimaryKey();
        var now       = DateTimeOffset.UtcNow;

        // Plain text: no entities, so nothing pings and nothing unfurls.
        var message = new ArgonMessageEntity
        {
            SpaceId   = SpaceId,
            ChannelId = channelId,
            CreatorId = UserEntity.SystemUser,
            Entities  = [],
            Text      = text,
            Webhook   = author,
            CreatedAt = now,
            UpdatedAt = now
        };

        var msgId = await messagesLayout.ExecuteInsertMessage(message, Random.Shared.NextInt64(1, long.MaxValue));
        message.MessageId = msgId;

        FireDetached(new MessageSent(SpaceId, message.ToDto()));

        if (lastMessage.Raise(msgId))
            ChannelGrainInstrument.LastMessageAbsorbed.Add(1);

        PublishLastMessageId(msgId);

        ChannelGrainInstrument.MessagesSent.Add(1,
            new KeyValuePair<string, object?>("channel_type", "webhook"));

        return WebhookPostOutcome.Posted;
    }

    private async Task<ChannelWebhookError> WebhookAccessAsync(CancellationToken ct)
    {
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), this.GetUserId(), ArgonEntitlement.ManageChannels, ct))
            return ChannelWebhookError.INSUFFICIENT_PERMISSIONS;

        return _self.ChannelType is ChannelType.Text or ChannelType.Announcement
            ? ChannelWebhookError.NONE
            : ChannelWebhookError.NOT_A_TEXT_CHANNEL;
    }

    private static ChannelWebhookError WebhookNameError(ref string name)
    {
        name = name?.Trim() ?? "";

        if (name.Length == 0)
            return ChannelWebhookError.NAME_EMPTY;

        return name.Length > ChannelWebhookEntity.MaxNameLength
            ? ChannelWebhookError.NAME_TOO_LONG
            : ChannelWebhookError.NONE;
    }
}
