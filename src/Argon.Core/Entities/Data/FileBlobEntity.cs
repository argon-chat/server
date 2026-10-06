namespace Argon.Entities;

using Argon.Features.EF;
using Argon.Features.Storage;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record FileBlobEntity : ArgonEntity, IEntityTypeConfiguration<FileBlobEntity>
{
    public required Guid        FileId    { get; set; }
    public required Guid        OwnerId   { get; set; }
    public required FilePurpose Purpose   { get; set; }
    public required long        SizeLimit { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>What the client says the bytes hash to. A hint for the dedup step at finalize, never stored as a fact.</summary>
    public byte[]? ClaimedSha256 { get; set; }

    /// <summary>The S3 multipart upload behind a video ticket, when it is uploaded in parts.</summary>
    [MaxLength(256)]
    public string? UploadId { get; set; }
    public long? PartSize  { get; set; }
    public int?  PartCount { get; set; }

    /// <summary>A video ticket's <c>VideoUploadDeclaration</c>, as JSON; checked against the header at completion.</summary>
    public string? Declaration { get; set; }

    public void Configure(EntityTypeBuilder<FileBlobEntity> builder)
    {
        builder.Property(x => x.Declaration).HasColumnType("jsonb");

        builder.HasHashShardedKey();

        // FileId is minted with the blob, so it is as time-ordered as the key.
        builder.HasIndex(x => x.FileId).IsHashSharded();

        // Partial index for the GC sweep (ExpiresAt < now LIMIT n). Remove() is a soft delete,
        // so expired rows pile up under IsDeleted = true forever; a plain ExpiresAt index puts
        // them all in front of the live ones and the planner falls back to a full PK scan.
        // Only live rows live in this index, so the sweep reads at most `n` entries.
        builder.HasIndex(x => x.ExpiresAt)
           .HasFilter("\"IsDeleted\" = false")
           .IsCreatedConcurrently()
           .IsHashSharded();
        builder.Property(x => x.Purpose).HasConversion<int>();
    }
}
