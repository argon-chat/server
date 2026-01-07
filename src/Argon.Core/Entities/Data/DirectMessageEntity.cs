namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

/// <summary>
/// Direct message between two users.
/// Messages are stored symmetrically - each message appears once with ordered SenderId/ReceiverId.
/// </summary>
public record DirectMessageEntity : ArgonEntityWithOwnershipNoKey, IEntityTypeConfiguration<DirectMessageEntity>,
                                    IMapper<DirectMessageEntity, DirectMessage>
{
    public const string TableName = "direct_messages";

    /// <summary>
    /// Unique message ID within the conversation.
    /// </summary>
    public long MessageId { get; set; }

    /// <summary>
    /// User who sent the message.
    /// </summary>
    public required Guid SenderId { get; set; }

    /// <summary>
    /// User who receives the message.
    /// </summary>
    public required Guid ReceiverId { get; set; }

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
    /// Message entities (mentions, links, formatting, etc).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<IMessageEntity> Entities { get; set; } = new();

    public void Configure(EntityTypeBuilder<DirectMessageEntity> builder)
    {
        builder.ToTable(TableName);

        // Composite key: SenderId + ReceiverId + MessageId
        builder.HasKey(m => new
        {
            m.SenderId,
            m.ReceiverId,
            m.MessageId
        });

        // Index for querying messages in a conversation
        builder.HasIndex(m => new
            {
                m.SenderId,
                m.ReceiverId,
                m.CreatedAt
            })
           .HasDatabaseName("ix_dm_conversation_time")
           .IncludeProperties(m => new
            {
                m.Text,
                m.Entities
            });

        // Auto-increment MessageId
        builder.Property(m => m.MessageId)
           .HasColumnType("BIGINT")
           .ValueGeneratedOnAdd()
           .HasDefaultValueSql("unique_rowid()");

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
    }

    public static DirectMessage Map(scoped in DirectMessageEntity self)
        => new(
            self.MessageId,
            self.SenderId,
            self.ReceiverId,
            self.Text,
            self.Entities,
            self.CreatedAt.UtcDateTime,
            self.ReplyTo
        );
}