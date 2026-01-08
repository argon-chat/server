namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record NotificationCounterEntity : IEntityTypeConfiguration<NotificationCounterEntity>
{
    public required Guid   UserId        { get; set; }
    public required string CounterType   { get; set; }
    public required long   Count         { get; set; }
    public          DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Configure(EntityTypeBuilder<NotificationCounterEntity> builder)
    {
        builder.ToTable("NotificationCounters");

        builder.HasKey(x => new { x.UserId, x.CounterType });

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.CounterType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Count).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_notification_counters_user_id");

        builder.HasIndex(x => x.UpdatedAt)
            .HasDatabaseName("ix_notification_counters_updated_at");
    }
}

public static class NotificationCounterType
{
    public const string UnreadInventoryItems = "unread_inventory";
    public const string PendingFriendRequests = "pending_friend_requests";
    public const string UnreadDirectMessages = "unread_direct_messages";
}
