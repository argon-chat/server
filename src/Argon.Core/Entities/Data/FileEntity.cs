namespace Argon.Entities;

using Argon.Features.EF;
using Argon.Features.Storage;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record FileEntity : ArgonEntity, IEntityTypeConfiguration<FileEntity>
{
    public required Guid        OwnerId     { get; set; }
    public required FilePurpose Purpose     { get; set; }
    [MaxLength(128)]
    public required string      BucketName  { get; set; }
    public          long        FileSize    { get; set; }
    public          string?     ContentType { get; set; }
    public          string?     Checksum    { get; set; }
    public          string?     FileName    { get; set; }
    public          bool        Finalized   { get; set; }

    public Guid? SpaceId   { get; set; }
    public Guid? ChannelId { get; set; }

    /// <summary>The object this file names; the key lives there. Null only on a soft-deleted row from before blobs.</summary>
    public Guid? BlobId { get; set; }

    public void Configure(EntityTypeBuilder<FileEntity> builder)
    {
        builder.HasHashShardedKey();
        builder.HasIndex(x => x.OwnerId);
        builder.HasIndex(x => x.BlobId);
        builder.HasIndex(x => new { x.SpaceId, x.ChannelId });
        builder.Property(x => x.Purpose).HasConversion<int>();
    }
}
