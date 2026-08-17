namespace Argon.Entities;

using Argon.Features.Auth;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A hardware key, and therefore a machine.
/// </summary>
/// <remarks>
/// <para>One row per key, not per account. The client keeps a single key per machine, so a second
/// account enrolling on the same hardware presents the same thumbprint and lands on the same
/// <see cref="DeviceId"/> — which is the alt signal, arrived at cryptographically rather than by
/// guessing from serial numbers.</para>
///
/// <para>Who used the machine lives in <see cref="DeviceObservationEntity"/>. Keeping the two apart
/// is what lets a device be shared without either account owning it, and lets a ban attach to the
/// machine rather than to whoever happened to enrol it first.</para>
/// </remarks>
public record DeviceKeyEntity : ArgonEntity, IEntityTypeConfiguration<DeviceKeyEntity>
{
    public Guid DeviceId { get; set; }

    /// <summary>SHA-256 of the SubjectPublicKeyInfo, base64url. The identity of the machine.</summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>SubjectPublicKeyInfo, base64. Public by definition — nothing here is a secret.</summary>
    public string PublicKey { get; set; } = string.Empty;

    public DevicePlatform Platform { get; set; }

    /// <summary>
    /// What was proven at enrolment.
    /// </summary>
    /// <remarks>
    /// Recorded rather than recomputed because attestation is a statement about a moment: the chain
    /// that vouched for this key was valid when it was offered, and re-deriving the level later from
    /// a blob nobody kept would answer a different question.
    /// </remarks>
    public DeviceAssurance Assurance { get; set; }

    /// <summary>Whatever the client called itself. Shown on the devices screen and trusted for nothing.</summary>
    public string ClientName { get; set; } = string.Empty;

    public DateTimeOffset EnrolledAt { get; set; }

    /// <summary>Last time the key answered a challenge, as opposed to merely being on file.</summary>
    public DateTimeOffset? LastProvenAt { get; set; }

    public void Configure(EntityTypeBuilder<DeviceKeyEntity> builder)
    {
        // Base64url of a SHA-256 digest is 43 characters; the ceiling is there because the column is
        // indexed and an unbounded text column cannot be.
        builder.Property(x => x.Thumbprint).HasMaxLength(64);

        // The thumbprint is the natural key: two enrolments of the same key are the same machine,
        // and a unique index is what makes that true rather than merely intended.
        builder.HasIndex(x => x.Thumbprint).IsUnique();
        builder.HasIndex(x => x.DeviceId).IsUnique();
    }
}
