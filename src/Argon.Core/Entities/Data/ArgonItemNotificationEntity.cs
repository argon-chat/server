namespace Argon.Api.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ArgonItemNotificationEntity : IEntityTypeConfiguration<ArgonItemNotificationEntity>
{
    public required Guid           OwnerUserId     { get; set; }
    public required Guid           InventoryItemId { get; set; }
    public required DateTimeOffset CreatedAt       { get; set; } = DateTimeOffset.UtcNow;
    public required string         TemplateId      { get; set; }

    public void Configure(EntityTypeBuilder<ArgonItemNotificationEntity> builder)
    {
        builder.HasKey(x => new
        {
            x.OwnerUserId,
            x.InventoryItemId
        });

        builder.Property(x => x.OwnerUserId).IsRequired();
        builder.Property(x => x.InventoryItemId).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => new { x.OwnerUserId, x.CreatedAt })
           .HasDatabaseName("ix_unread_owner_created");

        builder.HasOne<ArgonItemEntity>()
           .WithMany()
           .HasForeignKey(x => x.InventoryItemId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.TemplateId);
        builder.Property(x => x.TemplateId).HasMaxLength(255);
    }
}