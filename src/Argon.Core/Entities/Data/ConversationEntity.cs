namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Represents a direct message conversation between two users.
/// Each conversation has a deterministic ID generated from sorted participant IDs.
/// </summary>
public class ConversationEntity : IEntityTypeConfiguration<ConversationEntity>
{
    public const string TableName = "conversations";

    /// <summary>
    /// Unique conversation ID, deterministically generated from participant IDs.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// First participant (the one with smaller GUID).
    /// </summary>
    public Guid Participant1Id { get; set; }

    /// <summary>
    /// Second participant (the one with larger GUID).
    /// </summary>
    public Guid Participant2Id { get; set; }

    /// <summary>
    /// When the conversation was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Timestamp of the last message in this conversation.
    /// </summary>
    public DateTimeOffset? LastMessageAt { get; set; }

    /// <summary>
    /// Preview text of the last message.
    /// </summary>
    [MaxLength(2048)]
    public string? LastMessageText { get; set; }

    /// <summary>
    /// ID of the last message sender.
    /// </summary>
    public Guid? LastMessageSenderId { get; set; }

    public void Configure(EntityTypeBuilder<ConversationEntity> b)
    {
        b.ToTable(TableName);
        b.HasKey(x => x.Id);

        b.Property(x => x.Participant1Id).IsRequired();
        b.Property(x => x.Participant2Id).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();

        b.Property(x => x.LastMessageText).HasMaxLength(2048);

        // Index for looking up conversation by participants
        b.HasIndex(x => new { x.Participant1Id, x.Participant2Id })
            .HasDatabaseName("ix_conversations_participants")
            .IsUnique();

        // Index for participant lookups (find all conversations for a user)
        b.HasIndex(x => x.Participant1Id)
            .HasDatabaseName("ix_conversations_participant1");

        b.HasIndex(x => x.Participant2Id)
            .HasDatabaseName("ix_conversations_participant2");
    }

    /// <summary>
    /// Generates a deterministic conversation ID from two user IDs.
    /// The same two users will always produce the same conversation ID.
    /// </summary>
    public static Guid GenerateConversationId(Guid user1, Guid user2)
    {
        // Sort the IDs to ensure consistent ordering
        var (smaller, larger) = user1.CompareTo(user2) < 0 
            ? (user1, user2) 
            : (user2, user1);

        // Create deterministic GUID by hashing the two user IDs
        Span<byte> buffer = stackalloc byte[32];
        smaller.TryWriteBytes(buffer[..16]);
        larger.TryWriteBytes(buffer[16..]);

        // Use XxHash128 for fast deterministic hashing
        var hash = System.IO.Hashing.XxHash128.Hash(buffer);
        return new Guid(hash);
    }

    /// <summary>
    /// Gets the ordered participant IDs (smaller first).
    /// </summary>
    public static (Guid Participant1, Guid Participant2) OrderParticipants(Guid user1, Guid user2)
    {
        return user1.CompareTo(user2) < 0 
            ? (user1, user2) 
            : (user2, user1);
    }
}
