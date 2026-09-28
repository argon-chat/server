namespace Argon.Entities;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One object in the store. A <see cref="FileEntity"/> is a name for it; several files may share one.
/// </summary>
/// <remarks>
/// <para><see cref="Sha256"/> is only ever written from a hash the server computed over the object.
/// A client's claim is compared against it, never stored in it: a claim that decided a merge would let
/// anyone upload garbage under the hash of a popular file and have every later copy of that file land on
/// the garbage. <see cref="Md5"/> is the ETag of a single-part PUT and only narrows the candidates.</para>
///
/// <para><see cref="Links"/> counts live files pointing here, and is a hint: the sweep that removes the
/// object recounts before it deletes, so a decrement that raced a link cannot lose the bytes.
/// <see cref="CanonicalId"/> marks a blob merged into another; its object stays until
/// <see cref="DeleteAfter"/>, longer than the redirect cache remembers the old key.</para>
/// </remarks>
public record BlobEntity : ArgonEntity, IEntityTypeConfiguration<BlobEntity>
{
    [MaxLength(512)]
    public required string S3Key { get; set; }
    public long    Size        { get; set; }
    [MaxLength(255)]
    public string? ContentType { get; set; }
    public byte[]? Md5         { get; set; }
    public byte[]? Sha256      { get; set; }
    public long    Links       { get; set; }

    /// <summary>Whether two files may share this object; false where something writes back over the key.</summary>
    public bool Dedupable { get; set; }

    public DateTimeOffset? VerifyRequestedAt { get; set; }
    public Guid?           CanonicalId       { get; set; }
    public DateTimeOffset? DeleteAfter       { get; set; }

    public void Configure(EntityTypeBuilder<BlobEntity> builder)
    {
        builder.HasHashShardedKey();
        builder.HasIndex(x => x.S3Key).IsUnique();

        // One canonical object per content: the identity is the bytes and the type they are served as.
        builder.HasIndex(x => new { x.Sha256, x.ContentType })
           .IsUnique()
           .HasFilter("\"Sha256\" IS NOT NULL AND \"CanonicalId\" IS NULL AND \"IsDeleted\" = false");

        builder.HasIndex(x => new { x.Md5, x.Size, x.ContentType });

        builder.HasIndex(x => x.VerifyRequestedAt)
           .HasFilter("\"VerifyRequestedAt\" IS NOT NULL AND \"IsDeleted\" = false");

        builder.HasIndex(x => x.DeleteAfter)
           .HasFilter("\"DeleteAfter\" IS NOT NULL AND \"IsDeleted\" = false");
    }
}
