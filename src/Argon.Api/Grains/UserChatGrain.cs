namespace Argon.Grains;

using Argon.Api.Grains.Interfaces;
using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Argon.Core.Services;
using Argon.Features.Integrations.Crawler;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;
using Core.Entities.Data;

[StatelessWorker]
public class UserChatGrain(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IUserChatGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    IConversationService conversationService,
    ILinkPreviewService linkPreviews,
    IOptions<CrawlerOptions> crawlerOptions) : Grain, IUserChatGrain
{
    private Guid Me => this.GetUserId();

    public async Task<List<UserChat>> GetRecentChatsAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var result = await ctx.UserConversations
            .AsNoTracking()
            .Where(x => x.UserId == Me && !x.IsArchived)
            .OrderByDescending(x => x.IsPinned)
            .ThenByDescending(x => x.PinnedAt)
            .ThenByDescending(x => x.LastMessageAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        // Fixation: echo chat at all time top pinned
        var echoChat = result.FirstOrDefault(x => x.PeerId == UserEntity.EchoUser);

        if (echoChat is null)
        {
            var echoConversationId = ConversationEntity.GenerateConversationId(Me, UserEntity.EchoUser);
            result.Add(new UserConversationEntity
            {
                PeerId = UserEntity.EchoUser,
                IsPinned = true,
                PinnedAt = DateTimeOffset.UtcNow.AddDays(900),
                UserId = Me,
                ConversationId = echoConversationId,
                LastMessageAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            echoChat.IsPinned = true;
            echoChat.PinnedAt = DateTimeOffset.UtcNow.AddDays(900);
        }

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task PinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // Not allowed to pin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("PinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
            {
                record = new UserConversationEntity
                {
                    UserId = Me,
                    ConversationId = conversationId,
                    PeerId = peerId,
                    LastMessageAt = now,
                    IsPinned = true,
                    PinnedAt = now,
                    LastMessageText = null
                };
                ctx.UserConversations.Add(record);
            }
            else
            {
                record.IsPinned = true;
                record.PinnedAt = now;
                ctx.UserConversations.Update(record);
            }

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatPinnedEvent(peerId, now.UtcDateTime));
    }

    public async Task UnpinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // Not allowed to unpin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("UnpinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
                return;

            record.IsPinned = false;
            record.PinnedAt = null;

            ctx.UserConversations.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatUnpinnedEvent(peerId));
    }

    public async Task MarkChatReadAsync(Guid peerId, CancellationToken ct = default)
    {
        logger.LogInformation("MarkChatRead: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var hadUnread = false;

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
                return;

            hadUnread = record.UnreadCount > 0;
            record.UnreadCount = 0;
            ctx.UserConversations.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        // The caller's other windows still show the badge.
        if (hadUnread)
            await NotifyAsync(Me, new ChatReadEvent(peerId));
    }

    /// <summary>
    /// Hides the conversation on this side only. The messages are the peer's as much as mine, so
    /// nothing is destroyed: the row is archived, and the next message either side sends brings it
    /// back (<see cref="UpdateUserConversationAsync"/> clears the flag).
    /// </summary>
    public async Task DeleteChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // The echo chat is the fixture every list has; it cannot be removed.
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("DeleteChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
                return;

            record.IsArchived  = true;
            record.IsPinned    = false;
            record.PinnedAt    = null;
            record.UnreadCount = 0;
            ctx.UserConversations.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatDeletedEvent(peerId));
    }

    public async ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadAttachmentAsync(Guid peerId, CancellationToken ct = default)
    {
        try
        {
            var userId = Me;
            await using var ctx = await context.CreateDbContextAsync(ct);

            // The same wall that stops the message the file is for.
            var blocked = await ctx.UserBlocklist.AnyAsync(x => x.UserId == peerId && x.BlockedId == userId, ct);
            if (blocked)
                return UploadFileError.NOT_AUTHORIZED;

            var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(userId);
            var response = await fileGrain.RequestUploadAsync(
                new FileUploadRequest(FilePurpose.DirectAttachment, "", 0), ct);
            return new UploadTicket(response.BlobId, response.Url, response.Fields, response.TtlSeconds);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin upload attachment for direct chat {Me} -> {Peer}", Me, peerId);
            return UploadFileError.INTERNAL_ERROR;
        }
    }

    public async ValueTask<AttachmentInfo> CompleteUploadAttachmentAsync(Guid blobId, CancellationToken ct = default)
    {
        var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(Me);
        var fileInfo  = await fileGrain.FinalizeUploadAsync(blobId, ct);

        return new AttachmentInfo(fileInfo.FileId, fileInfo.FileName ?? "", fileInfo.FileSize, fileInfo.ContentType ?? "",
            fileInfo.DownloadUrl);
    }

    /// <summary>
    /// The card for a link in a direct message, settled before the insert: the client's stub is
    /// reduced to its URL, the crawler gets the send budget, and the stub is filled or dropped. No
    /// deferred path here — direct messages have no MessageUpdated counterpart yet, so a page the
    /// crawler had not cached in time goes without a card. The composer's lookup while typing is
    /// what makes that rare.
    /// </summary>
    private async Task SettleLinkPreviewAsync(List<IMessageEntity> entities, string text)
    {
        var stub = LinkPreviewEntities.TakeStub(entities, text);
        if (stub is null)
            return;

        var outcome = await linkPreviews.ResolveAsync(stub.url, crawlerOptions.Value.SendBudget);
        if (outcome.Status == LinkPreviewStatus.Ready)
            entities[entities.IndexOf(stub)] = LinkPreviewEntities.Fill(stub, outcome.Preview!);
        else
            entities.Remove(stub);
    }

    public async Task<long> SendDirectMessageAsync(
        Guid receiverId,
        string text,
        List<IMessageEntity> entities,
        long randomId,
        long? replyTo,
        CancellationToken ct = default)
    {
        var senderId = Me;

        logger.LogInformation(
            "SendDirectMessage: {SenderId} -> {ReceiverId}, TextLength={TextLength}, RandomId={RandomId}",
            senderId, receiverId, text?.Length ?? 0, randomId);

        entities ??= [];
        await SettleLinkPreviewAsync(entities, text ?? "");

        // Get or create conversation
        var conversation = await conversationService.GetOrCreateConversationAsync(senderId, receiverId, ct);

        await using var ctx = await context.CreateDbContextAsync(ct);

        // A blocked sender is not told: the message is kept for them and never reaches the receiver.
        var blockedByReceiver = await ctx.UserBlocklist
            .AnyAsync(x => x.UserId == receiverId && x.BlockedId == senderId, ct);

        var now         = DateTimeOffset.UtcNow;
        var previewText = text?.Length > 200 ? text[..200] : text;

        DirectMessageV2Entity  message;
        DirectMessageV2Entity? inserted = null;

        try
        {
            // MessageId comes back from the insert, so a commit that failed ambiguously is checked for
            // by that id instead of being retried into a second copy of the message.
            message = await ctx.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
                async token =>
                {
                    ctx.ChangeTracker.Clear();

                    var entity = new DirectMessageV2Entity
                    {
                        ConversationId = conversation.Id,
                        SenderId = senderId,
                        Text = text ?? "",
                        Entities = entities ?? [],
                        CreatedAt = now,
                        CreatorId = senderId,
                        ReplyTo = replyTo,
                        IsHiddenFromReceiver = blockedByReceiver
                    };

                    ctx.DirectMessages.Add(entity);

                    // Update sender's chat (no unread increment)
                    await UpdateUserConversationAsync(ctx, senderId, receiverId, conversation, previewText, now, false, token);

                    if (blockedByReceiver)
                    {
                        await ctx.SaveChangesAsync(token);
                        inserted = entity;
                        return entity;
                    }

                    // Update conversation metadata
                    conversation.LastMessageAt = now;
                    conversation.LastMessageText = previewText;
                    conversation.LastMessageSenderId = senderId;
                    ctx.Conversations.Update(conversation);

                    // An ignored sender still gets through — the chat stays honest on both sides — but
                    // they do not raise the receiver's unread count.
                    var ignoredByReceiver = await ctx.UserIgnorelist
                        .AnyAsync(x => x.UserId == receiverId && x.IgnoredId == senderId, token);

                    // Update receiver's chat (increment unread unless they ignore the sender)
                    await UpdateUserConversationAsync(ctx, receiverId, senderId, conversation, previewText, now, !ignoredByReceiver, token);

                    await ctx.SaveChangesAsync(token);

                    inserted = entity;
                    return entity;
                },
                token => ctx.DirectMessages
                    .AsNoTracking()
                    .AnyAsync(m => m.ConversationId == conversation.Id && m.MessageId == inserted!.MessageId, token),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send direct message from {SenderId} to {ReceiverId}", senderId, receiverId);
            throw;
        }

        var messageDto = message.ToDto(receiverId);

        var dmEvent = new DirectMessageSent(senderId, receiverId, messageDto);

        await NotifyAsync(senderId, dmEvent);
        if (!blockedByReceiver)
            await NotifyAsync(receiverId, dmEvent);

        logger.LogInformation("DirectMessage sent: MessageId={MessageId}, ConversationId={ConversationId}",
            message.MessageId, conversation.Id);

        var statsGrain = GrainFactory.GetGrain<IUserStatsGrain>(senderId);
        _ = statsGrain.IncrementMessagesAsync();

        return message.MessageId;
    }

    public async Task<List<DirectMessage>> QueryDirectMessagesAsync(
        Guid peerId,
        long? from,
        int limit,
        CancellationToken ct = default)
    {
        var me = Me;

        logger.LogDebug("QueryDirectMessages: {Me} <-> {Peer}, From={From}, Limit={Limit}", me, peerId, from, limit);

        var conversationId = ConversationEntity.GenerateConversationId(me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Direct messages are not ArgonEntity rows, so the global soft-delete filter does not
        // cover them; a message moderation took down has to be left out here by hand.
        var query = ctx.DirectMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && !m.IsDeleted
                     && (!m.IsHiddenFromReceiver || m.SenderId == me));

        if (from.HasValue)
        {
            query = query.Where(m => m.MessageId < from.Value);
        }

        var messages = await query
            .OrderByDescending(m => m.MessageId)
            .Take(limit)
            .ToListAsync(ct);

        // Determine receiver for each message
        return messages.Select(m =>
        {
            var receiverId = m.SenderId == me ? peerId : me;
            return m.ToDto(receiverId);
        }).ToList();
    }

    public async Task UpdateChatForAsync(
        Guid userId,
        Guid peerId,
        string? previewText,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        logger.LogDebug("UpdateChatForAsync: {UserId} <-> {Peer}", userId, peerId);

        var conversation = await conversationService.GetOrCreateConversationAsync(userId, peerId, ct);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            // Update conversation
            conversation.LastMessageAt = timestamp;
            conversation.LastMessageText = previewText;
            ctx.Conversations.Update(conversation);

            // Update user conversation
            await UpdateUserConversationAsync(ctx, userId, peerId, conversation, previewText, timestamp, false, ct);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(userId, new RecentChatUpdatedEvent(
            peerId,
            userId,
            previewText,
            timestamp.UtcDateTime
        ));
    }

    private static async Task UpdateUserConversationAsync(
        ApplicationDbContext ctx,
        Guid userId,
        Guid peerId,
        ConversationEntity conversation,
        string? previewText,
        DateTimeOffset timestamp,
        bool incrementUnread,
        CancellationToken ct)
    {
        var record = await ctx.UserConversations
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ConversationId == conversation.Id, ct);

        if (record is null)
        {
            record = new UserConversationEntity
            {
                UserId = userId,
                ConversationId = conversation.Id,
                PeerId = peerId,
                LastMessageAt = timestamp,
                LastMessageText = previewText,
                IsPinned = false,
                PinnedAt = null,
                UnreadCount = incrementUnread ? 1 : 0
            };

            ctx.UserConversations.Add(record);
        }
        else
        {
            record.LastMessageAt = timestamp;
            record.LastMessageText = previewText;
            // A deleted (archived) chat comes back with the next message, on either side.
            record.IsArchived = false;

            if (incrementUnread)
            {
                record.UnreadCount++;
            }

            ctx.UserConversations.Update(record);
        }
    }

    private async Task NotifyAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);

        if (sessions.Count == 0) return;

        await notifier.NotifySessionsAsync(sessions, payload);
    }

    private static async Task ExecuteInTransactionAsync(
        ApplicationDbContext ctx,
        Func<Task> action,
        CancellationToken ct)
    {
        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            ctx.ChangeTracker.Clear();
            await using var transaction = await ctx.Database.BeginTransactionAsync(token);
            await action();
            await transaction.CommitAsync(token);
        }, ct);
    }
}