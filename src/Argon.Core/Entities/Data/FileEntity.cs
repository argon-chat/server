namespace Argon.Entities;

using Argon.Features.EF;
using Argon.Features.Storage;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record FileEntity : ArgonEntity, IEntityTypeConfiguration<FileEntity>
{
    public required Guid        OwnerId     { get; set; }
    public required FilePurpose Purpose     { get; set; }
    [MaxLength(512)]
    public required string      S3Key       { get; set; }
    [MaxLength(128)]
    public required string      BucketName  { get; set; }
    public          long        FileSize    { get; set; }
    public          string?     ContentType { get; set; }
    public          string?     Checksum    { get; set; }
    public          string?     FileName    { get; set; }
    public          bool        Finalized   { get; set; }

    public Guid? SpaceId   { get; set; }
    public Guid? ChannelId { get; set; }

    /// <summary>The object this file names. Null only for a file not finalized yet, or finalized by a silo that predates blobs.</summary>
    /// <remarks><see cref="S3Key"/> mirrors the blob's key while readers still go by it; the blob is the truth and a merge rewrites both.</remarks>
    public Guid? BlobId { get; set; }

    public void Configure(EntityTypeBuilder<FileEntity> builder)
    {
        builder.HasHashShardedKey();
        builder.HasIndex(x => x.OwnerId);
        // Not unique any more: files that share an object share its key. A new name, because CockroachDB
        // refuses to create an index under the name of one dropped in the same transaction.
        builder.HasIndex(x => x.S3Key).HasDatabaseName("IX_Files_S3Key_Mirror");
        builder.HasIndex(x => x.BlobId);
        builder.HasIndex(x => new { x.SpaceId, x.ChannelId });
        builder.Property(x => x.Purpose).HasConversion<int>();
    }
}
