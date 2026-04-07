namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SystemNotificationEntity : IEntityTypeConfiguration<SystemNotificationEntity>
{
    public          Guid            Id          { get; set; } = Guid.CreateVersion7();
    public required Guid            UserId      { get; set; }
    public required string          Type        { get; set; }
    public          Guid?           ReferenceId { get; set; }
    public required string          Title       { get; set; }
    public          string?         Body        { get; set; }
    public          bool            IsRead      { get; set; }
    public          DateTimeOffset  CreatedAt   { get; set; } = DateTimeOffset.UtcNow;
    public          DateTimeOffset? ExpiresAt   { get; set; }

    public void Configure(EntityTypeBuilder<SystemNotificationEntity> builder)
    {
        builder.ToTable("SystemNotifications");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Type).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(2048);
        builder.Property(x => x.IsRead).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt })
            .HasDatabaseName("ix_system_notifications_user_unread")
            .IsDescending(false, false, true);

        builder.HasIndex(x => new { x.UserId, x.CreatedAt })
            .HasDatabaseName("ix_system_notifications_user_feed")
            .IsDescending(false, true);
    }
}

public static class SystemNotificationType
{
    public const string FriendRequestReceived = "friend_request_received";
    public const string FriendRequestAccepted = "friend_request_accepted";
    public const string ItemReceived          = "item_received";
    public const string SystemAnnouncement    = "system_announcement";
}
