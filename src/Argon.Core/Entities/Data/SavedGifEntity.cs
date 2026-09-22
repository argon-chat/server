namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SavedGifEntity : ArgonEntity, IEntityTypeConfiguration<SavedGifEntity>
{
    public required Guid UserId { get; set; }

    [MaxLength(256)]
    public string? Slug { get; set; }

    public required Guid FileId { get; set; }

    public int Width  { get; set; }
    public int Height { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Configure(EntityTypeBuilder<SavedGifEntity> builder)
    {
        builder.HasIndex(x => new { x.UserId, x.Slug }).IsUnique()
            .HasFilter("\"Slug\" IS NOT NULL");
        // The unique index is partial, so it cannot serve a user's whole list; this one pages it.
        builder.HasIndex(x => new { x.UserId, x.AddedAt });
        builder.HasIndex(x => x.FileId);
    }
}
