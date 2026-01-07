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
    ILogger<SystemMessageService> logger) : ISystemMessageService
{
    public async Task<long> SendCallStartedMessageAsync(Guid senderId, Guid receiverId, Guid callId, CancellationToken ct = default)
    {
        logger.LogInformation("Sending call started system message: {SenderId} -> {ReceiverId}, CallId={CallId}",
            senderId, receiverId, callId);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        // Create system message for both users in the conversation
        var message1 = new DirectMessageEntity
        {
            SenderId   = UserEntity.SystemUser,
            ReceiverId = senderId,
            Text       = $"Call started",
            Entities =
            [
                new MessageEntitySystemCallStarted(EntityType.SystemCallStarted, 0, 0, 1, senderId, callId)
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatorId = UserEntity.SystemUser
        };

        var message2 = new DirectMessageEntity
        {
            SenderId   = UserEntity.SystemUser,
            ReceiverId = receiverId,
            Text       = $"Call started",
            Entities =
            [
                new MessageEntitySystemCallStarted(EntityType.SystemCallStarted, 0, 0, 1, senderId, callId)
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatorId = UserEntity.SystemUser
        };

        ctx.DirectMessages.Add(message1);
        ctx.DirectMessages.Add(message2);
        await ctx.SaveChangesAsync(ct);

        return message1.MessageId;
    }

    public async Task<long> SendCallEndedMessageAsync(Guid senderId, Guid receiverId, Guid callId, int durationSeconds,
        CancellationToken ct = default)
    {
        logger.LogInformation("Sending call ended system message: {SenderId} -> {ReceiverId}, CallId={CallId}, Duration={Duration}s",
            senderId, receiverId, callId, durationSeconds);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var text = $@"Call ended ({TimeSpan.FromSeconds(durationSeconds):hh\:mm\:ss})";

        var message1 = new DirectMessageEntity
        {
            SenderId   = UserEntity.SystemUser,
            ReceiverId = senderId,
            Text       = text,
            Entities =
            [
                new MessageEntitySystemCallEnded(EntityType.SystemCallEnded, 0, 0, 1, senderId, callId, durationSeconds)
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatorId = UserEntity.SystemUser
        };

        var message2 = new DirectMessageEntity
        {
            SenderId   = UserEntity.SystemUser,
            ReceiverId = receiverId,
            Text       = text,
            Entities =
            [
                new MessageEntitySystemCallEnded(EntityType.SystemCallEnded, 0, 0, 1, senderId, callId, durationSeconds)
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatorId = UserEntity.SystemUser
        };

        ctx.DirectMessages.Add(message1);
        ctx.DirectMessages.Add(message2);
        await ctx.SaveChangesAsync(ct);

        return message1.MessageId;
    }

    public async Task<long> SendCallTimeoutMessageAsync(Guid senderId, Guid receiverId, Guid callId, CancellationToken ct = default)
    {
        logger.LogInformation("Sending call timeout system message: {SenderId} -> {ReceiverId}, CallId={CallId}",
            senderId, receiverId, callId);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var message1 = new DirectMessageEntity
        {
            SenderId   = UserEntity.SystemUser,
            ReceiverId = senderId,
            Text       = "Call not answered",
            Entities =
            [
                new MessageEntitySystemCallTimeout(EntityType.SystemCallTimeout, 0, 0, 1, senderId, callId)
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatorId = UserEntity.SystemUser
        };

        var message2 = new DirectMessageEntity
        {
            SenderId   = UserEntity.SystemUser,
            ReceiverId = receiverId,
            Text       = "Call not answered",
            Entities =
            [
                new MessageEntitySystemCallTimeout(EntityType.SystemCallTimeout, 0, 0, 1, senderId, callId)
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatorId = UserEntity.SystemUser
        };

        ctx.DirectMessages.Add(message1);
        ctx.DirectMessages.Add(message2);
        await ctx.SaveChangesAsync(ct);

        return message1.MessageId;
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