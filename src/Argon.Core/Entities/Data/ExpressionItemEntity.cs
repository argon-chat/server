namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>A sticker or custom emoji in a pack. <see cref="ArgonEntityWithOwnership.CreatorId"/> uploaded it.</summary>
public record ExpressionItemEntity : ArgonEntityWithOwnership, IEntityTypeConfiguration<ExpressionItemEntity>
{
    public const int MaxNameLength = 64;

    public required Guid             PackId      { get; set; }
    public required Guid             SpaceId     { get; set; }
    public required ExpressionKind   Kind        { get; set; }
    public required ExpressionFormat Format      { get; set; }
    public required string           Name        { get; set; }
    public required Guid             FileId      { get; set; }
    public          Guid?            ThumbFileId { get; set; }
    public          int              Width       { get; set; }
    public          int              Height      { get; set; }
    public          int              FileSize    { get; set; }
    public          List<string>     Emoji       { get; set; } = new();
    public          List<string>     Keywords    { get; set; } = new();
    public          byte[]?          Outline     { get; set; }
    public          bool             TextColor   { get; set; }
    public          int              SortOrder   { get; set; }

    public void Configure(EntityTypeBuilder<ExpressionItemEntity> builder)
    {
        builder.ToTable("ExpressionItems");

        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Format).HasConversion<int>();
        builder.Property(x => x.Name).HasMaxLength(MaxNameLength);

        builder.Property(x => x.Emoji)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>())
           .Metadata.SetValueComparer(new JsonValueComparer<List<string>>());

        builder.Property(x => x.Keywords)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>())
           .Metadata.SetValueComparer(new JsonValueComparer<List<string>>());

        builder.HasOne<ExpressionPackEntity>()
           .WithMany(p => p.Items)
           .HasForeignKey(x => x.PackId);

        builder.HasIndex(x => new { x.PackId, x.SortOrder });
        builder.HasIndex(x => new { x.SpaceId, x.Kind, x.IsDeleted });
        // Emoji names are unique per space; sticker captions are not.
        builder.HasIndex(x => new { x.SpaceId, x.Name })
           .IsUnique()
           .HasFilter("\"Kind\" = 1 AND \"IsDeleted\" = false");
    }
}
