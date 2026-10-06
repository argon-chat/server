namespace Argon.Entities;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// The media record of a video file: what its MP4 header says, plus the poster, storyboard and
/// preload hint its uploader declared. Written once, when the upload is checked.
/// </summary>
public record FileMediaEntity : ArgonEntityNoKey, IEntityTypeConfiguration<FileMediaEntity>
{
    public required Guid FileId { get; set; }

    public int  Width      { get; set; }
    public int  Height     { get; set; }
    public int  DurationMs { get; set; }
    public bool HasAudio   { get; set; }

    [MaxLength(64)]
    public string? VideoCodec { get; set; }
    [MaxLength(64)]
    public string? AudioCodec { get; set; }

    public Guid? PosterFileId     { get; set; }
    public Guid? StoryboardFileId { get; set; }

    public int? StoryboardFrameWidth  { get; set; }
    public int? StoryboardFrameHeight { get; set; }
    public int? StoryboardColumns     { get; set; }
    public int? StoryboardFrameCount  { get; set; }
    public int? StoryboardIntervalMs  { get; set; }

    [MaxLength(64)]
    public string? ThumbHash { get; set; }

    public long? PreloadPrefixSize { get; set; }

    public void Configure(EntityTypeBuilder<FileMediaEntity> builder)
    {
        builder.ToTable("FileMedia");
        builder.HasKey(x => x.FileId);
        builder.HasHashShardedKey();
        builder.Property(x => x.FileId).ValueGeneratedNever();

        builder.HasOne<FileEntity>()
           .WithOne()
           .HasForeignKey<FileMediaEntity>(x => x.FileId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
