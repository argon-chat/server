namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Per-user metadata for a conversation.
/// Stores pinning, unread count, and other user-specific settings.
/// </summary>
public class UserConversationEntity : IEntityTypeConfiguration<UserConversationEntity>, 
    IMapper<UserConversationEntity, UserChat>
{
    public const string TableName = "user_conversations";

    /// <summary>
    /// User who owns this record.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The conversation this metadata belongs to.
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The other participant in the conversation (cached for quick access).
    /// </summary>
    public Guid PeerId { get; set; }

    /// <summary>
    /// Whether this chat is pinned for this user.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// When the chat was pinned.
    /// </summary>
    public DateTimeOffset? PinnedAt { get; set; }

    /// <summary>
    /// Number of unread messages for this user.
    /// </summary>
    public int UnreadCount { get; set; }

    /// <summary>
    /// Last read message ID for this user.
    /// </summary>
    public long? LastReadMessageId { get; set; }

    /// <summary>
    /// Cached last message timestamp (from conversation).
    /// </summary>
    public DateTimeOffset LastMessageAt { get; set; }

    /// <summary>
    /// Cached last message text preview (from conversation).
    /// </summary>
    [MaxLength(2048)]
    public string? LastMessageText { get; set; }

    /// <summary>
    /// Whether the user has archived/hidden this conversation.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// Whether the user has muted notifications for this conversation.
    /// </summary>
    public bool IsMuted { get; set; }

    public void Configure(EntityTypeBuilder<UserConversationEntity> b)
    {
        b.ToTable(TableName);
        
        b.HasKey(x => new { x.UserId, x.ConversationId });

        b.Property(x => x.UserId).IsRequired();
        b.Property(x => x.ConversationId).IsRequired();
        b.Property(x => x.PeerId).IsRequired();

        b.Property(x => x.IsPinned).HasDefaultValue(false);
        b.Property(x => x.IsArchived).HasDefaultValue(false);
        b.Property(x => x.IsMuted).HasDefaultValue(false);
        b.Property(x => x.UnreadCount).HasDefaultValue(0);

        b.Property(x => x.LastMessageText).HasMaxLength(2048);

        // Main index for listing chats: pinned first, then by last message
        b.HasIndex(x => new { x.UserId, x.IsArchived, x.IsPinned, x.PinnedAt, x.LastMessageAt })
            .HasDatabaseName("ix_user_conversations_sort")
            .IsDescending(false, false, true, true, true);

        // Index for looking up by user + peer (alternative to conversation id)
        b.HasIndex(x => new { x.UserId, x.PeerId })
            .HasDatabaseName("ix_user_conversations_peer")
            .IsUnique();

        // Index for finding all users in a conversation
        b.HasIndex(x => x.ConversationId)
            .HasDatabaseName("ix_user_conversations_conversation");
    }

    public static UserChat Map(scoped in UserConversationEntity self)
        => new(
            self.PeerId,
            self.IsPinned,
            self.UserId,
            self.LastMessageText,
            self.LastMessageAt.UtcDateTime,
            self.PinnedAt?.UtcDateTime,
            self.UnreadCount
        );
}
