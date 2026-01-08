namespace Argon.Core.Services;

using Entities.Data;
using Argon.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Service for managing conversations between users.
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Gets or creates a conversation between two users.
    /// </summary>
    Task<ConversationEntity> GetOrCreateConversationAsync(Guid user1, Guid user2, CancellationToken ct = default);

    /// <summary>
    /// Gets conversation by ID.
    /// </summary>
    Task<ConversationEntity?> GetConversationAsync(Guid conversationId, CancellationToken ct = default);

    /// <summary>
    /// Gets conversation between two users if it exists.
    /// </summary>
    Task<ConversationEntity?> FindConversationAsync(Guid user1, Guid user2, CancellationToken ct = default);

    /// <summary>
    /// Gets the other participant in a conversation.
    /// </summary>
    Guid GetOtherParticipant(ConversationEntity conversation, Guid userId);

    /// <summary>
    /// Ensures user conversation metadata exists for both participants.
    /// </summary>
    Task EnsureUserConversationsAsync(
        ApplicationDbContext ctx,
        ConversationEntity conversation,
        Guid user1,
        Guid user2,
        CancellationToken ct = default);
}

public class ConversationService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    ILogger<ConversationService> logger) : IConversationService
{
    public async Task<ConversationEntity> GetOrCreateConversationAsync(
        Guid user1,
        Guid user2,
        CancellationToken ct = default)
    {
        var conversationId = ConversationEntity.GenerateConversationId(user1, user2);
        var (participant1, participant2) = ConversationEntity.OrderParticipants(user1, user2);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var conversation = await ctx.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is not null)
            return conversation;

        conversation = new ConversationEntity
        {
            Id = conversationId,
            Participant1Id = participant1,
            Participant2Id = participant2,
            CreatedAt = DateTimeOffset.UtcNow
        };

        ctx.Conversations.Add(conversation);

        try
        {
            await ctx.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created conversation {ConversationId} between {User1} and {User2}",
                conversationId, user1, user2);
        }
        catch (DbUpdateException)
        {
            // Race condition: another request created the conversation
            conversation = await ctx.Conversations
                .FirstAsync(c => c.Id == conversationId, ct);
        }

        return conversation;
    }

    public async Task<ConversationEntity?> GetConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
    }

    public async Task<ConversationEntity?> FindConversationAsync(Guid user1, Guid user2, CancellationToken ct = default)
    {
        var conversationId = ConversationEntity.GenerateConversationId(user1, user2);
        return await GetConversationAsync(conversationId, ct);
    }

    public Guid GetOtherParticipant(ConversationEntity conversation, Guid userId)
    {
        return conversation.Participant1Id == userId
            ? conversation.Participant2Id
            : conversation.Participant1Id;
    }

    public async Task EnsureUserConversationsAsync(
        ApplicationDbContext ctx,
        ConversationEntity conversation,
        Guid user1,
        Guid user2,
        CancellationToken ct = default)
    {
        await EnsureSingleUserConversationAsync(ctx, conversation, user1, user2, ct);
        await EnsureSingleUserConversationAsync(ctx, conversation, user2, user1, ct);
    }

    private static async Task EnsureSingleUserConversationAsync(
        ApplicationDbContext ctx,
        ConversationEntity conversation,
        Guid userId,
        Guid peerId,
        CancellationToken ct)
    {
        var exists = await ctx.UserConversations
            .AnyAsync(uc => uc.UserId == userId && uc.ConversationId == conversation.Id, ct);

        if (exists) return;

        var userConv = new UserConversationEntity
        {
            UserId = userId,
            ConversationId = conversation.Id,
            PeerId = peerId,
            LastMessageAt = conversation.LastMessageAt ?? conversation.CreatedAt,
            LastMessageText = conversation.LastMessageText
        };

        ctx.UserConversations.Add(userConv);
    }
}
