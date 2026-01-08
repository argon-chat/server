namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Argon.Core.Services;
using Orleans.Concurrency;
using Core.Entities.Data;

[StatelessWorker]
public class UserChatGrain(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IUserChatGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    INotificationCounterService notificationCounter,
    IConversationService conversationService) : Grain, IUserChatGrain
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

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var now = DateTimeOffset.UtcNow;
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

            await NotifyAsync(Me, new ChatPinnedEvent(peerId, now.UtcDateTime));
        }, ct);
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

        var unreadCount = 0L;

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var conversationId = ConversationEntity.GenerateConversationId(Me, peerId);

            var record = await ctx.UserConversations
                .FirstOrDefaultAsync(x => x.UserId == Me && x.ConversationId == conversationId, ct);

            if (record is null)
                return;

            unreadCount = record.UnreadCount;
            record.UnreadCount = 0;
            ctx.UserConversations.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        if (unreadCount > 0)
        {
            await notificationCounter.DecrementAsync(Me, NotificationCounterType.UnreadDirectMessages, unreadCount, ct);
        }
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

        // Get or create conversation
        var conversation = await conversationService.GetOrCreateConversationAsync(senderId, receiverId, ct);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var strategy = ctx.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(ct);

            try
            {
                var now = DateTimeOffset.UtcNow;

                // Create message in new table
                var message = new DirectMessageV2Entity
                {
                    ConversationId = conversation.Id,
                    SenderId = senderId,
                    Text = text ?? "",
                    Entities = entities ?? [],
                    CreatedAt = now,
                    CreatorId = senderId,
                    ReplyTo = replyTo
                };

                ctx.DirectMessagesV2.Add(message);
                await ctx.SaveChangesAsync(ct);

                var messageId = message.MessageId;
                var previewText = text?.Length > 200 ? text[..200] : text;

                // Update conversation metadata
                conversation.LastMessageAt = now;
                conversation.LastMessageText = previewText;
                conversation.LastMessageSenderId = senderId;
                ctx.Conversations.Update(conversation);

                // Update sender's chat (no unread increment)
                await UpdateUserConversationAsync(ctx, senderId, receiverId, conversation, previewText, now, false, ct);

                // Update receiver's chat (increment unread)
                await UpdateUserConversationAsync(ctx, receiverId, senderId, conversation, previewText, now, true, ct);

                await ctx.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                await notificationCounter.IncrementAsync(receiverId, NotificationCounterType.UnreadDirectMessages, 1, ct);

                var messageDto = message.ToDto(receiverId);

                var dmEvent = new DirectMessageSent(senderId, receiverId, messageDto);

                await NotifyAsync(senderId, dmEvent);
                await NotifyAsync(receiverId, dmEvent);

                logger.LogInformation("DirectMessage sent: MessageId={MessageId}, ConversationId={ConversationId}",
                    messageId, conversation.Id);

                var statsGrain = GrainFactory.GetGrain<IUserStatsGrain>(senderId);
                _ = statsGrain.IncrementMessagesAsync();

                return messageId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send direct message from {SenderId} to {ReceiverId}", senderId, receiverId);
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
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

        var query = ctx.DirectMessagesV2
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId);

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

    public async Task UpdateChatAsync(
        Guid peerId,
        string? previewText,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        await UpdateChatForAsync(Me, peerId, previewText, timestamp, ct);
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
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
            await action();
            await transaction.CommitAsync(ct);
        });
    }
}