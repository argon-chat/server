namespace Argon.Core.Services;

using Entities.Data;
using Argon.Entities;
using Microsoft.EntityFrameworkCore;

public interface ISystemMessageService
{
    Task<long> SendCallStartedMessageAsync(Guid senderId, Guid receiverId, Guid callId, CancellationToken ct = default);
    Task<long> SendCallEndedMessageAsync(Guid senderId, Guid receiverId, Guid callId, int durationSeconds, CancellationToken ct = default);
    Task<long> SendCallTimeoutMessageAsync(Guid senderId, Guid receiverId, Guid callId, CancellationToken ct = default);
    Task       SendUserJoinedMessageAsync(Guid spaceId, Guid userId, Guid? inviterId = null, CancellationToken ct = default);
}

public class SystemMessageService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IConversationService conversationService,
    ILogger<SystemMessageService> logger) : ISystemMessageService
{
    public async Task<long> SendCallStartedMessageAsync(Guid senderId, Guid receiverId, Guid callId, CancellationToken ct = default)
    {
        logger.LogInformation("Sending call started system message: {SenderId} -> {ReceiverId}, CallId={CallId}",
            senderId, receiverId, callId);

        return await SendSystemMessageToConversationAsync(
            senderId,
            receiverId,
            "Call started",
            [new MessageEntitySystemCallStarted(EntityType.SystemCallStarted, 0, 0, 1, senderId, callId)],
            ct);
    }

    public async Task<long> SendCallEndedMessageAsync(Guid senderId, Guid receiverId, Guid callId, int durationSeconds,
        CancellationToken ct = default)
    {
        logger.LogInformation("Sending call ended system message: {SenderId} -> {ReceiverId}, CallId={CallId}, Duration={Duration}s",
            senderId, receiverId, callId, durationSeconds);

        var text = $@"Call ended ({TimeSpan.FromSeconds(durationSeconds):hh\:mm\:ss})";

        return await SendSystemMessageToConversationAsync(
            senderId,
            receiverId,
            text,
            [new MessageEntitySystemCallEnded(EntityType.SystemCallEnded, 0, 0, 1, senderId, callId, durationSeconds)],
            ct);
    }

    public async Task<long> SendCallTimeoutMessageAsync(Guid senderId, Guid receiverId, Guid callId, CancellationToken ct = default)
    {
        logger.LogInformation("Sending call timeout system message: {SenderId} -> {ReceiverId}, CallId={CallId}",
            senderId, receiverId, callId);

        return await SendSystemMessageToConversationAsync(
            senderId,
            receiverId,
            "Call not answered",
            [new MessageEntitySystemCallTimeout(EntityType.SystemCallTimeout, 0, 0, 1, senderId, callId)],
            ct);
    }

    /// <summary>
    /// Sends a system message to a conversation. The message is stored once per conversation,
    /// visible to both participants.
    /// </summary>
    private async Task<long> SendSystemMessageToConversationAsync(
        Guid user1,
        Guid user2,
        string text,
        List<IMessageEntity> entities,
        CancellationToken ct)
    {
        // Get or create conversation between the two users
        var conversation = await conversationService.GetOrCreateConversationAsync(user1, user2, ct);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        // Create single message in the conversation
        var message = new DirectMessageV2Entity
        {
            ConversationId = conversation.Id,
            SenderId = UserEntity.SystemUser,
            Text = text,
            Entities = entities,
            CreatedAt = now,
            CreatorId = UserEntity.SystemUser
        };

        ctx.DirectMessages.Add(message);

        // Update conversation metadata
        conversation.LastMessageAt = now;
        conversation.LastMessageText = text;
        conversation.LastMessageSenderId = UserEntity.SystemUser;
        ctx.Conversations.Update(conversation);

        // Update both users' conversation metadata
        await UpdateUserConversationForSystemMessageAsync(ctx, user1, user2, conversation, text, now, ct);
        await UpdateUserConversationForSystemMessageAsync(ctx, user2, user1, conversation, text, now, ct);

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("System message sent to conversation {ConversationId}: MessageId={MessageId}",
            conversation.Id, message.MessageId);

        return message.MessageId;
    }

    private static async Task UpdateUserConversationForSystemMessageAsync(
        ApplicationDbContext ctx,
        Guid userId,
        Guid peerId,
        ConversationEntity conversation,
        string? previewText,
        DateTimeOffset timestamp,
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
                UnreadCount = 0 // System messages don't increment unread count
            };

            ctx.UserConversations.Add(record);
        }
        else
        {
            record.LastMessageAt = timestamp;
            record.LastMessageText = previewText;
            ctx.UserConversations.Update(record);
        }
    }

    public async Task SendUserJoinedMessageAsync(Guid spaceId, Guid userId, Guid? inviterId = null, CancellationToken ct = default)
    {
        logger.LogInformation("Sending user joined system message: SpaceId={SpaceId}, UserId={UserId}, InviterId={InviterId}",
            spaceId, userId, inviterId);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var space = await ctx.Spaces
           .AsNoTracking()
           .FirstOrDefaultAsync(s => s.Id == spaceId, ct);

        if (space == null)
        {
            logger.LogWarning("Space not found: {SpaceId}", spaceId);
            return;
        }

        if (space.IsCommunity)
        {
            logger.LogDebug("Skipping user joined message for community space: {SpaceId}", spaceId);
            return;
        }

        if (!space.DefaultChannelId.HasValue)
        {
            logger.LogWarning("Space has no default channel: {SpaceId}", spaceId);
            return;
        }

        var user = await ctx.Users
           .AsNoTracking()
           .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user == null)
        {
            logger.LogWarning("User not found: {UserId}", userId);
            return;
        }

        var inviterText = inviterId.HasValue ? $" (invited by {inviterId})" : "";
        var message = new ArgonMessageEntity
        {
            SpaceId   = spaceId,
            ChannelId = space.DefaultChannelId.Value,
            CreatorId = UserEntity.SystemUser,
            Text      = $"{user.Username} joined the space{inviterText}",
            Entities  = [new MessageEntitySystemUserJoined(EntityType.SystemUserJoined, 0, 0, 1, userId, inviterId)],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        ctx.Messages.Add(message);
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("User joined message sent: MessageId={MessageId}", message.MessageId);
    }
}