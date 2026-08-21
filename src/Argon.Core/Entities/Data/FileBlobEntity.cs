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
        builder.HasIndex(x => x.ExpiresAt);
        builder.Property(x => x.Purpose).HasConversion<int>();
    }
}
