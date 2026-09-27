namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>A space's sticker or custom emoji pack. <see cref="ArgonEntityWithOwnership.CreatorId"/> made it.</summary>
public record ExpressionPackEntity : ArgonEntityWithOwnership, IEntityTypeConfiguration<ExpressionPackEntity>
{
    public const int MaxTitleLength = 64;
    public const int MaxSlugLength  = 64;

    public required Guid           SpaceId     { get; set; }
    public required ExpressionKind Kind        { get; set; }
    public required string         Title       { get; set; }
    public required string         Slug        { get; set; }
    public          Guid?          CoverItemId { get; set; }
    public          int            SortOrder   { get; set; }
    public          long           Version     { get; set; }
    public          int            ItemCount   { get; set; }

    public virtual ICollection<ExpressionItemEntity> Items { get; set; } = new List<ExpressionItemEntity>();

    public void Configure(EntityTypeBuilder<ExpressionPackEntity> builder)
    {
        builder.ToTable("ExpressionPacks");

        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Title).HasMaxLength(MaxTitleLength);
        builder.Property(x => x.Slug).HasMaxLength(MaxSlugLength);

        builder.HasOne<SpaceEntity>()
           .WithMany()
           .HasForeignKey(x => x.SpaceId);

        builder.HasIndex(x => new { x.SpaceId, x.Kind, x.IsDeleted });
        builder.HasIndex(x => new { x.SpaceId, x.Slug })
           .IsUnique()
           .HasFilter("\"IsDeleted\" = false");
    }
}
