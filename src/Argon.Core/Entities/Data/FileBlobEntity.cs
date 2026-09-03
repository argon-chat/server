namespace Argon.Entities;

using Argon.Features.Storage;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record FileBlobEntity : ArgonEntity, IEntityTypeConfiguration<FileBlobEntity>
{
    public required Guid        FileId    { get; set; }
    public required Guid        OwnerId   { get; set; }
    public required FilePurpose Purpose   { get; set; }
    public required long        SizeLimit { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }

    public void Configure(EntityTypeBuilder<FileBlobEntity> builder)
    {
        builder.HasIndex(x => x.FileId);

        // Partial index for the GC sweep (ExpiresAt < now LIMIT n). Remove() is a soft delete,
        // so expired rows pile up under IsDeleted = true forever; a plain ExpiresAt index puts
        // them all in front of the live ones and the planner falls back to a full PK scan.
        // Only live rows live in this index, so the sweep reads at most `n` entries.
        builder.HasIndex(x => x.ExpiresAt)
           .HasFilter("\"IsDeleted\" = false")
           .IsCreatedConcurrently();
        builder.Property(x => x.Purpose).HasConversion<int>();
    }
}
