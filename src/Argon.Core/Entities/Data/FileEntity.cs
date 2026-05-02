namespace Argon.Entities;

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

    public void Configure(EntityTypeBuilder<FileEntity> builder)
    {
        builder.HasIndex(x => x.OwnerId);
        builder.HasIndex(x => x.S3Key).IsUnique();
        builder.HasIndex(x => new { x.SpaceId, x.ChannelId });
        builder.Property(x => x.Purpose).HasConversion<int>();
    }
}
