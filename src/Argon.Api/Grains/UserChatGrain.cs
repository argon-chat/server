namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Orleans.Concurrency;
using Core.Entities.Data;

[StatelessWorker]
public class UserChatGrain(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IUserChatGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier) : Grain, IUserChatGrain
{
    private Guid Me => this.GetUserId();

    public async Task<List<UserChat>> GetRecentChatsAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var result = await ctx.UserChatlist
           .AsNoTracking()
           .Where(x => x.UserId == Me)
           .OrderByDescending(x => x.IsPinned)
           .ThenByDescending(x => x.PinnedAt)
           .ThenByDescending(x => x.LastMessageAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);

        // fixation echo chat at all time top pinned
        var echoChat = result.FirstOrDefault(x => x.PeerId == UserEntity.EchoUser);

        if (echoChat is null)
        {
            result.Add(new UserChatEntity()
            {
                PeerId   = UserEntity.EchoUser,
                IsPinned = true,
                PinnedAt = DateTime.UtcNow.AddDays(900),
                UserId   = Me
            });
        }
        else
            echoChat.PinnedAt = DateTime.UtcNow.AddDays(900);

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task PinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // not allowed pin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("PinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var now = DateTimeOffset.UtcNow;

            var record = await ctx.UserChatlist
               .FirstOrDefaultAsync(x => x.UserId == Me && x.PeerId == peerId, ct);

            if (record is null)
            {
                record = new UserChatEntity
                {
                    UserId          = Me,
                    PeerId          = peerId,
                    LastMessageAt   = now,
                    IsPinned        = true,
                    PinnedAt        = now,
                    LastMessageText = null,
                };
                ctx.UserChatlist.Add(record);
            }
            else
            {
                record.IsPinned = true;
                record.PinnedAt = now;
                ctx.UserChatlist.Update(record);
            }

            await ctx.SaveChangesAsync(ct);

            await NotifyAsync(Me, new ChatPinnedEvent(peerId, now.UtcDateTime));
        }, ct);
    }

    public async Task UnpinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // not allowed unpin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("UnpinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var record = await ctx.UserChatlist
               .FirstOrDefaultAsync(x => x.UserId == Me && x.PeerId == peerId, ct);

            if (record is null)
                return;

            record.IsPinned = false;
            record.PinnedAt = null;

            ctx.UserChatlist.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatUnpinnedEvent(peerId));
    }

    public async Task MarkChatReadAsync(Guid peerId, CancellationToken ct = default)
    {
        logger.LogInformation("MarkChatRead: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var record = await ctx.UserChatlist
                .FirstOrDefaultAsync(x => x.UserId == Me && x.PeerId == peerId, ct);

            if (record is null)
                return;

            record.UnreadCount = 0;
            ctx.UserChatlist.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        //await NotifyAsync(Me, new ChatMarkedReadEvent(peerId));
    }

    public async Task<long> SendDirectMessageAsync(Guid receiverId, string text, List<IMessageEntity> entities, long randomId, long? replyTo, CancellationToken ct = default)
    {
        var senderId = Me;

        logger.LogInformation(
            "SendDirectMessage: {SenderId} -> {ReceiverId}, TextLength={TextLength}, RandomId={RandomId}",
            senderId, receiverId, text?.Length ?? 0, randomId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var strategy = ctx.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(ct);

            try
            {
                // Check for duplicate message using cache
                var dedupKey = $"dm_dedup:{senderId}:{receiverId}:{randomId}";
                // TODO: implement deduplication with cache similar to channel messages

                // Create message entity
                var message = new DirectMessageEntity
                {
                    SenderId = senderId,
                    ReceiverId = receiverId,
                    Text = text,
                    Entities = entities ?? [],
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatorId = senderId,
                    ReplyTo = replyTo
                };

                ctx.DirectMessages.Add(message);
                await ctx.SaveChangesAsync(ct);

                var messageId = message.MessageId;

                // Update chat preview for sender
                await UpdateChatPreviewAsync(ctx, senderId, receiverId, text, message.CreatedAt, false, ct);

                // Update chat preview for receiver and increment unread count
                await UpdateChatPreviewAsync(ctx, receiverId, senderId, text, message.CreatedAt, true, ct);

                await ctx.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                // Create DTO for the message
                var messageDto = message.ToDto();

                // Notify both users
                var dmEvent = new DirectMessageSent(senderId, receiverId, messageDto);

                _ = Task.Run(async () =>
                {
                    await NotifyAsync(senderId, dmEvent);
                    await NotifyAsync(receiverId, dmEvent);
                }, ct);

                logger.LogInformation("DirectMessage sent: MessageId={MessageId}", messageId);

                // Track message sent for stats
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

    public async Task<List<DirectMessage>> QueryDirectMessagesAsync(Guid peerId, long? from, int limit, CancellationToken ct = default)
    {
        var me = Me;

        logger.LogDebug("QueryDirectMessages: {Me} <-> {Peer}, From={From}, Limit={Limit}", me, peerId, from, limit);

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Query messages where either:
        // - I'm sender and peer is receiver
        // - Peer is sender and I'm receiver
        // - System message for me (SenderId = SystemUser, ReceiverId = me)
        var query = ctx.DirectMessages
            .AsNoTracking()
            .Where(m =>
                (m.SenderId == me && m.ReceiverId == peerId) ||
                (m.SenderId == peerId && m.ReceiverId == me) ||
                (m.SenderId == UserEntity.SystemUser && m.ReceiverId == me));

        if (from.HasValue)
        {
            query = query.Where(m => m.MessageId < from.Value);
        }

        var messages = await query
            .OrderByDescending(m => m.MessageId)
            .Take(limit)
            .ToListAsync(ct);

        return messages.Select(m => m.ToDto()).ToList();
    }

    public async Task UpdateChatForAsync(Guid userId, Guid peerId, string? previewText, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        var me = userId;

        logger.LogDebug("UpdateChatForAsync: {Me} <-> {Peer}", me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () => {
            var record = await ctx.UserChatlist
               .FirstOrDefaultAsync(x => x.UserId == me && x.PeerId == peerId, ct);

            if (record is null)
            {
                record = new UserChatEntity
                {
                    UserId          = me,
                    PeerId          = peerId,
                    LastMessageAt   = timestamp,
                    LastMessageText = previewText,
                    IsPinned        = false,
                    PinnedAt        = null
                };

                ctx.UserChatlist.Add(record);
            }
            else
            {
                record.LastMessageAt   = timestamp;
                record.LastMessageText = previewText;

                ctx.UserChatlist.Update(record);
            }

            await ctx.SaveChangesAsync(ct);
        }, ct);
        await NotifyAsync(me, new RecentChatUpdatedEvent(
            peerId,
            me,
            previewText,
            timestamp.UtcDateTime
        ));
    }

    public async Task UpdateChatAsync(Guid peerId, string? previewText, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        var me = Me;
        await UpdateChatForAsync(me, peerId, previewText, timestamp, ct);
    }

    private async Task UpdateChatPreviewAsync(
        ApplicationDbContext ctx,
        Guid userId,
        Guid peerId,
        string? previewText,
        DateTimeOffset timestamp,
        bool incrementUnread,
        CancellationToken ct)
    {
        var record = await ctx.UserChatlist
            .FirstOrDefaultAsync(x => x.UserId == userId && x.PeerId == peerId, ct);

        if (record is null)
        {
            record = new UserChatEntity
            {
                UserId = userId,
                PeerId = peerId,
                LastMessageAt = timestamp,
                LastMessageText = previewText,
                IsPinned = false,
                PinnedAt = null,
                UnreadCount = incrementUnread ? 1 : 0
            };

            ctx.UserChatlist.Add(record);
        }
        else
        {
            record.LastMessageAt = timestamp;
            record.LastMessageText = previewText;

            if (incrementUnread)
            {
                record.UnreadCount++;
            }

            ctx.UserChatlist.Update(record);
        }
    }


    private async Task NotifyAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);

        if (sessions.Count == 0) return;

        await notifier.NotifySessionsAsync(sessions, payload);
    }


    private async static Task ExecuteInTransactionAsync(
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