namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

/// <summary>
/// Direct message stored once per conversation.
/// Unlike the old DirectMessageEntity, messages are not duplicated per user.
/// System messages are stored with SenderId = SystemUser and appear naturally in the conversation.
/// </summary>
public record DirectMessageV2Entity : ArgonEntityWithOwnershipNoKey, 
    IEntityTypeConfiguration<DirectMessageV2Entity>,
    IMapper<DirectMessageV2Entity, DirectMessage>
{
    public const string TableName = "direct_messages_v2";

    /// <summary>
    /// Unique message ID within the conversation, auto-incremented.
    /// </summary>
    public long MessageId { get; set; }

    /// <summary>
    /// Reference to the conversation this message belongs to.
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// User who sent the message. Can be SystemUser for system messages.
    /// </summary>
    public required Guid SenderId { get; set; }

    /// <summary>
    /// Message ID this is replying to, if any.
    /// </summary>
    public long? ReplyTo { get; set; }

    /// <summary>
    /// Message text content.
    /// </summary>
    [MaxLength(4096)]
    public required string Text { get; set; }

    /// <summary>
    /// Message entities (mentions, links, formatting, system info, etc).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<IMessageEntity> Entities { get; set; } = [];

    public void Configure(EntityTypeBuilder<DirectMessageV2Entity> builder)
    {
        builder.ToTable(TableName);

        // Primary key: ConversationId + MessageId
        builder.HasKey(m => new { m.ConversationId, m.MessageId });

        // Auto-increment MessageId per conversation
        builder.Property(m => m.MessageId)
            .HasColumnType("BIGINT")
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("unique_rowid()");

        builder.Property(m => m.ConversationId).IsRequired();
        builder.Property(m => m.SenderId).IsRequired();

        builder.Property(m => m.ReplyTo)
            .HasColumnType("BIGINT");

        builder.Property(m => m.Text)
            .HasMaxLength(4096)
            .IsRequired();

        builder.Property(m => m.Entities)
            .HasConversion<PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>>()
            .HasColumnType("jsonb");

        builder.Property(m => m.CreatorId)
            .IsRequired();

        // Index for querying messages by conversation ordered by time
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAt })
            .HasDatabaseName("ix_dm_v2_conversation_time")
            .IsDescending(false, true);

        // Index for querying by sender (useful for user stats)
        builder.HasIndex(m => m.SenderId)
            .HasDatabaseName("ix_dm_v2_sender");
    }

    public static DirectMessage Map(scoped in DirectMessageV2Entity self)
        => new(
            self.MessageId,
            self.SenderId,
            Guid.Empty, // ReceiverId is now derived from conversation
            self.Text,
            self.Entities,
            self.CreatedAt.UtcDateTime,
            self.ReplyTo
        );

    /// <summary>
    /// Maps to DTO with explicit receiver ID (the other participant in conversation).
    /// </summary>
    public DirectMessage ToDto(Guid receiverId)
        => new(
            MessageId,
            SenderId,
            receiverId,
            Text,
            Entities,
            CreatedAt.UtcDateTime,
            ReplyTo
        );
}
